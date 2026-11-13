using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;

using AsyncKeyedLock;

using CommunityToolkit.HighPerformance.Buffers;

using EpicManifestParser.Json;

using Microsoft.Win32.SafeHandles;

using OffiUtils;

namespace EpicManifestParser.UE;

// https://github.com/NotOfficer/UnrealEngine/blob/1436d646b9b11ffe9a46b04e9617dba689b56d35/Engine/Source/Runtime/Online/BuildPatchServices/Private/BuildPatchManifest.h?plain=1#L60C1-L63C29
/// <summary>
/// UE FBuildPatchAppManifest struct
/// </summary>
public class FBuildPatchAppManifest
{
    /// <summary/>
    public FManifestMeta Meta { get; internal set; } = null!;
    /// <summary/>
    public IReadOnlyList<FChunkInfo> ChunkList { get; internal set; } = null!;
    /// <summary/>
    public IReadOnlyList<FFileManifest> Files { get; internal set; } = null!;
    /// <summary/>
    public IReadOnlyList<FCustomField> CustomFields { get; internal set; } = null!;
    /// <summary/>
    public IReadOnlyDictionary<FGuid, FChunkInfo> Chunks { get; internal set; } = null!;

    /// <summary/>
    public int64 TotalBuildSize { get; internal set; }
    /// <summary/>
    public int64 TotalDownloadSize { get; internal set; }

    // The encryption secret ID and hash used for this manifest, if encrypted.
    // These are serialised with the manifest header, not the main data.
    internal FGuid? EncryptionSecretId { get; set; }
    internal FAESAuthTag? EncryptionAuthTag { get; set; }
    internal FEncryptedData? EncryptedData { get; set; }

    internal ManifestParseOptions Options { get; init; } = null!;

    // created on first use, so that options set after parsing (ChunkBaseUrl, ChunkCacheDirectory, ...) take effect
    private AsyncKeyedLocker<FGuid>? _chunksLocker;
    private ChunkMemoryCache? _chunkMemoryCache;

    /// <summary>
    /// Serializes the download of each chunk. Created on first use.
    /// </summary>
    internal AsyncKeyedLocker<FGuid> ChunksLocker => Volatile.Read(ref _chunksLocker) ?? CreateChunksLocker();

    /// <summary>
    /// Decompressed chunks kept in memory when there is no <see cref="ManifestParseOptions.ChunkCacheDirectory"/>.
    /// Created on first use, if <see cref="ManifestParseOptions.ChunkCacheDirectory"/> is <see langword="null"/> and
    /// <see cref="ManifestParseOptions.ChunkMemoryCacheCapacity"/> is positive at that time; otherwise <see langword="null"/>.
    /// </summary>
    internal ChunkMemoryCache? ChunkMemoryCache
    {
        get
        {
            ChunkMemoryCache? cache = Volatile.Read(ref _chunkMemoryCache);
            if (cache is not null || Options.ChunkCacheDirectory is not null)
                return cache;

            int32 capacity = Options.ChunkMemoryCacheCapacity;
            if (capacity <= 0)
                return null;

            // a cache that loses the race holds no buffers yet
            cache = new ChunkMemoryCache(capacity);
            return Interlocked.CompareExchange(ref _chunkMemoryCache, cache, null) ?? cache;
        }
    }

    internal FBuildPatchAppManifest() { }

    private AsyncKeyedLocker<FGuid> CreateChunksLocker()
    {
        var locker = new AsyncKeyedLocker<FGuid>(static lockerOptions =>
        {
            lockerOptions.MaxCount = 1;
            lockerOptions.PoolSize = 128;
            lockerOptions.PoolInitialFill = 64;
        });

        AsyncKeyedLocker<FGuid>? winner = Interlocked.CompareExchange(ref _chunksLocker, locker, null);
        if (winner is null)
            return locker;

        locker.Dispose();
        return winner;
    }

    /// <summary>
    /// Finds a file by <see cref="FFileManifest.FileName"/>.
    /// </summary>
    /// <param name="fileName">The filename to find.</param>
    /// <param name="comparisonType">The type to compare the filename.</param>
    /// <returns>The <see cref="FFileManifest"/> instance if the the file was found; otherwise, <see langword="null"/>.</returns>
    public FFileManifest? FindFile(string fileName, StringComparison comparisonType = StringComparison.Ordinal)
        => TryFindFile(fileName, comparisonType, out FFileManifest? file) ? file : null;

    /// <summary>
    /// Tries to find a file by <see cref="FFileManifest.FileName"/> using <see cref="StringComparison.Ordinal"/> to compare it.
    /// </summary>
    /// <param name="fileName">The filename to find.</param>
    /// <param name="fileManifest">The find result.</param>
    /// <returns><see langword="true"/> if the the file was found; otherwise, <see langword="false"/>.</returns>
    public bool TryFindFile(string fileName, [NotNullWhen(true)] out FFileManifest? fileManifest)
        => TryFindFile(fileName, StringComparison.Ordinal, out fileManifest);

    /// <summary>
    /// Tries to find a file by <see cref="FFileManifest.FileName"/>.
    /// </summary>
    /// <param name="fileName">The filename to find.</param>
    /// <param name="comparisonType">The type to compare the filename.</param>
    /// <param name="fileManifest">The find result.</param>
    /// <returns><see langword="true"/> if the the file was found; otherwise, <see langword="false"/>.</returns>
    public bool TryFindFile(string fileName, StringComparison comparisonType, [NotNullWhen(true)] out FFileManifest? fileManifest)
    {
        foreach (FFileManifest file in Files)
        {
            if (!file.FileName.Equals(fileName, comparisonType))
                continue;

            fileManifest = file;
            return true;
        }

        fileManifest = null;
        return false;
    }

    /// <summary>
    /// Helper function to decide whether the passed in data is a JSON string we expect to deserialize a manifest from
    /// </summary>
    /// <remarks>
    /// The data is JSON if its first character is the mandatory opening curly brace, after an optional UTF-8 BOM and
    /// JSON whitespace. A binary manifest starts with a magic whose first byte is neither.
    /// </remarks>
    /// <returns><see langword="true"/> if the <paramref name="dataInput"/> is JSON; otherwise, <see langword="false"/>.</returns>
    public static bool IsJson(ManifestRoData dataInput)
    {
        ManifestRoData data = SkipUtf8Bom(dataInput).TrimStart(" \t\r\n"u8);
        return !data.IsEmpty && data[0] == (uint8)'{';
    }

    private static ManifestRoData Utf8Bom => [0xEF, 0xBB, 0xBF];

    private static ManifestRoData SkipUtf8Bom(ManifestRoData data) =>
        data.StartsWith(Utf8Bom) ? data[Utf8Bom.Length..] : data;

    /// <summary>
    /// Deserializes a binary or JSON manifest with default options
    /// </summary>
    /// <inheritdoc cref="Deserialize(ManifestRoData,ManifestParseOptions?)"/>
    public static FBuildPatchAppManifest Deserialize(ManifestRoData dataInput)
        => Deserialize(dataInput, (ManifestParseOptions?)null);

    /// <summary>
    /// Deserializes a binary or JSON manifest
    /// </summary>
    /// <param name="dataInput">The span to parse from</param>
    /// <param name="optionsBuilder">Builder for options/configuration to parse, <see langword="null"/> for the default options</param>
    /// <inheritdoc cref="Deserialize(ManifestRoData,ManifestParseOptions?)"/>
    public static FBuildPatchAppManifest Deserialize(ManifestRoData dataInput, Action<ManifestParseOptions>? optionsBuilder)
        => Deserialize(dataInput, BuildOptions(optionsBuilder));

    /// <summary>
    /// Deserializes a binary or JSON manifest, see <see cref="IsJson"/>
    /// </summary>
    /// <param name="dataInput">The span to parse from</param>
    /// <param name="options">Options/Configuration to parse, <see langword="null"/> for the default options</param>
    /// <exception cref="NotSupportedException">A binary manifest is older than <see cref="EFeatureLevel.StoredAsBinaryData"/></exception>
    /// <exception cref="FileLoadException">A binary manifest has an invalid magic or its data fails to decompress</exception>
    /// <exception cref="InvalidDataException">
    /// The manifest is truncated or corrupt: a size, count or reference in a binary manifest is invalid (the message names
    /// the header or section), the hash of the binary manifest data does not match, or a JSON manifest misses
    /// <c>FileManifestList</c> or has a value of the wrong type or format
    /// </exception>
    /// <exception cref="JsonException">A JSON manifest is not valid JSON</exception>
    public static FBuildPatchAppManifest Deserialize(ManifestRoData dataInput, ManifestParseOptions? options)
    {
        return IsJson(dataInput)
            ? DeserializeJson(dataInput, options)
            : DeserializeBinary(dataInput, options);
    }

    /// <summary>
    /// Deserializes a binary or JSON manifest
    /// </summary>
    /// <param name="path">The file path to parse from</param>
    /// <param name="options">Options/Configuration to parse, <see langword="null"/> for the default options</param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    /// <exception cref="InvalidDataException">
    /// The file is larger than the largest supported manifest (<see cref="Array.MaxLength"/> bytes),
    /// or the manifest is truncated or corrupt
    /// </exception>
    /// <exception cref="EndOfStreamException">The file got shorter while it was read</exception>
    /// <inheritdoc cref="Deserialize(ManifestRoData,ManifestParseOptions?)"/>
    public static async ValueTask<FBuildPatchAppManifest> DeserializeFileAsync(
        string path, ManifestParseOptions? options = null, CancellationToken cancellationToken = default)
    {
        using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous);
        int64 length = RandomAccess.GetLength(handle);
        if (length > Array.MaxLength)
            throw new InvalidDataException($"The manifest file has {length} bytes, more than the largest supported manifest of {Array.MaxLength} bytes");

        using var dataInputOwner = MemoryOwner<uint8>.Allocate((int32)length);
        Memory<uint8> dataInputMemory = dataInputOwner.Memory;

        int32 total = 0;
        while (total < dataInputMemory.Length)
        {
            int32 read = await RandomAccess.ReadAsync(handle, dataInputMemory[total..], total, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException($"The manifest file ended after {total} of {dataInputMemory.Length} bytes");
            total += read;
        }

        return Deserialize(dataInputMemory.Span, options);
    }

    /// <summary>
    /// Deserializes a JSON manifest with default options
    /// </summary>
    /// <inheritdoc cref="DeserializeJson(ManifestRoData,ManifestParseOptions?)"/>
    public static FBuildPatchAppManifest DeserializeJson(ManifestRoData dataInput)
        => DeserializeJson(dataInput, (ManifestParseOptions?)null);

    /// <summary>
    /// Deserializes a JSON manifest
    /// </summary>
    /// <param name="dataInput">The span to parse from</param>
    /// <param name="optionsBuilder">Builder for options/configuration to parse, <see langword="null"/> for the default options</param>
    /// <inheritdoc cref="DeserializeJson(ManifestRoData,ManifestParseOptions?)"/>
    public static FBuildPatchAppManifest DeserializeJson(ManifestRoData dataInput, Action<ManifestParseOptions>? optionsBuilder)
        => DeserializeJson(dataInput, BuildOptions(optionsBuilder));

    /// <summary>
    /// Deserializes a JSON manifest
    /// </summary>
    /// <remarks>
    /// The data may start with a UTF-8 BOM. As in UE, entries of the chunk lists whose GUID no file references are skipped,
    /// and a missing <c>DataGroupList</c> or <c>PrereqIds</c> is derived from the chunk GUIDs or the prerequisite installer.
    /// </remarks>
    /// <param name="dataInput">The span to parse from</param>
    /// <param name="options">Options/Configuration to parse, <see langword="null"/> for the default options</param>
    /// <exception cref="JsonException">The data is not valid JSON, or an object has the same property twice</exception>
    /// <exception cref="InvalidDataException">
    /// The root is not an object, <c>FileManifestList</c> is missing, or a value has the wrong type or format
    /// (including a string with an escape that is not valid text, such as a lone surrogate)
    /// </exception>
    public static FBuildPatchAppManifest DeserializeJson(ManifestRoData dataInput, ManifestParseOptions? options)
    {
        try
        {
            return DeserializeJsonCore(dataInput, options);
        }
        catch (InvalidOperationException ex)
        {
            // the DOM reads strings lazily: an escape that is not valid UTF-16 only fails when the value or name is used
            throw new InvalidDataException($"Invalid JSON manifest: {ex.Message}", ex);
        }
    }

    // duplicate properties are rejected while parsing (JsonException); the DOM would throw an ArgumentException on first use
    private static readonly JsonDocumentOptions JsonManifestOptions = new() { AllowDuplicateProperties = false };

    private static FBuildPatchAppManifest DeserializeJsonCore(ManifestRoData dataInput, ManifestParseOptions? options)
    {
        if (JsonNode.Parse(SkipUtf8Bom(dataInput), null, JsonManifestOptions) is not JsonObject reader)
            throw new InvalidDataException("Invalid JSON manifest: the root value is not an object");

        // https://github.com/EpicGames/UnrealEngine/blob/8c31706601135aadf2f957fb76e2af46f04a8ef9/Engine/Source/Runtime/Online/BuildPatchServices/Private/BuildPatchManifest.cpp#L398
        // Feature level did not always exist, then it is the one just before UE started to write it
        EFeatureLevel featureLevel = reader["ManifestFileVersion"].TryGetBlob(out EFeatureLevel storedFeatureLevel)
            ? storedFeatureLevel
            : EFeatureLevel.CustomFields;
        if (featureLevel == EFeatureLevel.BrokenJsonVersion)
            featureLevel = EFeatureLevel.StoresChunkFileSizes;

        var meta = new FManifestMeta
        {
            FeatureLevel = featureLevel,
            ChunkSubdir = FManifestMeta.GetChunkSubdir(featureLevel),
            AppID = reader["AppID"].GetBlob<uint32>(),
            AppName = reader["AppNameString"].GetString(),
            BuildVersion = reader["BuildVersionString"].GetString(),
            LaunchExe = reader["LaunchExeString"].GetString(),
            LaunchCommand = reader["LaunchCommand"].GetString(),
            PrereqName = reader["PrereqName"].GetString(),
            PrereqPath = reader["PrereqPath"].GetString(),
            PrereqArgs = reader["PrereqArgs"].GetString(),
            UninstallActionPath = "",
            UninstallActionArgs = ""
        };

        if (reader["FileManifestList"] is not JsonArray jsonFileManifestList)
            throw new InvalidDataException("Invalid JSON manifest: the FileManifestList field is missing or not an array");

        var fileManifests = new FFileManifest[jsonFileManifestList.Count];
        Span<FFileManifest> fileManifestsSpan = fileManifests.AsSpan();

        var mutableChunkInfoLookup = new Dictionary<FGuid, FChunkInfo>();

        for (int i = 0; i < fileManifestsSpan.Length; i++)
        {
            if (jsonFileManifestList[i] is not JsonObject jsonFileManifest)
                throw new InvalidDataException($"Invalid JSON manifest: FileManifestList[{i}] is not an object");

            FFileManifest fileManifest = fileManifestsSpan[i] = new FFileManifest
            {
                FileName = jsonFileManifest["Filename"].GetString(),
                SHA1Hash = jsonFileManifest["FileHash"].GetBlob<FSHAHash>(),
                InstallTags = jsonFileManifest["InstallTags"].GetStringArray(),
                SymlinkTarget = jsonFileManifest["SymlinkTarget"].GetString()
            };

            // a missing list is empty, as in UE
            switch (jsonFileManifest["FileChunkParts"])
            {
                case JsonArray { Count: > 0 } jsonFileChunkParts:
                {
                    fileManifest.ChunkPartsArray = new FChunkPart[jsonFileChunkParts.Count];
                    Span<FChunkPart> chunkPartsSpan = fileManifest.ChunkPartsArray.AsSpan();
                    int64 chunkPartsFileOffset = 0L;
                    for (int j = 0; j < chunkPartsSpan.Length; j++)
                    {
                        if (jsonFileChunkParts[j] is not JsonObject jsonFileChunkPart)
                            throw new InvalidDataException($"Invalid JSON manifest: FileManifestList[{i}].FileChunkParts[{j}] is not an object");

                        FGuid chunkPartGuid = jsonFileChunkPart["Guid"].GetFGuid();
                        uint32 chunkPartOffset = jsonFileChunkPart["Offset"].GetBlob<uint32>();
                        uint32 chunkPartSize = jsonFileChunkPart["Size"].GetBlob<uint32>();
                        chunkPartsSpan[j] = new FChunkPart(chunkPartGuid, chunkPartOffset, chunkPartSize, chunkPartsFileOffset, mutableChunkInfoLookup);
                        chunkPartsFileOffset += chunkPartSize;
                    }
                    break;
                }
                case null or JsonArray:
                    fileManifest.ChunkPartsArray = [];
                    break;
                default:
                    throw new InvalidDataException($"Invalid JSON manifest: FileManifestList[{i}].FileChunkParts is not an array");
            }

            if (jsonFileManifest["bIsUnixExecutable"].GetBool())
                fileManifest.FileMetaFlags |= EFileMetaFlags.UnixExecutable;
            if (jsonFileManifest["bIsReadOnly"].GetBool())
                fileManifest.FileMetaFlags |= EFileMetaFlags.ReadOnly;
            if (jsonFileManifest["bIsCompressed"].GetBool())
                fileManifest.FileMetaFlags |= EFileMetaFlags.Compressed;
        }

        var chunkList = new FChunkInfo[mutableChunkInfoLookup.Count];
        Span<FChunkInfo> chunkListSpan = chunkList.AsSpan();
        int chunkIndex = 0;
        foreach (FChunkInfo chunk in mutableChunkInfoLookup.Values)
        {
            chunkListSpan[chunkIndex++] = chunk;
        }

        // The chunk lists are keyed by GUID. As in UE, an entry whose GUID no file references is skipped,
        // and a list only counts as present (for the fallbacks below) if one of its entries is used.
        bool hasChunkHashList = false;
        if (GetObject(reader, "ChunkHashList") is { } jsonChunkHashList)
        {
            foreach ((string guidString, JsonNode? jsonChunkHash) in jsonChunkHashList)
            {
                FGuid guid = ParseGuidKey(guidString, "ChunkHashList");
                uint64 chunkHash = jsonChunkHash.GetBlob<uint64>();
                if (mutableChunkInfoLookup.TryGetValue(guid, out FChunkInfo? chunk))
                {
                    chunk.Hash = chunkHash;
                    hasChunkHashList = true;
                }
            }
        }

        if (GetObject(reader, "ChunkShaList") is { } jsonChunkShaList)
        {
            foreach ((string guidString, JsonNode? jsonSha) in jsonChunkShaList)
            {
                FGuid guid = ParseGuidKey(guidString, "ChunkShaList");
                FSHAHash chunkSha = jsonSha.GetShaHex();
                if (mutableChunkInfoLookup.TryGetValue(guid, out FChunkInfo? chunk))
                    chunk.ShaHash = chunkSha;
            }
        }

        meta.PrereqIds = reader.TryGetPropertyValue("PrereqIds", out JsonNode? jsonPrereqIds)
            ? jsonPrereqIds.GetStringArray()
            : GetPrereqIdsFromPrereqFile(meta.PrereqPath, fileManifests);

        if (GetObject(reader, "DataGroupList") is { } jsonDataGroupList)
        {
            foreach ((string guidString, JsonNode? jsonDataGroup) in jsonDataGroupList)
            {
                FGuid guid = ParseGuidKey(guidString, "DataGroupList");
                uint8 dataGroup = jsonDataGroup.GetBlob<uint8>();
                if (mutableChunkInfoLookup.TryGetValue(guid, out FChunkInfo? chunk))
                    chunk.GroupNumber = dataGroup;
            }
        }
        else
        {
            // without the list the grouping is the deprecated CRC of the GUID, as in UE
            foreach (FChunkInfo chunk in chunkListSpan)
            {
                chunk.GroupNumber = FChunkInfo.GetDeprecatedGroupNumber(chunk.Guid);
            }
        }

        bool hasChunkFilesizeList = false;
        if (GetObject(reader, "ChunkFilesizeList") is { } jsonChunkFilesizeList)
        {
            foreach ((string guidString, JsonNode? jsonFileSize) in jsonChunkFilesizeList)
            {
                FGuid guid = ParseGuidKey(guidString, "ChunkFilesizeList");
                int64 fileSize = jsonFileSize.GetBlob<int64>();
                if (mutableChunkInfoLookup.TryGetValue(guid, out FChunkInfo? chunk))
                {
                    chunk.FileSize = fileSize;
                    hasChunkFilesizeList = true;
                }
            }
        }

        if (!hasChunkFilesizeList)
        {
            // Missing chunk list, version before we saved them compressed. Assume original fixed chunk size of 1 MiB.
            foreach (FChunkInfo chunk in chunkListSpan)
            {
                chunk.FileSize = 1048576;
            }
        }

        // The variable exists from StoresIfChunkOrFileData on, before that a manifest without chunk hashes is file data
        meta.bIsFileData = reader["bIsFileData"].TryGetBool(out bool isFileData)
            ? isFileData
            : !hasChunkHashList;

        if (!meta.bIsFileData)
        {
            // JSON manifests predate variable sized chunks, each chunk has the fixed window
            foreach (FChunkInfo chunk in chunkListSpan)
            {
                chunk.DataSizeUncompressed = FChunkInfo.ChunkDataSize;
            }
        }

        FCustomField[]? customFields = null;
        if (GetObject(reader, "CustomFields") is { } jsonCustomFields)
        {
            customFields = new FCustomField[jsonCustomFields.Count];
            int customFieldIndex = 0;

            foreach ((string name, JsonNode? jsonValue) in jsonCustomFields)
            {
                customFields[customFieldIndex++] = new FCustomField
                {
                    Name = name,
                    Value = jsonValue.GetString()
                };
            }
        }

        meta.BuildId = FManifestMeta.GetBackwardsCompatibleBuildId(meta);

        var manifest = new FBuildPatchAppManifest
        {
            Meta = meta,
            ChunkList = chunkList,
            Files = fileManifests,
            CustomFields = customFields ?? [],
            Chunks = mutableChunkInfoLookup,
            Options = options ?? new ManifestParseOptions()
        };

        // FileDataList.OnPostLoad();
        {
            Array.Sort(fileManifests);
            for (int i = 0; i < fileManifestsSpan.Length; i++)
            {
                FFileManifest file = fileManifestsSpan[i];
                file.Manifest = manifest;
                foreach (FChunkPart chunkPart in file.ChunkPartsArray.AsSpan())
                {
                    file.FileSize += chunkPart.Size;
                }
            }
        }

        // after OnPostLoad, which computes the file sizes
        manifest.PostSetup();

        return manifest;
    }

    /// <returns><see langword="null"/> if the field is missing or <c>null</c>.</returns>
    /// <exception cref="InvalidDataException">The field is not an object.</exception>
    private static JsonObject? GetObject(JsonObject reader, string name) => reader[name] switch
    {
        null => null,
        JsonObject jsonObject => jsonObject,
        _ => throw new InvalidDataException($"Invalid JSON manifest: the {name} field is not an object")
    };

    /// <exception cref="InvalidDataException">The key is not a GUID.</exception>
    private static FGuid ParseGuidKey(string guidString, string listName) => FGuid.TryParse(guidString, out FGuid guid)
        ? guid
        : throw new InvalidDataException($"Invalid JSON manifest: the {listName} key \"{guidString}\" is not a GUID of {FGuid.Size * 2} hex digits");

    // Port of the PrereqIds fallback in FBuildPatchAppManifest::DeserializeFromJSON, BuildPatchManifest.cpp lines 590-612:
    // https://github.com/EpicGames/UnrealEngine/blob/8c31706601135aadf2f957fb76e2af46f04a8ef9/Engine/Source/Runtime/Online/BuildPatchServices/Private/BuildPatchManifest.cpp#L590-L612
    /// <summary>
    /// UE falls back to the SHA1 hash (uppercase hex) of the prerequisite installer if a JSON manifest has no prerequisite ids.
    /// </summary>
    /// <param name="prereqPath">The prerequisite path, its backslashes are taken as slashes.</param>
    /// <param name="files">The files in manifest order. As in UE (a case-insensitive map, the last insert wins) the last file
    /// whose name matches ignoring case is used.</param>
    private static string[] GetPrereqIdsFromPrereqFile(string prereqPath, ReadOnlySpan<FFileManifest> files)
    {
        string prereqFileName = prereqPath.Replace('\\', '/');
        FFileManifest? prereqFile = null;
        foreach (FFileManifest file in files)
        {
            if (string.Equals(file.FileName, prereqFileName, StringComparison.OrdinalIgnoreCase))
                prereqFile = file;
        }

        return prereqFile is null ? [] : [prereqFile.SHA1Hash.ToString()];
    }

    /// <summary>
    /// Deserializes a binary manifest with default options
    /// </summary>
    /// <inheritdoc cref="DeserializeBinary(ManifestRoData,ManifestParseOptions?)"/>
    public static FBuildPatchAppManifest DeserializeBinary(ManifestRoData dataInput)
        => DeserializeBinary(dataInput, (ManifestParseOptions?)null);

    /// <summary>
    /// Deserializes a binary manifest
    /// </summary>
    /// <param name="dataInput">The span to parse from</param>
    /// <param name="optionsBuilder">Builder for options/configuration to parse, <see langword="null"/> for the default options</param>
    /// <inheritdoc cref="DeserializeBinary(ManifestRoData,ManifestParseOptions?)"/>
    public static FBuildPatchAppManifest DeserializeBinary(ManifestRoData dataInput, Action<ManifestParseOptions>? optionsBuilder)
        => DeserializeBinary(dataInput, BuildOptions(optionsBuilder));

    /// <summary>
    /// Deserializes a binary manifest
    /// </summary>
    /// <remarks>
    /// Sizes and element counts are checked before anything is allocated for them, so corrupt or crafted data fails with an
    /// <see cref="InvalidDataException"/> instead of large allocations or reads outside the data.
    /// </remarks>
    /// <param name="dataInput">The span to parse from</param>
    /// <param name="options">Options/Configuration to parse, <see langword="null"/> for the default options</param>
    /// <exception cref="NotSupportedException">Manifest is older than <see cref="EFeatureLevel.StoredAsBinaryData"/></exception>
    /// <exception cref="FileLoadException">The magic is invalid or the data fails to decompress</exception>
    /// <exception cref="InvalidDataException">
    /// The manifest is truncated or corrupt: the input is shorter than the header, a size or element count in the header
    /// or a section does not fit the data, a chunk GUID is listed twice, a chunk part references a chunk that is not in the
    /// chunk list, the storage flags are unknown, or the hash of the data does not match. The message names the header or
    /// the section, except for a negative length prefix, which the reader reports with its own message.
    /// </exception>
    public static FBuildPatchAppManifest DeserializeBinary(ManifestRoData dataInput, ManifestParseOptions? options)
    {
        var fileReader = new ManifestReader(dataInput);
        var header = new FManifestHeader(ref fileReader);

        if (header.Version < EFeatureLevel.StoredAsBinaryData)
            throw new NotSupportedException("Manifests below feature level StoredAsBinaryData are not supported");

        bool isCompressed = header.StoredAs.HasFlag(EManifestStorageFlags.Compressed);
        EManifestStorageFlags storedAsRemaining = header.StoredAs & ~(EManifestStorageFlags.Compressed | EManifestStorageFlags.Encrypted);
        if (storedAsRemaining != EManifestStorageFlags.None)
            throw new InvalidDataException($"Invalid manifest header: unknown storage flags {header.StoredAs}");

        header.CheckDataSizes(fileReader.Length - fileReader.Position, Array.MaxLength);

        options ??= new ManifestParseOptions();

        ManifestRoData manifestStoredData = fileReader.ReadSpan(header.DataSizeCompressed);

        // uncompressed data is parsed where it is, only compressed data needs a buffer
        using SpanOwner<uint8> buffer = isCompressed ? SpanOwner<uint8>.Allocate(header.DataSizeUncompressed) : SpanOwner<uint8>.Empty;
        ManifestRoData manifestRawData = manifestStoredData;

        if (isCompressed)
        {
            if (!options.DecompressorOrDefault.TryDecompress(
                    CompressionAlgorithm.Zlib,
                    manifestStoredData,
                    buffer.Span,
                    out int bytesWritten) ||
                bytesWritten != header.DataSizeUncompressed)
            {
                throw new FileLoadException("Failed to uncompress data");
            }

            manifestRawData = buffer.Span;
        }

        var hash = FSHAHash.Compute(manifestRawData);
        if (header.SHAHash != hash)
            throw new InvalidDataException($"Hash does not match. expected: {header.SHAHash}, actual: {hash}");

        var reader = new ManifestReader(manifestRawData);
        var chunks = new Dictionary<FGuid, FChunkInfo>();
        var manifest = new FBuildPatchAppManifest
        {
            Chunks = chunks,
            Options = options
        };

        if (header.EncryptionSecretId.HasValue)
        {
            manifest.EncryptionSecretId = header.EncryptionSecretId;
            manifest.EncryptionAuthTag = header.EncryptionAuthTag;
        }

        string section = ManifestSection.Meta;
        try
        {
            manifest.Meta = new FManifestMeta(ref reader);
            section = ManifestSection.ChunkDataList;
            manifest.ChunkList = FChunkInfo.ReadChunkDataList(ref reader, chunks);
            section = ManifestSection.FileManifestList;
            manifest.Files = FFileManifest.ReadFileDataList(ref reader, manifest);
            section = ManifestSection.CustomFields;
            manifest.CustomFields = FCustomField.ReadCustomFields(ref reader);

            if (header.Version >= EFeatureLevel.ManifestEncryptionSupport)
            {
                if (header.EncryptionSecretId.HasValue && header.EncryptionSecretId.Value.IsValid())
                {
                    section = ManifestSection.EncryptedData;
                    manifest.EncryptedData = FEncryptedData.ReadEncryptedData(ref reader);
                    // TODO: read "header"
                }
            }
        }
        catch (EndOfStreamException ex)
        {
            // the reader checks every read against the data: a string, array or value that does not end inside it
            throw new InvalidDataException($"Invalid manifest {section} section: a value lies outside the manifest data", ex);
        }

        manifest.PostSetup();

        return manifest;
    }

    private static ManifestParseOptions BuildOptions(Action<ManifestParseOptions>? optionsBuilder)
    {
        var options = new ManifestParseOptions();
        optionsBuilder?.Invoke(options);
        return options;
    }

    // TODO
    private void DecryptData()
    {

    }

    private void PostSetup()
    {
        foreach (FFileManifest file in Files)
        {
            TotalBuildSize += file.FileSize;
        }

        foreach (FChunkInfo chunk in ChunkList)
        {
            TotalDownloadSize += chunk.FileSize;
        }
    }
}
