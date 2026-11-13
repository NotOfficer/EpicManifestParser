using EpicManifestParser.Tests.Fixtures;
using EpicManifestParser.UE;

namespace EpicManifestParser.Tests;

/// <summary>
/// <see cref="SaveProgressChangedEventArgs"/>: an event per percentage step and one for the last write, in integer math,
/// with <see cref="SaveProgressChangedEventArgs.BytesWritten"/> since the previous event.
/// </summary>
public class ProgressTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void A_file_just_above_1_GiB_reaches_100_percent_with_its_last_write()
    {
        // float rounds 2^30 + 32 to 2^30: the percentage used to reach 100 one part early, the real last event was suppressed
        const long size = (1L << 30) + 32;
        var progress = new List<SaveProgressChangedEventArgs>();
        DownloadState<int> state = CreateState(size, progress);

        for (int i = 0; i < 32; i++)
            state.OnBytesWritten(1 << 25);
        Assert.NotEqual(100, progress[^1].ProgressPercentage);
        state.OnBytesWritten(32);

        Assert.Equal(100, progress[^1].ProgressPercentage);
        Assert.Equal(size, progress[^1].TotalBytesWritten);
        Assert.Equal(32, progress[^1].BytesWritten);
        Assert.Single(progress, e => e.ProgressPercentage == 100);
        AssertConsistent(progress, size);
    }

    [Fact]
    public void BytesWritten_counts_every_write_since_the_previous_event()
    {
        var progress = new List<SaveProgressChangedEventArgs>();
        DownloadState<int> state = CreateState(1000, progress);

        // 0.3 % per write: an event every third or fourth write
        for (int i = 0; i < 333; i++)
            state.OnBytesWritten(3);
        state.OnBytesWritten(1);

        Assert.Equal(100, progress.Count);
        Assert.Equal(Enumerable.Range(1, 100), progress.Select(e => e.ProgressPercentage));
        AssertConsistent(progress, 1000);
    }

    [Fact]
    public void Empty_writes_after_the_last_one_raise_no_event()
    {
        var progress = new List<SaveProgressChangedEventArgs>();
        DownloadState<int> state = CreateState(100, progress);

        state.OnBytesWritten(0);
        state.OnBytesWritten(50);
        state.OnBytesWritten(50);
        state.OnBytesWritten(0);

        Assert.Equal([50, 50], progress.Select(e => e.BytesWritten));
        Assert.Equal([50, 100], progress.Select(e => e.ProgressPercentage));
    }

    [Theory]
    [InlineData(0, 1, 0)]
    [InlineData(1, 100, 1)]
    [InlineData(99, 100, 99)]
    [InlineData(100, 100, 100)]
    [InlineData((1L << 30) - 1, (1L << 30) + 32, 99)]
    [InlineData(long.MaxValue - 1, long.MaxValue, 99)]
    [InlineData(long.MaxValue, long.MaxValue, 100)]
    public void Percentages_are_rounded_down_in_integer_math(long done, long total, int expected)
    {
        Assert.Equal(expected, DownloadState<int>.GetPercentage(done, total));
    }

    [Theory]
    [InlineData("SaveToAsync-stream")]
    [InlineData("SaveToAsync-MemoryStream")]
    [InlineData("SaveBytesAsync")]
    public async Task The_events_of_a_save_add_up_to_the_file(string method)
    {
        using HttpClient client = new FakeCdn().CreateClient();
        FFileManifest file = TestData.CreateFile(TestData.Parse(client), TestData.ManySmallParts(400, 6, 3000, seed: 9));
        var progress = new List<SaveProgressChangedEventArgs>();
        FFileManifestStream stream = file.GetStream();

        switch (method)
        {
            case "SaveToAsync-stream":
                await stream.SaveToAsync(new RecordingStream(), progress.Add, cancellationToken: Ct);
                break;
            case "SaveToAsync-MemoryStream":
                await stream.SaveToAsync(new MemoryStream(), progress.Add, cancellationToken: Ct);
                break;
            case "SaveBytesAsync":
                await stream.SaveBytesAsync(progress.Add, cancellationToken: Ct);
                break;
        }

        AssertConsistent(progress, file.FileSize);
    }

    /// <summary>
    /// Strictly increasing, 100 % only in the last event, which reports the whole file, and the bytes of all events add up to it.
    /// </summary>
    internal static void AssertConsistent(IReadOnlyList<SaveProgressChangedEventArgs> progress, long fileSize)
    {
        Assert.NotEmpty(progress);
        Assert.All(progress, e => Assert.Equal(fileSize, e.TotalBytesToWrite));
        for (int i = 1; i < progress.Count; i++)
        {
            Assert.True(progress[i].TotalBytesWritten > progress[i - 1].TotalBytesWritten);
            Assert.True(progress[i].ProgressPercentage > progress[i - 1].ProgressPercentage);
            Assert.Equal(progress[i].TotalBytesWritten - progress[i - 1].TotalBytesWritten, progress[i].BytesWritten);
        }
        Assert.Equal(progress[0].TotalBytesWritten, progress[0].BytesWritten);
        Assert.Equal(fileSize, progress[^1].TotalBytesWritten);
        Assert.Equal(100, progress[^1].ProgressPercentage);
        Assert.Single(progress, e => e.ProgressPercentage == 100);
        Assert.Equal(fileSize, progress.Sum(e => e.BytesWritten));
    }

    private static DownloadState<int> CreateState(long fileSize, List<SaveProgressChangedEventArgs> progress)
    {
        var file = new FFileManifest { FileName = "progress.bin", FileSize = fileSize };
        return new DownloadState<int>(0, file, null, progress.Add);
    }
}
