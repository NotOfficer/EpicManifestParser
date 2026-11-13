using EpicManifestParser.Api;
using EpicManifestParser.Tests.Fixtures;
using EpicManifestParser.UE;

using OffiUtils;

namespace EpicManifestParser.Tests;

/// <summary>
/// The chunk lock and the memory cache are created on first use, so options set after parsing work. Without an own client,
/// downloads use one shared default client, and the options are left as they are.
/// </summary>
public class LateOptionsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly byte[] SmallManifest = ManifestWriter.Wrap(ManifestWriter.Valid().ToArray());

    [Fact]
    public async Task ChunkBaseUrl_set_after_parsing()
    {
        var cdn = new FakeCdn();
        using HttpClient client = cdn.CreateClient();
        var options = new ManifestParseOptions
        {
            Decompressor = DecompressorBuilder.Default.Build(),
            DownloadRetryBaseDelay = TimeSpan.Zero
        };
        FBuildPatchAppManifest manifest = TestData.DescribeSyntheticChunks(FBuildPatchAppManifest.Deserialize(TestData.ManifestBytes, options));
        FFileManifest file = TestData.GetFile(manifest);

        // used to throw a NullReferenceException in the chunk lock
        options.ChunkBaseUrl = TestData.ChunkBaseUrl;
        options.Client = client;

        byte[] expected = TestData.ExpectedFile(file);
        Assert.Equal(expected, await file.GetStream().SaveBytesAsync(cancellationToken: Ct));

        byte[] syncRead = new byte[file.FileSize];
        Assert.Equal(syncRead.Length, file.GetStream().ReadAt(0, syncRead));
        Assert.Equal(expected, syncRead);

        // the memory cache was created on first use and served the second read
        Assert.NotNull(manifest.ChunkMemoryCache);
        Assert.Equal(TestData.DistinctChunks(file).Length, cdn.Requests);
    }

    [Fact]
    public void Without_an_own_client_all_options_share_one_default_client()
    {
        var first = new ManifestParseOptions();
        var second = new ManifestParseOptions();

        var seen = new HttpClient[64];
        Parallel.For(0, seen.Length, i => seen[i] = (i % 2 == 0 ? first : second).DownloadClient);

        Assert.All(seen, client => Assert.Same(seen[0], client));
        // never handed out through the options: a caller that disposes options.Client cannot dispose the shared one
        Assert.Null(first.Client);
        Assert.Null(second.Client);
    }

    [Fact]
    public void Own_client_is_used()
    {
        using var client = new HttpClient();
        var options = new ManifestParseOptions();
        FBuildPatchAppManifest.Deserialize(SmallManifest, options);
        options.Client = client;

        Assert.Same(client, options.DownloadClient);
        Assert.Same(client, options.Client);
    }

    [Fact]
    public async Task DownloadAndParseAsync_leaves_the_options_without_a_client()
    {
        // a closed local port refuses the connection, so the shared client is used without a request leaving the machine
        var info = ManifestInfo.Deserialize("""
            { "elements": [ { "appName": "a", "labelName": "l", "buildVersion": "b",
              "manifests": [ { "uri": "http://127.0.0.1:9/x.manifest" } ] } ] }
            """u8)!;
        var options = new ManifestParseOptions { MaxDownloadRetries = 0 };

        await Assert.ThrowsAsync<HttpRequestException>(() => info.DownloadAndParseAsync(options, cancellationToken: Ct));

        Assert.Null(options.Client);
    }

    [Fact]
    public void Chunk_lock_is_created_once()
    {
        FBuildPatchAppManifest manifest = FBuildPatchAppManifest.Deserialize(SmallManifest);

        var lockers = new object[64];
        Parallel.For(0, lockers.Length, i => lockers[i] = manifest.ChunksLocker);

        Assert.All(lockers, locker => Assert.Same(lockers[0], locker));
    }

    [Fact]
    public void Memory_cache_follows_the_options_at_first_use()
    {
        var options = new ManifestParseOptions { ChunkMemoryCacheCapacity = 0 };
        FBuildPatchAppManifest manifest = FBuildPatchAppManifest.Deserialize(SmallManifest, options);
        Assert.Null(manifest.ChunkMemoryCache);

        options.ChunkMemoryCacheCapacity = 4;
        var caches = new object?[64];
        Parallel.For(0, caches.Length, i => caches[i] = manifest.ChunkMemoryCache);
        Assert.Equal(4, manifest.ChunkMemoryCache!.Capacity);
        Assert.All(caches, cache => Assert.Same(manifest.ChunkMemoryCache, cache));

        // a cache directory set after parsing means no memory cache
        var withDirectory = new ManifestParseOptions();
        FBuildPatchAppManifest manifest2 = FBuildPatchAppManifest.Deserialize(SmallManifest, withDirectory);
        withDirectory.ChunkCacheDirectory = Path.GetTempPath();
        Assert.Null(manifest2.ChunkMemoryCache);
    }
}
