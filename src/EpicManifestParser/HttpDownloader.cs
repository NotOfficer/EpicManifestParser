using System.Buffers;
using System.Net;
using System.Net.Http.Headers;

namespace EpicManifestParser;

/// <summary>
/// GET with retries: one attempt covers the request, reading the whole body and decoding it.
/// </summary>
internal static class HttpDownloader
{
    private static readonly TimeSpan MaxBackoffDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxRetryAfterDelay = TimeSpan.FromSeconds(30);
    private const int MinGrowSize = 64 * 1024;

    /// <summary>
    /// <c>maxResponseSize</c> of <see cref="DownloadAsync{TState,TResult}"/> without a limit of its own (manifests).
    /// </summary>
    public const int Unbounded = int.MaxValue;

    /// <summary>
    /// Downloads <paramref name="uri"/> and decodes the body, retrying transient failures as configured in <paramref name="options"/>.
    /// </summary>
    /// <param name="options">Client and retry settings.</param>
    /// <param name="uri">The resource to download.</param>
    /// <param name="initialBufferSize">Buffer size when the response has no <c>Content-Length</c>.</param>
    /// <param name="maxResponseSize">
    /// The largest body accepted, <see cref="Unbounded"/> for no limit beyond what an array can hold. A larger
    /// <c>Content-Length</c> is rejected before a buffer is rented, a body without one as soon as it grows past the limit; both
    /// with an <see cref="InvalidDataException"/>, which is retried like other corrupt responses.
    /// </param>
    /// <param name="state">Passed to <paramref name="decode"/>.</param>
    /// <param name="decode">
    /// Validates and decodes the body. It takes ownership of the body when it returns; when it throws, the body is returned
    /// to the pool here. <see cref="InvalidDataException"/> and <see cref="IOException"/> mark the payload as corrupt and are retried.
    /// </param>
    /// <param name="cancellationToken">The caller's token. Cancellation through it is never retried.</param>
    /// <exception cref="HttpRequestException">The last attempt failed with this exception; <c>Data["Uri"]</c> and <c>Data["Attempts"]</c> are set.</exception>
    /// <exception cref="TimeoutException">
    /// The last attempt received no data for <see cref="ManifestParseOptions.DownloadIdleTimeout"/>; <c>Data</c> is set the same way.
    /// </exception>
    public static async ValueTask<TResult> DownloadAsync<TState, TResult>(ManifestParseOptions options, Uri uri, int initialBufferSize,
        int maxResponseSize, TState state, Func<TState, RentedBuffer, TResult> decode, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxResponseSize);
        HttpClient client = options.DownloadClient;
        int maxRetries = options.MaxDownloadRetries;
        TimeSpan idleTimeout = options.DownloadIdleTimeout;

        for (int attempt = 1; ; attempt++)
        {
            TimeSpan delay;

            try
            {
                RentedBuffer body = await DownloadOnceAsync(client, uri, initialBufferSize, maxResponseSize, idleTimeout, cancellationToken).ConfigureAwait(false);
                try
                {
                    return decode(state, body);
                }
                catch
                {
                    body.Return();
                    throw;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                if (attempt > maxRetries || !IsTransient(ex))
                {
                    ex.Data["Uri"] = uri;
                    ex.Data["Attempts"] = attempt;
                    throw;
                }

                delay = GetRetryDelay(attempt, options.DownloadRetryBaseDelay, ex);
            }

            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            else
                cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static async ValueTask<RentedBuffer> DownloadOnceAsync(HttpClient client, Uri uri, int initialBufferSize, int maxResponseSize,
        TimeSpan idleTimeout, CancellationToken cancellationToken)
    {
        // the largest buffer the body may need: the limit plus the spare byte that notices a longer body
        int bufferLimit = (int)Math.Min((long)maxResponseSize + 1, Array.MaxLength);

        // linked to the caller's token, re-armed after every read: aborts stalled connections, never slow ones
        using CancellationTokenSource? idleCts = idleTimeout == Timeout.InfiniteTimeSpan
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken token = cancellationToken;
        if (idleCts is not null)
        {
            idleCts.CancelAfter(idleTimeout);
            token = idleCts.Token;
        }

        try
        {
            using HttpResponseMessage res = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            EnsureSuccessStatusCode(res, uri);

            long? contentLength = res.Content.Headers.ContentLength;
            if (contentLength > maxResponseSize || contentLength >= Array.MaxLength)
                throw new InvalidDataException($"Content-Length {contentLength} exceeds the limit of {Math.Min(maxResponseSize, Array.MaxLength - 1)} bytes");

            // one spare byte so that a body longer than its Content-Length is noticed without growing the buffer
            byte[] buffer = ArrayPool<byte>.Shared.Rent(contentLength is { } expectedLength ? (int)expectedLength + 1 : Math.Min(initialBufferSize, bufferLimit));
            int length = 0;

            try
            {
                Stream body = await res.Content.ReadAsStreamAsync(token).ConfigureAwait(false);

                while (true)
                {
                    if (length == buffer.Length)
                        buffer = Grow(buffer, length, bufferLimit);

                    int read = await body.ReadAsync(buffer.AsMemory(length), token).ConfigureAwait(false);
                    if (read == 0)
                        break;

                    length += read;
                    if (length > contentLength)
                        throw new InvalidDataException($"Response body is longer than its Content-Length of {contentLength} bytes");
                    if (length > maxResponseSize)
                        throw new InvalidDataException($"Response body exceeds the limit of {maxResponseSize} bytes");

                    idleCts?.CancelAfter(idleTimeout);
                }

                if (contentLength.HasValue && length != contentLength.Value)
                    throw new InvalidDataException($"Response body ended after {length} of {contentLength} bytes");

                return new RentedBuffer(buffer, 0, length);
            }
            catch
            {
                ArrayPool<byte>.Shared.Return(buffer);
                throw;
            }
        }
        // the idle timer fired, not the caller: a stalled connection must not look like a cancellation
        catch (OperationCanceledException ex) when (idleCts is { IsCancellationRequested: true } && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"No data was received for {idleTimeout.TotalMilliseconds:0} ms (ManifestParseOptions.DownloadIdleTimeout)", ex);
        }
    }

    /// <summary>
    /// Doubles the buffer, but grows it to at least <see cref="MinGrowSize"/> bytes, so that a tiny or empty initial buffer
    /// cannot make the read loop stall, and to at most <paramref name="limit"/> bytes.
    /// </summary>
    private static byte[] Grow(byte[] buffer, int length, int limit)
    {
        // the read loop rejects a body past the limit first, this only guards the arithmetic
        if (buffer.Length >= limit)
            throw new InvalidDataException("Response body is too large");

        long newSize = Math.Max((long)buffer.Length * 2, MinGrowSize);
        byte[] newBuffer = ArrayPool<byte>.Shared.Rent((int)Math.Min(newSize, limit));
        buffer.AsSpan(0, length).CopyTo(newBuffer);
        ArrayPool<byte>.Shared.Return(buffer);
        return newBuffer;
    }

    internal static void EnsureSuccessStatusCode(HttpResponseMessage res, Uri uri)
    {
        try
        {
            res.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            ex.Data["Uri"] = uri;
            ex.Data["Headers"] = res.Headers;
            throw;
        }
    }

    /// <summary>
    /// Whether a failed attempt is worth repeating. Caller cancellation is filtered out before this is called.
    /// </summary>
    private static bool IsTransient(Exception ex) => ex switch
    {
        // no status code: connection, DNS or protocol error
        HttpRequestException { StatusCode: null } => true,
        HttpRequestException { StatusCode: { } statusCode } => statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
                                                               || (int)statusCode is >= 500 and <= 599,
        // HttpClient.Timeout (TaskCanceledException with an inner TimeoutException)
        OperationCanceledException => true,
        // DownloadIdleTimeout
        TimeoutException => true,
        // network error while reading the body, bad chunk magic, failed decompression
        IOException => true,
        // body length does not match Content-Length or the chunk header
        InvalidDataException => true,
        _ => false
    };

    private static TimeSpan GetRetryDelay(int attempt, TimeSpan baseDelay, Exception ex)
    {
        TimeSpan delay = TimeSpan.Zero;

        if (baseDelay > TimeSpan.Zero)
        {
            double ms = baseDelay.TotalMilliseconds * Math.Pow(2, Math.Min(attempt - 1, 30));
            ms *= 0.5 + Random.Shared.NextDouble(); // ±50 % jitter
            delay = TimeSpan.FromMilliseconds(Math.Min(ms, MaxBackoffDelay.TotalMilliseconds));
        }

        if (ex is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable }
            && ex.Data["Headers"] is HttpResponseHeaders { RetryAfter: { } retryAfter })
        {
            TimeSpan serverDelay = retryAfter.Delta ?? (retryAfter.Date - DateTimeOffset.UtcNow) ?? TimeSpan.Zero;
            serverDelay = TimeSpan.FromTicks(Math.Clamp(serverDelay.Ticks, 0, MaxRetryAfterDelay.Ticks));
            if (serverDelay > delay)
                delay = serverDelay;
        }

        return delay;
    }
}
