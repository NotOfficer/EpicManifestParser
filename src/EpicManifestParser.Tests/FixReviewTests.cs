using System.Net;

using EpicManifestParser.Tests.Fixtures;
using EpicManifestParser.UE;

namespace EpicManifestParser.Tests;

/// <summary>
/// Cases the review of the v3 fixes found: a cache file that cannot be replaced, temporary file names next to long file names,
/// and a <see cref="MemoryStream"/> destination of a save that cannot start or fails.
/// </summary>
public class FixReviewTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<byte[]> ReadAsync(FFileManifest file, bool sync, long position, int count)
    {
        byte[] buffer = new byte[count];
        FFileManifestStream stream = file.GetStream();
        int read = sync ? stream.ReadAt(position, buffer.AsSpan()) : await stream.ReadAtAsync(position, buffer.AsMemory(), Ct);
        Assert.Equal(count, read);
        return buffer;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_cache_file_that_cannot_be_replaced_is_not_trusted(bool sync)
    {
        using var dir = new TempDirectory();
        var cdn = new FakeCdn();
        using HttpClient client = cdn.CreateClient();
        FFileManifest file = TestData.GetFile(TestData.Parse(client, o => o.ChunkCacheDirectory = dir.Path));
        FChunkPart part = file.ChunkParts[0];
        string cachePath = TestData.CacheFilePath(dir.Path, part.Chunk);

        // a file of the wrong length, so it is rejected, whose bytes cover the range that is read
        byte[] stale = new byte[part.Chunk.DataSizeUncompressed - 1];
        stale.AsSpan().Fill(0xAA);
        int count = (int)Math.Min(part.Size, 4096);
        Assert.True(part.Offset + count < stale.Length);
        await File.WriteAllBytesAsync(cachePath, stale, Ct);
        // read-only: on Windows it can neither be deleted nor replaced by the rename
        File.SetAttributes(cachePath, FileAttributes.ReadOnly);

        try
        {
            byte[] expected = TestData.ExpectedFile(file).AsSpan((int)part.FileOffset, count).ToArray();

            byte[] first = await ReadAsync(file, sync, part.FileOffset, count);
            // the write of the downloaded chunk failed; the file that is still there must not count as that chunk
            byte[] second = await ReadAsync(file, sync, part.FileOffset, count);

            Assert.Equal(expected, first);
            Assert.Equal(expected, second);
            Assert.DoesNotContain(dir.Files, f => f.EndsWith(".tmp", StringComparison.Ordinal));
            if (OperatingSystem.IsWindows())
            {
                // elsewhere the rename replaces a read-only file, which heals the cache
                Assert.Equal(stale.Length, new FileInfo(cachePath).Length);
                Assert.Equal(2, cdn.RequestsFor(part.Chunk.Guid));
            }
        }
        finally
        {
            File.SetAttributes(cachePath, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task SaveFileAsync_works_for_a_file_name_near_the_length_limit()
    {
        using var dir = new TempDirectory();
        using HttpClient client = new FakeCdn().CreateClient();
        FFileManifest file = TestData.GetFile(TestData.Parse(client));
        // valid by itself, too long with the ".{16 hex digits}.tmp" suffix of the temporary file
        string path = Path.Combine(dir.Path, new string('n', 240) + ".bin");

        await file.GetStream().SaveFileAsync(path, cancellationToken: Ct);

        Assert.Equal(TestData.ExpectedFile(file), await File.ReadAllBytesAsync(path, Ct));
        Assert.Equal([path], dir.Files);
    }

    [Theory]
    [InlineData("n", 10, true)]
    [InlineData("n", 234, true)]
    [InlineData("n", 235, false)]
    [InlineData("n", 255, false)]
    // 3 bytes per character in UTF-8, which is what counts on Linux file systems
    [InlineData("中", 78, true)]
    [InlineData("中", 79, false)]
    public void Temporary_file_names_fit_the_file_name_limit(string character, int nameLength, bool keepsName)
    {
        string directory = Path.Combine(Path.GetTempPath(), "emp");
        string name = string.Concat(Enumerable.Repeat(character, nameLength));
        string path = Path.Combine(directory, name);

        string temp = AtomicFile.GetTempPath(path);
        string tempName = Path.GetFileName(temp);

        Assert.Equal(directory, Path.GetDirectoryName(temp));
        Assert.EndsWith(".tmp", tempName);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(tempName) <= 255, $"{tempName.Length} characters");
        Assert.Equal(keepsName, tempName.StartsWith(name + ".", StringComparison.Ordinal));
        Assert.NotEqual(temp, AtomicFile.GetTempPath(path));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SaveToAsync_that_cannot_start_leaves_a_MemoryStream_untouched(bool cancelled)
    {
        var cdn = new FakeCdn();
        using HttpClient client = cdn.CreateClient();
        FFileManifest file = TestData.GetFile(TestData.Parse(client));
        using var destination = new MemoryStream();
        destination.Write([1, 2, 3]);
        // behind the end: SetLength would pull the position back when the length is restored
        destination.Position = 10;
        using var cts = new CancellationTokenSource();

        if (cancelled)
        {
            await cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => file.GetStream().SaveToAsync(destination, cancellationToken: cts.Token));
        }
        else
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => file.GetStream().SaveToAsync(destination, 0, cts.Token));
        }

        Assert.Equal(3, destination.Length);
        Assert.Equal(10, destination.Position);
        Assert.Equal(new byte[] { 1, 2, 3 }, destination.ToArray());
        Assert.Equal(0, cdn.Requests);
    }

    [Fact]
    public async Task SaveToAsync_that_fails_restores_length_and_position_of_a_MemoryStream()
    {
        var cdn = new FakeCdn
        {
            Behavior = static (_, _) => Task.FromResult<HttpResponseMessage?>(new HttpResponseMessage(HttpStatusCode.NotFound))
        };
        using HttpClient client = cdn.CreateClient();
        FFileManifest file = TestData.GetFile(TestData.Parse(client, o => o.MaxDownloadRetries = 0));
        using var destination = new MemoryStream();
        destination.Write([1, 2, 3]);
        destination.Position = 10;

        await Assert.ThrowsAsync<HttpRequestException>(() => file.GetStream().SaveToAsync(destination, cancellationToken: Ct));

        Assert.Equal(3, destination.Length);
        Assert.Equal(10, destination.Position);
        Assert.Equal(new byte[] { 1, 2, 3 }, destination.ToArray());
    }

    [Fact]
    public async Task SaveToAsync_that_fails_over_MemoryStream_content_leaves_a_prefix_of_the_file()
    {
        var cdn = new FakeCdn();
        using HttpClient client = cdn.CreateClient();
        FFileManifest file = TestData.GetFile(TestData.Parse(client, o => o.MaxDownloadRetries = 0));
        FGuid first = file.ChunkParts[0].Chunk.Guid;
        // the first part fails after the others had time to finish: in parallel they would land in the old content
        cdn.Behavior = async (request, token) =>
        {
            if (request.Chunk != first)
                return null;
            await Task.Delay(300, token);
            return FakeCdn.Status(HttpStatusCode.NotFound);
        };
        byte[] old = new byte[file.FileSize + 100];
        old.AsSpan().Fill(0xEE);
        using var destination = new MemoryStream();
        destination.Write(old);
        destination.Position = 0;

        await Assert.ThrowsAsync<HttpRequestException>(() => file.GetStream().SaveToAsync(destination, cancellationToken: Ct));

        Assert.Equal(old.Length, destination.Length);
        Assert.Equal(old, destination.ToArray());
        Assert.Equal(0, destination.Position);
    }
}
