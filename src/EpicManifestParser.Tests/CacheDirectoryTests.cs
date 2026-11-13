using EpicManifestParser.Tests.Fixtures;
using EpicManifestParser.UE;

namespace EpicManifestParser.Tests;

public class CacheDirectoryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Cancelled_download_leaves_only_complete_files_and_the_next_run_returns_correct_bytes()
    {
        using var dir = new TempDirectory();
        var cdn = new FakeCdn();
        FGuid stalledChunk = TestData.ChunkGuids.First();
        cdn.Behavior = (request, _) => Task.FromResult(request.Chunk == stalledChunk ? FakeCdn.Stalls(request.Body) : null);
        using HttpClient client = cdn.CreateClient();

        FBuildPatchAppManifest manifest = TestData.Parse(client, o =>
        {
            o.ChunkCacheDirectory = dir.Path;
            o.DownloadIdleTimeout = Timeout.InfiniteTimeSpan;
        });
        FFileManifest file = TestData.GetFile(manifest);
        int otherChunks = TestData.DistinctChunks(file).Length - 1;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        Task save = file.GetStream().SaveBytesAsync(new byte[file.FileSize], 16, cts.Token);

        // every other chunk is downloaded and persisted, the stalled one is mid-body
        await WaitUntilAsync(() => dir.Files.Count(f => f.EndsWith(".chunk", StringComparison.Ordinal)) == otherChunks);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => save);

        AssertCacheDirectoryIsConsistent(dir.Path, file);
        Assert.Equal(otherChunks, dir.Files.Length);

        // next run (fresh manifest, same directory) only downloads the missing chunk
        cdn.Behavior = null;
        int requestsBefore = cdn.Requests;
        FBuildPatchAppManifest manifest2 = TestData.Parse(client, o => o.ChunkCacheDirectory = dir.Path);
        FFileManifest file2 = TestData.GetFile(manifest2);
        byte[] actual = await file2.GetStream().SaveBytesAsync(cancellationToken: Ct);

        Assert.Equal(TestData.ExpectedFile(file2), actual);
        Assert.Equal(1, cdn.Requests - requestsBefore);
        AssertCacheDirectoryIsConsistent(dir.Path, file2);
    }

    [Fact]
    public async Task Cancellation_at_random_points_never_leaves_files_that_yield_wrong_data()
    {
        var cdn = new FakeCdn
        {
            Behavior = static async (_, token) =>
            {
                await Task.Delay(Random.Shared.Next(0, 3), token);
                return null;
            }
        };
        using HttpClient client = cdn.CreateClient();
        var rng = new Random(7);

        for (int attempt = 0; attempt < 30; attempt++)
        {
            using var dir = new TempDirectory();
            FBuildPatchAppManifest manifest = TestData.Parse(client, o => o.ChunkCacheDirectory = dir.Path);
            FFileManifest file = TestData.GetFile(manifest);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            cts.CancelAfter(TimeSpan.FromMilliseconds(rng.Next(0, 40)));
            try
            {
                await file.GetStream().SaveBytesAsync(new byte[file.FileSize], 16, cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
            }

            AssertCacheDirectoryIsConsistent(dir.Path, file);

            FBuildPatchAppManifest manifest2 = TestData.Parse(client, o => o.ChunkCacheDirectory = dir.Path);
            FFileManifest file2 = TestData.GetFile(manifest2);
            Assert.Equal(TestData.ExpectedFile(file2), await file2.GetStream().SaveBytesAsync(cancellationToken: Ct));
        }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(16, false)]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(-1, true)]
    public async Task Truncated_cache_files_are_healed(int length, bool sync)
    {
        using var dir = new TempDirectory();
        var cdn = new FakeCdn();
        using HttpClient client = cdn.CreateClient();
        FBuildPatchAppManifest manifest = TestData.Parse(client, o => o.ChunkCacheDirectory = dir.Path);
        FFileManifest file = TestData.GetFile(manifest);
        FChunkInfo[] chunks = TestData.DistinctChunks(file);

        // what an interrupted write of an older version leaves behind: the final name, too few bytes
        foreach (FChunkInfo chunk in chunks)
        {
            byte[] data = TestData.GetChunkData(chunk.Guid);
            await File.WriteAllBytesAsync(TestData.CacheFilePath(dir.Path, chunk), data.AsMemory(0, length < 0 ? data.Length / 2 : length), Ct);
        }

        byte[] actual = new byte[file.FileSize];
        if (sync)
            Assert.Equal(actual.Length, file.GetStream().ReadAt(0, actual.AsSpan()));
        else
            await file.GetStream().SaveBytesAsync(actual, cancellationToken: Ct);

        Assert.Equal(TestData.ExpectedFile(file), actual);
        Assert.Equal(chunks.Length, cdn.Requests);
        foreach (FChunkInfo chunk in chunks)
            Assert.Equal(TestData.GetChunkData(chunk.Guid), await File.ReadAllBytesAsync(TestData.CacheFilePath(dir.Path, chunk), Ct));
        AssertCacheDirectoryIsConsistent(dir.Path, file);
    }

    [Fact]
    public async Task Cache_file_deleted_while_running_is_downloaded_again()
    {
        using var dir = new TempDirectory();
        var cdn = new FakeCdn();
        using HttpClient client = cdn.CreateClient();
        FBuildPatchAppManifest manifest = TestData.Parse(client, o => o.ChunkCacheDirectory = dir.Path);
        FFileManifest file = TestData.GetFile(manifest);
        FFileManifestStream stream = file.GetStream();
        byte[] expected = TestData.ExpectedFile(file);

        Assert.Equal(expected, await stream.SaveBytesAsync(cancellationToken: Ct));
        foreach (string path in dir.Files)
            File.Delete(path);

        Assert.Equal(expected, await stream.SaveBytesAsync(cancellationToken: Ct));
        byte[] syncRead = new byte[file.FileSize];
        foreach (string path in dir.Files)
            File.Delete(path);
        Assert.Equal(syncRead.Length, stream.ReadAt(0, syncRead.AsSpan()));
        Assert.Equal(expected, syncRead);
        Assert.Equal(3 * TestData.DistinctChunks(file).Length, cdn.Requests);
    }

    [Fact]
    public async Task Existing_cache_files_are_used_without_downloading()
    {
        using var dir = new TempDirectory();
        var cdn = new FakeCdn();
        using HttpClient client = cdn.CreateClient();
        FBuildPatchAppManifest manifest = TestData.Parse(client, o => o.ChunkCacheDirectory = dir.Path);
        FFileManifest file = TestData.GetFile(manifest);

        // the file name format of the decompressed cache must not change, existing caches keep working
        foreach (FChunkInfo chunk in TestData.DistinctChunks(file))
            await File.WriteAllBytesAsync(Path.Combine(dir.Path, $"{chunk.Hash:X16}_{chunk.Guid}.chunk"), TestData.GetChunkData(chunk.Guid), Ct);

        Assert.Equal(TestData.ExpectedFile(file), await file.GetStream().SaveBytesAsync(cancellationToken: Ct));
        byte[] syncRead = new byte[file.FileSize];
        Assert.Equal(syncRead.Length, TestData.GetFile(TestData.Parse(client, o => o.ChunkCacheDirectory = dir.Path)).GetStream().ReadAt(0, syncRead.AsSpan()));
        Assert.Equal(TestData.ExpectedFile(file), syncRead);
        Assert.Equal(0, cdn.Requests);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(4096, false)]
    [InlineData(4096, true)]
    public async Task Cache_files_of_an_earlier_run_with_the_wrong_length_are_downloaded_again(int extraBytes, bool sync)
    {
        using var dir = new TempDirectory();
        var cdn = new FakeCdn();
        using HttpClient client = cdn.CreateClient();
        FBuildPatchAppManifest manifest = TestData.Parse(client, o => o.ChunkCacheDirectory = dir.Path);
        FFileManifest file = TestData.GetFile(manifest);
        FChunkInfo[] chunks = TestData.DistinctChunks(file);

        // the right data followed by garbage: every range of the file reads fine, only the length tells
        foreach (FChunkInfo chunk in chunks)
            await File.WriteAllBytesAsync(TestData.CacheFilePath(dir.Path, chunk), [.. TestData.GetChunkData(chunk.Guid), .. new byte[extraBytes]], Ct);

        byte[] actual = new byte[file.FileSize];
        if (sync)
            Assert.Equal(actual.Length, file.GetStream().ReadAt(0, actual.AsSpan()));
        else
            await file.GetStream().SaveBytesAsync(actual, cancellationToken: Ct);

        Assert.Equal(TestData.ExpectedFile(file), actual);
        Assert.Equal(chunks.Length, cdn.Requests);
        AssertCacheDirectoryIsConsistent(dir.Path, file);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_missing_cache_directory_is_created(bool sync)
    {
        using var dir = new TempDirectory();
        string cacheDirectory = Path.Combine(dir.Path, "not", "there");
        var cdn = new FakeCdn();
        using HttpClient client = cdn.CreateClient();
        FFileManifest file = TestData.GetFile(TestData.Parse(client, o => o.ChunkCacheDirectory = cacheDirectory));
        byte[] expected = TestData.ExpectedFile(file);

        // used to fail every read with a DirectoryNotFoundException, after the download
        for (int i = 0; i < 3; i++)
        {
            byte[] actual = new byte[file.FileSize];
            if (sync)
                Assert.Equal(actual.Length, file.GetStream().ReadAt(0, actual.AsSpan()));
            else
                await file.GetStream().SaveBytesAsync(actual, cancellationToken: Ct);
            Assert.Equal(expected, actual);
        }

        Assert.Equal(TestData.DistinctChunks(file).Length, cdn.Requests);
        AssertCacheDirectoryIsConsistent(cacheDirectory, file);
    }

    [Theory]
    [InlineData("directory-at-the-file-path", false)]
    [InlineData("directory-at-the-file-path", true)]
    [InlineData("file-at-the-directory-path", false)]
    [InlineData("file-at-the-directory-path", true)]
    public async Task A_failed_cache_write_still_returns_the_data_and_leaves_no_temporary_file(string obstacle, bool sync)
    {
        using var dir = new TempDirectory();
        var cdn = new FakeCdn();
        using HttpClient client = cdn.CreateClient();
        string cacheDirectory = obstacle == "file-at-the-directory-path" ? Path.Combine(dir.Path, "chunks") : dir.Path;
        FFileManifest file = TestData.GetFile(TestData.Parse(client, o => o.ChunkCacheDirectory = cacheDirectory));
        FChunkInfo[] chunks = TestData.DistinctChunks(file);
        if (obstacle == "file-at-the-directory-path")
        {
            // the directory cannot be created
            await File.WriteAllBytesAsync(cacheDirectory, [1, 2, 3], Ct);
        }
        else
        {
            // the temporary file is written, the rename onto the final name fails
            foreach (FChunkInfo chunk in chunks)
                Directory.CreateDirectory(TestData.CacheFilePath(dir.Path, chunk));
        }

        for (int i = 0; i < 2; i++)
        {
            byte[] actual = new byte[file.FileSize];
            if (sync)
                Assert.Equal(actual.Length, file.GetStream().ReadAt(0, actual.AsSpan()));
            else
                await file.GetStream().SaveBytesAsync(actual, cancellationToken: Ct);
            Assert.Equal(TestData.ExpectedFile(file), actual);
        }

        // nothing could be cached: every part downloads its chunk, also the second time
        Assert.Equal(2 * file.ChunkParts.Count, cdn.Requests);
        Assert.Empty(Directory.GetFiles(dir.Path, "*.tmp", SearchOption.AllDirectories));
    }

    /// <summary>
    /// Every file in the directory has a final chunk file name and exactly the chunk data; no temporary files are left.
    /// </summary>
    internal static void AssertCacheDirectoryIsConsistent(string directory, FFileManifest file)
    {
        Dictionary<string, FChunkInfo> chunksByName = TestData.DistinctChunks(file)
            .ToDictionary(c => Path.GetFileName(TestData.CacheFilePath(directory, c)), StringComparer.OrdinalIgnoreCase);

        foreach (string path in Directory.GetFiles(directory))
        {
            string name = Path.GetFileName(path);
            Assert.True(chunksByName.TryGetValue(name, out FChunkInfo? chunk), $"unexpected file in the cache directory: {name}");
            Assert.True(TestData.GetChunkData(chunk.Guid).AsSpan().SequenceEqual(File.ReadAllBytes(path)), $"{name} does not contain the chunk data");
        }
    }

    internal static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!condition())
            await Task.Delay(5, timeout.Token);
    }
}
