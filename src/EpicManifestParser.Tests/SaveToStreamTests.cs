using System.Net;

using EpicManifestParser.Tests.Fixtures;
using EpicManifestParser.UE;

namespace EpicManifestParser.Tests;

/// <summary>
/// <see cref="FFileManifestStream.SaveToAsync(Stream,Action{SaveProgressChangedEventArgs}?,object?,int?,CancellationToken)"/> into a
/// stream that is not exactly a <see cref="MemoryStream"/>: chunk parts are read ahead, the stream is written in file order.
/// </summary>
public class SaveToStreamTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>How long a cancelled request takes to stop, see <see cref="FakeCdn.HangUntilCancelledAsync"/>.</summary>
    private static readonly TimeSpan SlowStop = TimeSpan.FromMilliseconds(200);

    private const int PartSize = 4096;

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(16)]
    public async Task Writes_the_file_in_order_one_part_per_write_and_reports_progress(int maxDegreeOfParallelism)
    {
        var cdn = new FakeCdn();
        using HttpClient client = cdn.CreateClient();
        FBuildPatchAppManifest manifest = TestData.Parse(client);

        foreach (FFileManifest file in new[] { TestData.GetFile(manifest), TestData.CreateFile(manifest, TestData.ManySmallParts(300, 6, 3000, seed: 4)) })
        {
            var destination = new RecordingStream();
            var progress = new List<SaveProgressChangedEventArgs>();
            object state = new();

            await file.GetStream().SaveToAsync(destination, progress.Add, state, maxDegreeOfParallelism, Ct);

            Assert.Equal(TestData.ExpectedFile(file), destination.Written);
            Assert.Equal(file.ChunkParts.Select(p => (int)p.Size), destination.WriteSizes);
            Assert.Equal(1, destination.Flushes);

            Assert.NotEmpty(progress);
            Assert.All(progress, e => Assert.Same(state, e.UserState));
            Assert.All(progress, e => Assert.Equal(file.FileSize, e.TotalBytesToWrite));
            for (int i = 1; i < progress.Count; i++)
            {
                Assert.True(progress[i].TotalBytesWritten > progress[i - 1].TotalBytesWritten);
                Assert.True(progress[i].ProgressPercentage > progress[i - 1].ProgressPercentage);
            }
            Assert.Equal(file.FileSize, progress[^1].TotalBytesWritten);
            Assert.Equal(100, progress[^1].ProgressPercentage);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(16)]
    public async Task Reads_ahead_at_most_maxDegreeOfParallelism_parts(int maxDegreeOfParallelism)
    {
        // slow requests, so that a missing bound would pile them up
        var cdn = new FakeCdn
        {
            Behavior = static async (_, token) =>
            {
                await Task.Delay(5, token);
                return null;
            }
        };
        // a save that does not read ahead never gets past this
        if (maxDegreeOfParallelism > 1)
            cdn.HoldUntilInFlight(maxDegreeOfParallelism);
        using HttpClient client = cdn.CreateClient();
        FFileManifest file = TestData.CreateFile(TestData.Parse(client), TestData.DistinctChunkParts(40, PartSize));
        var destination = new RecordingStream();

        using CancellationTokenSource timeout = Wait.Timeout(10);
        await file.GetStream().SaveToAsync(destination, maxDegreeOfParallelism, timeout.Token);

        Assert.Equal(TestData.ExpectedFile(file), destination.Written);
        Assert.Equal(maxDegreeOfParallelism, cdn.PeakInFlight);
        Assert.Equal(40, cdn.Requests);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(16)]
    public async Task A_failing_chunk_stops_the_save_and_leaves_a_correct_prefix(int maxDegreeOfParallelism)
    {
        var cdn = new FakeCdn();
        using HttpClient client = cdn.CreateClient();
        FFileManifest file = TestData.CreateFile(TestData.Parse(client), TestData.DistinctChunkParts(12, PartSize));
        FGuid missing = file.ChunkParts[6].Chunk.Guid;
        HashSet<FGuid> hanging = [.. file.ChunkParts.Skip(7).Select(p => p.Chunk.Guid)];
        var hangingInFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // parts behind the missing one never arrive; with read-ahead the 404 only comes once one of them is in flight
        cdn.Behavior = async (request, token) =>
        {
            if (request.Chunk == missing)
            {
                if (maxDegreeOfParallelism > 1)
                    await hangingInFlight.Task.WaitAsync(token);
                return FakeCdn.Status(HttpStatusCode.NotFound);
            }

            if (!hanging.Contains(request.Chunk!.Value))
                return null;

            hangingInFlight.TrySetResult();
            return await FakeCdn.HangUntilCancelledAsync(SlowStop, token);
        };
        var destination = new RecordingStream();

        using CancellationTokenSource timeout = Wait.Timeout(10);
        Exception? error = await Record.ExceptionAsync(() => file.GetStream().SaveToAsync(destination, maxDegreeOfParallelism, timeout.Token));
        int inFlight = cdn.InFlight;

        HttpRequestException ex = Assert.IsType<HttpRequestException>(error);
        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        Assert.Equal(0, inFlight);
        Assert.False(timeout.IsCancellationRequested);
        AssertPrefix(file, destination, maxParts: 6);
        Assert.Equal(0, destination.Flushes);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(16)]
    public async Task A_failing_destination_stops_the_save_and_leaves_a_correct_prefix(int maxDegreeOfParallelism)
    {
        var cdn = new FakeCdn();
        using HttpClient client = cdn.CreateClient();
        FFileManifest file = TestData.CreateFile(TestData.Parse(client), TestData.DistinctChunkParts(12, PartSize));
        HashSet<FGuid> hanging = [.. file.ChunkParts.Skip(3).Select(p => p.Chunk.Guid)];
        // with read-ahead, the parts after the third one are in flight when the third write fails
        cdn.Behavior = (request, token) => hanging.Contains(request.Chunk!.Value)
            ? FakeCdn.HangUntilCancelledAsync(SlowStop, token)
            : Task.FromResult<HttpResponseMessage?>(null);
        var destination = new RecordingStream { FailOnWrite = 3 };

        using CancellationTokenSource timeout = Wait.Timeout(10);
        Exception? error = await Record.ExceptionAsync(() => file.GetStream().SaveToAsync(destination, maxDegreeOfParallelism, timeout.Token));
        int inFlight = cdn.InFlight;

        Assert.Same(destination.WriteFailure, error);
        Assert.Equal(0, inFlight);
        Assert.False(timeout.IsCancellationRequested);
        Assert.Equal(2, destination.WriteSizes.Length);
        AssertPrefix(file, destination, maxParts: 2);
        Assert.Equal(0, destination.Flushes);
    }

    [Fact]
    public async Task A_throwing_progress_callback_stops_the_save()
    {
        var cdn = new FakeCdn();
        using HttpClient client = cdn.CreateClient();
        FFileManifest file = TestData.CreateFile(TestData.Parse(client), TestData.DistinctChunkParts(12, PartSize));
        HashSet<FGuid> hanging = [.. file.ChunkParts.Skip(3).Select(p => p.Chunk.Guid)];
        cdn.Behavior = (request, token) => hanging.Contains(request.Chunk!.Value)
            ? FakeCdn.HangUntilCancelledAsync(SlowStop, token)
            : Task.FromResult<HttpResponseMessage?>(null);
        var destination = new RecordingStream();
        var failure = new InvalidOperationException("progress callback failed");

        using CancellationTokenSource timeout = Wait.Timeout(10);
        Exception? error = await Record.ExceptionAsync(() => file.GetStream().SaveToAsync(destination, e =>
        {
            if (e.TotalBytesWritten >= 2 * PartSize)
                throw failure;
        }, null, 4, timeout.Token));
        int inFlight = cdn.InFlight;

        Assert.Same(failure, error);
        Assert.Equal(0, inFlight);
        Assert.False(timeout.IsCancellationRequested);
        AssertPrefix(file, destination, maxParts: 2);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task Caller_cancellation_stops_the_save_and_leaves_a_correct_prefix(int maxDegreeOfParallelism)
    {
        var cdn = new FakeCdn();
        using HttpClient client = cdn.CreateClient();
        FFileManifest file = TestData.CreateFile(TestData.Parse(client, o => o.DownloadIdleTimeout = Timeout.InfiniteTimeSpan),
            TestData.DistinctChunkParts(12, PartSize));
        HashSet<FGuid> hanging = [.. file.ChunkParts.Skip(2).Select(p => p.Chunk.Guid)];
        cdn.Behavior = (request, token) => hanging.Contains(request.Chunk!.Value)
            ? FakeCdn.HangUntilCancelledAsync(SlowStop, token)
            : Task.FromResult<HttpResponseMessage?>(null);
        var destination = new RecordingStream();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        Task save = file.GetStream().SaveToAsync(destination, maxDegreeOfParallelism, cts.Token);
        await Wait.UntilAsync(() => destination.WriteSizes.Length == 2 && cdn.InFlight >= 1);
        cts.Cancel();
        Exception? error = await Record.ExceptionAsync(() => save);
        int inFlight = cdn.InFlight;

        Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.Equal(0, inFlight);
        Assert.Equal(2, destination.WriteSizes.Length);
        AssertPrefix(file, destination, maxParts: 2);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(16)]
    public async Task An_empty_file_writes_nothing(int? maxDegreeOfParallelism)
    {
        var cdn = new FakeCdn();
        using HttpClient client = cdn.CreateClient();
        FFileManifest file = TestData.CreateFile(TestData.Parse(client), []);
        var destination = new RecordingStream();
        var progress = new List<SaveProgressChangedEventArgs>();

        await file.GetStream().SaveToAsync(destination, progress.Add, null, maxDegreeOfParallelism, Ct);

        Assert.Empty(destination.WriteSizes);
        Assert.Empty(progress);
        Assert.Equal(1, destination.Flushes);
        Assert.Equal(0, cdn.Requests);
    }

    /// <summary>
    /// <see cref="ParallelOptions.MaxDegreeOfParallelism"/>, which the other save methods go through, rejects <c>0</c> and values
    /// below <c>-1</c>; the generic SaveToAsync path does the same.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    [InlineData(int.MinValue)]
    public async Task maxDegreeOfParallelism_of_0_or_below_minus_1_is_rejected_by_every_save_method(int maxDegreeOfParallelism)
    {
        using var dir = new TempDirectory();
        var cdn = new FakeCdn();
        using HttpClient client = cdn.CreateClient();
        FFileManifestStream stream = TestData.GetFile(TestData.Parse(client)).GetStream();
        FFileManifestStream empty = TestData.CreateFile(TestData.Parse(client), []).GetStream();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => stream.SaveToAsync(new RecordingStream(), maxDegreeOfParallelism, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => empty.SaveToAsync(new RecordingStream(), maxDegreeOfParallelism, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => stream.SaveToAsync(new MemoryStream(), maxDegreeOfParallelism, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => stream.SaveBytesAsync(maxDegreeOfParallelism, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => stream.SaveFileAsync(Path.Combine(dir.Path, "file.bin"), maxDegreeOfParallelism, Ct));
        Assert.Equal(0, cdn.Requests);
    }

    /// <summary><c>-1</c> means <see cref="Environment.ProcessorCount"/>, as for <see cref="ParallelOptions.MaxDegreeOfParallelism"/>.</summary>
    [Fact]
    public async Task maxDegreeOfParallelism_of_minus_1_is_the_default_for_every_save_method()
    {
        using var dir = new TempDirectory();
        using HttpClient client = new FakeCdn().CreateClient();
        FFileManifest file = TestData.GetFile(TestData.Parse(client));
        byte[] expected = TestData.ExpectedFile(file);
        string path = Path.Combine(dir.Path, "file.bin");

        var destination = new RecordingStream();
        await file.GetStream().SaveToAsync(destination, -1, Ct);
        var ms = new MemoryStream();
        await file.GetStream().SaveToAsync(ms, -1, Ct);
        byte[] bytes = await file.GetStream().SaveBytesAsync(-1, Ct);
        await file.GetStream().SaveFileAsync(path, -1, Ct);

        Assert.Equal(expected, destination.Written);
        Assert.Equal(expected, ms.ToArray());
        Assert.Equal(expected, bytes);
        Assert.Equal(expected, await File.ReadAllBytesAsync(path, Ct));
    }

    /// <summary>What was written consists of whole parts in file order, at most <paramref name="maxParts"/> of them.</summary>
    private static void AssertPrefix(FFileManifest file, RecordingStream destination, int maxParts)
    {
        int[] sizes = destination.WriteSizes;
        byte[] written = destination.Written;

        Assert.InRange(sizes.Length, 0, maxParts);
        Assert.Equal(file.ChunkParts.Take(sizes.Length).Select(p => (int)p.Size), sizes);
        Assert.True(TestData.ExpectedFile(file).AsSpan(0, written.Length).SequenceEqual(written), "the written bytes are not a prefix of the file");
    }
}
