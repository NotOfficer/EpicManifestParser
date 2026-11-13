using EpicManifestParser.Tests.Fixtures;
using EpicManifestParser.UE;

namespace EpicManifestParser.Tests;

public class HashTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4096)]
    [InlineData(3 * 1024 * 1024 + 123)]
    public async Task ComputeAsync_file_path_equals_Compute(int length)
    {
        using var dir = new TempDirectory();
        string path = Path.Combine(dir.Path, "data.bin");
        byte[] data = new byte[length];
        new Random(length).NextBytes(data);
        await File.WriteAllBytesAsync(path, data, Ct);

        Assert.Equal(FSHAHash.Compute(data), await FSHAHash.ComputeAsync(path, Ct));
        Assert.Equal(FMD5Hash.Compute(data), await FMD5Hash.ComputeAsync(path, Ct));
        Assert.Equal(FSHA256Hash.Compute(data), await FSHA256Hash.ComputeAsync(path, Ct));

        FileAssert.IsClosed(path);
    }

    [Fact]
    public async Task ComputeAsync_file_path_concurrent_calls()
    {
        using var dir = new TempDirectory();
        string path = Path.Combine(dir.Path, "data.bin");
        byte[] data = new byte[256 * 1024];
        new Random(3).NextBytes(data);
        await File.WriteAllBytesAsync(path, data, Ct);
        FSHAHash expected = FSHAHash.Compute(data);

        FSHAHash[] results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => FSHAHash.ComputeAsync(path, Ct)));

        Assert.All(results, actual => Assert.Equal(expected, actual));
    }

    [Fact]
    public async Task ComputeAsync_file_path_honors_cancellation()
    {
        using var dir = new TempDirectory();
        string path = Path.Combine(dir.Path, "data.bin");
        await File.WriteAllBytesAsync(path, new byte[3 * 1024 * 1024], Ct);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FSHAHash.ComputeAsync(path, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FMD5Hash.ComputeAsync(path, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FSHA256Hash.ComputeAsync(path, cts.Token));

        FileAssert.IsClosed(path);
    }
}
