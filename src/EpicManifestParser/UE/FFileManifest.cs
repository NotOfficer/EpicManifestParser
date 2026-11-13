namespace EpicManifestParser.UE;

// https://github.com/NotOfficer/UnrealEngine/blob/1436d646b9b11ffe9a46b04e9617dba689b56d35/Engine/Source/Runtime/Online/BuildPatchServices/Private/Data/ManifestData.h?plain=1#L155C22-L155C22
/// <summary>
/// UE FFileManifest struct
/// </summary>
public sealed class FFileManifest : IComparable<FFileManifest>, IComparable
{
    /// <summary>
    /// The build relative filename.
    /// Can be an obfuscated string for encrypted manifests.
    /// </summary>
    public string FileName { get; internal set; } = "";
    /// <summary>
    /// Whether this is a symlink to another file.
    /// Can be an obfuscated string for encrypted manifests.
    /// </summary>
    public string SymlinkTarget { get; internal set; } = "";
    /// <summary>
    /// The file SHA1.
    /// </summary>
    public FSHAHash SHA1Hash { get; internal set; }
    /// <summary>
    /// The file MD5.
    /// </summary>
    public FMD5Hash? MD5Hash { get; internal set; }
    /// <summary>
    /// The file SHA256.
    /// </summary>
    public FSHA256Hash? SHA256Hash { get; internal set; }
    /// <summary>
    /// The flags for this file.
    /// </summary>
    public EFileMetaFlags FileMetaFlags { get; internal set; }
    /// <summary>
    /// The install tags for this file.
    /// </summary>
    public IReadOnlyList<string> InstallTags { get; internal set; } = [];
    /// <summary>
    /// The list of chunk parts to stitch.
    /// </summary>
    public IReadOnlyList<FChunkPart> ChunkParts => ChunkPartsArray;
    internal FChunkPart[] ChunkPartsArray = [];
    /// <summary>
    /// The size of this file.
    /// </summary>
    public int64 FileSize { get; internal set; }
    /// <summary>
    /// The calculated MIME type for the file.
    /// </summary>
    public string MIMEType { get; internal set; } = "";

    internal FBuildPatchAppManifest Manifest { get; set; } = null!;

    internal FFileManifest() { }

    // https://github.com/NotOfficer/UnrealEngine/blob/1436d646b9b11ffe9a46b04e9617dba689b56d35/Engine/Source/Runtime/Online/BuildPatchServices/Private/Data/ManifestData.cpp?plain=1#L677C69-L677C69
    /// <exception cref="InvalidDataException">
    /// The section, an element count or a chunk part is invalid, or a chunk part references a chunk that is not in the chunk data list.
    /// </exception>
    internal static FFileManifest[] ReadFileDataList(ref ManifestReader reader, FBuildPatchAppManifest manifest)
    {
        int32 end = reader.BeginSection(ManifestSection.FileManifestList, ManifestSection.ListHeaderSize, reader.Length);
        EFileManifestListVersion dataVersion = reader.Read<EFileManifestListVersion>();
        int32 elementCount = reader.Read<int32>();
        reader.CheckCount(ManifestSection.FileManifestList, elementCount, GetMinSerializedSize(dataVersion), end);

        var files = new FFileManifest[elementCount];
        Span<FFileManifest> filesSpan = files.AsSpan();

        if (dataVersion >= EFileManifestListVersion.Original)
        {
            for (int i = 0; i < elementCount; i++)
            {
                var file = new FFileManifest();
                file.FileName = reader.ReadFString();
                filesSpan[i] = file;
            }
            for (int i = 0; i < elementCount; i++)
                filesSpan[i].SymlinkTarget = reader.ReadFString();
            for (int i = 0; i < elementCount; i++)
                filesSpan[i].SHA1Hash = reader.Read<FSHAHash>();
            for (int i = 0; i < elementCount; i++)
                filesSpan[i].FileMetaFlags = reader.Read<EFileMetaFlags>();
            for (int i = 0; i < elementCount; i++)
                filesSpan[i].InstallTags = reader.ReadFStringArray();
            for (int i = 0; i < elementCount; i++)
            {
                int32 length = reader.Read<int32>();
                if (length == 0)
                {
                    filesSpan[i].ChunkPartsArray = [];
                    continue;
                }

                reader.CheckCount(ManifestSection.FileManifestList, length, FChunkPart.SerializedSize, end);

                FChunkPart[] chunkPartsArray = filesSpan[i].ChunkPartsArray = new FChunkPart[length];
                Span<FChunkPart> chunkPartsSpan = chunkPartsArray.AsSpan();
                long fileOffset = 0L;

                for (int p = 0; p < length; p++)
                {
                    var chunkPart = new FChunkPart(ref reader, fileOffset, manifest.Chunks, end);
                    chunkPartsSpan[p] = chunkPart;
                    fileOffset += chunkPart.Size;
                }
            }

            if (dataVersion >= EFileManifestListVersion.HasMD5AndMIMEType)
            {
                for (int i = 0; i < elementCount; i++)
                {
                    bool bIsValid = reader.Read<int32>() == 1; // FMD5Hash
                    if (bIsValid)
                    {
                        filesSpan[i].MD5Hash = reader.Read<FMD5Hash>();
                    }
                }
                for (int i = 0; i < elementCount; i++)
                    filesSpan[i].MIMEType = reader.ReadFString();
            }

            if (dataVersion >= EFileManifestListVersion.HasSHA256)
            {
                for (int i = 0; i < elementCount; i++)
                    filesSpan[i].SHA256Hash = reader.Read<FSHA256Hash>();
            }

            // FileDataList.OnPostLoad();
            {
                Array.Sort(files);
                for (int i = 0; i < elementCount; i++)
                {
                    FFileManifest file = filesSpan[i];
                    file.Manifest = manifest;
                    foreach (FChunkPart chunkPart in file.ChunkPartsArray.AsSpan())
                    {
                        file.FileSize += chunkPart.Size;
                    }
                }
            }
        }
        else
        {
            var defaultFile = new FFileManifest();
            filesSpan.Fill(defaultFile);
        }

        reader.EndSection(ManifestSection.FileManifestList, end);
        return files;
    }

    /// <summary>
    /// The smallest number of bytes one file takes in a file manifest list of <paramref name="version"/>: empty strings and arrays.
    /// </summary>
    private static int32 GetMinSerializedSize(EFileManifestListVersion version)
    {
        // FileName, SymlinkTarget, SHA1Hash, FileMetaFlags, InstallTags, ChunkParts
        int32 size = sizeof(int32) + sizeof(int32) + FSHAHash.Size + sizeof(uint8) + sizeof(int32) + sizeof(int32);
        if (version >= EFileManifestListVersion.HasMD5AndMIMEType)
            size += sizeof(int32) + sizeof(int32); // MD5 without hash, MIMEType
        if (version >= EFileManifestListVersion.HasSHA256)
            size += FSHA256Hash.Size;
        return size;
    }

    /// <summary>
    /// Creates a read-only stream to read filedata from.
    /// </summary>
    public FFileManifestStream GetStream() => new(this);

    /// <summary>
    /// Attempts to find the chunk part that contains the specified file offset.
    /// </summary>
    /// <param name="fileOffset">The offset within the file to locate.</param>
    /// <param name="index">
    /// When this method returns, contains the index of the chunk part that contains the specified offset, if found; otherwise, -1.
    /// </param>
    /// <param name="chunkPartOffset">
    /// When this method returns, contains the offset within the found chunk part, if found; otherwise, 0.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if thr chunk part was found; otherwise, <see langword="false"/>.
    /// </returns>
    public bool TryFindChunkPart(long fileOffset, out int index, out uint chunkPartOffset)
    {
        var chunkParts = new ReadOnlySpan<FChunkPart>(ChunkPartsArray);

        int left = 0;
        int right = chunkParts.Length - 1;

        while (left <= right)
        {
            int mid = left + ((right - left) >> 1); // `>> 1` is equal to `/ 2` but "faster"
            ref readonly FChunkPart part = ref chunkParts[mid];

            if (fileOffset < part.FileOffset)
            {
                right = mid - 1;
            }
            else if (fileOffset >= part.FileOffset + part.Size)
            {
                left = mid + 1;
            }
            else
            {
                chunkPartOffset = (uint)(fileOffset - part.FileOffset);
                index = mid;
                return true;
            }
        }

        index = -1;
        chunkPartOffset = 0;
        return false;
    }

    /// <inheritdoc />
    public int CompareTo(FFileManifest? other)
    {
        if (ReferenceEquals(this, other)) return 0;
        if (ReferenceEquals(null, other)) return 1;
        return string.Compare(FileName, other.FileName, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public int CompareTo(object? obj)
    {
        if (ReferenceEquals(null, obj)) return 1;
        if (ReferenceEquals(this, obj)) return 0;
        return obj is FFileManifest other ? CompareTo(other) : throw new ArgumentException($"Object must be of type {nameof(FFileManifest)}");
    }

    /// <summary/>
    public static bool operator <(FFileManifest? left, FFileManifest? right)
    {
        return Comparer<FFileManifest>.Default.Compare(left, right) < 0;
    }
    
    /// <summary/>
    public static bool operator >(FFileManifest? left, FFileManifest? right)
    {
        return Comparer<FFileManifest>.Default.Compare(left, right) > 0;
    }
    
    /// <summary/>
    public static bool operator <=(FFileManifest? left, FFileManifest? right)
    {
        return Comparer<FFileManifest>.Default.Compare(left, right) <= 0;
    }
    
    /// <summary/>
    public static bool operator >=(FFileManifest? left, FFileManifest? right)
    {
        return Comparer<FFileManifest>.Default.Compare(left, right) >= 0;
    }
}
