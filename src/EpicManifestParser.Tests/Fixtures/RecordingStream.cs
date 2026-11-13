namespace EpicManifestParser.Tests.Fixtures;

/// <summary>
/// A write-only destination that is not a <see cref="MemoryStream"/>: keeps what was written and the size of every write, can
/// fail a given write, and throws if writes overlap. Like many streams it ignores the cancellation token, so whatever it is
/// handed gets written.
/// </summary>
internal sealed class RecordingStream : Stream
{
    private readonly MemoryStream _data = new();
    private readonly List<int> _writeSizes = [];
    private int _writing;
    private int _flushes;

    /// <summary>The 1-based number of the write that throws <see cref="WriteFailure"/>, 0 for none.</summary>
    public int FailOnWrite { get; init; }

    public Exception WriteFailure { get; } = new IOException("destination failed");

    public byte[] Written
    {
        get
        {
            lock (_data)
                return _data.ToArray();
        }
    }

    public int[] WriteSizes
    {
        get
        {
            lock (_data)
                return [.. _writeSizes];
        }
    }

    public int Flushes => Volatile.Read(ref _flushes);

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _writing, 1) != 0)
            throw new InvalidOperationException("overlapping writes");

        try
        {
            // completes asynchronously like a real stream
            await Task.Yield();

            lock (_data)
            {
                if (_writeSizes.Count + 1 == FailOnWrite)
                    throw WriteFailure;

                _data.Write(buffer.Span);
                _writeSizes.Add(buffer.Length);
            }
        }
        finally
        {
            Volatile.Write(ref _writing, 0);
        }
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _flushes);
        return Task.CompletedTask;
    }

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("expected WriteAsync(ReadOnlyMemory)");
    public override void Flush() => throw new NotSupportedException("expected FlushAsync");
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
}
