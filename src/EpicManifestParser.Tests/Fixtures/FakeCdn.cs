using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;

using EpicManifestParser.UE;

namespace EpicManifestParser.Tests.Fixtures;

/// <summary>
/// In-memory CDN: serves <see cref="TestData.GetChunkFile"/> for <c>.../{Hash:X16}_{Guid}.chunk</c> and registered blobs
/// for any other path. <see cref="Behavior"/> can replace individual responses to inject failures.
/// </summary>
internal sealed class FakeCdn : HttpMessageHandler
{
    private readonly ConcurrentDictionary<FGuid, int> _chunkRequests = new();
    private readonly ConcurrentDictionary<string, byte[]> _blobs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _blobRequests = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<Uri> _requestUris = new();
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _requests;
    private int _inFlight;
    private int _peakInFlight;
    private int _holdUntilInFlight;

    /// <summary>Total number of requests.</summary>
    public int Requests => Volatile.Read(ref _requests);

    /// <summary>Number of requests that are being served right now (including any time spent in <see cref="Behavior"/>).</summary>
    public int InFlight => Volatile.Read(ref _inFlight);

    /// <summary>The highest <see cref="InFlight"/> so far.</summary>
    public int PeakInFlight => Volatile.Read(ref _peakInFlight);

    /// <summary>
    /// Holds every request until <paramref name="count"/> requests are in flight at the same time, then lets them and every later
    /// one through. A client that sends one request after the other never gets past it, so pair it with a timeout.
    /// </summary>
    public void HoldUntilInFlight(int count) => _holdUntilInFlight = count;

    /// <summary>The URI of every request, in the order they arrived.</summary>
    public IReadOnlyCollection<Uri> RequestUris => _requestUris;

    /// <summary>Number of requests per chunk GUID.</summary>
    public int RequestsFor(FGuid guid) => _chunkRequests.GetValueOrDefault(guid);

    /// <summary>Number of distinct chunks requested at least once.</summary>
    public int DistinctChunksRequested => _chunkRequests.Count;

    /// <summary>
    /// Called for every request with its per-resource attempt number (1-based) and the body that would be served;
    /// returns a replacement response or <see langword="null"/> to serve normally.
    /// </summary>
    public Func<FakeRequest, CancellationToken, Task<HttpResponseMessage?>>? Behavior { get; set; }

    public void AddBlob(string absolutePath, byte[] content) => _blobs[absolutePath] = content;

    public HttpClient CreateClient() => new(this, false);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _requests);
        _requestUris.Enqueue(request.RequestUri!);
        string path = request.RequestUri!.AbsolutePath;
        // counted before the hold: a request that is cancelled while it waits there was still sent
        (FGuid? guid, int attempt) = CountRequest(path);
        int inFlight = Interlocked.Increment(ref _inFlight);

        try
        {
            int peak;
            while (inFlight > (peak = Volatile.Read(ref _peakInFlight)) && Interlocked.CompareExchange(ref _peakInFlight, inFlight, peak) != peak)
            {
            }

            int holdUntil = Volatile.Read(ref _holdUntilInFlight);
            if (holdUntil > 0)
            {
                if (inFlight >= holdUntil)
                    _released.TrySetResult();
                await _released.Task.WaitAsync(cancellationToken);
            }

            return await ServeAsync(path, guid, attempt, cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    /// <returns>The chunk GUID of a chunk request, and the per-resource attempt number of the request.</returns>
    private (FGuid? Guid, int Attempt) CountRequest(string path)
    {
        if (!path.EndsWith(".chunk", StringComparison.Ordinal))
            return (null, _blobRequests.AddOrUpdate(path, 1, static (_, count) => count + 1));

        FGuid guid = new(path.AsSpan(path.Length - ".chunk".Length - 32, 32));
        return (guid, _chunkRequests.AddOrUpdate(guid, 1, static (_, count) => count + 1));
    }

    private async Task<HttpResponseMessage> ServeAsync(string path, FGuid? guid, int attempt, CancellationToken cancellationToken)
    {
        byte[]? body = guid is { } chunkGuid ? TestData.GetChunkFile(chunkGuid) : _blobs.GetValueOrDefault(path);

        if (Behavior is not null && body is not null)
        {
            HttpResponseMessage? replacement = await Behavior(new FakeRequest(path, guid, attempt, body), cancellationToken);
            if (replacement is not null)
                return replacement;
        }

        return body is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : Ok(body);
    }

    public static HttpResponseMessage Ok(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    public static HttpResponseMessage Status(HttpStatusCode statusCode, TimeSpan? retryAfter = null)
    {
        var response = new HttpResponseMessage(statusCode) { Content = new ByteArrayContent([]) };
        if (retryAfter is { } delay)
            response.Headers.RetryAfter = new RetryConditionHeaderValue(delay);
        return response;
    }

    /// <summary>Announces the full length but ends after half of the body.</summary>
    public static HttpResponseMessage EndsEarly(byte[] body)
    {
        var content = new StreamContent(new MemoryStream(body, 0, body.Length / 2));
        content.Headers.ContentLength = body.Length;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    /// <summary>Serves the body without a <c>Content-Length</c>: the stream is not seekable, so the length cannot be computed.</summary>
    public static HttpResponseMessage NoContentLength(byte[] body) =>
        new(HttpStatusCode.OK) { Content = new StreamContent(new ScriptedStream(body, body.Length, TimeSpan.Zero, stallAtEnd: false)) };

    /// <summary>Sends half of the body, then nothing until the request is cancelled.</summary>
    public static HttpResponseMessage Stalls(byte[] body)
    {
        var content = new StreamContent(new ScriptedStream(body, body.Length / 2, TimeSpan.Zero, stallAtEnd: true));
        content.Headers.ContentLength = body.Length;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    /// <summary>
    /// For <see cref="Behavior"/>: never answers, and once the request is cancelled takes <paramref name="stopDelay"/> to give up,
    /// like a connection that is slow to notice. A client that does not wait for its requests still sees this one in flight.
    /// </summary>
    public static async Task<HttpResponseMessage?> HangUntilCancelledAsync(TimeSpan stopDelay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await Task.Delay(stopDelay, CancellationToken.None);
            throw;
        }

        return null;
    }

    /// <summary>Sends the body in pieces with a pause before each one.</summary>
    public static HttpResponseMessage Trickles(byte[] body, int pieces, TimeSpan pause)
    {
        var content = new StreamContent(new ScriptedStream(body, (body.Length + pieces - 1) / pieces, pause, stallAtEnd: false));
        content.Headers.ContentLength = body.Length;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    /// <summary>
    /// Serves <c>body</c> in pieces of <c>pieceSize</c> bytes with a pause before each piece; with <c>stallAtEnd</c> it never
    /// gets past the first piece.
    /// </summary>
    private sealed class ScriptedStream : Stream
    {
        private readonly byte[] _body;
        private readonly int _pieceSize;
        private readonly TimeSpan _pause;
        private readonly bool _stall;
        private int _position;

        public ScriptedStream(byte[] body, int pieceSize, TimeSpan pause, bool stallAtEnd)
        {
            _body = body;
            _pieceSize = pieceSize;
            _pause = pause;
            _stall = stallAtEnd;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_stall && _position >= _pieceSize)
                await Task.Delay(Timeout.Infinite, cancellationToken);
            if (_pause > TimeSpan.Zero)
                await Task.Delay(_pause, cancellationToken);

            int count = Math.Min(Math.Min(buffer.Length, _pieceSize), _body.Length - _position);
            _body.AsSpan(_position, count).CopyTo(buffer.Span);
            _position += count;
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <param name="Path">The request path.</param>
/// <param name="Chunk">The chunk GUID for chunk requests.</param>
/// <param name="Attempt">1 for the first request of this resource, 2 for the first retry, ...</param>
/// <param name="Body">The body that would be served.</param>
internal sealed record FakeRequest(string Path, FGuid? Chunk, int Attempt, byte[] Body);

/// <summary>A temporary directory that is deleted on dispose.</summary>
internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } = Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "emp_tests", Guid.NewGuid().ToString("N"))).FullName;

    public string[] Files => Directory.GetFiles(Path);

    public void Dispose()
    {
        for (int i = 0; i < 5; i++)
        {
            try
            {
                Directory.Delete(Path, true);
                return;
            }
            catch (IOException) when (i < 4)
            {
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException) when (i < 4)
            {
                Thread.Sleep(50);
            }
        }
    }
}
