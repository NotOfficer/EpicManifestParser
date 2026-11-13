using System.Buffers;

using EpicManifestParser.Tests.Fixtures;
using EpicManifestParser.UE;

namespace EpicManifestParser.Tests;

public class MemoryCacheTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const int ReadSize = 64 * 1024;

    [Fact]
    public async Task Sequential_small_reads_download_each_chunk_once()
    {
        var cdn = new FakeCdn();
        using HttpClient client = cdn.CreateClient();
        FFileManifest file = TestData.GetFile(TestData.Parse(client));
        byte[] expected = TestData.ExpectedFile(file);

        FFileManifestStream stream = file.GetStream();
        byte[] actual = new byte[file.FileSize];
        int total = 0, read;
        while ((read = stream.Read(actual.AsSpan(total, Math.Min(ReadSize, actual.Length - total)))) > 0)
            total += read;

        Assert.Equal(expected, actual);
        Assert.Equal(TestData.DistinctChunks(file).Length, cdn.Requests);

        // async reads on a second stream of the same manifest hit the same cache
        FFileManifestStream stream2 = file.GetStream();
        Array.Clear(actual);
        total = 0;
        while ((read = await stream2.ReadAsync(actual.AsMemory(total, Math.Min(ReadSize, actual.Length - total)), Ct)) > 0)
            total += read;

        Assert.Equal(expected, actual);
        Assert.Equal(TestData.DistinctChunks(file).Length, cdn.Requests);
    }

    [Fact]
    public async Task SaveBytesAsync_downloads_each_chunk_once()
    {
        var cdn = new FakeCdn();
        using HttpClient client = cdn.CreateClient();
        FFileManifest file = TestData.GetFile(TestData.Parse(client));

        byte[] actual = await file.GetStream().SaveBytesAsync(16, Ct);

        Assert.Equal(TestData.ExpectedFile(file), actual);
        Assert.True(file.ChunkParts.Count > TestData.DistinctChunks(file).Length, "the test file must reference a chunk twice");
        Assert.Equal(TestData.DistinctChunks(file).Length, cdn.Requests);
        foreach (FChunkInfo chunk in TestData.DistinctChunks(file))
            Assert.Equal(1, cdn.RequestsFor(chunk.Guid));
    }

    [Fact]
    public async Task Capacity_zero_downloads_once_per_chunk_part_read()
    {
        var cdn = new FakeCdn();
        using HttpClient client = cdn.CreateClient();
        FFileManifest file = TestData.GetFile(TestData.Parse(client, o => o.ChunkMemoryCacheCapacity = 0));
        byte[] expected = TestData.ExpectedFile(file);

        byte[] actual = await file.GetStream().SaveBytesAsync(16, Ct);
        Assert.Equal(expected, actual);
        Assert.Equal(file.ChunkParts.Count, cdn.Requests);

        int requestsBefore = cdn.Requests;
        FFileManifestStream stream = file.GetStream();
        int expectedRequests = 0;
        for (long position = 0; position < file.FileSize; position += ReadSize)
        {
            long end = Math.Min(position + ReadSize, file.FileSize);
            expectedRequests += file.ChunkParts.Count(p => p.FileOffset < end && p.FileOffset + p.Size > position);
            Assert.Equal((int)(end - position), stream.Read(actual.AsSpan((int)position, (int)(end - position))));
        }

        Assert.Equal(expected, actual);
        Assert.Equal(expectedRequests, cdn.Requests - requestsBefore);
    }

    [Fact]
    public async Task Capacity_smaller_than_the_chunk_count_still_returns_correct_data()
    {
        var cdn = new FakeCdn();
        using HttpClient client = cdn.CreateClient();
        FFileManifest file = TestData.GetFile(TestData.Parse(client, o => o.ChunkMemoryCacheCapacity = 1));
        byte[] expected = TestData.ExpectedFile(file);

        Assert.Equal(expected, await file.GetStream().SaveBytesAsync(16, Ct));
        Assert.Equal(expected, await file.GetStream().SaveBytesAsync(1, Ct));
    }

    [Fact]
    public void No_memory_cache_with_a_cache_directory_or_capacity_zero()
    {
        using var dir = new TempDirectory();
        using HttpClient client = new FakeCdn().CreateClient();

        Assert.NotNull(TestData.Parse(client).ChunkMemoryCache);
        Assert.Equal(32, TestData.Parse(client).ChunkMemoryCache!.Capacity);
        Assert.Null(TestData.Parse(client, o => o.ChunkMemoryCacheCapacity = 0).ChunkMemoryCache);
        Assert.Null(TestData.Parse(client, o => o.ChunkCacheDirectory = dir.Path).ChunkMemoryCache);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ManifestParseOptions { ChunkMemoryCacheCapacity = -1 });
    }

    [Fact]
    public void ChunkMemoryCache_evicts_the_least_recently_used_chunk()
    {
        var cache = new ChunkMemoryCache(2);
        FGuid a = new(1, 0, 0, 0), b = new(2, 0, 0, 0), c = new(3, 0, 0, 0);
        Span<byte> destination = stackalloc byte[4];

        cache.Add(a, Rent(1));
        cache.Add(b, Rent(2));
        Assert.True(cache.TryCopy(a, 0, destination)); // a is now more recent than b
        cache.Add(c, Rent(3));

        Assert.True(cache.Contains(a));
        Assert.False(cache.Contains(b));
        Assert.True(cache.Contains(c));
        Assert.True(cache.TryCopy(c, 2, destination));
        Assert.Equal([3, 3, 3, 3], destination.ToArray());
        Assert.False(cache.TryCopy(b, 0, destination));
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void ChunkMemoryCache_keeps_the_first_copy_of_a_chunk()
    {
        var cache = new ChunkMemoryCache(4);
        FGuid a = new(1, 0, 0, 0);
        Span<byte> destination = stackalloc byte[8];

        cache.Add(a, Rent(1));
        cache.Add(a, Rent(2));

        Assert.Equal(1, cache.Count);
        Assert.True(cache.TryCopy(a, 0, destination));
        Assert.Equal(1, destination[0]);
    }

    [Fact]
    public void ChunkMemoryCache_rejects_ranges_outside_the_chunk()
    {
        var cache = new ChunkMemoryCache(1);
        FGuid a = new(1, 0, 0, 0);
        cache.Add(a, Rent(1));
        byte[] destination = new byte[8];

        Assert.Throws<InvalidDataException>(() => cache.TryCopy(a, 9, destination));
        Assert.Throws<InvalidDataException>(() => cache.TryCopy(a, -1, destination));
        Assert.True(cache.TryCopy(a, 8, destination)); // exactly up to the end of the 16 bytes
        Assert.All(destination, b => Assert.Equal(1, b));
    }

    /// <summary>16 bytes of <paramref name="value"/> at offset 3 of a rented array.</summary>
    private static RentedBuffer Rent(byte value)
    {
        byte[] array = ArrayPool<byte>.Shared.Rent(32);
        array.AsSpan().Fill(0xEE);
        array.AsSpan(3, 16).Fill(value);
        return new RentedBuffer(array, 3, 16);
    }
}
