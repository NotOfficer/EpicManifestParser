using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Runtime.InteropServices;

using EpicManifestParser.UE;

using OffiUtils;

namespace EpicManifestParser.Tests.Fixtures;

/// <summary>
/// A small file of <c>files/manifest.bin</c> whose chunks are synthesized: a deterministic pseudo-random payload per chunk GUID,
/// served by <see cref="FakeCdn"/> either raw or zlib compressed. The chunk files have real headers: the chunk's GUID, the
/// SHA1 of the payload, the rolling hash of the file name. The manifest's SHA1 of those chunks is replaced by the one of the payload,
/// see <see cref="DescribeSyntheticChunks"/>; chunks of <see cref="CreateFile"/> are not in any chunk list, so the manifest knows
/// neither their size nor their hash.
/// </summary>
internal static class TestData
{
    /// <summary>5 chunk parts over 4 distinct chunks, one chunk is referenced twice.</summary>
    public const string FileName = "Engine/Binaries/ThirdParty/CEF3/Win64/Resources/swiftshader/libGLESv2.dll";
    public const string ChunkBaseUrl = "https://cdn.test/CloudDir/";

    public static readonly byte[] ManifestBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "files", "manifest.bin"));
    private static readonly IDecompressor Decompressor = DecompressorBuilder.Default.Build();
    private static readonly Lazy<Probe> LazyProbe = new(CreateProbe);
    private static readonly ConcurrentDictionary<FGuid, byte[]> ChunkData = new();
    private static readonly ConcurrentDictionary<FGuid, byte[]> ChunkFiles = new();

    /// <summary>The size of the decompressed data of a chunk of <see cref="CreateFile"/>.</summary>
    public const int SyntheticChunkSize = 64 * 1024;
    private static readonly ConcurrentDictionary<FGuid, int> SyntheticWindowSizes = new();

    private sealed record Probe(Dictionary<FGuid, int> WindowSizes, Dictionary<FGuid, ulong> RollingHashes, HashSet<FGuid> Compressed);

    private static Probe CreateProbe()
    {
        FBuildPatchAppManifest manifest = FBuildPatchAppManifest.Deserialize(ManifestBytes);
        FFileManifest file = manifest.FindFile(FileName) ?? throw new InvalidOperationException($"{FileName} not found in manifest.bin");

        var windowSizes = new Dictionary<FGuid, int>();
        var rollingHashes = new Dictionary<FGuid, ulong>();
        var compressed = new HashSet<FGuid>();
        foreach (FChunkPart part in file.ChunkParts)
        {
            if (!windowSizes.TryAdd(part.Chunk.Guid, (int)part.Chunk.DataSizeUncompressed))
                continue;
            rollingHashes.Add(part.Chunk.Guid, part.Chunk.Hash);
            if (windowSizes.Count % 2 == 1)
                compressed.Add(part.Chunk.Guid); // every other distinct chunk is served zlib compressed
        }

        return new Probe(windowSizes, rollingHashes, compressed);
    }

    /// <summary>Parses the manifest with a fresh state (no resolved cache paths), retries without delay.</summary>
    public static FBuildPatchAppManifest Parse(HttpClient client, Action<ManifestParseOptions>? configure = null)
    {
        FBuildPatchAppManifest manifest = FBuildPatchAppManifest.Deserialize(ManifestBytes, options =>
        {
            options.ChunkBaseUrl = ChunkBaseUrl;
            options.Client = client;
            options.Decompressor = Decompressor;
            options.DownloadRetryBaseDelay = TimeSpan.Zero;
            configure?.Invoke(options);
        });
        return DescribeSyntheticChunks(manifest);
    }

    /// <summary>
    /// The chunks of <see cref="FileName"/> carry the SHA1 of the real build in <c>manifest.bin</c>, the CDN serves synthesized data:
    /// makes their <see cref="FChunkInfo.ShaHash"/> the SHA1 of that data, so that chunk verification sees a consistent manifest.
    /// Call it on every manifest parsed from <see cref="ManifestBytes"/> that reads chunks; <see cref="Parse"/> does.
    /// </summary>
    public static FBuildPatchAppManifest DescribeSyntheticChunks(FBuildPatchAppManifest manifest)
    {
        foreach (FGuid guid in ChunkGuids)
        {
            if (manifest.Chunks.TryGetValue(guid, out FChunkInfo? chunk))
                chunk.ShaHash = GetChunkSha(guid);
        }
        return manifest;
    }

    /// <summary>The SHA1 of <see cref="GetChunkData"/>.</summary>
    public static FSHAHash GetChunkSha(FGuid guid) => ChunkShas.GetOrAdd(guid, static guid => FSHAHash.Compute(GetChunkData(guid)));

    private static readonly ConcurrentDictionary<FGuid, FSHAHash> ChunkShas = new();

    public static FFileManifest GetFile(FBuildPatchAppManifest manifest) => manifest.FindFile(FileName)!;

    public static IReadOnlyCollection<FGuid> ChunkGuids => LazyProbe.Value.WindowSizes.Keys;

    public static bool IsServedCompressed(FGuid guid) => LazyProbe.Value.Compressed.Contains(guid);

    /// <summary>The decompressed data of a chunk.</summary>
    public static byte[] GetChunkData(FGuid guid) => ChunkData.GetOrAdd(guid, static guid =>
    {
        int windowSize = LazyProbe.Value.WindowSizes.TryGetValue(guid, out int size) ? size : SyntheticWindowSizes.GetValueOrDefault(guid, 1024 * 1024);
        byte[] data = new byte[windowSize];
        ReadOnlySpan<int> seed = MemoryMarshal.Cast<byte, int>(guid.AsSpan());
        new Random(seed[0] ^ seed[1] ^ seed[2] ^ seed[3]).NextBytes(data);
        // runs of repeated bytes so that zlib actually has something to compress
        for (int i = 0; i < data.Length; i += 64)
            data.AsSpan(i, Math.Min(32, data.Length - i)).Fill(data[i]);
        return data;
    });

    /// <summary>The chunk file as served by the CDN: 66 byte header (version 3) followed by the raw or zlib compressed data.</summary>
    public static byte[] GetChunkFile(FGuid guid) => ChunkFiles.GetOrAdd(guid, static guid => CreateChunkFile(guid, IsServedCompressed(guid)));

    public static byte[] CreateChunkFile(FGuid guid, bool compress, byte storedAs = 0xFF) =>
        BuildChunkFile(new ChunkFileSpec(guid, GetChunkData(guid), compress) { StoredAs = storedAs == 0xFF ? null : storedAs });

    /// <summary>The flag for an SHA1 in the chunk header (<c>EChunkHashFlags::Sha1</c>).</summary>
    public const byte HashTypeSha1 = 2;

    /// <summary>
    /// What <see cref="BuildChunkFile"/> writes: by default a version 3 chunk file of <paramref name="Data"/> as the CDN serves it.
    /// </summary>
    /// <param name="Guid">The GUID in the header.</param>
    /// <param name="Data">The decompressed chunk data.</param>
    /// <param name="Compress">Store the data zlib compressed.</param>
    public sealed record ChunkFileSpec(FGuid Guid, byte[] Data, bool Compress)
    {
        /// <summary>The header version; the header has the fields up to this version.</summary>
        public uint Version { get; init; } = 3;
        /// <summary>The header size written into the header and used as the data offset, defaults to the size of <see cref="Version"/>.</summary>
        public int? HeaderSize { get; init; }
        /// <summary>The storage flags, defaults to 1 when compressed and 0 otherwise.</summary>
        public byte? StoredAs { get; init; }
        /// <summary>
        /// The hash type. The header carries the SHA1 of the data; it is not a real rolling hash (its value is the one of the file
        /// name), so the default only claims the SHA1.
        /// </summary>
        public byte HashType { get; init; } = HashTypeSha1;
        /// <summary>The SHA1 in the header, defaults to the SHA1 of <see cref="Data"/>.</summary>
        public FSHAHash? Sha { get; init; }
        /// <summary>The rolling hash in the header, defaults to the chunk's hash in the manifest (not the hash of the synthesized data).</summary>
        public ulong? RollingHash { get; init; }
        /// <summary>The uncompressed size in a version 3 header, defaults to the length of <see cref="Data"/>.</summary>
        public int? DataSizeUncompressed { get; init; }
    }

    /// <summary>
    /// A chunk file: header (magic, version, header size, compressed size, GUID, rolling hash, storage flags, SHA1, hash type,
    /// uncompressed size, as far as the version has them) followed by the raw or zlib compressed data.
    /// </summary>
    public static byte[] BuildChunkFile(ChunkFileSpec spec)
    {
        byte[] payload = spec.Data;
        if (spec.Compress)
        {
            using var ms = new MemoryStream();
            using (var zlib = new ZLibStream(ms, CompressionLevel.Fastest, true))
                zlib.Write(spec.Data);
            payload = ms.ToArray();
        }

        int fieldsSize = spec.Version switch { <= 1 => 41, 2 => 62, _ => 66 };
        Span<byte> fields = stackalloc byte[66];
        fields.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(fields, 0xB1FE3AA2);
        BinaryPrimitives.WriteUInt32LittleEndian(fields[4..], spec.Version);
        int headerSize = spec.HeaderSize ?? fieldsSize;
        BinaryPrimitives.WriteInt32LittleEndian(fields[8..], headerSize);
        BinaryPrimitives.WriteInt32LittleEndian(fields[12..], payload.Length);
        spec.Guid.AsSpan().CopyTo(fields[16..]);
        BinaryPrimitives.WriteUInt64LittleEndian(fields[32..], spec.RollingHash ?? LazyProbe.Value.RollingHashes.GetValueOrDefault(spec.Guid));
        fields[40] = spec.StoredAs ?? (byte)(spec.Compress ? 1 : 0);
        if (fieldsSize >= 62)
        {
            FSHAHash sha = spec.Sha ?? FSHAHash.Compute(spec.Data);
            sha.GetSpan().CopyTo(fields[41..]);
            fields[61] = spec.HashType;
        }
        if (fieldsSize >= 66)
            BinaryPrimitives.WriteInt32LittleEndian(fields[62..], spec.DataSizeUncompressed ?? spec.Data.Length);

        // a header size below the size of its fields cuts them off: the data starts where the header size says
        byte[] file = new byte[headerSize + payload.Length];
        fields[..Math.Min(fieldsSize, headerSize)].CopyTo(file);
        payload.CopyTo(file, headerSize);
        return file;
    }

    /// <summary>The content of <see cref="FileName"/>: the chunk parts over the synthesized chunk data.</summary>
    public static byte[] ExpectedFile(FFileManifest file)
    {
        byte[] expected = new byte[file.FileSize];
        foreach (FChunkPart part in file.ChunkParts)
            GetChunkData(part.Chunk.Guid).AsSpan((int)part.Offset, (int)part.Size).CopyTo(expected.AsSpan((int)part.FileOffset));
        return expected;
    }

    /// <summary>
    /// A file that is not in the manifest, made of <paramref name="parts"/> in this order: each one names a synthetic chunk by
    /// index (same index, same chunk) and a range of its <see cref="SyntheticChunkSize"/> bytes. Only <see cref="FakeCdn"/> knows
    /// these chunks; they are served raw.
    /// </summary>
    public static FFileManifest CreateFile(FBuildPatchAppManifest manifest, ReadOnlySpan<(int Chunk, int Offset, int Size)> parts)
    {
        var chunks = new Dictionary<FGuid, FChunkInfo>();
        var chunkParts = new FChunkPart[parts.Length];
        long fileOffset = 0;

        for (int i = 0; i < parts.Length; i++)
        {
            (int chunk, int offset, int size) = parts[i];
            if (offset < 0 || size < 0 || offset + size > SyntheticChunkSize)
                throw new ArgumentOutOfRangeException(nameof(parts), $"part {i} exceeds the chunk");

            var guid = new FGuid(0x5EED0000, (uint)chunk, 0xC0DE, 0xF11E);
            SyntheticWindowSizes.TryAdd(guid, SyntheticChunkSize);
            chunkParts[i] = new FChunkPart(guid, (uint)offset, (uint)size, fileOffset, chunks);
            fileOffset += size;
        }

        var file = new FFileManifest
        {
            FileName = $"Synthetic/{parts.Length}-parts-over-{chunks.Count}-chunks.bin",
            FileSize = fileOffset,
            Manifest = manifest
        };
        file.ChunkPartsArray = chunkParts;
        return file;
    }

    /// <summary><paramref name="count"/> parts of <paramref name="size"/> bytes, each one from a different chunk.</summary>
    public static (int Chunk, int Offset, int Size)[] DistinctChunkParts(int count, int size)
    {
        var parts = new (int, int, int)[count];
        for (int i = 0; i < count; i++)
            parts[i] = (i, (i * 997) % (SyntheticChunkSize - size + 1), size);
        return parts;
    }

    /// <summary>
    /// <paramref name="count"/> parts of 1 to <paramref name="maxSize"/> bytes at varying offsets, spread over
    /// <paramref name="chunkCount"/> chunks; consecutive parts often come from the same chunk.
    /// </summary>
    public static (int Chunk, int Offset, int Size)[] ManySmallParts(int count, int chunkCount, int maxSize, int seed)
    {
        var rng = new Random(seed);
        var parts = new (int, int, int)[count];
        for (int i = 0; i < count; i++)
        {
            int size = rng.Next(1, maxSize + 1);
            parts[i] = (rng.Next(3) == 0 && i > 0 ? parts[i - 1].Item1 : rng.Next(chunkCount), rng.Next(SyntheticChunkSize - size + 1), size);
        }
        return parts;
    }

    public static string CacheFilePath(string cacheDirectory, FChunkInfo chunk) =>
        Path.Combine(cacheDirectory, $"{chunk.Hash:X16}_{chunk.Guid}.chunk");

    public static FChunkInfo[] DistinctChunks(FFileManifest file) => file.ChunkParts.Select(p => p.Chunk).Distinct().ToArray();
}
