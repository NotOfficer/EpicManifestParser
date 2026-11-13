using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;

using EpicManifestParser.Tests.Fixtures;
using EpicManifestParser.UE;

namespace EpicManifestParser.Tests;

public class RetryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string, int> TransientFailures => new()
    {
        { "503", 2 },
        { "500", 1 },
        { "408", 1 },
        { "429", 1 },
        { "connection-error", 2 },
        { "ends-early", 2 },
        { "stalls", 1 },
        { "bad-magic", 1 },
        { "size-mismatch", 1 },
        { "bad-zlib", 1 },
        { "httpclient-timeout", 1 }
    };

    [Theory]
    [MemberData(nameof(TransientFailures))]
    public async Task Transient_failures_are_retried(string failure, int failures)
    {
        bool clientTimeout = failure == "httpclient-timeout";
        (FakeCdn cdn, FGuid chunk, byte[] expected, byte[] actual) = await ReadFirstPartAsync(
            (request, token) => request.Attempt <= failures ? FailAsync(failure, request, token) : Task.FromResult<HttpResponseMessage?>(null),
            // a short timeout only where a stall is the point: the successful attempt runs under the same timeout, and a slow
            // runner that pauses it past the timeout would add a request; the HttpClient timeout must not be caught by the idle one
            o => o.DownloadIdleTimeout = failure switch
            {
                "stalls" => TimeSpan.FromMilliseconds(500),
                "httpclient-timeout" => Timeout.InfiniteTimeSpan,
                _ => TimeSpan.FromSeconds(30)
            },
            client => client.Timeout = clientTimeout ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(30));

        Assert.Equal(expected, actual);
        Assert.Equal(failures + 1, cdn.RequestsFor(chunk));
    }

    [Fact]
    public async Task Sync_reads_retry_too()
    {
        var cdn = new FakeCdn
        {
            Behavior = static (request, _) => Task.FromResult(request.Attempt == 1 ? FakeCdn.Status(HttpStatusCode.BadGateway) : null)
        };
        using HttpClient client = cdn.CreateClient();
        FFileManifest file = TestData.GetFile(TestData.Parse(client));

        byte[] actual = new byte[file.FileSize];
        Assert.Equal(actual.Length, file.GetStream().ReadAt(0, actual.AsSpan()));

        Assert.Equal(TestData.ExpectedFile(file), actual);
        Assert.Equal(2 * TestData.DistinctChunks(file).Length, cdn.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task Client_errors_are_not_retried(HttpStatusCode statusCode)
    {
        (FakeCdn cdn, FGuid chunk, HttpRequestException ex) = await ReadFirstPartFailsAsync<HttpRequestException>(_ => FakeCdn.Status(statusCode));

        Assert.Equal(statusCode, ex.StatusCode);
        Assert.Equal(1, cdn.RequestsFor(chunk));
        Assert.Equal(1, ex.Data["Attempts"]);
        Assert.IsType<Uri>(ex.Data["Uri"]);
        Assert.NotNull(ex.Data["Headers"]);
    }

    [Fact]
    public async Task Encrypted_chunks_are_not_retried()
    {
        (FakeCdn cdn, FGuid chunk, _) = await ReadFirstPartFailsAsync<NotSupportedException>(
            request => FakeCdn.Ok(TestData.CreateChunkFile(request.Chunk!.Value, false, storedAs: 2)));

        Assert.Equal(1, cdn.RequestsFor(chunk));
    }

    [Fact]
    public async Task Compressed_chunks_are_read_without_a_decompressor_in_the_options()
    {
        var cdn = new FakeCdn { Behavior = static (request, _) => Task.FromResult<HttpResponseMessage?>(FakeCdn.Ok(TestData.CreateChunkFile(request.Chunk!.Value, true))) };
        using HttpClient client = cdn.CreateClient();
        // a JSON or uncompressed manifest is parsed without a decompressor, so the chunks are the first compressed data
        FBuildPatchAppManifest manifest = TestData.Parse(client, o => o.Decompressor = null);
        FFileManifest file = TestData.GetFile(manifest);

        Assert.Equal(TestData.ExpectedFile(file), await file.GetStream().SaveBytesAsync(cancellationToken: Ct));

        // the shared default decompressor was used, the options stay as they were
        Assert.Null(manifest.Options.Decompressor);
        Assert.All(TestData.DistinctChunks(file), chunk => Assert.Equal(1, cdn.RequestsFor(chunk.Guid)));
    }

    [Fact]
    public async Task Exhausted_retries_throw_the_last_exception_with_diagnostics()
    {
        (FakeCdn cdn, FGuid chunk, HttpRequestException ex) = await ReadFirstPartFailsAsync<HttpRequestException>(
            _ => FakeCdn.Status(HttpStatusCode.ServiceUnavailable),
            o => o.MaxDownloadRetries = 2);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
        Assert.Equal(3, cdn.RequestsFor(chunk));
        Assert.Equal(3, ex.Data["Attempts"]);
        Assert.IsType<Uri>(ex.Data["Uri"]);
        Assert.NotNull(ex.Data["Headers"]);
    }

    [Fact]
    public async Task Zero_retries_means_a_single_attempt()
    {
        (FakeCdn cdn, FGuid chunk, InvalidDataException ex) = await ReadFirstPartFailsAsync<InvalidDataException>(
            request => FakeCdn.EndsEarly(request.Body),
            o => o.MaxDownloadRetries = 0);

        Assert.Equal(1, cdn.RequestsFor(chunk));
        Assert.Equal(1, ex.Data["Attempts"]);
    }

    [Fact]
    public async Task Caller_cancellation_during_a_request_is_not_retried()
    {
        var cdn = new FakeCdn
        {
            Behavior = static async (_, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return null;
            }
        };
        using HttpClient client = cdn.CreateClient();
        FFileManifest file = TestData.GetFile(TestData.Parse(client, o => o.DownloadIdleTimeout = Timeout.InfiniteTimeSpan));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(100);

        Stopwatch sw = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => file.GetStream().ReadAtAsync(0, new byte[16], cts.Token).AsTask());

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"cancellation took {sw.Elapsed}");
        Assert.Equal(1, cdn.Requests);
    }

    [Fact]
    public async Task Caller_cancellation_during_the_backoff_delay_surfaces_promptly()
    {
        var cdn = new FakeCdn { Behavior = static (_, _) => Task.FromResult<HttpResponseMessage?>(FakeCdn.Status(HttpStatusCode.ServiceUnavailable)) };
        using HttpClient client = cdn.CreateClient();
        FFileManifest file = TestData.GetFile(TestData.Parse(client, o => o.DownloadRetryBaseDelay = TimeSpan.FromSeconds(30)));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(200);

        Stopwatch sw = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => file.GetStream().ReadAtAsync(0, new byte[16], cts.Token).AsTask());

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"cancellation took {sw.Elapsed}");
        Assert.Equal(1, cdn.Requests);
    }

    [Fact]
    public async Task Retry_After_is_honored()
    {
        // timed between the two requests: the setup before them (parsing the manifest) must not count as the delay
        long first = 0, second = 0;
        (FakeCdn cdn, FGuid chunk, byte[] expected, byte[] actual) = await ReadFirstPartAsync((request, _) =>
        {
            if (request.Attempt == 1)
            {
                first = Stopwatch.GetTimestamp();
                return Task.FromResult<HttpResponseMessage?>(FakeCdn.Status(HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(1)));
            }
            second = Stopwatch.GetTimestamp();
            return Task.FromResult<HttpResponseMessage?>(null);
        });

        Assert.Equal(expected, actual);
        Assert.Equal(2, cdn.RequestsFor(chunk));
        TimeSpan delay = Stopwatch.GetElapsedTime(first, second);
        Assert.True(delay >= TimeSpan.FromMilliseconds(900), $"retried after {delay}");
    }

    [Fact]
    public async Task Slow_but_progressing_bodies_are_not_aborted()
    {
        // 12 pieces 60 ms apart: 720 ms in total, longer than the timeout, but every gap is 440 ms short of it
        (FakeCdn cdn, FGuid chunk, byte[] expected, byte[] actual) = await ReadFirstPartAsync(
            (request, _) => Task.FromResult<HttpResponseMessage?>(FakeCdn.Trickles(request.Body, 12, TimeSpan.FromMilliseconds(60))),
            o => o.DownloadIdleTimeout = TimeSpan.FromMilliseconds(500));

        Assert.Equal(expected, actual);
        Assert.Equal(1, cdn.RequestsFor(chunk));
    }

    [Theory]
    [InlineData("stalls")]
    [InlineData("no-headers")]
    public async Task Stalls_that_exhaust_the_retries_throw_TimeoutException(string failure)
    {
        var cdn = new FakeCdn { Behavior = (request, token) => FailAsync(failure, request, token) };
        using HttpClient client = cdn.CreateClient();
        FFileManifest file = TestData.GetFile(TestData.Parse(client, ConfigureShortIdleTimeout));
        FChunkPart part = file.ChunkParts[0];

        TimeoutException ex = await Assert.ThrowsAsync<TimeoutException>(() => file.GetStream().ReadAtAsync(0, new byte[part.Size], Ct).AsTask());

        Assert.Contains("DownloadIdleTimeout", ex.Message);
        Assert.IsAssignableFrom<OperationCanceledException>(ex.InnerException);
        Assert.Equal(2, cdn.RequestsFor(part.Chunk.Guid));
        Assert.Equal(2, ex.Data["Attempts"]);
        Assert.IsType<Uri>(ex.Data["Uri"]);
    }

    [Fact]
    public async Task Stalls_that_exhaust_the_retries_throw_TimeoutException_from_SaveBytesAsync()
    {
        var cdn = new FakeCdn { Behavior = static (request, _) => Task.FromResult<HttpResponseMessage?>(FakeCdn.Stalls(request.Body)) };
        using HttpClient client = cdn.CreateClient();
        FFileManifest file = TestData.GetFile(TestData.Parse(client, ConfigureShortIdleTimeout));

        TimeoutException ex = await Assert.ThrowsAsync<TimeoutException>(() => file.GetStream().SaveBytesAsync(new byte[file.FileSize], cancellationToken: Ct));

        Assert.IsAssignableFrom<OperationCanceledException>(ex.InnerException);
        Assert.Equal(2, ex.Data["Attempts"]);
        Assert.IsType<Uri>(ex.Data["Uri"]);
    }

    [Theory]
    // the idle timer is armed, but the caller cancels first; without retries a misreported cancellation would surface as is
    [InlineData(30_000, 100, 0, 1, 1)]
    // stalled attempts time out and are retried until the caller cancels during one of them
    [InlineData(150, 600, 100, 2, 101)]
    public async Task Caller_cancellation_during_a_stalled_body_throws_OperationCanceledException(
        int idleTimeoutMs, int cancelAfterMs, int maxRetries, int minRequests, int maxRequests)
    {
        var cdn = new FakeCdn { Behavior = static (request, _) => Task.FromResult<HttpResponseMessage?>(FakeCdn.Stalls(request.Body)) };
        using HttpClient client = cdn.CreateClient();
        FFileManifest file = TestData.GetFile(TestData.Parse(client, o =>
        {
            o.ChunkMemoryCacheCapacity = 0;
            o.MaxDownloadRetries = maxRetries;
            o.DownloadIdleTimeout = TimeSpan.FromMilliseconds(idleTimeoutMs);
        }));
        FGuid chunk = file.ChunkParts[0].Chunk.Guid;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(cancelAfterMs);

        Stopwatch sw = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => file.GetStream().ReadAtAsync(0, new byte[16], cts.Token).AsTask());

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"cancellation took {sw.Elapsed}");
        Assert.InRange(cdn.RequestsFor(chunk), minRequests, maxRequests);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(null, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    public async Task Responses_without_Content_Length_are_read_completely(int? chunkDownloadBufferSize, bool compressed)
    {
        var cdn = new FakeCdn
        {
            Behavior = (request, _) =>
            {
                HttpResponseMessage response = FakeCdn.NoContentLength(TestData.CreateChunkFile(request.Chunk!.Value, compressed));
                Assert.Null(response.Content.Headers.ContentLength);
                return Task.FromResult<HttpResponseMessage?>(response);
            }
        };
        using HttpClient client = cdn.CreateClient();
        FFileManifest file = TestData.GetFile(TestData.Parse(client, o =>
        {
            o.MaxDownloadRetries = 0;
            if (chunkDownloadBufferSize is { } size)
                o.ChunkDownloadBufferSize = size;
        }));

        byte[] actual = await file.GetStream().SaveBytesAsync(cancellationToken: Ct);

        Assert.Equal(TestData.ExpectedFile(file), actual);
        Assert.Equal(TestData.DistinctChunks(file).Length, cdn.Requests);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Downloads_without_Content_Length_grow_a_tiny_or_empty_initial_buffer(int initialBufferSize)
    {
        byte[] blob = new byte[300 * 1024];
        new Random(5).NextBytes(blob);
        var cdn = new FakeCdn { Behavior = static (request, _) => Task.FromResult<HttpResponseMessage?>(FakeCdn.NoContentLength(request.Body)) };
        cdn.AddBlob("/blob", blob);
        using HttpClient client = cdn.CreateClient();
        var options = new ManifestParseOptions { Client = client, MaxDownloadRetries = 0 };

        RentedBuffer body = await HttpDownloader.DownloadAsync(options, new Uri("https://cdn.test/blob"), initialBufferSize, HttpDownloader.Unbounded, 0,
            static (_, rented) => rented, Ct);
        try
        {
            Assert.Equal(blob, body.Span.ToArray());
        }
        finally
        {
            body.Return();
        }
    }

    [Fact]
    public void Options_validate_their_values()
    {
        var options = new ManifestParseOptions();
        Assert.Equal(3, options.MaxDownloadRetries);
        Assert.Equal(TimeSpan.FromMilliseconds(250), options.DownloadRetryBaseDelay);
        Assert.Equal(TimeSpan.FromSeconds(30), options.DownloadIdleTimeout);
        Assert.Equal(2097152, options.ChunkDownloadBufferSize);

        Assert.Throws<ArgumentOutOfRangeException>(() => options.MaxDownloadRetries = -1);
        Assert.Throws<ArgumentOutOfRangeException>(() => options.DownloadRetryBaseDelay = TimeSpan.FromMilliseconds(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => options.DownloadIdleTimeout = TimeSpan.Zero);
        Assert.Throws<ArgumentOutOfRangeException>(() => options.ChunkDownloadBufferSize = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => options.ChunkDownloadBufferSize = -1);
        options.DownloadIdleTimeout = Timeout.InfiniteTimeSpan;
        options.DownloadRetryBaseDelay = TimeSpan.Zero;
        options.ChunkDownloadBufferSize = 1;

        // CancellationTokenSource.CancelAfter takes at most uint.MaxValue - 1 ms; longer values used to fail every download
        options.DownloadIdleTimeout = TimeSpan.FromMilliseconds(uint.MaxValue - 1);
        Assert.Equal(ManifestParseOptions.MaxDownloadIdleTimeout, options.DownloadIdleTimeout);
        Assert.Throws<ArgumentOutOfRangeException>(() => options.DownloadIdleTimeout = TimeSpan.FromMilliseconds(uint.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => options.DownloadIdleTimeout = TimeSpan.MaxValue);
        using var cts = new CancellationTokenSource();
        Assert.Throws<ArgumentOutOfRangeException>(() => cts.CancelAfter(TimeSpan.FromMilliseconds(uint.MaxValue)));
        cts.CancelAfter(ManifestParseOptions.MaxDownloadIdleTimeout);
    }

    [Fact]
    public async Task The_longest_idle_timeout_works_for_downloads()
    {
        (FakeCdn cdn, FGuid chunk, byte[] expected, byte[] actual) = await ReadFirstPartAsync(
            static (_, _) => Task.FromResult<HttpResponseMessage?>(null),
            o => o.DownloadIdleTimeout = ManifestParseOptions.MaxDownloadIdleTimeout);

        Assert.Equal(expected, actual);
        Assert.Equal(1, cdn.RequestsFor(chunk));
    }

    private static void ConfigureShortIdleTimeout(ManifestParseOptions options)
    {
        options.ChunkMemoryCacheCapacity = 0;
        options.MaxDownloadRetries = 1;
        options.DownloadIdleTimeout = TimeSpan.FromMilliseconds(200);
    }

    private static async Task<HttpResponseMessage?> FailAsync(string failure, FakeRequest request, CancellationToken token)
    {
        byte[] body = request.Body;
        switch (failure)
        {
            case "503": return FakeCdn.Status(HttpStatusCode.ServiceUnavailable);
            case "500": return FakeCdn.Status(HttpStatusCode.InternalServerError);
            case "408": return FakeCdn.Status(HttpStatusCode.RequestTimeout);
            case "429": return FakeCdn.Status(HttpStatusCode.TooManyRequests);
            case "connection-error": throw new HttpRequestException("simulated connection reset");
            case "ends-early": return FakeCdn.EndsEarly(body);
            case "stalls": return FakeCdn.Stalls(body);
            case "bad-magic":
            {
                byte[] corrupt = (byte[])body.Clone();
                corrupt[0] ^= 0xFF;
                return FakeCdn.Ok(corrupt);
            }
            case "size-mismatch":
            {
                byte[] corrupt = (byte[])body.Clone();
                BinaryPrimitives.WriteInt32LittleEndian(corrupt.AsSpan(12), BinaryPrimitives.ReadInt32LittleEndian(corrupt.AsSpan(12)) - 1);
                return FakeCdn.Ok(corrupt);
            }
            case "bad-zlib":
                // claims to be compressed, but the payload is the raw data
                return FakeCdn.Ok(TestData.CreateChunkFile(request.Chunk!.Value, false, storedAs: 1));
            case "httpclient-timeout":
            case "no-headers":
                // no response until HttpClient.Timeout or the idle timeout cancels the request
                await Task.Delay(Timeout.Infinite, token);
                return null;
            default:
                throw new ArgumentOutOfRangeException(nameof(failure));
        }
    }

    /// <summary>Reads the first chunk part (asynchronously, no cache directory, no memory cache) through a CDN with the given behavior.</summary>
    private static async Task<(FakeCdn Cdn, FGuid Chunk, byte[] Expected, byte[] Actual)> ReadFirstPartAsync(
        Func<FakeRequest, CancellationToken, Task<HttpResponseMessage?>> behavior, Action<ManifestParseOptions>? configure = null,
        Action<HttpClient>? configureClient = null)
    {
        var cdn = new FakeCdn { Behavior = behavior };
        using HttpClient client = cdn.CreateClient();
        configureClient?.Invoke(client);
        FFileManifest file = TestData.GetFile(TestData.Parse(client, o =>
        {
            o.ChunkMemoryCacheCapacity = 0;
            configure?.Invoke(o);
        }));
        FChunkPart part = file.ChunkParts[0];

        byte[] actual = new byte[part.Size];
        Assert.Equal(actual.Length, await file.GetStream().ReadAtAsync(0, actual.AsMemory(), Ct));

        return (cdn, part.Chunk.Guid, TestData.ExpectedFile(file).AsSpan(0, (int)part.Size).ToArray(), actual);
    }

    private static async Task<(FakeCdn Cdn, FGuid Chunk, TException Exception)> ReadFirstPartFailsAsync<TException>(
        Func<FakeRequest, HttpResponseMessage?> behavior, Action<ManifestParseOptions>? configure = null)
        where TException : Exception
    {
        var cdn = new FakeCdn { Behavior = (request, _) => Task.FromResult(behavior(request)) };
        using HttpClient client = cdn.CreateClient();
        FFileManifest file = TestData.GetFile(TestData.Parse(client, o =>
        {
            o.ChunkMemoryCacheCapacity = 0;
            configure?.Invoke(o);
        }));
        FChunkPart part = file.ChunkParts[0];

        TException ex = await Assert.ThrowsAsync<TException>(() => file.GetStream().ReadAtAsync(0, new byte[part.Size], 0, (int)part.Size, Ct));
        return (cdn, part.Chunk.Guid, ex);
    }
}
