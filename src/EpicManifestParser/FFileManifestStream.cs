using System.Buffers;
using System.Collections;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

using Microsoft.Win32.SafeHandles;

using OffiUtils;

namespace EpicManifestParser;
// ReSharper disable UseSymbolAlias

/// <summary>
/// A stream representing a <see cref="FFileManifest"/>
/// </summary>
/// <remarks>
/// <see cref="ReadAt(long,Span{byte})"/> and <see cref="ReadAtAsync(long,Memory{byte},CancellationToken)"/> do not use
/// <see cref="Position"/> and are safe to call concurrently.
/// </remarks>
public sealed class FFileManifestStream : RandomAccessStream
{
    /// <summary>
    /// The default for <see cref="MaxConcurrentPartReads"/>.
    /// </summary>
    internal const int DefaultMaxConcurrentPartReads = 16;

    private readonly FFileManifest _fileManifest;

    /// <summary>
    /// The maximum number of chunk parts one <see cref="ReadAtAsync(long,Memory{byte},CancellationToken)"/> or
    /// <see cref="ReadAt(long,Span{byte})"/> call loads at the same time. Internal so that tests can lower it.
    /// </summary>
    internal int MaxConcurrentPartReads { get; set; } = DefaultMaxConcurrentPartReads;

    /// <summary>Always <see langword="true"/></summary>
    public override bool CanRead => true;
    /// <summary>Always <see langword="true"/></summary>
    public override bool CanSeek => true;
    /// <summary>Always <see langword="false"/></summary>
    public override bool CanWrite => false;
    /// <summary>Gets the length/size of the stream</summary>
    public override long Length => _fileManifest.FileSize;

    private long _position;

    /// <summary>Gets or sets the current position within the stream.</summary>
    public override long Position
    {
        get => _position;
        set
        {
            if ((ulong)value > (ulong)Length)
                throw new ArgumentOutOfRangeException(nameof(Position), value, "Value is negative or exceeds the stream's length");

            _position = value;
        }
    }

    /// <summary>Gets the file name of the <see cref="FFileManifest"/> represented by this stream</summary>
    public string FileName => _fileManifest.FileName;

    internal FFileManifestStream(FFileManifest fileManifest)
    {
        if (string.IsNullOrEmpty(fileManifest.Manifest.Options.ChunkBaseUrl))
            throw new ArgumentException("Missing ChunkBaseUrl");
        if (fileManifest.Manifest.Meta.bIsFileData)
            throw new NotSupportedException("File-data manifests are not supported");

        _fileManifest = fileManifest;
    }

    /// <summary>
    /// Asynchronously saves the current stream to another stream, starting at its current position.
    /// </summary>
    /// <remarks>
    /// A <see cref="MemoryStream"/> with an accessible buffer whose position is at or behind its end is filled in parallel;
    /// afterwards it looks as if the data had been written through <see cref="Stream.Write(byte[],int,int)"/>, and if the save
    /// fails or is cancelled, the stream has its old length, content and position again. Any other stream, and a
    /// <see cref="MemoryStream"/> whose content would be overwritten, is written strictly in file order, one chunk part per
    /// <see cref="Stream.WriteAsync(ReadOnlyMemory{byte},CancellationToken)"/> call, while up to
    /// <paramref name="maxDegreeOfParallelism"/> chunk parts are loaded ahead into pooled buffers. If the save fails or is
    /// cancelled, what has been written to such a stream is a prefix of the file.
    /// </remarks>
    /// <param name="destination">The destination stream.</param>
    /// <param name="progressCallback">The progress change callback. (optional)</param>
    /// <param name="userState">The user state for the <paramref name="progressCallback"/>. (optional)</param>
    /// <param name="maxDegreeOfParallelism">
    /// The maximum number of concurrent tasks saving/downloading to the destination, defaults to
    /// <see cref="Environment.ProcessorCount"/>, as does <c>-1</c>. (optional)
    /// </param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the entire save operation.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxDegreeOfParallelism"/> is <c>0</c> or less than <c>-1</c>.</exception>
    /// <exception cref="HttpRequestException">A chunk fails to download, also after <see cref="ManifestParseOptions.MaxDownloadRetries"/> retries for transient errors.</exception>
    /// <exception cref="TimeoutException">The last attempt to download a chunk received no data for <see cref="ManifestParseOptions.DownloadIdleTimeout"/>.</exception>
    /// <exception cref="InvalidDataException">The last download of a chunk is corrupt: its header, size or hash does not match the manifest.</exception>
    /// <exception cref="FileLoadException">The last download of a chunk has an invalid magic or fails to decompress.</exception>
    /// <exception cref="NotSupportedException">A chunk is encrypted or stored in an unknown way.</exception>
    /// <exception cref="OperationCanceledException">The <paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task SaveToAsync(Stream destination, Action<SaveProgressChangedEventArgs>? progressCallback,
        object? userState = null, int? maxDegreeOfParallelism = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);

        // the same values as ParallelOptions.MaxDegreeOfParallelism, which the other save methods go through;
        // checked, like the token, before the destination is touched
        int readAhead = maxDegreeOfParallelism ?? Environment.ProcessorCount;
        if (readAhead == -1)
            readAhead = Environment.ProcessorCount;
        else if (readAhead < 1)
            throw new ArgumentOutOfRangeException(nameof(maxDegreeOfParallelism), readAhead, "Value must be -1 or greater than 0");
        cancellationToken.ThrowIfCancellationRequested();

        if (destination is MemoryStream ms && TryPrepareMemoryStream(ms, out Memory<byte> target, out long start, out long originalLength))
        {
            try
            {
                await SaveBytesAsync(target, progressCallback, userState, maxDegreeOfParallelism, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // as if nothing had been written: SetLength pulls a position behind the old length back, so restore it as well
                if (ms.Length > originalLength)
                    ms.SetLength(originalLength);
                ms.Position = start;
                throw;
            }

            ms.Position = start + Length;
            return;
        }

        if (_fileManifest.ChunkPartsArray.Length != 0)
        {
            var downloadState = new DownloadState<Stream>(destination, _fileManifest, userState, progressCallback);
            await SaveInOrderAsync(downloadState, readAhead, cancellationToken).ConfigureAwait(false);
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the chunk parts to the destination in file order, one at a time, while up to <paramref name="readAhead"/> of them
    /// are read ahead into pooled buffers.
    /// </summary>
    /// <remarks>
    /// On the first failure (a read, the destination, the progress callback) or the caller's cancellation no further part is
    /// started or written, the reads in flight are cancelled, and only when all of them have finished do their buffers go back
    /// to the pool and the exception is thrown.
    /// </remarks>
    private async Task SaveInOrderAsync(DownloadState<Stream> downloadState, int readAhead, CancellationToken cancellationToken)
    {
        Stream destination = downloadState.Destination;
        FChunkPart[] chunkParts = _fileManifest.ChunkPartsArray;
        FBuildPatchAppManifest manifest = _fileManifest.Manifest;
        int slots = Math.Min(readAhead, chunkParts.Length);

        uint maxPartSize = 0;
        foreach (FChunkPart chunkPart in chunkParts)
            maxPartSize = Math.Max(maxPartSize, chunkPart.Size);

        using var reads = new ConcurrentPartReads(cancellationToken);
        // part i uses slot i % slots: its buffer and its pending read
        byte[]?[] buffers = new byte[slots][];
        Task?[] pending = new Task[slots];

        try
        {
            for (int i = 0; i < slots && !reads.IsStopped; i++)
            {
                byte[] buffer = buffers[i] = ArrayPool<byte>.Shared.Rent((int)maxPartSize);
                pending[i] = reads.ReadAsync(chunkParts[i], buffer, manifest);
            }

            for (int i = 0; i < chunkParts.Length && !reads.IsStopped; i++)
            {
                int slot = i % slots;
                // completes successfully even when the read failed, the failure stops the reads instead
                await pending[slot]!.ConfigureAwait(false);
                if (reads.IsStopped)
                    break;

                uint size = chunkParts[i].Size;
                await destination.WriteAsync(buffers[slot].AsMemory(0, (int)size), reads.Token).ConfigureAwait(false);
                downloadState.OnBytesWritten(size);

                // the slot is free again: read the part that is a full window ahead
                int ahead = i + slots;
                if (ahead < chunkParts.Length && !reads.IsStopped)
                    pending[slot] = reads.ReadAsync(chunkParts[ahead], buffers[slot]!, manifest);
            }
        }
        catch (Exception ex)
        {
            // the destination or the progress callback
            reads.Fail(ex);
        }
        finally
        {
            // a read that is still running writes into its buffer, which must not go back to the pool before it is done
            foreach (Task? read in pending)
            {
                if (read is not null)
                    await read.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }

            foreach (byte[]? buffer in buffers)
            {
                if (buffer is not null)
                    ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        reads.ThrowIfStopped(cancellationToken);
    }

    /// <inheritdoc cref="SaveToAsync(Stream,Action{SaveProgressChangedEventArgs}?,object?,int?,CancellationToken)"/>
    public Task SaveToAsync(Stream destination, int? maxDegreeOfParallelism = null, CancellationToken cancellationToken = default)
    {
        return SaveToAsync(destination, null, null, maxDegreeOfParallelism, cancellationToken);
    }

    /// <summary>
    /// Makes room for the file at the current position of a <see cref="MemoryStream"/> whose buffer can be written directly.
    /// </summary>
    /// <returns><see langword="false"/> if the sequential path has to be used instead.</returns>
    private bool TryPrepareMemoryStream(MemoryStream ms, out Memory<byte> target, out long start, out long originalLength)
    {
        target = default;
        start = 0;
        originalLength = 0;

        // subclasses may track their contents differently, writing behind their back is only safe for MemoryStream itself;
        // an empty file writes nothing, which the sequential path already does
        if (Length == 0 || ms.GetType() != typeof(MemoryStream) || !ms.CanWrite || !ms.TryGetBuffer(out _))
            return false;

        start = ms.Position;
        originalLength = ms.Length;
        // parts finish in any order: a failed save can only be taken back completely when nothing but new space was written,
        // so content that would be overwritten goes the sequential way, which leaves a prefix of the file like in any stream
        if (start < originalLength)
            return false;

        long end = start + Length;
        if (end > Array.MaxLength)
            return false;

        try
        {
            // grows the buffer if needed and zero-fills a gap between the old length and the position, like Write does
            ms.SetLength(end);
        }
        catch (NotSupportedException)
        {
            // not expandable and too small
            return false;
        }

        // SetLength may have replaced the buffer
        ms.TryGetBuffer(out ArraySegment<byte> buffer);
        target = buffer.Array.AsMemory(buffer.Offset + (int)start, (int)Length);
        return true;
    }

    /// <summary>
    /// Asynchronously saves the current stream to a buffer.
    /// </summary>
    /// <param name="destination">The destination buffer, at least <see cref="Length"/> bytes long.</param>
    /// <param name="progressCallback">The progress change callback. (optional)</param>
    /// <param name="userState">The user state for the <paramref name="progressCallback"/>. (optional)</param>
    /// <param name="maxDegreeOfParallelism">The maximum number of concurrent tasks saving/downloading to the destination. (optional)</param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the entire save operation.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="destination"/> is shorter than <see cref="Length"/>.</exception>
    /// <exception cref="HttpRequestException">A chunk fails to download, also after <see cref="ManifestParseOptions.MaxDownloadRetries"/> retries for transient errors.</exception>
    /// <exception cref="TimeoutException">The last attempt to download a chunk received no data for <see cref="ManifestParseOptions.DownloadIdleTimeout"/>.</exception>
    /// <exception cref="InvalidDataException">The last download of a chunk is corrupt: its header, size or hash does not match the manifest.</exception>
    /// <exception cref="FileLoadException">The last download of a chunk has an invalid magic or fails to decompress.</exception>
    /// <exception cref="NotSupportedException">A chunk is encrypted or stored in an unknown way.</exception>
    /// <exception cref="OperationCanceledException">The <paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task SaveBytesAsync(Memory<byte> destination, Action<SaveProgressChangedEventArgs>? progressCallback,
        object? userState = null, int? maxDegreeOfParallelism = null, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, Length);

        var downloadState = new DownloadState<Memory<byte>>(destination, _fileManifest, userState, progressCallback);
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = maxDegreeOfParallelism ?? Environment.ProcessorCount,
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(downloadState, parallelOptions, SaveAsync).ConfigureAwait(false);

        return;

        static async ValueTask SaveAsync(ChunkWithOffset<Memory<byte>> part, CancellationToken token)
        {
            Memory<byte> target = part.State.Destination.Slice((int)part.Offset, (int)part.ChunkPartSize);
            await part.Chunk.ReadDataAsync(target, GetChunkOffset(part.ChunkPartOffset, 0), part.State.FileManifest.Manifest, token).ConfigureAwait(false);
            part.State.OnBytesWritten(part.ChunkPartSize);
        }
    }

    /// <inheritdoc cref="SaveBytesAsync(Memory{byte},Action{SaveProgressChangedEventArgs}?,object?,int?,CancellationToken)"/>
    public Task SaveBytesAsync(Memory<byte> destination, int? maxDegreeOfParallelism = null, CancellationToken cancellationToken = default)
    {
        return SaveBytesAsync(destination, null, null, maxDegreeOfParallelism, cancellationToken);
    }

    /// <inheritdoc cref="SaveBytesAsync(Memory{byte},Action{SaveProgressChangedEventArgs}?,object?,int?,CancellationToken)"/>
    public Task SaveBytesAsync(byte[] destination, Action<SaveProgressChangedEventArgs>? progressCallback,
        object? userState = null, int? maxDegreeOfParallelism = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        return SaveBytesAsync(destination.AsMemory(), progressCallback, userState, maxDegreeOfParallelism, cancellationToken);
    }

    /// <inheritdoc cref="SaveBytesAsync(Memory{byte},Action{SaveProgressChangedEventArgs}?,object?,int?,CancellationToken)"/>
    public Task SaveBytesAsync(byte[] destination, int? maxDegreeOfParallelism = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        return SaveBytesAsync(destination.AsMemory(), null, null, maxDegreeOfParallelism, cancellationToken);
    }

    /// <summary>
    /// Asynchronously saves the current stream to a buffer.
    /// </summary>
    /// <param name="progressCallback">The progress change callback. (optional)</param>
    /// <param name="userState">The user state for the <paramref name="progressCallback"/>. (optional)</param>
    /// <param name="maxDegreeOfParallelism">The maximum number of concurrent tasks saving/downloading to the destination. (optional)</param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the entire save operation.</returns>
    /// <exception cref="HttpRequestException">A chunk fails to download, also after <see cref="ManifestParseOptions.MaxDownloadRetries"/> retries for transient errors.</exception>
    /// <exception cref="TimeoutException">The last attempt to download a chunk received no data for <see cref="ManifestParseOptions.DownloadIdleTimeout"/>.</exception>
    /// <exception cref="InvalidDataException">The last download of a chunk is corrupt: its header, size or hash does not match the manifest.</exception>
    /// <exception cref="FileLoadException">The last download of a chunk has an invalid magic or fails to decompress.</exception>
    /// <exception cref="NotSupportedException">A chunk is encrypted or stored in an unknown way.</exception>
    /// <exception cref="OperationCanceledException">The <paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<byte[]> SaveBytesAsync(Action<SaveProgressChangedEventArgs> progressCallback, object? userState = null,
        int? maxDegreeOfParallelism = null, CancellationToken cancellationToken = default)
    {
        byte[] destination = new byte[Length];
        await SaveBytesAsync(destination.AsMemory(), progressCallback, userState, maxDegreeOfParallelism, cancellationToken).ConfigureAwait(false);
        return destination;
    }

    /// <summary>
    /// Asynchronously saves the current stream to a buffer.
    /// </summary>
    /// <param name="maxDegreeOfParallelism">The maximum number of concurrent tasks saving/downloading to the destination. (optional)</param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the entire save operation.</returns>
    /// <exception cref="HttpRequestException">A chunk fails to download, also after <see cref="ManifestParseOptions.MaxDownloadRetries"/> retries for transient errors.</exception>
    /// <exception cref="TimeoutException">The last attempt to download a chunk received no data for <see cref="ManifestParseOptions.DownloadIdleTimeout"/>.</exception>
    /// <exception cref="InvalidDataException">The last download of a chunk is corrupt: its header, size or hash does not match the manifest.</exception>
    /// <exception cref="FileLoadException">The last download of a chunk has an invalid magic or fails to decompress.</exception>
    /// <exception cref="NotSupportedException">A chunk is encrypted or stored in an unknown way.</exception>
    /// <exception cref="OperationCanceledException">The <paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<byte[]> SaveBytesAsync(int? maxDegreeOfParallelism = null, CancellationToken cancellationToken = default)
    {
        byte[] destination = new byte[Length];
        await SaveBytesAsync(destination.AsMemory(), null, null, maxDegreeOfParallelism, cancellationToken).ConfigureAwait(false);
        return destination;
    }

    /// <summary>
    /// Asynchronously saves the current stream to a file.
    /// </summary>
    /// <remarks>
    /// The parts are written in parallel to a temporary file in the directory of <paramref name="path"/>
    /// (<c>{path}.{random}.tmp</c>), which replaces <paramref name="path"/> once every part has been written. A file that already
    /// exists at <paramref name="path"/> stays untouched until then: if the save fails or is cancelled, the temporary file is deleted
    /// and the destination is as it was. A process that is killed during the save can leave the temporary file behind, never a
    /// partially written file under <paramref name="path"/>. The arguments and the token are checked before any file is created.
    /// </remarks>
    /// <param name="path">The path of the destination file.</param>
    /// <param name="progressCallback">The progress change callback. (optional)</param>
    /// <param name="userState">The user state for the <paramref name="progressCallback"/>. (optional)</param>
    /// <param name="maxDegreeOfParallelism">
    /// The maximum number of concurrent tasks saving/downloading to the destination, defaults to
    /// <see cref="Environment.ProcessorCount"/>, as does <c>-1</c>. (optional)
    /// </param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the entire save operation.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxDegreeOfParallelism"/> is <c>0</c> or less than <c>-1</c>.</exception>
    /// <exception cref="HttpRequestException">A chunk fails to download, also after <see cref="ManifestParseOptions.MaxDownloadRetries"/> retries for transient errors.</exception>
    /// <exception cref="TimeoutException">The last attempt to download a chunk received no data for <see cref="ManifestParseOptions.DownloadIdleTimeout"/>.</exception>
    /// <exception cref="InvalidDataException">The last download of a chunk is corrupt: its header, size or hash does not match the manifest.</exception>
    /// <exception cref="FileLoadException">The last download of a chunk has an invalid magic or fails to decompress.</exception>
    /// <exception cref="NotSupportedException">A chunk is encrypted or stored in an unknown way.</exception>
    /// <exception cref="OperationCanceledException">The <paramref name="cancellationToken"/> was cancelled.</exception>
    /// <exception cref="IOException">The file or its temporary file cannot be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The file or its directory cannot be written.</exception>
    public async Task SaveFileAsync(string path, Action<SaveProgressChangedEventArgs>? progressCallback, object? userState = null,
        int? maxDegreeOfParallelism = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        // validates maxDegreeOfParallelism before anything touches the file system
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = maxDegreeOfParallelism ?? Environment.ProcessorCount,
            CancellationToken = cancellationToken
        };
        cancellationToken.ThrowIfCancellationRequested();

        string tempPath = AtomicFile.GetTempPath(path);
        try
        {
            using (SafeFileHandle destination = File.OpenHandle(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, FileOptions.Asynchronous, Length))
            {
                var downloadState = new DownloadState<SafeFileHandle>(destination, _fileManifest, userState, progressCallback);
                await Parallel.ForEachAsync(downloadState, parallelOptions, SaveAsync).ConfigureAwait(false);
            }

            File.Move(tempPath, path, true);
        }
        catch
        {
            AtomicFile.TryDelete(tempPath);
            throw;
        }

        return;

        static async ValueTask SaveAsync(ChunkWithOffset<SafeFileHandle> part, CancellationToken token)
        {
            int size = (int)part.ChunkPartSize;
            byte[] poolBuffer = ArrayPool<byte>.Shared.Rent(size);

            try
            {
                Memory<byte> partBuffer = poolBuffer.AsMemory(0, size);
                await part.Chunk.ReadDataAsync(partBuffer, GetChunkOffset(part.ChunkPartOffset, 0), part.State.FileManifest.Manifest, token).ConfigureAwait(false);
                await RandomAccess.WriteAsync(part.State.Destination, partBuffer, part.Offset, token).ConfigureAwait(false);
                part.State.OnBytesWritten(part.ChunkPartSize);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(poolBuffer);
            }
        }
    }

    /// <inheritdoc cref="SaveFileAsync(string,Action{SaveProgressChangedEventArgs}?,object?,int?,CancellationToken)"/>
    public Task SaveFileAsync(string path, int? maxDegreeOfParallelism = null, CancellationToken cancellationToken = default)
    {
        return SaveFileAsync(path, null, null, maxDegreeOfParallelism, cancellationToken);
    }

    /// <summary>
    /// Reads a sequence of bytes from the current stream and advances the position by the number of bytes read.
    /// </summary>
    /// <param name="buffer">The region of memory to write the data into.</param>
    /// <returns>
    /// The total number of bytes written into the <paramref name="buffer"/>,
    /// less than its length only when the end of the stream is reached.
    /// </returns>
    /// <exception cref="HttpRequestException">A chunk fails to download, also after <see cref="ManifestParseOptions.MaxDownloadRetries"/> retries for transient errors.</exception>
    /// <exception cref="TimeoutException">The last attempt to download a chunk received no data for <see cref="ManifestParseOptions.DownloadIdleTimeout"/>.</exception>
    /// <exception cref="InvalidDataException">The last download of a chunk is corrupt: its header, size or hash does not match the manifest.</exception>
    /// <exception cref="FileLoadException">The last download of a chunk has an invalid magic or fails to decompress.</exception>
    /// <exception cref="NotSupportedException">A chunk is encrypted or stored in an unknown way.</exception>
    public override int Read(Span<byte> buffer)
    {
        int bytesRead = ReadAt(_position, buffer);
        _position += bytesRead;
        return bytesRead;
    }

    /// <summary>
    /// Reads a sequence of bytes from the current stream and advances the position by the number of bytes read.
    /// </summary>
    /// <param name="buffer">
    /// When this method returns, contains the specified byte array with the values between
    /// <paramref name="offset"></paramref> and (<paramref name="offset"></paramref> + <paramref name="count"></paramref> - 1)
    /// replaced by the characters read from the current stream.
    /// </param>
    /// <param name="offset">The zero-based byte offset in <paramref name="buffer"/> at which to begin storing data from the current stream.</param>
    /// <param name="count">The maximum number of bytes to read.</param>
    /// <returns>The total number of bytes written into the <paramref name="buffer"/>.</returns>
    /// <exception cref="HttpRequestException">A chunk fails to download, also after <see cref="ManifestParseOptions.MaxDownloadRetries"/> retries for transient errors.</exception>
    /// <exception cref="TimeoutException">The last attempt to download a chunk received no data for <see cref="ManifestParseOptions.DownloadIdleTimeout"/>.</exception>
    /// <exception cref="InvalidDataException">The last download of a chunk is corrupt: its header, size or hash does not match the manifest.</exception>
    /// <exception cref="FileLoadException">The last download of a chunk has an invalid magic or fails to decompress.</exception>
    /// <exception cref="NotSupportedException">A chunk is encrypted or stored in an unknown way.</exception>
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(new Span<byte>(buffer, offset, count));
    }

    /// <summary>
    /// Asynchronously reads a sequence of bytes from the current stream and advances the position by the number of bytes read.
    /// </summary>
    /// <remarks>
    /// A read that spans several chunk parts loads them in parallel, see <see cref="ReadAtAsync(long,Memory{byte},CancellationToken)"/>.
    /// </remarks>
    /// <param name="buffer">The region of memory to write the data into.</param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    /// <returns>
    /// The total number of bytes written into the <paramref name="buffer"/>,
    /// less than its length only when the end of the stream is reached.
    /// </returns>
    /// <exception cref="HttpRequestException">A chunk fails to download, also after <see cref="ManifestParseOptions.MaxDownloadRetries"/> retries for transient errors.</exception>
    /// <exception cref="TimeoutException">The last attempt to download a chunk received no data for <see cref="ManifestParseOptions.DownloadIdleTimeout"/>.</exception>
    /// <exception cref="InvalidDataException">The last download of a chunk is corrupt: its header, size or hash does not match the manifest.</exception>
    /// <exception cref="FileLoadException">The last download of a chunk has an invalid magic or fails to decompress.</exception>
    /// <exception cref="NotSupportedException">A chunk is encrypted or stored in an unknown way.</exception>
    /// <exception cref="OperationCanceledException">The <paramref name="cancellationToken"/> was cancelled.</exception>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int bytesRead = await ReadAtAsync(_position, buffer, cancellationToken).ConfigureAwait(false);
        _position += bytesRead;
        return bytesRead;
    }

    /// <summary>
    /// Asynchronously reads a sequence of bytes from the current stream and advances the position by the number of bytes read.
    /// </summary>
    /// <remarks>
    /// A read that spans several chunk parts loads them in parallel, see <see cref="ReadAtAsync(long,Memory{byte},CancellationToken)"/>.
    /// </remarks>
    /// <param name="buffer">
    /// When this method returns, contains the specified byte array with the values between
    /// <paramref name="offset"></paramref> and (<paramref name="offset"></paramref> + <paramref name="count"></paramref> - 1)
    /// replaced by the characters read from the current stream.
    /// </param>
    /// <param name="offset">The zero-based byte offset in <paramref name="buffer"/> at which to begin storing data from the current stream.</param>
    /// <param name="count">The maximum number of bytes to read.</param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    /// <returns>The total number of bytes written into the <paramref name="buffer"/>.</returns>
    /// <exception cref="HttpRequestException">A chunk fails to download, also after <see cref="ManifestParseOptions.MaxDownloadRetries"/> retries for transient errors.</exception>
    /// <exception cref="TimeoutException">The last attempt to download a chunk received no data for <see cref="ManifestParseOptions.DownloadIdleTimeout"/>.</exception>
    /// <exception cref="InvalidDataException">The last download of a chunk is corrupt: its header, size or hash does not match the manifest.</exception>
    /// <exception cref="FileLoadException">The last download of a chunk has an invalid magic or fails to decompress.</exception>
    /// <exception cref="NotSupportedException">A chunk is encrypted or stored in an unknown way.</exception>
    /// <exception cref="OperationCanceledException">The <paramref name="cancellationToken"/> was cancelled.</exception>
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(new Memory<byte>(buffer, offset, count), cancellationToken).AsTask();
    }

    /// <summary>
    /// Reads a sequence of bytes from the given <paramref name="position"/> of the current stream. Does not use or change <see cref="Position"/>.
    /// </summary>
    /// <remarks>
    /// Cached chunks (cache directory, memory cache) are read synchronously on the calling thread. Chunks that have to be
    /// downloaded block on the asynchronous download: a single one directly, several of one read in parallel like
    /// <see cref="ReadAtAsync(long,Memory{byte},CancellationToken)"/> does, with one wait for all of them. If one of them fails,
    /// the others are cancelled, and the method throws only after none of them runs anymore.
    /// </remarks>
    /// <param name="position">The position to begin reading from.</param>
    /// <param name="buffer">The region of memory to write the data into.</param>
    /// <returns>
    /// The total number of bytes written into the <paramref name="buffer"/>,
    /// less than its length only when the end of the stream is reached; <c>0</c> at or past the end.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="position"/> is negative.</exception>
    /// <exception cref="HttpRequestException">A chunk fails to download, also after <see cref="ManifestParseOptions.MaxDownloadRetries"/> retries for transient errors.</exception>
    /// <exception cref="TimeoutException">The last attempt to download a chunk received no data for <see cref="ManifestParseOptions.DownloadIdleTimeout"/>.</exception>
    /// <exception cref="InvalidDataException">The last download of a chunk is corrupt: its header, size or hash does not match the manifest.</exception>
    /// <exception cref="FileLoadException">The last download of a chunk has an invalid magic or fails to decompress.</exception>
    /// <exception cref="NotSupportedException">A chunk is encrypted or stored in an unknown way.</exception>
    public override int ReadAt(long position, Span<byte> buffer)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        if (buffer.IsEmpty || !_fileManifest.TryFindChunkPart(position, out int chunkPartIndex, out uint chunkPartOffset))
            return 0;

        ReadOnlySpan<FChunkPart> chunkParts = _fileManifest.ChunkPartsArray;
        FBuildPatchAppManifest manifest = _fileManifest.Manifest;
        int bytesRead = 0;

        for (; chunkPartIndex < chunkParts.Length && bytesRead < buffer.Length; chunkPartIndex++)
        {
            ref readonly FChunkPart chunkPart = ref chunkParts[chunkPartIndex];
            int count = (int)Math.Min(chunkPart.Size - chunkPartOffset, (uint)(buffer.Length - bytesRead));
            if (count != 0 && !chunkPart.Chunk.TryReadLocal(buffer.Slice(bytesRead, count), GetChunkOffset(chunkPart.Offset, chunkPartOffset), manifest))
            {
                // this part is not local: the rest of the read collects the parts to download and loads them together
                return ReadAtWithDownloads(position, buffer, chunkPartIndex, bytesRead);
            }

            bytesRead += count;
            chunkPartOffset = 0;
        }

        return bytesRead;
    }

    /// <summary>
    /// The rest of <see cref="ReadAt(long,Span{byte})"/> from the part at <paramref name="chunkPartIndex"/>, which is not local:
    /// the following parts that are local are read synchronously, the ones that are not are downloaded, several of them in parallel.
    /// </summary>
    /// <param name="position">The position of the read.</param>
    /// <param name="buffer">The whole buffer of the read.</param>
    /// <param name="chunkPartIndex">The first part that is not local.</param>
    /// <param name="bytesRead">The bytes of the read before that part, already in <paramref name="buffer"/>.</param>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private int ReadAtWithDownloads(long position, Span<byte> buffer, int chunkPartIndex, int bytesRead)
    {
        FChunkPart[] chunkParts = _fileManifest.ChunkPartsArray;
        FBuildPatchAppManifest manifest = _fileManifest.Manifest;
        // every pending part covers at least one byte of the buffer
        PendingPart[] pending = ArrayPool<PendingPart>.Shared.Rent(Math.Min(chunkParts.Length - chunkPartIndex, buffer.Length - bytesRead));
        int pendingCount = 0;
        int pendingBytes = 0;

        try
        {
            for (int firstIndex = chunkPartIndex; chunkPartIndex < chunkParts.Length && bytesRead < buffer.Length; chunkPartIndex++)
            {
                ref readonly FChunkPart chunkPart = ref chunkParts[chunkPartIndex];
                // only the first part of the read can begin before the read
                uint offsetInPart = (uint)(position + bytesRead - chunkPart.FileOffset);
                int count = (int)Math.Min(chunkPart.Size - offsetInPart, (uint)(buffer.Length - bytesRead));
                if (count != 0)
                {
                    int chunkOffset = GetChunkOffset(chunkPart.Offset, offsetInPart);
                    // the first one was not local a moment ago
                    if (chunkPartIndex == firstIndex || !chunkPart.Chunk.TryReadLocal(buffer.Slice(bytesRead, count), chunkOffset, manifest))
                    {
                        pending[pendingCount++] = new PendingPart(chunkPartIndex, chunkOffset, count, bytesRead, pendingBytes);
                        pendingBytes += count;
                    }
                }

                bytesRead += count;
            }

            if (pendingCount == 1)
            {
                PendingPart part = pending[0];
                chunkParts[part.Index].Chunk.ReadDataBlocking(buffer.Slice(part.DestinationOffset, part.Count), part.ChunkOffset, manifest);
            }
            else
            {
                LoadPendingParts(pending, pendingCount, pendingBytes, buffer);
            }
        }
        finally
        {
            ArrayPool<PendingPart>.Shared.Return(pending);
        }

        return bytesRead;
    }

    /// <summary>
    /// Downloads the <paramref name="pending"/> parts in parallel, at most <see cref="MaxConcurrentPartReads"/> at a time, and
    /// copies them into <paramref name="buffer"/> once all of them have loaded. A span cannot cross an await, so they load into one
    /// pooled buffer first, which goes back to the pool only after none of the downloads runs anymore.
    /// </summary>
    private void LoadPendingParts(PendingPart[] pending, int pendingCount, int pendingBytes, Span<byte> buffer)
    {
        byte[] data = ArrayPool<byte>.Shared.Rent(pendingBytes);
        try
        {
            PendingPartsRead.Run(_fileManifest, pending, pendingCount, data, MaxConcurrentPartReads);

            foreach (ref readonly PendingPart part in pending.AsSpan(0, pendingCount))
                data.AsSpan(part.BufferOffset, part.Count).CopyTo(buffer.Slice(part.DestinationOffset));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(data);
        }
    }

    /// <summary>
    /// The offset into the chunk of the byte <paramref name="offsetInPart"/> bytes into a part at <paramref name="partOffset"/>,
    /// computed in 64 bits: a corrupt part must not wrap around to the start of the chunk.
    /// </summary>
    /// <exception cref="InvalidDataException">The offset lies beyond any chunk.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int GetChunkOffset(uint partOffset, long offsetInPart)
    {
        long chunkOffset = partOffset + offsetInPart;
        if ((ulong)chunkOffset > int.MaxValue)
            ThrowChunkOffsetOutOfRange(partOffset, offsetInPart);
        return (int)chunkOffset;
    }

    [DoesNotReturn]
    private static void ThrowChunkOffsetOutOfRange(uint partOffset, long offsetInPart) =>
        throw new InvalidDataException($"Chunk part offset {partOffset} + {offsetInPart} lies beyond any chunk");

    /// <summary>
    /// Reads a sequence of bytes from the given <paramref name="position"/> of the current stream. Does not use or change <see cref="Position"/>.
    /// </summary>
    /// <param name="position">The position to begin reading from.</param>
    /// <param name="buffer">
    /// When this method returns, contains the specified byte array with the values between
    /// <paramref name="offset"></paramref> and (<paramref name="offset"></paramref> + <paramref name="count"></paramref> - 1)
    /// replaced by the characters read from the current stream.
    /// </param>
    /// <param name="offset">The zero-based byte offset in <paramref name="buffer"/> at which to begin storing data from the current stream.</param>
    /// <param name="count">The maximum number of bytes to read.</param>
    /// <returns>The total number of bytes written into the <paramref name="buffer"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="position"/> is negative.</exception>
    /// <exception cref="HttpRequestException">A chunk fails to download, also after <see cref="ManifestParseOptions.MaxDownloadRetries"/> retries for transient errors.</exception>
    /// <exception cref="TimeoutException">The last attempt to download a chunk received no data for <see cref="ManifestParseOptions.DownloadIdleTimeout"/>.</exception>
    /// <exception cref="InvalidDataException">The last download of a chunk is corrupt: its header, size or hash does not match the manifest.</exception>
    /// <exception cref="FileLoadException">The last download of a chunk has an invalid magic or fails to decompress.</exception>
    /// <exception cref="NotSupportedException">A chunk is encrypted or stored in an unknown way.</exception>
    public override int ReadAt(long position, byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAt(position, new Span<byte>(buffer, offset, count));
    }

    /// <summary>
    /// Asynchronously reads a sequence of bytes from the given <paramref name="position"/> of the current stream.
    /// Does not use or change <see cref="Position"/>.
    /// </summary>
    /// <remarks>
    /// A read that spans several chunk parts loads them in parallel, up to 16 at a time, each one directly into its own part of
    /// <paramref name="buffer"/>. If one of them fails or the read is cancelled, the others are cancelled too, and the method
    /// completes only after none of them writes into <paramref name="buffer"/> anymore.
    /// </remarks>
    /// <param name="position">The position to begin reading from.</param>
    /// <param name="buffer">The region of memory to write the data into.</param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    /// <returns>
    /// The total number of bytes written into the <paramref name="buffer"/>,
    /// less than its length only when the end of the stream is reached; <c>0</c> at or past the end.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="position"/> is negative.</exception>
    /// <exception cref="HttpRequestException">A chunk fails to download, also after <see cref="ManifestParseOptions.MaxDownloadRetries"/> retries for transient errors.</exception>
    /// <exception cref="TimeoutException">The last attempt to download a chunk received no data for <see cref="ManifestParseOptions.DownloadIdleTimeout"/>.</exception>
    /// <exception cref="InvalidDataException">The last download of a chunk is corrupt: its header, size or hash does not match the manifest.</exception>
    /// <exception cref="FileLoadException">The last download of a chunk has an invalid magic or fails to decompress.</exception>
    /// <exception cref="NotSupportedException">A chunk is encrypted or stored in an unknown way.</exception>
    /// <exception cref="OperationCanceledException">The <paramref name="cancellationToken"/> was cancelled.</exception>
    public override async ValueTask<int> ReadAtAsync(long position, Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        cancellationToken.ThrowIfCancellationRequested();

        if (buffer.IsEmpty || !_fileManifest.TryFindChunkPart(position, out int chunkPartIndex, out uint chunkPartOffset))
            return 0;

        FChunkPart[] chunkParts = _fileManifest.ChunkPartsArray;
        FChunkPart chunkPart = chunkParts[chunkPartIndex];
        int bytesRead = (int)Math.Min(chunkPart.Size - chunkPartOffset, (uint)buffer.Length);

        if (bytesRead == buffer.Length || chunkPartIndex == chunkParts.Length - 1)
        {
            // the common small read stays within one chunk part
            await chunkPart.Chunk.ReadDataAsync(buffer[..bytesRead], GetChunkOffset(chunkPart.Offset, chunkPartOffset), _fileManifest.Manifest,
                cancellationToken).ConfigureAwait(false);
            return bytesRead;
        }

        long end = position + buffer.Length;
        int lastIndex = _fileManifest.TryFindChunkPart(end - 1, out int endIndex, out _) ? endIndex : chunkParts.Length - 1;
        FChunkPart lastPart = chunkParts[lastIndex];
        bytesRead = (int)(Math.Min(end, lastPart.FileOffset + lastPart.Size) - position);

        // returns a ValueTask like ReadDataAsync, so the state machine of the single part path does not grow by another awaiter
        await MultiPartRead.RunAsync(_fileManifest, position, buffer, chunkPartIndex, lastIndex, MaxConcurrentPartReads,
            cancellationToken).ConfigureAwait(false);
        return bytesRead;
    }

    /// <summary>
    /// Asynchronously reads a sequence of bytes from the given <paramref name="position"/> of the current stream.
    /// Does not use or change <see cref="Position"/>.
    /// </summary>
    /// <remarks>
    /// A read that spans several chunk parts loads them in parallel, see <see cref="ReadAtAsync(long,Memory{byte},CancellationToken)"/>.
    /// </remarks>
    /// <param name="position">The position to begin reading from.</param>
    /// <param name="buffer">
    /// When this method returns, contains the specified byte array with the values between
    /// <paramref name="offset"></paramref> and (<paramref name="offset"></paramref> + <paramref name="count"></paramref> - 1)
    /// replaced by the characters read from the current stream.
    /// </param>
    /// <param name="offset">The zero-based byte offset in <paramref name="buffer"/> at which to begin storing data from the current stream.</param>
    /// <param name="count">The maximum number of bytes to read.</param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    /// <returns>The total number of bytes written into the <paramref name="buffer"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="position"/> is negative.</exception>
    /// <exception cref="HttpRequestException">A chunk fails to download, also after <see cref="ManifestParseOptions.MaxDownloadRetries"/> retries for transient errors.</exception>
    /// <exception cref="TimeoutException">The last attempt to download a chunk received no data for <see cref="ManifestParseOptions.DownloadIdleTimeout"/>.</exception>
    /// <exception cref="InvalidDataException">The last download of a chunk is corrupt: its header, size or hash does not match the manifest.</exception>
    /// <exception cref="FileLoadException">The last download of a chunk has an invalid magic or fails to decompress.</exception>
    /// <exception cref="NotSupportedException">A chunk is encrypted or stored in an unknown way.</exception>
    /// <exception cref="OperationCanceledException">The <paramref name="cancellationToken"/> was cancelled.</exception>
    public override Task<int> ReadAtAsync(long position, byte[] buffer, int offset, int count, CancellationToken cancellationToken = default)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAtAsync(position, new Memory<byte>(buffer, offset, count), cancellationToken).AsTask();
    }

    /// <summary>Sets the position within the current stream to the specified value.</summary>
    /// <param name="offset">The new position within the stream. This is relative to the <paramref name="loc"/> parameter, and can be positive or negative.</param>
    /// <param name="loc">A value of type <see cref="SeekOrigin"/>, which acts as the seek reference point.</param>
    /// <returns>The new position within the stream, calculated by combining the initial reference point and the offset.</returns>
    /// <exception cref="ArgumentException">There is an invalid <see cref="SeekOrigin"/>.</exception>
    public override long Seek(long offset, SeekOrigin loc)
    {
        Position = loc switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentException("Invalid loc", nameof(loc))
        };
        return _position;
    }

    /// <summary>Not supported</summary>
    public override void SetLength(long value)
        => throw new NotSupportedException();
    /// <summary>Not supported</summary>
    public override void Write(byte[] buffer, int offset, int count)
        => throw new NotSupportedException();
    /// <summary>Does nothing, the stream is read-only.</summary>
    public override void Flush() { }
}


/// <summary>
/// Provides data for the save progress changed event.
/// </summary>
public sealed class SaveProgressChangedEventArgs : EventArgs
{
    /// <summary>
    /// Gets an optional user-defined object that qualifies or contains
    /// information about the save operation.
    /// </summary>
    public object? UserState { get; }

    /// <summary>
    /// Gets the number of bytes written since the previous event (since the start for the first one).
    /// The values of all events of a save add up to <see cref="TotalBytesToWrite"/>.
    /// </summary>
    public long BytesWritten { get; }

    /// <summary>
    /// Gets the cumulative number of bytes written so far.
    /// </summary>
    public long TotalBytesWritten { get; }

    /// <summary>
    /// Gets the total number of bytes that will be written.
    /// </summary>
    public long TotalBytesToWrite { get; }

    /// <summary>
    /// Gets the progress of the save operation as a percentage (0–100), <see cref="TotalBytesWritten"/> * 100 /
    /// <see cref="TotalBytesToWrite"/> rounded down: 100 only once everything is written.
    /// </summary>
    /// <remarks>
    /// An event is raised when a write changes the percentage, and always for the last write of a save.
    /// </remarks>
    public int ProgressPercentage { get; }

    internal SaveProgressChangedEventArgs(
        object? userState,
        long bytesWritten,
        long totalBytesWritten,
        long totalBytesToWrite,
        int progressPercentage)
    {
        UserState = userState;
        BytesWritten = bytesWritten;
        TotalBytesWritten = totalBytesWritten;
        TotalBytesToWrite = totalBytesToWrite;
        ProgressPercentage = progressPercentage;
    }
}


internal sealed class DownloadState<TDestination> : IEnumerable<ChunkWithOffset<TDestination>>, IEnumerator<ChunkWithOffset<TDestination>>
{
    public readonly TDestination Destination;
    public readonly FFileManifest FileManifest;

    private readonly Lock? _lock;
    private readonly object? _userState;
    private readonly Action<SaveProgressChangedEventArgs>? _callback;
    private readonly long _totalBytesToSave;
    private long _bytesSaved;
    // TotalBytesWritten and ProgressPercentage of the last event
    private long _reportedBytes;
    private int _lastProgress;

    // IEnumerable & IEnumerator
    private long _offset;
    private int _chunkpartIndex;

    public DownloadState(TDestination destination, FFileManifest fileManifest, object? userState, Action<SaveProgressChangedEventArgs>? callback)
    {
        Reset();
        Destination = destination;
        FileManifest = fileManifest;

        if (callback is null)
            return;
        _lock = new Lock();
        _userState = userState;
        _callback = callback;
        _totalBytesToSave = fileManifest.FileSize;
    }

    public void OnBytesWritten(long amount)
    {
        if (_callback is null)
            return;

        lock (_lock!)
        {
            _bytesSaved += amount;
            int progress = GetPercentage(_bytesSaved, _totalBytesToSave);
            // the last write always reports, so that the events add up to the file; nothing new, no event
            if (_bytesSaved == _reportedBytes || (progress == _lastProgress && _bytesSaved != _totalBytesToSave))
                return;

            var eventArgs = new SaveProgressChangedEventArgs(_userState, _bytesSaved - _reportedBytes, _bytesSaved, _totalBytesToSave, progress);
            _reportedBytes = _bytesSaved;
            _lastProgress = progress;
            _callback(eventArgs);
        }
    }

    /// <summary>
    /// <paramref name="done"/> * 100 / <paramref name="total"/> in integer math, rounded down: exact for every file size,
    /// where <see cref="float"/> reaches 100 before the last write of a file just above 1 GiB.
    /// </summary>
    internal static int GetPercentage(long done, long total) =>
        total <= 0 ? 100 : (int)Int128.Clamp((Int128)done * 100 / total, 0, 100);

    // IEnumerable

    public IEnumerator<ChunkWithOffset<TDestination>> GetEnumerator() => this;
    IEnumerator IEnumerable.GetEnumerator() => this;

    // IEnumerator

    public bool MoveNext()
    {
        _chunkpartIndex++;
        if (_chunkpartIndex >= FileManifest.ChunkPartsArray.Length)
            return false;

        FChunkPart chunkPart = FileManifest.ChunkPartsArray[_chunkpartIndex];
        _offset = chunkPart.FileOffset;
        return true;
    }

    public void Reset()
    {
        _offset = 0;
        _chunkpartIndex = -1;
    }

    public ChunkWithOffset<TDestination> Current
    {
        get
        {
            FChunkPart chunkPart = FileManifest.ChunkPartsArray[_chunkpartIndex];
            return new ChunkWithOffset<TDestination>(this, chunkPart.Chunk, chunkPart.Offset, chunkPart.Size, _offset);
        }
    }

    object IEnumerator.Current => Current;

    public void Dispose() { }
}

internal readonly struct ChunkWithOffset<TDestination>
{
    public readonly DownloadState<TDestination> State;
    public readonly FChunkInfo Chunk;
    public readonly uint ChunkPartOffset;
    public readonly uint ChunkPartSize;
    public readonly long Offset;

    public ChunkWithOffset(DownloadState<TDestination> state, FChunkInfo chunk, uint chunkPartOffset, uint chunkPartSize, long offset)
    {
        State = state;
        Chunk = chunk;
        ChunkPartOffset = chunkPartOffset;
        ChunkPartSize = chunkPartSize;
        Offset = offset;
    }
}

/// <summary>
/// The chunk part reads of one call, which run at the same time and stop together: the first failure, or the caller's
/// cancellation, cancels all of them. The cancellation this causes in the other reads does not count as a failure.
/// </summary>
internal class ConcurrentPartReads : IDisposable
{
    private readonly CancellationTokenSource _cts;
    private Exception? _failure;

    public ConcurrentPartReads(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Token = _cts.Token;
    }

    /// <summary>
    /// The token for the reads (and writes) of the call, cancelled on the first failure or by the caller.
    /// </summary>
    public CancellationToken Token { get; }

    /// <summary>
    /// A read failed or the caller cancelled: nothing new may be started.
    /// </summary>
    public bool IsStopped => Token.IsCancellationRequested;

    /// <summary>
    /// Records <paramref name="exception"/> unless a failure was recorded before, and cancels the other reads.
    /// </summary>
    public void Fail(Exception exception)
    {
        if (Interlocked.CompareExchange(ref _failure, exception, null) is null)
            _cts.Cancel();
    }

    /// <summary>
    /// Reads <paramref name="chunkPart"/> into the start of <paramref name="buffer"/>. A failure is recorded through
    /// <see cref="Fail"/> instead of thrown, so the returned task always completes successfully.
    /// </summary>
    public async Task ReadAsync(FChunkPart chunkPart, byte[] buffer, FBuildPatchAppManifest manifest)
    {
        try
        {
            await chunkPart.Chunk.ReadDataAsync(buffer.AsMemory(0, (int)chunkPart.Size), FFileManifestStream.GetChunkOffset(chunkPart.Offset, 0),
                manifest, Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    /// <summary>
    /// Call once everything that was started has finished. Returns if nothing stopped the reads, otherwise throws
    /// <see cref="OperationCanceledException"/> if the caller cancelled, else the first failure.
    /// </summary>
    public void ThrowIfStopped(CancellationToken cancellationToken)
    {
        if (!IsStopped)
            return;

        cancellationToken.ThrowIfCancellationRequested();
        // only a failure cancels the token apart from the caller, and it is recorded before it does
        Debug.Assert(_failure is not null);
        ExceptionDispatchInfo.Throw(_failure);
    }

    public void Dispose() => _cts.Dispose();
}

/// <summary>
/// A fixed number of chunk part reads of one call, done by a bounded number of workers: each one takes the next read and does it,
/// until none is left. Stops on the first failure like <see cref="ConcurrentPartReads"/>.
/// </summary>
internal abstract class ParallelPartReads : ConcurrentPartReads
{
    private readonly int _count;
    private int _next;

    protected ParallelPartReads(int count, CancellationToken cancellationToken) : base(cancellationToken)
    {
        _count = count;
    }

    /// <summary>
    /// Read <paramref name="index"/> (<c>0</c> to the count - 1). May throw synchronously; either way it counts as a failure.
    /// </summary>
    protected abstract ValueTask ReadPartAsync(int index, CancellationToken cancellationToken);

    /// <summary>
    /// Does all reads of <paramref name="reads"/>, at most <paramref name="maxConcurrency"/> at a time, and disposes it. Completes only
    /// when none of them runs anymore, then throws like <see cref="ConcurrentPartReads.ThrowIfStopped"/>.
    /// </summary>
    protected static async ValueTask RunAsync(ParallelPartReads reads, int maxConcurrency, CancellationToken cancellationToken)
    {
        using (reads)
        {
            // the number of tasks depends on the bound, not on the number of parts
            var workers = new Task[Math.Min(Math.Max(maxConcurrency, 1), reads._count)];
            for (int i = 0; i < workers.Length; i++)
                workers[i] = reads.WorkAsync();

            // the parts write into the caller's buffer: none of them may still be running when this returns or throws
            foreach (Task worker in workers)
                await worker.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

            reads.ThrowIfStopped(cancellationToken);
        }
    }

    private async Task WorkAsync()
    {
        CancellationToken token = Token;

        try
        {
            while (!token.IsCancellationRequested)
            {
                int index = Interlocked.Increment(ref _next) - 1;
                if (index >= _count)
                    return;

                await ReadPartAsync(index, token).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }
}

/// <summary>
/// An <see cref="FFileManifestStream.ReadAtAsync(long,Memory{byte},CancellationToken)"/> over several chunk parts: each part is read
/// into its own slice of the caller's buffer.
/// </summary>
internal sealed class MultiPartRead : ParallelPartReads
{
    private readonly FChunkPart[] _chunkParts;
    private readonly FBuildPatchAppManifest _manifest;
    private readonly long _position;
    private readonly Memory<byte> _buffer;
    private readonly int _firstIndex;

    private MultiPartRead(FFileManifest fileManifest, long position, Memory<byte> buffer, int firstIndex, int lastIndex,
        CancellationToken cancellationToken) : base(lastIndex - firstIndex + 1, cancellationToken)
    {
        _chunkParts = fileManifest.ChunkPartsArray;
        _manifest = fileManifest.Manifest;
        _position = position;
        _buffer = buffer;
        _firstIndex = firstIndex;
    }

    /// <summary>
    /// Reads the parts <paramref name="firstIndex"/> to <paramref name="lastIndex"/> (inclusive) that the range of
    /// <paramref name="buffer"/> at <paramref name="position"/> covers, at most <paramref name="maxConcurrency"/> at a time.
    /// </summary>
    public static ValueTask RunAsync(FFileManifest fileManifest, long position, Memory<byte> buffer, int firstIndex, int lastIndex,
        int maxConcurrency, CancellationToken cancellationToken)
    {
        // not async itself: one state machine for the whole read
        return RunAsync(new MultiPartRead(fileManifest, position, buffer, firstIndex, lastIndex, cancellationToken), maxConcurrency, cancellationToken);
    }

    protected override ValueTask ReadPartAsync(int index, CancellationToken cancellationToken)
    {
        // the first part may begin before the range, the last one may end after it
        FChunkPart chunkPart = _chunkParts[_firstIndex + index];
        long start = Math.Max(chunkPart.FileOffset, _position);
        int bufferOffset = (int)(start - _position);
        int count = (int)Math.Min(chunkPart.FileOffset + chunkPart.Size - start, _buffer.Length - bufferOffset);
        if (count == 0)
            return ValueTask.CompletedTask;

        return chunkPart.Chunk.ReadDataAsync(_buffer.Slice(bufferOffset, count),
            FFileManifestStream.GetChunkOffset(chunkPart.Offset, start - chunkPart.FileOffset), _manifest, cancellationToken);
    }
}

/// <summary>
/// A part of a synchronous <see cref="FFileManifestStream.ReadAt(long,Span{byte})"/> that is not local.
/// </summary>
/// <param name="Index">The index of the chunk part.</param>
/// <param name="ChunkOffset">The offset of the range in the chunk data.</param>
/// <param name="Count">The length of the range.</param>
/// <param name="DestinationOffset">Where the range goes in the caller's buffer.</param>
/// <param name="BufferOffset">Where the range is loaded in the pooled buffer.</param>
internal readonly record struct PendingPart(int Index, int ChunkOffset, int Count, int DestinationOffset, int BufferOffset);

/// <summary>
/// The parts of a synchronous <see cref="FFileManifestStream.ReadAt(long,Span{byte})"/> that are not local, loaded into one pooled
/// buffer with the same workers as <see cref="MultiPartRead"/>.
/// </summary>
internal sealed class PendingPartsRead : ParallelPartReads
{
    private readonly FChunkPart[] _chunkParts;
    private readonly FBuildPatchAppManifest _manifest;
    private readonly PendingPart[] _pending;
    private readonly byte[] _buffer;

    private PendingPartsRead(FFileManifest fileManifest, PendingPart[] pending, int count, byte[] buffer) : base(count, CancellationToken.None)
    {
        _chunkParts = fileManifest.ChunkPartsArray;
        _manifest = fileManifest.Manifest;
        _pending = pending;
        _buffer = buffer;
    }

    /// <summary>
    /// Loads the first <paramref name="count"/> parts of <paramref name="pending"/> into <paramref name="buffer"/> at their
    /// <see cref="PendingPart.BufferOffset"/>, at most <paramref name="maxConcurrency"/> at a time, and blocks until none of them
    /// runs anymore. Both arrays may go back to the pool once this returns or throws.
    /// </summary>
    public static void Run(FFileManifest fileManifest, PendingPart[] pending, int count, byte[] buffer, int maxConcurrency)
    {
        RunAsync(new PendingPartsRead(fileManifest, pending, count, buffer), maxConcurrency, CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();
    }

    protected override ValueTask ReadPartAsync(int index, CancellationToken cancellationToken)
    {
        PendingPart part = _pending[index];
        return _chunkParts[part.Index].Chunk.ReadDataAsync(_buffer.AsMemory(part.BufferOffset, part.Count), part.ChunkOffset, _manifest, cancellationToken);
    }
}
