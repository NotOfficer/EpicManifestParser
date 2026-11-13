namespace EpicManifestParser.Tests.Fixtures;

internal static class Wait
{
    /// <summary>A token that is cancelled after <paramref name="seconds"/> or with the test.</summary>
    public static CancellationTokenSource Timeout(int seconds)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(seconds));
        return cts;
    }

    /// <summary>Polls <paramref name="condition"/> until it holds; throws <see cref="TimeoutException"/> after 10 seconds.</summary>
    public static async Task UntilAsync(Func<bool> condition)
    {
        var timeout = TimeSpan.FromSeconds(10);
        long start = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - start > timeout.TotalMilliseconds)
                throw new TimeoutException("condition not met within 10 seconds");
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }
}
