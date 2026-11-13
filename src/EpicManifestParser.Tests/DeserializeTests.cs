using EpicManifestParser.UE;

namespace EpicManifestParser.Tests;

public class DeserializeTests
{
    private const string BinaryPath = "files/manifest.bin";
    private const string JsonPath = "files/manifest.json";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // golden values, determined once from the fixtures
    private const int BinaryFileCount = 925;
    private const int BinaryChunkCount = 113789;
    private const long BinaryTotalBuildSize = 120014187513;
    private const int JsonFileCount = 3430;
    private const int JsonChunkCount = 246;
    private const long JsonTotalBuildSize = 239239382;

    public static TheoryData<string, int, int, long> Fixtures => new()
    {
        { BinaryPath, BinaryFileCount, BinaryChunkCount, BinaryTotalBuildSize },
        { JsonPath, JsonFileCount, JsonChunkCount, JsonTotalBuildSize },
    };

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task Deserialize_buffer(string path, int fileCount, int chunkCount, long totalBuildSize)
    {
        byte[] manifestBuffer = await File.ReadAllBytesAsync(path, Ct);
        AssertConsistent(FBuildPatchAppManifest.Deserialize(manifestBuffer), fileCount, chunkCount, totalBuildSize);
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task DeserializeFileAsync(string path, int fileCount, int chunkCount, long totalBuildSize)
    {
        FBuildPatchAppManifest manifest = await FBuildPatchAppManifest.DeserializeFileAsync(path, cancellationToken: Ct);
        AssertConsistent(manifest, fileCount, chunkCount, totalBuildSize);
    }

    private static void AssertConsistent(FBuildPatchAppManifest manifest, int fileCount, int chunkCount, long totalBuildSize)
    {
        Assert.Equal(fileCount, manifest.Files.Count);
        Assert.Equal(chunkCount, manifest.ChunkList.Count);
        Assert.Equal(chunkCount, manifest.Chunks.Count);
        Assert.Equal(totalBuildSize, manifest.TotalBuildSize);
        Assert.Equal(manifest.Files.Sum(f => f.FileSize), manifest.TotalBuildSize);
        Assert.Equal(manifest.ChunkList.Sum(c => c.FileSize), manifest.TotalDownloadSize);

        foreach (FChunkInfo chunk in manifest.ChunkList)
            Assert.Same(chunk, manifest.Chunks[chunk.Guid]);

        foreach (FFileManifest file in manifest.Files)
        {
            long fileOffset = 0;
            foreach (FChunkPart part in file.ChunkParts)
            {
                Assert.Equal(fileOffset, part.FileOffset);
                Assert.True(manifest.Chunks.TryGetValue(part.Chunk.Guid, out FChunkInfo? chunk), $"{file.FileName}: chunk {part.Chunk.Guid} is not in Chunks");
                Assert.Same(chunk, part.Chunk);
                fileOffset += part.Size;
            }

            Assert.Equal(fileOffset, file.FileSize);
        }

        // sorted by name, as UE's OnPostLoad does
        for (int i = 1; i < manifest.Files.Count; i++)
            Assert.True(string.CompareOrdinal(manifest.Files[i - 1].FileName, manifest.Files[i].FileName) <= 0);
    }

    [Fact]
    public async Task Single_argument_overloads_compile_and_use_default_options()
    {
        byte[] binary = await File.ReadAllBytesAsync(BinaryPath, Ct);
        byte[] json = await File.ReadAllBytesAsync(JsonPath, Ct);

        // these calls were ambiguous (CS0121) while both overloads had an optional second parameter
        FBuildPatchAppManifest[] manifests =
        [
            FBuildPatchAppManifest.Deserialize(binary),
            FBuildPatchAppManifest.Deserialize(json),
            FBuildPatchAppManifest.DeserializeBinary(binary),
            FBuildPatchAppManifest.DeserializeJson(json),
        ];

        foreach (FBuildPatchAppManifest manifest in manifests)
        {
            Assert.NotNull(manifest.Options);
            Assert.Null(manifest.Options.ChunkBaseUrl);
            Assert.Equal(32, manifest.Options.ChunkMemoryCacheCapacity);
        }
    }

    [Fact]
    public async Task Null_second_argument_means_default_options()
    {
        byte[] binary = await File.ReadAllBytesAsync(BinaryPath, Ct);
        byte[] json = await File.ReadAllBytesAsync(JsonPath, Ct);
        Action<ManifestParseOptions>? noBuilder = null;
        ManifestParseOptions? noOptions = null;

        FBuildPatchAppManifest[] manifests =
        [
            FBuildPatchAppManifest.Deserialize(binary, noBuilder),
            FBuildPatchAppManifest.Deserialize(json, noOptions),
            FBuildPatchAppManifest.DeserializeBinary(binary, noBuilder),
            FBuildPatchAppManifest.DeserializeBinary(binary, noOptions),
            FBuildPatchAppManifest.DeserializeJson(json, noBuilder),
            FBuildPatchAppManifest.DeserializeJson(json, noOptions),
        ];

        foreach (FBuildPatchAppManifest manifest in manifests)
        {
            Assert.NotNull(manifest.Options);
            Assert.Null(manifest.Options.ChunkBaseUrl);
        }
    }

    [Fact]
    public async Task Options_and_builder_overloads_pass_the_options()
    {
        byte[] json = await File.ReadAllBytesAsync(JsonPath, Ct);
        var options = new ManifestParseOptions { ChunkBaseUrl = "https://cdn.test/" };

        Assert.Same(options, FBuildPatchAppManifest.DeserializeJson(json, options).Options);
        Assert.Equal("https://cdn.test/", FBuildPatchAppManifest.DeserializeJson(json, o => o.ChunkBaseUrl = "https://cdn.test/").Options.ChunkBaseUrl);
    }

    [Fact]
    public async Task DeserializeFileAsync_rejects_an_empty_file()
    {
        using var dir = new Fixtures.TempDirectory();
        string path = Path.Combine(dir.Path, "empty.manifest");
        await File.WriteAllBytesAsync(path, [], Ct);

        await Assert.ThrowsAsync<InvalidDataException>(async () => await FBuildPatchAppManifest.DeserializeFileAsync(path, cancellationToken: Ct));
    }

    [Fact]
    public async Task DeserializeFileAsync_rejects_a_file_larger_than_an_array()
    {
        using var dir = new Fixtures.TempDirectory();
        string path = Path.Combine(dir.Path, "huge.manifest");
        await using (var stream = new FileStream(path, FileMode.CreateNew))
        {
            try
            {
                // NTFS and ext4 extend the length without writing the zeros
                stream.SetLength(Array.MaxLength + 1L);
            }
            catch (IOException ex)
            {
                Assert.Skip($"cannot create a {Array.MaxLength + 1L} byte file here: {ex.Message}");
            }
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        var ex2 = await Assert.ThrowsAsync<InvalidDataException>(async () => await FBuildPatchAppManifest.DeserializeFileAsync(path, cancellationToken: Ct));
        Assert.Contains("largest supported", ex2.Message);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1024 * 1024);
    }
}
