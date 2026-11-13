using System.Buffers;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Runtime.CompilerServices;

using Microsoft.Win32.SafeHandles;

using OffiUtils;

namespace EpicManifestParser.UE;

// https://github.com/NotOfficer/UnrealEngine/blob/1436d646b9b11ffe9a46b04e9617dba689b56d35/Engine/Source/Runtime/Online/BuildPatchServices/Private/Data/ChunkData.h?plain=1#L263C1-L266C19
/// <summary>
/// UE FChunkInfo struct
/// </summary>
public sealed class FChunkInfo
{
    /// <summary>
    /// The GUID for this data.
    /// </summary>
    public FGuid Guid { get; internal set; }
    /// <summary>
    /// The FRollingHash hashed value for this chunk data.
    /// </summary>
    public uint64 Hash { get; internal set; }
    /// <summary>
    /// The FSHA hashed value for this chunk data.
    /// </summary>
    public FSHAHash ShaHash { get; internal set; }
    /// <summary>
    /// The group number this chunk divides into.
    /// </summary>
    public uint8 GroupNumber { get; internal set; }
    /// <summary>
    /// The size of this data compressed.
    /// </summary>
    public uint32 DataSizeCompressed { get; internal set; }
    /// <summary>
    /// The size of this data uncompressed (also known as window size in older algorithms).
    /// </summary>
    public uint32 DataSizeUncompressed { get; internal set; }
    /// <summary>
    /// The file download size for this chunk.
    /// </summary>
    public int64 FileSize { get; internal set; }

    /// <summary>
    /// The ID of the encryption secret key used by this chunk.
    /// </summary>
    internal FGuid? EncryptionSecretId { get; set; }
    /// <summary>
    /// The 16 byte AuthTag space used for AES encryption, which is an additional integrity check value.
    /// </summary>
    internal FAESAuthTag? AESAuthTag { get; set; }

    /// <param name="manifest"></param>
    /// <returns>
    /// Url <see cref="string"/> to download this chunk. The path behind <see cref="ManifestParseOptions.ChunkBaseUrl"/> depends on
    /// the manifest's feature level, as in UE's <c>GetChunkNewFilename</c>:
    /// <c>Chunks/{group}/{Guid}.chunk</c> before <see cref="EFeatureLevel.DataFileRenames"/>,
    /// <c>{ChunkSubdir}/{GroupNumber:D2}/{Hash:X16}_{Guid}.chunk</c> before <see cref="EFeatureLevel.ChunksStoredBySecret"/>, and
    /// <c>{ChunkSubdir}/{secret}/{GroupNumber:D2}/{hash}_{guid}.chunk</c> from it on, where the hash, the GUID and the secret are
    /// unpadded base64url and the secret is <c>plain</c> for an unencrypted chunk.
    /// </returns>
    public string GetUrl(FBuildPatchAppManifest manifest)
    {
        string? baseUrl = manifest.Options.ChunkBaseUrl;
        // documented with a trailing slash, one without works too
        string separator = baseUrl is null || baseUrl.EndsWith('/') ? "" : "/";
        EFeatureLevel featureLevel = manifest.Meta.FeatureLevel;
        FGuid guid = Guid;

        // BuildPatchUtil.cpp, FBuildPatchUtils::GetChunkOldFilename: the group comes from the deprecated CRC of the GUID
        if (featureLevel < EFeatureLevel.DataFileRenames)
            return $"{baseUrl}{separator}Chunks/{GetDeprecatedGroupNumber(guid):D2}/{guid}.chunk";

        if (featureLevel < EFeatureLevel.ChunksStoredBySecret)
            return $"{baseUrl}{separator}{manifest.Meta.ChunkSubdir}/{GroupNumber:D2}/{Hash:X16}_{guid}.chunk";

        Span<uint8> hash = stackalloc uint8[sizeof(uint64)];
        BinaryPrimitives.WriteUInt64LittleEndian(hash, Hash);
        string secret = EncryptionSecretId is { } secretId && secretId.IsValid() ? Base64Url.EncodeToString(secretId.AsSpan()) : "plain";
        return $"{baseUrl}{separator}{manifest.Meta.ChunkSubdir}/{secret}/{GroupNumber:D2}/{Base64Url.EncodeToString(hash)}_{Base64Url.EncodeToString(guid.AsSpan())}.chunk";
    }

    /// <param name="manifest"></param>
    /// <returns>
    /// <see cref="Uri"/> to download this chunk
    /// </returns>
    public Uri GetUri(FBuildPatchAppManifest manifest) => new(GetUrl(manifest), UriKind.Absolute);

    internal string? CachePath { get; set; }

    /// <summary>
    /// The fixed size of the decompressed data of a chunk before variable sized chunks (UE <c>BuildPatchServices::ChunkDataSize</c>), 1 MiB.
    /// </summary>
    internal const uint32 ChunkDataSize = 1024 * 1024;

    /// <summary>
    /// The largest decompressed size accepted for a chunk whose data is not exactly the size the manifest records: its size is
    /// unknown (<see cref="DataSizeUncompressed"/> is 0), or the chunk file carries more data than the manifest's window. 16 MiB.
    /// </summary>
    /// <remarks>
    /// UE splits builds into chunks of <see cref="ChunkDataSize"/> (1 MiB, the default chunk window size of BuildPatchTool), and the
    /// manifest records the size of every chunk it lists: binary manifests store it, chunk-based JSON manifests imply 1 MiB.
    /// UE itself accepts a chunk file whose data is larger than that window and uses the first window bytes
    /// (<c>ValidateAndRead</c> in ChunkData.cpp: "chunkinfos that report window sizes that are not the full chunk size"), so a
    /// larger file is not an error here either. 16 times the standard size leaves room for such files and for builds with larger
    /// windows, while a forged header can make a download rent at most this much (or the manifest's own size) instead of up to 2 GiB.
    /// </remarks>
    internal const int32 MaxUnknownDataSize = 16 * (int32)ChunkDataSize;

    /// <summary>
    /// The largest decompressed size accepted for this chunk: <see cref="DataSizeUncompressed"/> or <see cref="MaxUnknownDataSize"/>,
    /// whichever is larger.
    /// </summary>
    internal int64 MaxDataSize => Math.Max(DataSizeUncompressed, MaxUnknownDataSize);

    /// <summary>
    /// The largest response accepted for this chunk. A chunk file is its header followed by the raw data or the zlib compressed data,
    /// which UE only stores when it is smaller than the raw data; zlib's own worst case adds less than 0.1 %. An eighth of
    /// <see cref="MaxDataSize"/> plus 64 KiB covers both with a wide margin.
    /// </summary>
    internal int32 MaxDownloadSize
    {
        get
        {
            int64 dataSize = MaxDataSize;
            // one byte below the array limit, the download buffer has a spare byte to notice longer bodies
            return (int32)Math.Min(dataSize + dataSize / 8 + 64 * 1024, Array.MaxLength - 1);
        }
    }

    // https://github.com/NotOfficer/UnrealEngine/blob/1436d646b9b11ffe9a46b04e9617dba689b56d35/Engine/Source/Runtime/Online/BuildPatchServices/Private/Data/ManifestData.cpp?plain=1#L579C67-L579C67
    /// <exception cref="InvalidDataException">The section or its element count is invalid, or a chunk GUID is listed twice.</exception>
    internal static FChunkInfo[] ReadChunkDataList(ref ManifestReader reader, Dictionary<FGuid, FChunkInfo> chunksDict)
    {
        int32 end = reader.BeginSection(ManifestSection.ChunkDataList, ManifestSection.ListHeaderSize, reader.Length);
        EChunkDataListVersion dataVersion = reader.Read<EChunkDataListVersion>();
        int32 elementCount = reader.Read<int32>();
        // every element has the same size, so this check covers all reads below
        reader.CheckCount(ManifestSection.ChunkDataList, elementCount, GetSerializedSize(dataVersion), end);

        var chunks = new FChunkInfo[elementCount];
        Span<FChunkInfo> chunksSpan = chunks.AsSpan();

        chunksDict.EnsureCapacity(elementCount);

        if (dataVersion >= EChunkDataListVersion.Original)
        {
            for (int i = 0; i < elementCount; i++)
            {
                var chunk = new FChunkInfo();
                chunk.Guid = reader.Read<FGuid>();
                chunksSpan[i] = chunk;
                if (!chunksDict.TryAdd(chunk.Guid, chunk))
                    ManifestSection.ThrowDuplicateChunk(chunk.Guid);
            }
            for (int i = 0; i < elementCount; i++)
                chunksSpan[i].Hash = reader.Read<uint64>();
            for (int i = 0; i < elementCount; i++)
                chunksSpan[i].ShaHash = reader.Read<FSHAHash>();
            for (int i = 0; i < elementCount; i++)
                chunksSpan[i].GroupNumber = reader.Read<uint8>();
            for (int i = 0; i < elementCount; i++)
                chunksSpan[i].DataSizeUncompressed = reader.Read<uint32>();
            for (int i = 0; i < elementCount; i++)
                chunksSpan[i].FileSize = reader.Read<int64>();

            if (dataVersion >= EChunkDataListVersion.SerialisesEncryptionSecretId)
            {
                for (int i = 0; i < elementCount; i++)
                    chunksSpan[i].EncryptionSecretId = reader.Read<FGuid>();
            }

            if (dataVersion >= EChunkDataListVersion.SerialisesCompressesDataSize)
            {
                for (int i = 0; i < elementCount; i++)
                    chunksSpan[i].DataSizeCompressed = reader.Read<uint32>();
            }

            if (dataVersion >= EChunkDataListVersion.SerialisesAESAuthTag)
            {
                for (int i = 0; i < elementCount; i++)
                    chunksSpan[i].AESAuthTag = reader.Read<FAESAuthTag>();
            }
        }
        else
        {
            var defaultChunk = new FChunkInfo
            {
                DataSizeUncompressed = ChunkDataSize
            };
            chunksSpan.Fill(defaultChunk);
        }

        reader.EndSection(ManifestSection.ChunkDataList, end);
        return chunks;
    }

    /// <summary>
    /// The number of bytes one chunk takes in a chunk data list of <paramref name="version"/>.
    /// </summary>
    private static int32 GetSerializedSize(EChunkDataListVersion version)
    {
        // Guid, Hash, ShaHash, GroupNumber, DataSizeUncompressed (window size), FileSize
        int32 size = FGuid.Size + sizeof(uint64) + FSHAHash.Size + sizeof(uint8) + sizeof(uint32) + sizeof(int64);
        if (version >= EChunkDataListVersion.SerialisesEncryptionSecretId)
            size += FGuid.Size;
        if (version >= EChunkDataListVersion.SerialisesCompressesDataSize)
            size += sizeof(uint32);
        if (version >= EChunkDataListVersion.SerialisesAESAuthTag)
            size += FAESAuthTag.AES256_GCM_AuthTagSizeInBytes;
        return size;
    }

    // Port of the DataGroupList fallback in FBuildPatchAppManifest::DeserializeFromJSON, BuildPatchManifest.cpp lines 633-641:
    // https://github.com/EpicGames/UnrealEngine/blob/8c31706601135aadf2f957fb76e2af46f04a8ef9/Engine/Source/Runtime/Online/BuildPatchServices/Private/BuildPatchManifest.cpp#L633-L641
    /// <summary>
    /// The group number UE derives for a chunk of a JSON manifest without <c>DataGroupList</c>:
    /// <see cref="MemCrcDeprecated"/> of the 16 GUID bytes, modulo 100.
    /// </summary>
    internal static uint8 GetDeprecatedGroupNumber(FGuid guid) => (uint8)(MemCrcDeprecated(guid.AsSpan()) % 100);

    // Port of FCrc::MemCrc_DEPRECATED, Crc.cpp lines 592-639:
    // https://github.com/EpicGames/UnrealEngine/blob/8c31706601135aadf2f957fb76e2af46f04a8ef9/Engine/Source/Runtime/Core/Private/Misc/Crc.cpp#L592-L639
    /// <summary>
    /// UE's deprecated memory CRC. Its slicing-by-8 tables are the byte swapped tables of a CRC-32 that is not bit reflected,
    /// so it equals CRC-32/BZIP2: polynomial <c>0x04C11DB7</c>, initial value and final XOR <c>0xFFFFFFFF</c>.
    /// Computed bitwise, it is only needed for old JSON manifests.
    /// </summary>
    internal static uint32 MemCrcDeprecated(ReadOnlySpan<byte> data)
    {
        const uint32 polynomial = 0x04C11DB7;
        uint32 crc = uint32.MaxValue;
        foreach (byte value in data)
        {
            crc ^= (uint32)value << 24;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ polynomial : crc << 1;
        }
        return ~crc;
    }

    /// <summary>
    /// Copies <c>destination.Length</c> bytes starting at <paramref name="chunkOffset"/> of the decompressed chunk data into
    /// <paramref name="destination"/>, from the memory cache, the chunk cache directory or a download.
    /// Either the whole destination is filled or an exception is thrown.
    /// </summary>
    internal ValueTask ReadDataAsync(Memory<byte> destination, int32 chunkOffset, FBuildPatchAppManifest manifest, CancellationToken cancellationToken = default)
    {
        if (destination.IsEmpty)
            return ValueTask.CompletedTask;

        string? cachePath = CachePath;
        if (cachePath is not null)
            return ReadCacheFileAsync(cachePath, destination, chunkOffset, manifest, cancellationToken);

        if (manifest.Options.ChunkCacheDirectory is null && manifest.ChunkMemoryCache is { } memoryCache
            && memoryCache.TryCopy(Guid, chunkOffset, destination.Span))
        {
            return ValueTask.CompletedTask;
        }

        return ReadDataLockedAsync(destination, chunkOffset, manifest, cancellationToken);
    }

    /// <summary>
    /// Hot path for a known cache file, kept to a single async level: reads the file and only if it is gone or shorter than the
    /// requested range falls back to <see cref="ReadDataLockedAsync"/>, which heals it.
    /// </summary>
    private async ValueTask ReadCacheFileAsync(string cachePath, Memory<byte> destination, int32 chunkOffset,
        FBuildPatchAppManifest manifest, CancellationToken cancellationToken)
    {
        SafeFileHandle? handle = TryOpenCacheFile(cachePath);
        if (handle is not null)
        {
            using (handle)
            {
                int32 total = 0;
                while (total < destination.Length)
                {
                    int32 read = await RandomAccess.ReadAsync(handle, destination[total..], chunkOffset + total, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                        break;
                    total += read;
                }

                if (total == destination.Length)
                    return;
            }
        }

        await ReadDataLockedAsync(destination, chunkOffset, manifest, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Synchronous counterpart of <see cref="ReadDataAsync"/>: local data (cache file, memory cache) is read without blocking on
    /// the async path, only a chunk that has to be downloaded goes through it.
    /// </summary>
    internal void ReadData(Span<byte> destination, int32 chunkOffset, FBuildPatchAppManifest manifest)
    {
        if (destination.IsEmpty || TryReadLocal(destination, chunkOffset, manifest))
            return;

        ReadDataBlocking(destination, chunkOffset, manifest);
    }

    /// <summary>
    /// Copies the range from local data only: the known cache file, a cache file of an earlier run, or the memory cache.
    /// Never blocks on a download or the chunk lock.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> if the chunk has to be downloaded first, or its cache file is gone, has the wrong length or is too
    /// short for the range; <see cref="ReadDataAsync"/> and <see cref="ReadDataBlocking"/> look again under the chunk lock and heal it.
    /// </returns>
    internal bool TryReadLocal(Span<byte> destination, int32 chunkOffset, FBuildPatchAppManifest manifest)
    {
        string? cachePath = CachePath;
        if (cachePath is not null)
            return TryReadCacheFile(cachePath, destination, chunkOffset);

        if (manifest.Options.ChunkCacheDirectory is { } cacheDirectory)
        {
            // cached by an earlier run: a file with the final name is complete by construction, its length is checked once
            cachePath = GetCachePath(cacheDirectory);
            if (!File.Exists(cachePath) || !TryAdoptCacheFile(cachePath, destination, chunkOffset))
                return false;

            CachePath = cachePath;
            return true;
        }

        return manifest.ChunkMemoryCache is { } memoryCache && memoryCache.TryCopy(Guid, chunkOffset, destination);
    }

    /// <summary>
    /// Downloads (or heals) the chunk under the chunk lock on the async path and blocks until the range is copied.
    /// Only for data that <see cref="TryReadLocal"/> did not find.
    /// </summary>
    internal void ReadDataBlocking(Span<byte> destination, int32 chunkOffset, FBuildPatchAppManifest manifest)
    {
        // a span cannot cross an await
        byte[] buffer = ArrayPool<byte>.Shared.Rent(destination.Length);
        try
        {
            Memory<byte> memory = buffer.AsMemory(0, destination.Length);
            ReadDataLockedAsync(memory, chunkOffset, manifest, CancellationToken.None).AsTask().GetAwaiter().GetResult();
            memory.Span.CopyTo(destination);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Slow path under the per-chunk lock: looks again (another reader may have done the work while we waited),
    /// heals a corrupt cache file, otherwise downloads the chunk and persists it to the cache directory or the memory cache.
    /// </summary>
    private async ValueTask ReadDataLockedAsync(Memory<byte> destination, int32 chunkOffset, FBuildPatchAppManifest manifest, CancellationToken cancellationToken)
    {
        using IDisposable _ = await manifest.ChunksLocker.LockAsync(Guid, cancellationToken).ConfigureAwait(false);

        ManifestParseOptions options = manifest.Options;
        string? cacheDirectory = options.ChunkCacheDirectory;
        ChunkMemoryCache? memoryCache = null;
        string? cachePath = CachePath;

        if (cachePath is not null)
        {
            if (await TryReadCacheFileAsync(cachePath, destination, chunkOffset, 0, cancellationToken).ConfigureAwait(false))
                return;
            DiscardCacheFile(cachePath);
        }
        else if (cacheDirectory is not null)
        {
            cachePath = GetCachePath(cacheDirectory);
            if (File.Exists(cachePath))
            {
                // cached by an earlier run: a file of the wrong length is corrupt
                if (await TryReadCacheFileAsync(cachePath, destination, chunkOffset, DataSizeUncompressed, cancellationToken).ConfigureAwait(false))
                {
                    CachePath = cachePath;
                    return;
                }
                DiscardCacheFile(cachePath);
            }
        }
        else
        {
            memoryCache = manifest.ChunkMemoryCache;
            if (memoryCache is not null && memoryCache.TryCopy(Guid, chunkOffset, destination.Span))
                return;
        }

        // verified before it is returned: nothing below ever sees data that failed a check
        RentedBuffer data = await HttpDownloader.DownloadAsync(options, GetUri(manifest), options.ChunkDownloadBufferSize, MaxDownloadSize,
            (Chunk: this, Options: options), DecodeChunk, cancellationToken).ConfigureAwait(false);

        try
        {
            CopyChunkRange(data.Span, chunkOffset, destination.Span);

            if (cachePath is not null)
            {
                // temp file + rename, deliberately not cancellable: once a chunk is downloaded it gets persisted;
                // a failed write is not an error of the read, the chunk is downloaded again next time.
                // Only a file that holds this data is trusted from here on: a file that could not be replaced
                // (it was rejected above, or another writer got there first) is checked again by the next read
                if (await AtomicFile.TryWriteAsync(cachePath, data.Memory).ConfigureAwait(false))
                    CachePath = cachePath;
            }
            else if (memoryCache is not null)
            {
                // takes ownership; a raw chunk stays in its download buffer (copying it out measured slower than keeping it)
                memoryCache.Add(Guid, data);
                data = default;
            }
        }
        finally
        {
            if (data.Array is not null)
                data.Return();
        }
    }

    /// <returns><see langword="null"/> if the file does not exist (anymore).</returns>
    private static SafeFileHandle? TryOpenCacheFile(string path)
    {
        try
        {
            // FileShare.Delete: a healing reader can delete or replace the file while others still read it
            return File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <returns>
    /// <see langword="false"/> if the file is gone or shorter than the requested range, which means it is corrupt.
    /// </returns>
    private static bool TryReadCacheFile(string path, Span<byte> destination, int64 fileOffset)
    {
        SafeFileHandle? handle = TryOpenCacheFile(path);
        if (handle is null)
            return false;

        using (handle)
            return TryReadFully(handle, destination, fileOffset);
    }

    /// <summary>
    /// <see cref="TryReadCacheFile"/> for a file of an earlier run, which is used only if it has the length of the chunk data.
    /// </summary>
    private bool TryAdoptCacheFile(string path, Span<byte> destination, int64 fileOffset)
    {
        SafeFileHandle? handle = TryOpenCacheFile(path);
        if (handle is null)
            return false;

        using (handle)
            return HasDataLength(handle, DataSizeUncompressed) && TryReadFully(handle, destination, fileOffset);
    }

    private static bool TryReadFully(SafeFileHandle handle, Span<byte> destination, int64 fileOffset)
    {
        int32 total = 0;
        while (total < destination.Length)
        {
            int32 read = RandomAccess.Read(handle, destination[total..], fileOffset + total);
            if (read == 0)
                return false;
            total += read;
        }
        return true;
    }

    /// <returns>
    /// <see langword="true"/> if <paramref name="dataSize"/> is 0 (unknown) or the length of the file.
    /// </returns>
    private static bool HasDataLength(SafeFileHandle handle, uint32 dataSize) => dataSize == 0 || RandomAccess.GetLength(handle) == dataSize;

    /// <inheritdoc cref="TryReadCacheFile"/>
    /// <param name="path"></param>
    /// <param name="destination"></param>
    /// <param name="fileOffset"></param>
    /// <param name="expectedLength">The chunk data size the file must have, 0 to skip the check (a file this run has used already).</param>
    /// <param name="cancellationToken"></param>
    private static async ValueTask<bool> TryReadCacheFileAsync(string path, Memory<byte> destination, int64 fileOffset, uint32 expectedLength,
        CancellationToken cancellationToken)
    {
        SafeFileHandle? handle = TryOpenCacheFile(path);
        if (handle is null)
            return false;

        using (handle)
        {
            if (!HasDataLength(handle, expectedLength))
                return false;

            int32 total = 0;
            while (total < destination.Length)
            {
                int32 read = await RandomAccess.ReadAsync(handle, destination[total..], fileOffset + total, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    return false;
                total += read;
            }
            return true;
        }
    }

    /// <summary>
    /// Forgets a corrupt cache file (call under the chunk lock). It gets replaced after the download.
    /// </summary>
    private void DiscardCacheFile(string path)
    {
        CachePath = null;
        AtomicFile.TryDelete(path);
    }

    /// <summary>
    /// Copies a range of decompressed chunk data.
    /// </summary>
    /// <exception cref="InvalidDataException">The range lies outside the chunk data.</exception>
    internal static void CopyChunkRange(ReadOnlySpan<byte> chunkData, int32 chunkOffset, Span<byte> destination)
    {
        if ((uint32)chunkOffset > (uint32)chunkData.Length || destination.Length > chunkData.Length - chunkOffset)
            throw new InvalidDataException($"Chunk part at offset {chunkOffset} with size {destination.Length} exceeds the {chunkData.Length} bytes of chunk data");

        chunkData.Slice(chunkOffset, destination.Length).CopyTo(destination);
    }

    /// <summary>
    /// Validates a downloaded chunk file against the requested chunk and returns its decompressed data.
    /// Takes ownership of <paramref name="body"/> when it returns; data that fails a check never leaves this method.
    /// </summary>
    /// <remarks>
    /// Checked are the header structure, the header GUID against <see cref="Guid"/>, the decompressed size (at least
    /// <see cref="DataSizeUncompressed"/>, at most <see cref="MaxDataSize"/>), and with
    /// <see cref="ManifestParseOptions.VerifyChunkHashes"/> the SHA1 of the data against <see cref="ShaHash"/>, or against the
    /// header's SHA1 or rolling hash when the manifest has none or the chunk file carries more data than the manifest's window. The returned
    /// data is cut to <see cref="DataSizeUncompressed"/> bytes when that is known.
    /// </remarks>
    /// <exception cref="InvalidDataException">
    /// The header is invalid, does not match the body or the requested chunk, or the SHA1 does not match (retried).
    /// </exception>
    /// <exception cref="FileLoadException">Bad magic or failed decompression (retried).</exception>
    /// <exception cref="NotSupportedException">Encrypted or unknown storage (not retried).</exception>
    internal static RentedBuffer DecodeChunk((FChunkInfo Chunk, ManifestParseOptions Options) state, RentedBuffer body)
    {
        (FChunkInfo chunk, ManifestParseOptions options) = state;
        Span<byte> data = body.Span;
        FChunkHeader header = FChunkHeader.Parse(data);

        if (header.Guid != chunk.Guid)
            throw new InvalidDataException($"The response is chunk {header.Guid}, chunk {chunk.Guid} was requested");
        if (header.DataSizeCompressed < 0 || header.HeaderSize + (int64)header.DataSizeCompressed != data.Length)
            throw new InvalidDataException($"Chunk header announces {header.HeaderSize} + {header.DataSizeCompressed} bytes, the response has {data.Length}");
        if (header.StoredAs.HasFlag(EChunkStorageFlags.Encrypted))
            throw new NotSupportedException("Encrypted chunks are not supported");

        RentedBuffer decoded;
        if (header.StoredAs == EChunkStorageFlags.None)
        {
            chunk.CheckDataSize(header.DataSizeCompressed);
            decoded = new RentedBuffer(body.Array, body.Offset + header.HeaderSize, header.DataSizeCompressed);
        }
        else
        {
            if (header.StoredAs != EChunkStorageFlags.Compressed)
                throw new NotSupportedException($"Unknown chunk storage flags: {header.StoredAs}");

            // before the buffer is rented: the header alone must not decide how much memory a download takes
            chunk.CheckDataSize(header.DataSizeUncompressed);
            decoded = Decompress(options.DecompressorOrDefault, data.Slice(header.HeaderSize, header.DataSizeCompressed), header.DataSizeUncompressed);
        }

        try
        {
            if (options.VerifyChunkHashes)
                chunk.VerifyHash(decoded.Span, header);
        }
        catch
        {
            // the body itself is returned by the caller
            if (decoded.Array != body.Array)
                decoded.Return();
            throw;
        }

        if (decoded.Array != body.Array)
            body.Return();

        // a chunk file with more data than the manifest's window: the chunk is its first window bytes, as in UE,
        // so that what gets cached always has the size the manifest records
        if (chunk.DataSizeUncompressed != 0 && (uint32)decoded.Length > chunk.DataSizeUncompressed)
            decoded = new RentedBuffer(decoded.Array, decoded.Offset, (int32)chunk.DataSizeUncompressed);

        return decoded;
    }

    /// <exception cref="FileLoadException">The data fails to decompress or does not have <paramref name="size"/> bytes.</exception>
    private static RentedBuffer Decompress(IDecompressor decompressor, ReadOnlySpan<byte> compressed, int32 size)
    {
        byte[] uncompressed = ArrayPool<byte>.Shared.Rent(size);
        bool success = false;

        try
        {
            if (!decompressor.TryDecompress(CompressionAlgorithm.Zlib, compressed, uncompressed.AsSpan(0, size), out int32 bytesWritten)
                || bytesWritten != size)
            {
                throw new FileLoadException("Failed to uncompress data");
            }

            success = true;
            return new RentedBuffer(uncompressed, 0, size);
        }
        finally
        {
            if (!success)
                ArrayPool<byte>.Shared.Return(uncompressed);
        }
    }

    /// <summary>
    /// The decompressed data of a downloaded chunk must have at least <see cref="DataSizeUncompressed"/> bytes (the chunk parts
    /// index into that window) and at most <see cref="MaxDataSize"/>. As in UE, a chunk file may carry more data than the window.
    /// </summary>
    /// <exception cref="InvalidDataException">The size is outside that range.</exception>
    private void CheckDataSize(int32 size)
    {
        if (size <= 0 || (uint32)size < DataSizeUncompressed)
            throw new InvalidDataException($"Chunk {Guid} has {size} bytes of data, the manifest says {DataSizeUncompressed}");
        if (size > MaxDataSize)
        {
            throw new InvalidDataException(DataSizeUncompressed != 0
                ? $"Chunk {Guid} claims {size} bytes of data, the manifest says {DataSizeUncompressed} and the limit is {MaxDataSize}"
                : $"Chunk {Guid} claims {size} bytes of data, the manifest does not know its size and the limit is {MaxDataSize}");
        }
    }

    /// <summary>
    /// Compares the SHA1 of the decompressed data with <see cref="ShaHash"/>. If the manifest has no hash for the chunk (all zero)
    /// or the chunk file carries more data than the manifest's window (the manifest's hash describes the window), the data is
    /// checked against the header the way UE's <c>CheckChunkHash</c> does: its SHA1 when the hash type has
    /// <see cref="EChunkHashFlags.Sha1"/>, otherwise its rolling hash. As in UE, a header with neither hash type is invalid.
    /// </summary>
    /// <exception cref="InvalidDataException">The header has no hash type, or the hash does not match.</exception>
    private void VerifyHash(ReadOnlySpan<byte> data, in FChunkHeader header)
    {
        if ((header.HashType & (EChunkHashFlags.Sha1 | EChunkHashFlags.RollingPoly64)) == EChunkHashFlags.None)
            throw new InvalidDataException($"The header of chunk {Guid} has no hash type");

        FSHAHash expected = ShaHash;
        ReadOnlySpan<byte> expectedBytes = expected;
        bool hasWindowSize = DataSizeUncompressed == 0 || (uint32)data.Length == DataSizeUncompressed;
        if (!hasWindowSize || !expectedBytes.ContainsAnyExcept((uint8)0))
        {
            if (!header.HashType.HasFlag(EChunkHashFlags.Sha1))
            {
                uint64 rollingHash = FRollingHash.GetHashForDataSet(data);
                if (rollingHash != header.RollingHash)
                    throw new InvalidDataException($"The rolling hash of chunk {Guid} is {rollingHash:X16}, expected {header.RollingHash:X16}");
                return;
            }
            expected = header.ShaHash;
        }

        FSHAHash actual = FSHAHash.Compute(data);
        if (actual != expected)
            throw new InvalidDataException($"The SHA1 of chunk {Guid} is {actual}, expected {expected}");
    }

    // Format: {Hash:X16}_{Guid}.chunk
    // Hash: 16 chars, '_': 1, Guid: 32, ".chunk": 6 => total: 55 chars
    private const int32 FileNameLength = 16 + 1 + 32 + 6;

    private string GetCachePath(string cacheDirectory)
    {
        Span<char> fileName = stackalloc char[FileNameLength];
        if (!TryWriteChunkFileName(Hash, Guid, fileName, out int32 charsWritten))
            throw new Exception("Failed to create chunk fileName");

        return Path.Join(cacheDirectory, fileName[..charsWritten]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryWriteChunkFileName(
        uint64 hash,
        FGuid guid,
        Span<char> destination,
        out int32 charsWritten)
    {
        charsWritten = 0;

        if (destination.Length < FileNameLength)
            return false;

        if (!hash.TryFormat(destination, out int32 hashLen, "X16"))
            return false;
        charsWritten += hashLen;

        destination[charsWritten++] = '_';

        if (!guid.TryFormat(destination[charsWritten..], out int32 guidLen, default))
            return false;
        charsWritten += guidLen;

        ".chunk".CopyTo(destination[charsWritten..]);
        charsWritten += 6;

        return true;
    }

    // ReSharper disable once UseSymbolAlias
    internal static void TestDecompress(byte[] uncompressPoolBuffer, byte[] chunkBuffer, IDecompressor decompressor)
    {
        var header = FChunkHeader.Parse(chunkBuffer);

        if (!decompressor.TryDecompress(CompressionAlgorithm.Zlib,
            new ReadOnlySpan<byte>(chunkBuffer, header.HeaderSize, header.DataSizeCompressed),
            new Span<byte>(uncompressPoolBuffer, 0, header.DataSizeUncompressed),
            out int bytesWritten) || bytesWritten != header.DataSizeUncompressed)
        {
            throw new FileLoadException("Failed to uncompress data");
        }
    }
}
