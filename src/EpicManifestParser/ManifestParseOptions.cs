using System.Net;

using EpicManifestParser.Api;
using OffiUtils;

namespace EpicManifestParser;
// ReSharper disable UseSymbolAlias

/// <summary>
/// Options/Configuration for parsing manifests
/// </summary>
public class ManifestParseOptions
{
    /// <summary>
    /// Used to decompress zlib data in chunks and binary serialized manifests.
    /// </summary>
    /// <remarks>
    /// Without one, a decompressor that all options share is used, built once from <see cref="DecompressorBuilder.Default"/>.
    /// The library never sets this property.
    /// </remarks>
    public IDecompressor? Decompressor { get; set; }

    /// <summary>
    /// The decompressor for manifests and chunks: <see cref="Decompressor"/>, or the shared default one.
    /// </summary>
    internal IDecompressor DecompressorOrDefault => Decompressor ?? SharedDecompressor.Instance;

    // the type initializer runs once, when compressed data is first decompressed without an own decompressor
    private static class SharedDecompressor
    {
        public static readonly IDecompressor Instance = DecompressorBuilder.Default.Build();
    }

    /// <summary>
    /// Required for downloading: the URL of the build's cloud directory, which the chunk path
    /// (<c>{ChunkSubdir}/{GroupNumber:D2}/{Hash:X16}_{Guid}.chunk</c>) is appended to. Should end with a slash; one is inserted when it does not.
    /// </summary>
    /// <remarks>
    /// Example: <code>http://epicgames-download1.akamaized.net/Builds/Fortnite/CloudDir/</code><br/>
    /// Distributionpoints can be found here: <see href="https://launcher-public-service-prod06.ol.epicgames.com/launcher/api/public/distributionpoints">here.</see>
    /// </remarks>
    public string? ChunkBaseUrl { get; set; }

    /// <summary>
    /// Your own (optional) <see cref="HttpClient"/> used for downloading, must not have a <see cref="HttpClient.BaseAddress"/> !
    /// </summary>
    /// <remarks>
    /// Without one, downloads use a single client that all manifests and options share, created on first use with the settings
    /// of <see cref="CreateDefaultClient"/>. The library never sets this property and never disposes a client.
    /// </remarks>
    public HttpClient? Client { get; set; }

    /// <summary>
    /// The client for downloads: <see cref="Client"/>, or the shared default client.
    /// </summary>
    internal HttpClient DownloadClient => Client ?? SharedClient.Instance;

    // the type initializer runs once, on the first download without an own client
    private static class SharedClient
    {
        public static readonly HttpClient Instance = CreateDefaultClient();
    }

    /// <summary>
    /// Initial buffer size for a chunk download whose response has no <c>Content-Length</c>, defaults to <value>2097152</value> bytes (2 MiB).
    /// The buffer grows when the response is larger.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public int ChunkDownloadBufferSize
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            field = value;
        }
    } = 2097152;

    /// <summary>
    /// Optional for caching chunks, very recommended.
    /// </summary>
    /// <remarks>
    /// Chunks are stored decompressed as <c>{Hash:X16}_{Guid}.chunk</c>. A file with that name is complete even when the process
    /// is cancelled or killed while writing: it is written under a temporary name first and then renamed. The data is not flushed
    /// to disk, so after a power loss or an operating system crash a cache file can hold data that never reached the disk;
    /// delete the directory then. The directory is created when it is missing.
    /// A cache file of an earlier run is used once its length matches the chunk size of the manifest, otherwise it is downloaded
    /// again. When a chunk cannot be written (<see cref="IOException"/>, <see cref="UnauthorizedAccessException"/>), the read
    /// still returns the downloaded data; the chunk is downloaded again on its next read.
    /// </remarks>
    public string? ChunkCacheDirectory { get; set; }

    /// <summary>
    /// The number of decompressed chunks kept in memory when <see cref="ChunkCacheDirectory"/> is <see langword="null"/>,
    /// so that repeated reads of the same chunk do not download it again. Defaults to <value>32</value>, <value>0</value> disables it.
    /// </summary>
    /// <remarks>
    /// Read once, when a manifest reads its first chunk without a <see cref="ChunkCacheDirectory"/>; later changes do not affect that
    /// manifest. The least recently used chunk is evicted when the cache is full.
    /// Each entry keeps a pooled buffer: 1 MiB for a compressed 1 MiB chunk, 2 MiB for a raw one (its download buffer).
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int ChunkMemoryCacheCapacity
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            field = value;
        }
    } = 32;

    /// <summary>
    /// Whether the SHA1 of every downloaded chunk is compared with the chunk's hash in the manifest, defaults to <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// The check runs once per download, before the data is returned or cached (about 1 ms per MiB); reads from the cache
    /// directory or the memory cache are not hashed again. When the manifest has no hash for a chunk, the chunk is checked against
    /// its header as in UE: against the header's SHA1 if it has one, otherwise against its rolling hash; a header without either
    /// hash is invalid. A mismatch counts as a corrupt response and is retried (<see cref="MaxDownloadRetries"/>), then thrown as
    /// an <see cref="InvalidDataException"/>.
    /// <para>
    /// The header's GUID and the size of the data are always checked against the manifest, also with this option off.
    /// </para>
    /// </remarks>
    public bool VerifyChunkHashes { get; set; } = true;

    /// <summary>
    /// The number of retries after a failed chunk or manifest download, defaults to <value>3</value>, <value>0</value> disables retries.
    /// </summary>
    /// <remarks>
    /// Retried are network errors, timeouts, the status codes 408, 429 and 5xx, and responses that end early or are corrupt:
    /// a chunk whose header, size or hash does not match the manifest, a response larger than its chunk can be, or a manifest that
    /// fails to parse (<see cref="InvalidDataException"/>, <see cref="FileLoadException"/>).
    /// Other status codes (for example 403 and 404), encrypted chunks and cancellation through the caller's token are not retried.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int MaxDownloadRetries
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            field = value;
        }
    } = 3;

    /// <summary>
    /// The base delay of the exponential backoff between download retries, defaults to 250 milliseconds.
    /// </summary>
    /// <remarks>
    /// The delay before retry <c>n</c> is <c>DownloadRetryBaseDelay * 2^(n-1)</c> with ±50 % jitter, capped at 10 seconds.
    /// A <c>Retry-After</c> header on a 429 or 503 response is honored up to 30 seconds. <see cref="TimeSpan.Zero"/> retries without delay.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public TimeSpan DownloadRetryBaseDelay
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.Zero);
            field = value;
        }
    } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// The maximum time a download attempt may go without receiving data, defaults to 30 seconds.
    /// <see cref="Timeout.InfiniteTimeSpan"/> disables it.
    /// </summary>
    /// <remarks>
    /// The timer restarts after every successful read, so slow downloads that keep making progress are never aborted.
    /// It also covers the response body, which <see cref="HttpClient.Timeout"/> does not.
    /// <para>
    /// A stalled attempt fails with a <see cref="TimeoutException"/> (the cancellation of the request is its
    /// <see cref="Exception.InnerException"/>) and is retried like other transient failures. When no retries are left, the
    /// download throws that <see cref="TimeoutException"/>. An <see cref="OperationCanceledException"/> therefore means cancellation
    /// through the caller's token, or <see cref="HttpClient.Timeout"/>, which the BCL reports as a
    /// <see cref="TaskCanceledException"/> with an inner <see cref="TimeoutException"/>.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The value is neither positive nor <see cref="Timeout.InfiniteTimeSpan"/>, or it is longer than 4294967294 milliseconds
    /// (about 49.7 days), the longest delay <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/> supports.
    /// </exception>
    public TimeSpan DownloadIdleTimeout
    {
        get;
        set
        {
            if (value != Timeout.InfiniteTimeSpan)
            {
                ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
                ArgumentOutOfRangeException.ThrowIfGreaterThan(value, MaxDownloadIdleTimeout);
            }
            field = value;
        }
    } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The longest <see cref="DownloadIdleTimeout"/>: <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/> accepts up to
    /// <c>uint.MaxValue - 1</c> milliseconds.
    /// </summary>
    internal static readonly TimeSpan MaxDownloadIdleTimeout = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    /// <summary>
    /// Optional for caching manifests when using <see cref="ManifestInfo.DownloadAndParseAsync(ManifestParseOptions, Predicate&lt;ManifestInfoElement&gt;?, Predicate&lt;ManifestInfoElementDownload&gt;?, CancellationToken)"/>.
    /// </summary>
    /// <remarks>
    /// A cached manifest that fails to parse, for example one left truncated by an older version, is deleted and downloaded again.
    /// The directory is created when it is missing; a manifest that cannot be written there is still returned.
    /// </remarks>
    public string? ManifestCacheDirectory { get; set; }

    /// <summary>
    /// Creates a new <see cref="HttpClient"/> instance optimized for chunk downloading.
    /// </summary>
    /// <remarks>
    /// Downloads without an own <see cref="Client"/> already share one client with these settings. Create one only when you need
    /// an instance of your own, for example to change its headers; it is yours to dispose.
    /// </remarks>
    /// <returns>The created <see cref="HttpClient"/> instance.</returns>
    public static HttpClient CreateDefaultClient()
    {
        var handler = new SocketsHttpHandler
        {
            UseCookies = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.None,
            MaxConnectionsPerServer = 256,
            EnableMultipleHttp3Connections = true,
            EnableMultipleHttp2Connections = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2)
        };
        var client = new HttpClient(handler)
        {
            DefaultRequestVersion = HttpVersion.Version30,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            Timeout = TimeSpan.FromSeconds(50)
        };
        client.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        client.DefaultRequestHeaders.UserAgent.ParseAdd("EpicOnlineServicesInstallHelper/5.3.0-54393070+++UE5+Dev-Distro-5.5-5e3057 (http-eventloop) Windows/10.0.26200.1.256.64bit");
        return client;
    }
}
