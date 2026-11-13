using System.Net;

using EpicManifestParser.Tests.Fixtures;
using EpicManifestParser.UE;

namespace EpicManifestParser.Tests;

/// <summary>
/// <see cref="FFileManifestStream.ReadAtAsync(long,Memory{byte},CancellationToken)"/> over several chunk parts: the parts load in
/// parallel, at most <see cref="FFileManifestStream.MaxConcurrentPartReads"/> at a time, and none of them outlives the call.
/// </summary>
public class ParallelReadTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>How long a cancelled request takes to stop, see <see cref="FakeCdn.HangUntilCancelledAsync"/>.</summary>
    private static readonly TimeSpan SlowStop = TimeSpan.FromMilliseconds(200);

    private const byte Guard = 0xEE;

    public static TheoryData<string> Modes => ["cache-directory", "memory-cache", "no-cache"];

    public static TheoryData<string, int> ModesAndLimits => new()
    {
        { "cache-directory", FFileManifestStream.DefaultMaxConcurrentPartReads },
        { "memory-cache", FFileManifestStream.DefaultMaxConcurrentPartReads },
        { "no-cache", FFileManifestStream.DefaultMaxConcurrentPartReads },
        { "cache-directory", 2 },
        { "memory-cache", 2 },
        { "no-cache", 2 }
    };

    [Theory]
    [MemberData(nameof(ModesAndLimits))]
    public async Task Multi_part_reads_return_correct_data(string mode, int limit)
    {
        using var dir = new TempDirectory();
        using HttpClient client = new FakeCdn().CreateClient();
        FFileManifest file = TestData.CreateFile(TestData.Parse(client, o => Configure(o, mode, dir.Path)), TestData.ManySmallParts(300, 6, 3000, seed: 1));
        byte[] expected = TestData.ExpectedFile(file);
        IReadOnlyList<FChunkPart> parts = file.ChunkParts;
        long length = file.FileSize;
        int big = Enumerable.Range(1, parts.Count - 2).First(i => parts[i].Size >= 100);

        var cases = new List<(string Name, long Position, int Count)>
        {
            ("inside one part", parts[big].FileOffset + 1, (int)parts[big].Size - 2),
            ("exactly one part", parts[big].FileOffset, (int)parts[big].Size),
            ("across two parts", parts[big + 1].FileOffset - 5, 5 + (int)Math.Min(5, parts[big + 1].Size)),
            ("over a hundred parts", parts[20].FileOffset + 3, (int)(parts[120].FileOffset - parts[20].FileOffset)),
            ("whole file", 0, (int)length),
            ("exactly to the end", length - 5000, 5000),
            ("crossing the end", length - 4000, 10_000),
            ("at the end", length, 10),
            ("past the end", length + 5, 10)
        };
        var rng = new Random(11);
        for (int i = 0; i < 30; i++)
            cases.Add(($"random {i}", rng.NextInt64(length), rng.Next(1, 40_000)));

        FFileManifestStream stream = file.GetStream();
        stream.MaxConcurrentPartReads = limit;

        foreach ((string name, long position, int count) in cases)
        {
            int expectedCount = (int)Math.Clamp(length - position, 0, count);

            for (int variant = 0; variant < 2; variant++)
            {
                // ReadAtAsync and ReadAsync, which goes through it
                if (variant == 1 && position > length)
                    continue;

                byte[] buffer = new byte[count + 6];
                buffer.AsSpan().Fill(Guard);
                Memory<byte> target = buffer.AsMemory(3, count);
                int bytesRead;
                if (variant == 0)
                {
                    bytesRead = await stream.ReadAtAsync(position, target, Ct);
                }
                else
                {
                    stream.Position = position;
                    bytesRead = await stream.ReadAsync(target, Ct);
                    Assert.Equal(position + bytesRead, stream.Position);
                }

                string context = $"{name} (position {position}, count {count}, mode {mode}, limit {limit}, variant {variant})";
                Assert.True(expectedCount == bytesRead, $"{context}: read {bytesRead}, expected {expectedCount}");
                Assert.True(buffer.AsSpan(3, bytesRead).SequenceEqual(expected.AsSpan((int)Math.Min(position, length), bytesRead)), $"{context}: wrong data");
                Assert.True(buffer.AsSpan(0, 3).IndexOfAnyExcept(Guard) < 0, $"{context}: wrote before the buffer");
                Assert.True(buffer.AsSpan(3 + bytesRead).IndexOfAnyExcept(Guard) < 0, $"{context}: wrote past the bytes read");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task The_parts_of_one_read_load_concurrently(string mode)
    {
        using var dir = new TempDirectory();
        var cdn = new FakeCdn();
        // a read that requests one chunk after the other never gets past this
        cdn.HoldUntilInFlight(2);
        using HttpClient client = cdn.CreateClient();
        FFileManifest file = TestData.GetFile(TestData.Parse(client, o => Configure(o, mode, dir.Path)));
        byte[] actual = new byte[file.FileSize];

        using CancellationTokenSource timeout = Wait.Timeout(10);
        Exception? error = await Record.ExceptionAsync(async () => Assert.Equal(actual.Length, await file.GetStream().ReadAtAsync(0, actual, timeout.Token)));

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
        Assert.Equal(16, FFileManifestStream.DefaultMaxConcurrentPartReads);

        // slow requests, so that a missing bound would pile them up
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
        FFileManifest file = TestData.CreateFile(TestData.Parse(client), TestData.DistinctChunkParts(limit * 3, 4096));
        FFileManifestStream stream = file.GetStream();
        Assert.Equal(FFileManifestStream.DefaultMaxConcurrentPartReads, stream.MaxConcurrentPartReads);
        stream.MaxConcurrentPartReads = limit;
        byte[] actual = new byte[file.FileSize];

        using CancellationTokenSource timeout = Wait.Timeout(10);
        Assert.Equal(actual.Length, await stream.ReadAtAsync(0, actual, timeout.Token));

        Assert.Equal(TestData.ExpectedFile(file), actual);
        Assert.Equal(limit, cdn.PeakInFlight);
        Assert.Equal(limit * 3, cdn.Requests);
    }

    [Fact]
    public async Task A_read_within_one_part_issues_exactly_one_request()
    {
        var cdn = new FakeCdn();
        using HttpClient client = cdn.CreateClient();
        FFileManifest file = TestData.GetFile(TestData.Parse(client, o => o.ChunkMemoryCacheCapacity = 0));
        byte[] expected = TestData.ExpectedFile(file);
        FChunkPart part = file.ChunkParts[1];
        FChunkPart last = file.ChunkParts[^1];
        FFileManifestStream stream = file.GetStream();

        (FChunkPart Part, long Position, int Count, int Expected)[] reads =
        [
            (part, part.FileOffset + 10, 100, 100),
            (part, part.FileOffset, (int)part.Size, (int)part.Size),
            (part, part.FileOffset + part.Size - 7, 7, 7),
            // the last part, crossing the end of the file
            (last, last.FileOffset + 5, (int)last.Size + 100, (int)last.Size - 5)
        ];

        foreach ((FChunkPart chunkPart, long position, int count, int expectedCount) in reads)
        {
            int requests = cdn.Requests;
            int chunkRequests = cdn.RequestsFor(chunkPart.Chunk.Guid);
            byte[] buffer = new byte[count];

            Assert.Equal(expectedCount, await stream.ReadAtAsync(position, buffer, Ct));

            Assert.True(buffer.AsSpan(0, expectedCount).SequenceEqual(expected.AsSpan((int)position, expectedCount)));
            Assert.Equal(1, cdn.Requests - requests);
            Assert.Equal(1, cdn.RequestsFor(chunkPart.Chunk.Guid) - chunkRequests);
        }

        Assert.Equal(1, cdn.PeakInFlight);
    }

    [Fact]
    public async Task A_failing_part_cancels_the_others_and_the_call_waits_for_them()
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

        using CancellationTokenSource timeout = Wait.Timeout(10);
        Exception? error = await Record.ExceptionAsync(() => file.GetStream().ReadAtAsync(0, new byte[file.FileSize], timeout.Token).AsTask());
        int inFlight = cdn.InFlight;

        // not the cancellation of the slow part, which the failure caused
        HttpRequestException ex = Assert.IsType<HttpRequestException>(error);
        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        Assert.Equal(0, inFlight);
        Assert.False(timeout.IsCancellationRequested);
        Assert.Equal(1, cdn.RequestsFor(missingChunk));
        Assert.Equal(1, cdn.RequestsFor(slowChunk));
    }

    [Fact]
    public async Task Caller_cancellation_cancels_every_part_and_the_call_waits_for_them()
    {
        var cdn = new FakeCdn { Behavior = static (_, token) => FakeCdn.HangUntilCancelledAsync(SlowStop, token) };
        using HttpClient client = cdn.CreateClient();
        FFileManifest file = TestData.GetFile(TestData.Parse(client, o => o.DownloadIdleTimeout = Timeout.InfiniteTimeSpan));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        Task<int> read = file.GetStream().ReadAtAsync(0, new byte[file.FileSize], cts.Token).AsTask();
        await Wait.UntilAsync(() => cdn.InFlight >= 2);
        cts.Cancel();
        Exception? error = await Record.ExceptionAsync(() => read);
        int inFlight = cdn.InFlight;

        Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.Equal(0, inFlight);
    }

    [Theory]
    [InlineData("cache-directory", FFileManifestStream.DefaultMaxConcurrentPartReads)]
    [InlineData("memory-cache-capacity-2", FFileManifestStream.DefaultMaxConcurrentPartReads)]
    [InlineData("no-cache", FFileManifestStream.DefaultMaxConcurrentPartReads)]
    [InlineData("memory-cache-capacity-2", 3)]
    public async Task Concurrent_multi_part_reads_on_one_stream_return_correct_data(string mode, int limit)
    {
        using var dir = new TempDirectory();
        using HttpClient client = new FakeCdn().CreateClient();
        FFileManifest file = TestData.CreateFile(TestData.Parse(client, o => Configure(o, mode, dir.Path)), TestData.ManySmallParts(400, 8, 4000, seed: 2));
        FFileManifestStream stream = file.GetStream();
        stream.MaxConcurrentPartReads = limit;
        byte[] expected = TestData.ExpectedFile(file);

        await Task.WhenAll(Enumerable.Range(0, 16).Select(worker => Task.Run(async () =>
        {
            var rng = new Random(worker);
            byte[] buffer = new byte[24 * 1024];
            for (int i = 0; i < 15; i++)
            {
                long position = rng.NextInt64(expected.Length);
                int count = rng.Next(1, buffer.Length);
                int expectedCount = (int)Math.Min(count, expected.Length - position);
                buffer.AsSpan().Fill(Guard);

                int bytesRead = await stream.ReadAtAsync(position, buffer.AsMemory(0, count), Ct);

                Assert.Equal(expectedCount, bytesRead);
                Assert.True(buffer.AsSpan(0, bytesRead).SequenceEqual(expected.AsSpan((int)position, bytesRead)));
                Assert.True(buffer.AsSpan(bytesRead).IndexOfAnyExcept(Guard) < 0);
            }
        }, Ct)));
    }

    [Fact]
    public async Task A_read_over_thousands_of_parts_allocates_no_more_than_one_over_a_hundred()
    {
        using HttpClient client = new FakeCdn().CreateClient();
        FFileManifest file = TestData.CreateFile(TestData.Parse(client), TestData.ManySmallParts(2000, 4, 64, seed: 3));
        IReadOnlyList<FChunkPart> parts = file.ChunkParts;
        FFileManifestStream stream = file.GetStream();
        byte[] expected = TestData.ExpectedFile(file);
        byte[] buffer = new byte[expected.Length];

        // fills the memory cache, after that every part is copied synchronously on this thread
        Assert.Equal(buffer.Length, await stream.ReadAtAsync(0, buffer, Ct));
        Assert.Equal(expected, buffer);

        long hundred = Allocated(parts[100].FileOffset, (int)(parts[200].FileOffset - parts[100].FileOffset));
        long thousands = Allocated(0, buffer.Length);

        Assert.True(thousands <= hundred, $"2000 parts: {thousands} B, 100 parts: {hundred} B");
        Assert.True(thousands < 2048, $"2000 parts: {thousands} B");
        Assert.Equal(expected, buffer);

        long Allocated(long position, int count)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            ValueTask<int> read = stream.ReadAtAsync(position, buffer.AsMemory((int)position, count), Ct);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.True(read.IsCompletedSuccessfully);
            Assert.Equal(count, read.Result);
            return allocated;
        }
    }

    private static void Configure(ManifestParseOptions options, string mode, string directory)
    {
        switch (mode)
        {
            case "cache-directory":
                options.ChunkCacheDirectory = directory;
                break;
            case "memory-cache":
                break;
            case "memory-cache-capacity-2":
                options.ChunkMemoryCacheCapacity = 2;
                break;
            case "no-cache":
                options.ChunkMemoryCacheCapacity = 0;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
    }
}
