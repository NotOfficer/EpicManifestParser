using System.Net;

using EpicManifestParser.Tests.Fixtures;
using EpicManifestParser.UE;

namespace EpicManifestParser.Tests;

/// <summary>
/// <see cref="FFileManifestStream.ReadAt(long,Span{byte})"/>: local parts are read on the calling thread, parts that have to be
/// downloaded load in parallel like in <see cref="FFileManifestStream.ReadAtAsync(long,Memory{byte},CancellationToken)"/>, and
/// none of them outlives the call.
/// </summary>
public class SyncReadTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>How long a cancelled request takes to stop, see <see cref="FakeCdn.HangUntilCancelledAsync"/>.</summary>
    private static readonly TimeSpan SlowStop = TimeSpan.FromMilliseconds(200);

    private const byte Guard = 0xEE;

    public static TheoryData<string> Modes => ["cache-directory", "memory-cache", "no-cache"];

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task A_cold_multi_part_read_downloads_the_parts_in_parallel(string mode)
    {
        using var dir = new TempDirectory();
        var cdn = new FakeCdn();
        // a read that requests one chunk after the other never gets past this: its request times out
        cdn.HoldUntilInFlight(2);
        using HttpClient client = cdn.CreateClient();
        FFileManifest file = TestData.GetFile(TestData.Parse(client, o =>
        {
            Configure(o, mode, dir.Path);
            o.MaxDownloadRetries = 0;
            o.DownloadIdleTimeout = TimeSpan.FromSeconds(5);
        }));
        byte[] actual = new byte[file.FileSize];

        Exception? error = await Record.ExceptionAsync(() => RunSync(() => Assert.Equal(actual.Length, file.GetStream().ReadAt(0, actual.AsSpan()))));

        Assert.True(error is null, $"the parts were not requested at the same time: {error}");
        Assert.Equal(TestData.ExpectedFile(file), actual);
        Assert.True(cdn.PeakInFlight > 1, $"peak in flight {cdn.PeakInFlight}");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(FFileManifestStream.DefaultMaxConcurrentPartReads)]
    public async Task At_most_MaxConcurrentPartReads_parts_load_at_the_same_time(int limit)
    {
        var cdn = new FakeCdn
        {
            Behavior = static async (_, token) =>
            {
                await Task.Delay(10, token);
                return null;
            }
        };
        if (limit > 1)
            cdn.HoldUntilInFlight(limit);
        using HttpClient client = cdn.CreateClient();
        FFileManifest file = TestData.CreateFile(TestData.Parse(client, o => o.DownloadIdleTimeout = TimeSpan.FromSeconds(10)),
            TestData.DistinctChunkParts(limit * 3, 4096));
        FFileManifestStream stream = file.GetStream();
        stream.MaxConcurrentPartReads = limit;
        byte[] actual = new byte[file.FileSize];

        await RunSync(() => Assert.Equal(actual.Length, stream.ReadAt(0, actual.AsSpan())));

        Assert.Equal(TestData.ExpectedFile(file), actual);
        Assert.Equal(limit, cdn.PeakInFlight);
        Assert.Equal(limit * 3, cdn.Requests);
    }

    [Theory]
    [InlineData("cache-directory")]
    [InlineData("memory-cache")]
    public async Task A_warm_multi_part_read_makes_no_request(string mode)
    {
        using var dir = new TempDirectory();
        var cdn = new FakeCdn();
        using HttpClient client = cdn.CreateClient();
        FFileManifest file = TestData.CreateFile(TestData.Parse(client, o => Configure(o, mode, dir.Path)), TestData.ManySmallParts(300, 6, 3000, seed: 5));
        FFileManifestStream stream = file.GetStream();
        byte[] expected = TestData.ExpectedFile(file);
        byte[] actual = new byte[expected.Length];

        await RunSync(() => stream.ReadAt(0, actual.AsSpan()));
        Assert.Equal(expected, actual);
        int requests = cdn.Requests;
        Assert.Equal(TestData.DistinctChunks(file).Length, requests);

        await RunSync(() =>
        {
            Array.Clear(actual);
            Assert.Equal(actual.Length, stream.ReadAt(0, actual.AsSpan()));
        });

        Assert.Equal(expected, actual);
        Assert.Equal(requests, cdn.Requests);
    }

    [Fact]
    public async Task A_warm_multi_part_read_from_the_memory_cache_allocates_nothing()
    {
        using HttpClient client = new FakeCdn().CreateClient();
        FFileManifest file = TestData.CreateFile(TestData.Parse(client), TestData.ManySmallParts(2000, 4, 64, seed: 3));
        FFileManifestStream stream = file.GetStream();
        byte[] expected = TestData.ExpectedFile(file);
        byte[] buffer = new byte[expected.Length];
        // fills the memory cache
        await RunSync(() => Assert.Equal(buffer.Length, stream.ReadAt(0, buffer.AsSpan())));
        Assert.Equal(buffer.Length, stream.ReadAt(0, buffer.AsSpan()));

        long before = GC.GetAllocatedBytesForCurrentThread();
        int read = stream.ReadAt(0, buffer.AsSpan());
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(buffer.Length, read);
        Assert.Equal(expected, buffer);
        Assert.Equal(0, allocated);
    }

    [Theory]
    [InlineData("cache-directory")]
    [InlineData("memory-cache")]
    public async Task A_read_mixing_local_and_missing_parts_returns_the_right_bytes(string mode)
    {
        using var dir = new TempDirectory();
        var cdn = new FakeCdn();
        using HttpClient client = cdn.CreateClient();
        FFileManifest file = TestData.CreateFile(TestData.Parse(client, o => Configure(o, mode, dir.Path)), TestData.ManySmallParts(300, 8, 3000, seed: 6));
        FFileManifestStream stream = file.GetStream();
        byte[] expected = TestData.ExpectedFile(file);
        IReadOnlyList<FChunkPart> parts = file.ChunkParts;
        FChunkInfo[] chunks = TestData.DistinctChunks(file);

        // makes every other chunk local: one part of each, read alone
        HashSet<FGuid> local = chunks.Where((_, i) => i % 2 == 0).Select(c => c.Guid).ToHashSet();
        foreach (FGuid guid in local)
        {
            FChunkPart part = parts.First(p => p.Chunk.Guid == guid);
            byte[] one = new byte[part.Size];
            await RunSync(() => stream.ReadAt(part.FileOffset, one.AsSpan()));
        }
        Assert.Equal(local.Count, cdn.Requests);

        byte[] actual = new byte[expected.Length + 6];
        actual.AsSpan().Fill(Guard);
        await RunSync(() => Assert.Equal(expected.Length, stream.ReadAt(0, actual.AsSpan(3, expected.Length))));

        Assert.True(actual.AsSpan(3, expected.Length).SequenceEqual(expected), "wrong data");
        Assert.True(actual.AsSpan(0, 3).IndexOfAnyExcept(Guard) < 0, "wrote before the buffer");
        Assert.True(actual.AsSpan(3 + expected.Length).IndexOfAnyExcept(Guard) < 0, "wrote past the buffer");
        // only the chunks that were not local, each once
        Assert.Equal(chunks.Length, cdn.Requests);
        Assert.All(chunks, c => Assert.Equal(1, cdn.RequestsFor(c.Guid)));
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task Multi_part_sync_reads_return_correct_data(string mode)
    {
        using var dir = new TempDirectory();
        using HttpClient client = new FakeCdn().CreateClient();
        FFileManifest file = TestData.CreateFile(TestData.Parse(client, o => Configure(o, mode, dir.Path)), TestData.ManySmallParts(300, 6, 3000, seed: 7));
        byte[] expected = TestData.ExpectedFile(file);
        IReadOnlyList<FChunkPart> parts = file.ChunkParts;
        long length = file.FileSize;

        var cases = new List<(long Position, int Count)>
        {
            (parts[10].FileOffset + 1, (int)(parts[40].FileOffset - parts[10].FileOffset)),
            (0, (int)length),
            (length - 4000, 10_000),
            (length, 10),
            (length + 5, 10)
        };
        var rng = new Random(12);
        for (int i = 0; i < 30; i++)
            cases.Add((rng.NextInt64(length), rng.Next(1, 40_000)));

        FFileManifestStream stream = file.GetStream();
        stream.MaxConcurrentPartReads = 4;

        await RunSync(() =>
        {
            foreach ((long position, int count) in cases)
            {
                int expectedCount = (int)Math.Clamp(length - position, 0, count);
                byte[] buffer = new byte[count + 6];
                buffer.AsSpan().Fill(Guard);

                int bytesRead = stream.ReadAt(position, buffer.AsSpan(3, count));

                string context = $"position {position}, count {count}, mode {mode}";
                Assert.True(expectedCount == bytesRead, $"{context}: read {bytesRead}, expected {expectedCount}");
                Assert.True(buffer.AsSpan(3, bytesRead).SequenceEqual(expected.AsSpan((int)Math.Min(position, length), bytesRead)), $"{context}: wrong data");
                Assert.True(buffer.AsSpan(0, 3).IndexOfAnyExcept(Guard) < 0, $"{context}: wrote before the buffer");
                Assert.True(buffer.AsSpan(3 + bytesRead).IndexOfAnyExcept(Guard) < 0, $"{context}: wrote past the bytes read");
            }
        });
    }

    [Fact]
    public async Task A_failing_part_throws_and_leaves_nothing_running()
    {
        var cdn = new FakeCdn();
        using HttpClient client = cdn.CreateClient();
        FFileManifest file = TestData.GetFile(TestData.Parse(client));
        FGuid slowChunk = file.ChunkParts[0].Chunk.Guid;
        FGuid missingChunk = file.ChunkParts.First(p => p.Chunk.Guid != slowChunk).Chunk.Guid;
        // the 404 only comes once the slow request is in flight
        cdn.HoldUntilInFlight(2);
        cdn.Behavior = (request, token) =>
            request.Chunk == slowChunk ? FakeCdn.HangUntilCancelledAsync(SlowStop, token)
            : request.Chunk == missingChunk ? Task.FromResult<HttpResponseMessage?>(FakeCdn.Status(HttpStatusCode.NotFound))
            : Task.FromResult<HttpResponseMessage?>(null);
        byte[] buffer = new byte[file.FileSize];

        Exception? error = await Record.ExceptionAsync(() => RunSync(() => file.GetStream().ReadAt(0, buffer.AsSpan())));
        int inFlight = cdn.InFlight;

        // not the cancellation of the slow part, which the failure caused
        HttpRequestException ex = Assert.IsType<HttpRequestException>(error);
        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        Assert.Equal(0, inFlight);
        Assert.Equal(1, cdn.RequestsFor(missingChunk));
        Assert.Equal(1, cdn.RequestsFor(slowChunk));
    }

    [Fact]
    public async Task A_chunk_two_missing_parts_share_is_downloaded_once()
    {
        var cdn = new FakeCdn();
        using HttpClient client = cdn.CreateClient();
        // parts 0 and 2 come from chunk 0, part 1 from chunk 1
        FFileManifest file = TestData.CreateFile(TestData.Parse(client), [(0, 0, 1000), (1, 0, 1000), (0, 5000, 1000)]);
        byte[] actual = new byte[file.FileSize];

        await RunSync(() => Assert.Equal(actual.Length, file.GetStream().ReadAt(0, actual.AsSpan())));

        Assert.Equal(TestData.ExpectedFile(file), actual);
        Assert.Equal(2, cdn.Requests);
    }

    /// <summary>Runs a synchronous read on a thread pool thread, so that a read that hangs fails the test instead of blocking it.</summary>
    private static Task RunSync(Action read) => Task.Run(read, Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct);

    private static void Configure(ManifestParseOptions options, string mode, string directory)
    {
        switch (mode)
        {
            case "cache-directory":
                options.ChunkCacheDirectory = directory;
                break;
            case "memory-cache":
                break;
            case "no-cache":
                options.ChunkMemoryCacheCapacity = 0;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
    }
}
