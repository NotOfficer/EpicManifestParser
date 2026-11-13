using System.Net;

using EpicManifestParser.Tests.Fixtures;
using EpicManifestParser.UE;

namespace EpicManifestParser.Tests;

/// <summary>
/// <see cref="FFileManifestStream.SaveFileAsync(string,Action{SaveProgressChangedEventArgs}?,object?,int?,CancellationToken)"/>
/// writes a temporary file and replaces the destination only when everything has been written.
/// </summary>
public class SaveFileTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>How long a cancelled request takes to stop, see <see cref="FakeCdn.HangUntilCancelledAsync"/>.</summary>
    private static readonly TimeSpan SlowStop = TimeSpan.FromMilliseconds(200);

    private static readonly byte[] OldContent = "the old file, which must survive a failed save"u8.ToArray();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_successful_save_replaces_the_file(bool exists)
    {
        using var dir = new TempDirectory();
        using HttpClient client = new FakeCdn().CreateClient();
        FFileManifest file = TestData.GetFile(TestData.Parse(client));
        string path = Path.Combine(dir.Path, "file.bin");
        if (exists)
            await File.WriteAllBytesAsync(path, new byte[file.FileSize * 2], Ct);

        await file.GetStream().SaveFileAsync(path, cancellationToken: Ct);

        Assert.Equal(TestData.ExpectedFile(file), await File.ReadAllBytesAsync(path, Ct));
        Assert.Equal([path], dir.Files);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public async Task An_invalid_maxDegreeOfParallelism_leaves_the_file_untouched(int maxDegreeOfParallelism)
    {
        using var dir = new TempDirectory();
        var cdn = new FakeCdn();
        using HttpClient client = cdn.CreateClient();
        FFileManifest file = TestData.GetFile(TestData.Parse(client));
        string path = await CreateOldFileAsync(dir);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => file.GetStream().SaveFileAsync(path, maxDegreeOfParallelism, Ct));

        await AssertUntouchedAsync(dir, path);
        Assert.Equal(0, cdn.Requests);
    }

    [Fact]
    public async Task A_cancelled_token_leaves_the_file_untouched()
    {
        using var dir = new TempDirectory();
        var cdn = new FakeCdn();
        using HttpClient client = cdn.CreateClient();
        FFileManifest file = TestData.GetFile(TestData.Parse(client));
        string path = await CreateOldFileAsync(dir);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => file.GetStream().SaveFileAsync(path, cancellationToken: cts.Token));

        await AssertUntouchedAsync(dir, path);
        Assert.Equal(0, cdn.Requests);
    }

    [Fact]
    public async Task A_failing_save_leaves_the_file_untouched_and_no_temporary_file()
    {
        using var dir = new TempDirectory();
        var cdn = new FakeCdn();
        using HttpClient client = cdn.CreateClient();
        FFileManifest file = TestData.GetFile(TestData.Parse(client));
        FGuid missing = file.ChunkParts[^1].Chunk.Guid;
        cdn.Behavior = (request, _) => Task.FromResult(request.Chunk == missing ? FakeCdn.Status(HttpStatusCode.NotFound) : null);
        string path = await CreateOldFileAsync(dir);

        HttpRequestException ex = await Assert.ThrowsAsync<HttpRequestException>(() => file.GetStream().SaveFileAsync(path, 2, Ct));

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        await AssertUntouchedAsync(dir, path);
    }

    [Fact]
    public async Task Cancellation_during_the_save_leaves_the_file_untouched_and_no_temporary_file()
    {
        using var dir = new TempDirectory();
        var cdn = new FakeCdn();
        using HttpClient client = cdn.CreateClient();
        FFileManifest file = TestData.GetFile(TestData.Parse(client, o => o.DownloadIdleTimeout = Timeout.InfiniteTimeSpan));
        FGuid slow = file.ChunkParts[^1].Chunk.Guid;
        cdn.Behavior = (request, token) => request.Chunk == slow ? FakeCdn.HangUntilCancelledAsync(SlowStop, token) : Task.FromResult<HttpResponseMessage?>(null);
        string path = await CreateOldFileAsync(dir);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        Task save = file.GetStream().SaveFileAsync(path, cancellationToken: cts.Token);
        // the temporary file exists while the save runs, the destination is the old file
        await Wait.UntilAsync(() => cdn.RequestsFor(slow) == 1 && dir.Files.Length == 2);
        await AssertUntouchedContentAsync(path);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => save);

        await AssertUntouchedAsync(dir, path);
    }

    [Fact]
    public async Task Progress_events_add_up_to_the_file()
    {
        using var dir = new TempDirectory();
        using HttpClient client = new FakeCdn().CreateClient();
        FFileManifest file = TestData.CreateFile(TestData.Parse(client), TestData.ManySmallParts(300, 6, 3000, seed: 8));
        string path = Path.Combine(dir.Path, "file.bin");
        var progress = new List<SaveProgressChangedEventArgs>();

        await file.GetStream().SaveFileAsync(path, progress.Add, cancellationToken: Ct);

        Assert.Equal(TestData.ExpectedFile(file), await File.ReadAllBytesAsync(path, Ct));
        ProgressTests.AssertConsistent(progress, file.FileSize);
    }

    private static async Task<string> CreateOldFileAsync(TempDirectory dir)
    {
        string path = Path.Combine(dir.Path, "file.bin");
        await File.WriteAllBytesAsync(path, OldContent, Ct);
        return path;
    }

    /// <summary>The destination has its old content and is the only file in the directory.</summary>
    private static async Task AssertUntouchedAsync(TempDirectory dir, string path)
    {
        await AssertUntouchedContentAsync(path);
        Assert.Equal([path], dir.Files);
    }

    private static async Task AssertUntouchedContentAsync(string path) => Assert.Equal(OldContent, await File.ReadAllBytesAsync(path, Ct));
}
