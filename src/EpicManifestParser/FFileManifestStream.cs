using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using EpicManifestParser.UE;

using Microsoft.Win32.SafeHandles;

using OffiUtils;

namespace EpicManifestParser;
// ReSharper disable UseSymbolAlias

/// <summary>
/// A stream representing a <see cref="FFileManifest"/>
/// </summary>
public class FFileManifestStream : Stream, IRandomAccessStream
{
	private readonly FFileManifest _fileManifest;
	private readonly bool _cacheAsIs;

	/// <summary>Always <see langword="true"/></summary>
	public override bool CanRead => true;
	/// <summary>Always <see langword="true"/></summary>
	public override bool CanSeek => true;
	/// <summary>Always <see langword="true"/></summary>
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
			if (value > Length || value < 0)
				throw new ArgumentOutOfRangeException(nameof(value), value, "Value is negative or exceeds the stream's length");

			_position = value;
		}
	}

	/// <summary>Gets the file name of the <see cref="FFileManifest"/> represented by this stream</summary>
	public string FileName => _fileManifest.FileName;

	internal FFileManifestStream(FFileManifest fileManifest, bool cacheAsIs)
	{
		if (string.IsNullOrEmpty(fileManifest.Manifest.Options.ChunkBaseUrl))
			throw new ArgumentException("Missing ChunkBaseUrl");
		if (fileManifest.Manifest.ManifestMeta.bIsFileData)
			throw new NotSupportedException("File-data manifests are not supported");

		_fileManifest = fileManifest;
		_cacheAsIs = cacheAsIs;
	}

	private class DownloadState<T>
	{
		public readonly byte[] DestinationBuffer;
		public readonly SafeFileHandle DestinationHandle;
		public readonly FBuildPatchAppManifest Manifest;

		private readonly T? _userState;
		private readonly Action<SaveProgressChangedEventArgs<T>>? _callback;
		private readonly long _totalBytesToSave;
		private long _bytesSaved;
		private int _lastProgress;

		public DownloadState(SafeFileHandle destinationHandle, FBuildPatchAppManifest manifest, long totalBytesToSave, T? userState, Action<SaveProgressChangedEventArgs<T>>? callback)
		{
			DestinationHandle = destinationHandle;
			DestinationBuffer = null!;
			Manifest = manifest;
			_userState = userState;
			_callback = callback;
			_totalBytesToSave = totalBytesToSave;
		}

		public DownloadState(byte[] destinationBuffer, FBuildPatchAppManifest manifest, long totalBytesToSave, T? userState, Action<SaveProgressChangedEventArgs<T>>? callback)
		{
			DestinationBuffer = destinationBuffer;
			DestinationHandle = null!;
			Manifest = manifest;
			_userState = userState;
			_callback = callback;
			_totalBytesToSave = totalBytesToSave;
		}

		public void OnBytesWritten(long amount)
		{
			if (_callback is null)
				return;

			lock (Manifest)
			{
				_bytesSaved += amount;
				var progress = (int)Math.Truncate((double)_bytesSaved / _totalBytesToSave * 100);
				if (progress != _lastProgress)
				{
					_lastProgress = progress;
					var eventArgs = new SaveProgressChangedEventArgs<T>(_userState, _bytesSaved, _totalBytesToSave, progress);
					_callback(eventArgs);
				}
			}
		}
	}

	/// <summary>
	/// Asynchronously saves the current stream to another stream.
	/// </summary>
	/// <typeparam name="TState"></typeparam>
	/// <param name="destination"></param>
	/// <param name="progressCallback"></param>
	/// <param name="userState"></param>
	/// <param name="maxDegreeOfParallelism"></param>
	/// <param name="cancellationToken"></param>
	/// <returns>A task that represents the entire save operation.</returns>
	public async Task SaveToAsync<TState>(Stream destination, Action<SaveProgressChangedEventArgs<TState>>? progressCallback,
		TState? userState = default, int maxDegreeOfParallelism = 16, CancellationToken cancellationToken = default)
	{
		if (destination is MemoryStream {Position: 0} ms)
		{
			ms.Capacity = (int)Length;
			if (ms.TryGetBuffer(out var buffer))
			{
				await SaveBytesAsync(buffer.Array!, progressCallback, userState, maxDegreeOfParallelism, cancellationToken).ConfigureAwait(false);
				ms.Position = (int)Length;
				return;
			}
		}

		// TODO: make concurrent

		var downloadState = new DownloadState<TState>((byte[])null!, _fileManifest.Manifest, Length, userState, progressCallback);

		if (_cacheAsIs)
		{
			var poolBuffer = ArrayPool<byte>.Shared.Rent(_fileManifest.Manifest.Options.ChunkDownloadBufferSize);

			try
			{
				foreach (var fileChunkPart in _fileManifest.ChunkParts)
				{
					var chunk = _fileManifest.Manifest.Chunks[fileChunkPart.Guid];
					await chunk.ReadDataAsIsAsync(poolBuffer, _fileManifest.Manifest, cancellationToken).ConfigureAwait(false);
					await destination.WriteAsync(new ReadOnlyMemory<byte>(poolBuffer, (int)fileChunkPart.Offset, (int)fileChunkPart.Size),
						cancellationToken).ConfigureAwait(false);
					downloadState.OnBytesWritten(fileChunkPart.Size);
				}
			}
			finally
			{
				ArrayPool<byte>.Shared.Return(poolBuffer);
			}
		}
		else
		{
			var poolBuffer = ArrayPool<byte>.Shared.Rent(_fileManifest.Manifest.Options.ChunkDownloadBufferSize);

			try
			{
				foreach (var fileChunkPart in _fileManifest.ChunkParts)
				{
					var chunk = _fileManifest.Manifest.Chunks[fileChunkPart.Guid];
					await chunk.ReadDataAsync(poolBuffer, 0, (int)fileChunkPart.Size,
						(int)fileChunkPart.Offset, _fileManifest.Manifest, cancellationToken).ConfigureAwait(false);
					await destination.WriteAsync(new ReadOnlyMemory<byte>(poolBuffer, 0, (int)fileChunkPart.Size),
						cancellationToken).ConfigureAwait(false);
					downloadState.OnBytesWritten(fileChunkPart.Size);
				}
			}
			finally
			{
				ArrayPool<byte>.Shared.Return(poolBuffer);
			}
		}

		await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc cref="SaveToAsync{TState}"/>
	public Task SaveToAsync(Stream destination, int maxDegreeOfParallelism = 16, CancellationToken cancellationToken = default)
	{
		return SaveToAsync<nint>(destination, null, 0, maxDegreeOfParallelism, cancellationToken);
	}

	/// <summary>
	/// Asynchronously saves the current stream to a buffer.
	/// </summary>
	/// <typeparam name="TState">The type of the <paramref name="userState"/>.</typeparam>
	/// <param name="destination">The destination buffer.</param>
	/// <param name="progressCallback">The progress change callback. (optional)</param>
	/// <param name="userState">The user state for the <paramref name="progressCallback"/>. (optional)</param>
	/// <param name="maxDegreeOfParallelism">The maximum number of concurrent tasks saving/downloading to the destination.</param>
	/// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
	/// <returns>A task that represents the entire save operation.</returns>
	public async Task SaveBytesAsync<TState>(byte[] destination, Action<SaveProgressChangedEventArgs<TState>>? progressCallback,
		TState? userState = default, int maxDegreeOfParallelism = 16, CancellationToken cancellationToken = default)
	{
		ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, Length);

		var downloadState = new DownloadState<TState>(destination, _fileManifest.Manifest, Length, userState, progressCallback);
		var parallelOptions = new ParallelOptions
		{
			MaxDegreeOfParallelism = maxDegreeOfParallelism,
			CancellationToken = cancellationToken
		};
		await Parallel.ForEachAsync(EnumerateChunksWithOffset(downloadState), parallelOptions, _cacheAsIs ? SaveAsIsAsync : SaveAsync).ConfigureAwait(false);
		return;

		static async ValueTask SaveAsync<T>((DownloadState<T> State, FChunkInfo Chunk, uint ChunkPartOffset, uint ChunkPartSize, long Offset) tuple, CancellationToken token)
		{
			await tuple.Chunk.ReadDataAsync(tuple.State.DestinationBuffer, (int)tuple.Offset, (int)tuple.ChunkPartSize,
				(int)tuple.ChunkPartOffset, tuple.State.Manifest, token).ConfigureAwait(false);
			tuple.State.OnBytesWritten(tuple.ChunkPartSize);
		}

		static async ValueTask SaveAsIsAsync<T>((DownloadState<T> State, FChunkInfo Chunk, uint ChunkPartOffset, uint ChunkPartSize, long Offset) tuple, CancellationToken token)
		{
			var poolBuffer = ArrayPool<byte>.Shared.Rent(tuple.State.Manifest.Options.ChunkDownloadBufferSize);

			try
			{
				await tuple.Chunk.ReadDataAsIsAsync(poolBuffer, tuple.State.Manifest, token).ConfigureAwait(false);
				Unsafe.CopyBlockUnaligned(ref tuple.State.DestinationBuffer[tuple.Offset],
					ref poolBuffer[tuple.ChunkPartOffset], tuple.ChunkPartSize);
				tuple.State.OnBytesWritten(tuple.ChunkPartSize);
			}
			finally
			{
				ArrayPool<byte>.Shared.Return(poolBuffer);
			}
		}
	}

	/// <summary>
	/// Asynchronously saves the current stream to a buffer.
	/// </summary>
	/// <param name="destination">The destination buffer.</param>
	/// <param name="maxDegreeOfParallelism">The maximum number of concurrent tasks saving/downloading to the destination.</param>
	/// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
	/// <returns>A task that represents the entire save operation.</returns>
	public Task SaveBytesAsync(byte[] destination, int maxDegreeOfParallelism = 16, CancellationToken cancellationToken = default)
	{
		return SaveBytesAsync<nint>(destination, null, 0, maxDegreeOfParallelism, cancellationToken);
	}

	/// <summary>
	/// Asynchronously saves the current stream to a buffer.
	/// </summary>
	/// <typeparam name="TState">The type of the <paramref name="userState"/>.</typeparam>
	/// <param name="progressCallback">The progress change callback. (optional)</param>
	/// <param name="userState">The user state for the <paramref name="progressCallback"/>. (optional)</param>
	/// <param name="maxDegreeOfParallelism">The maximum number of concurrent tasks saving/downloading to the destination.</param>
	/// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
	/// <returns>A task that represents the entire save operation.</returns>
	public async Task<byte[]> SaveBytesAsync<TState>(Action<SaveProgressChangedEventArgs<TState>> progressCallback, TState? userState = default,
		int maxDegreeOfParallelism = 16, CancellationToken cancellationToken = default)
	{
		var destination = new byte[Length];
		await SaveBytesAsync(destination, progressCallback, userState, maxDegreeOfParallelism, cancellationToken).ConfigureAwait(false);
		return destination;
	}

	/// <summary>
	/// Asynchronously saves the current stream to a buffer.
	/// </summary>
	/// <param name="maxDegreeOfParallelism">The maximum number of concurrent tasks saving/downloading to the destination.</param>
	/// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
	/// <returns>A task that represents the entire save operation.</returns>
	public async Task<byte[]> SaveBytesAsync(int maxDegreeOfParallelism = 16, CancellationToken cancellationToken = default)
	{
		var destination = new byte[Length];
		await SaveBytesAsync(destination, maxDegreeOfParallelism, cancellationToken).ConfigureAwait(false);
		return destination;
	}

	private IEnumerable<(DownloadState<T> State, FChunkInfo Chunk, uint ChunkPartOffset, uint ChunkPartSize, long Offset)>
		EnumerateChunksWithOffset<T>(DownloadState<T> state)
	{
		var offset = 0L;

		foreach (var fileChunkPart in _fileManifest.ChunkParts)
		{
			var chunk = _fileManifest.Manifest.Chunks[fileChunkPart.Guid];
			yield return (state, chunk, fileChunkPart.Offset, fileChunkPart.Size, offset);
			offset += fileChunkPart.Size;
		}
	}

	/// <summary>
	/// Asynchronously saves the current stream to a file.
	/// </summary>
	/// <typeparam name="TState">The type of the <paramref name="userState"/>.</typeparam>
	/// <param name="path">The path of the destination file.</param>
	/// <param name="progressCallback">The progress change callback. (optional)</param>
	/// <param name="userState">The user state for the <paramref name="progressCallback"/>. (optional)</param>
	/// <param name="maxDegreeOfParallelism">The maximum number of concurrent tasks saving/downloading to the destination.</param>
	/// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
	/// <returns>A task that represents the entire save operation.</returns>
	public async Task SaveFileAsync<TState>(string path, Action<SaveProgressChangedEventArgs<TState>>? progressCallback, TState? userState = default,
		int maxDegreeOfParallelism = 16, CancellationToken cancellationToken = default)
	{
		using var destination = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, FileOptions.Asynchronous, Length);
		var downloadState = new DownloadState<TState>(destination, _fileManifest.Manifest, Length, userState, progressCallback);

		var parallelOptions = new ParallelOptions
		{
			MaxDegreeOfParallelism = maxDegreeOfParallelism,
			CancellationToken = cancellationToken
		};
		await Parallel.ForEachAsync(EnumerateChunksWithOffset(downloadState), parallelOptions, _cacheAsIs ? SaveAsIsAsync : SaveAsync).ConfigureAwait(false);
		RandomAccess.FlushToDisk(destination);
		return;

		static async ValueTask SaveAsync<T>((DownloadState<T> State, FChunkInfo Chunk, uint ChunkPartOffset, uint ChunkPartSize, long Offset) tuple, CancellationToken token)
		{
			var poolBuffer = ArrayPool<byte>.Shared.Rent((int)tuple.ChunkPartSize);

			try
			{
				await tuple.Chunk.ReadDataAsync(poolBuffer, 0, (int)tuple.ChunkPartSize,
					(int)tuple.ChunkPartOffset, tuple.State.Manifest, token).ConfigureAwait(false);
				await RandomAccess.WriteAsync(tuple.State.DestinationHandle,
					new ReadOnlyMemory<byte>(poolBuffer, 0, (int)tuple.ChunkPartSize),
					tuple.Offset, token).ConfigureAwait(false);
				tuple.State.OnBytesWritten(tuple.ChunkPartSize);
			}
			finally
			{
				ArrayPool<byte>.Shared.Return(poolBuffer);
			}
		}

		static async ValueTask SaveAsIsAsync<T>((DownloadState<T> State, FChunkInfo Chunk, uint ChunkPartOffset, uint ChunkPartSize, long Offset) tuple, CancellationToken token)
		{
			var poolBuffer = ArrayPool<byte>.Shared.Rent(tuple.State.Manifest.Options.ChunkDownloadBufferSize);

			try
			{
				await tuple.Chunk.ReadDataAsIsAsync(poolBuffer, tuple.State.Manifest, token).ConfigureAwait(false);
				await RandomAccess.WriteAsync(tuple.State.DestinationHandle,
					new ReadOnlyMemory<byte>(poolBuffer, (int)tuple.ChunkPartOffset, (int)tuple.ChunkPartSize),
					tuple.Offset, token).ConfigureAwait(false);
				tuple.State.OnBytesWritten(tuple.ChunkPartSize);
			}
			finally
			{
				ArrayPool<byte>.Shared.Return(poolBuffer);
			}
		}
	}

	/// <inheritdoc cref="SaveFileAsync{TState}"/>
	public Task SaveFileAsync(string path, int maxDegreeOfParallelism = 16, CancellationToken cancellationToken = default)
	{
		return SaveFileAsync<nint>(path, null, 0, maxDegreeOfParallelism, cancellationToken);
	}

	/// <summary>
	/// Reads a sequence of bytes from the current stream.
	/// </summary>
	/// <param name="buffer">
	/// When this method returns, contains the specified byte array with the values between
	/// <paramref name="offset"></paramref> and (<paramref name="offset"></paramref> + <paramref name="count"></paramref> - 1)
	/// replaced by the characters read from the current stream.
	/// </param>
	/// <param name="offset">The zero-based byte offset in <paramref name="buffer"/> at which to begin storing data from the current stream.</param>
	/// <param name="count">The maximum number of bytes to read.</param>
	/// <returns>The total number of bytes written into the <paramref name="buffer"/>.</returns>
	public override int Read(byte[] buffer, int offset, int count)
	{
		return ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
	}

	/// <summary>
	/// Asynchronously reads a sequence of bytes from the the current stream.
	/// </summary>
	/// <param name="buffer">
	/// When this method returns, contains the specified byte array with the values between
	/// <paramref name="offset"></paramref> and (<paramref name="offset"></paramref> + <paramref name="count"></paramref> - 1)
	/// replaced by the characters read from the current stream.
	/// </param>
	/// <param name="offset">The zero-based byte offset in <paramref name="buffer"/> at which to begin storing data from the current stream.</param>
	/// <param name="count">The maximum number of bytes to read.</param>
	/// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
	/// <returns>The total number of bytes written into the <paramref name="buffer"/>.</returns>
	public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
	{
		if (cancellationToken.IsCancellationRequested)
			return await Task.FromCanceled<int>(cancellationToken).ConfigureAwait(false);

		var bytesRead = await ReadAtAsync(_position, buffer, offset, count, cancellationToken).ConfigureAwait(false);
		_position += bytesRead;
		return bytesRead;
	}

	/// <summary>
	/// Asynchronously reads a sequence of bytes from the current stream.
	/// </summary>
	/// <param name="buffer">The region of memory to write the data into.</param>
	/// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
	/// <returns>The total number of bytes written into the <paramref name="buffer"/>.</returns>
	public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
	{
		var bytesRead = await ReadAtAsync(_position, buffer, cancellationToken).ConfigureAwait(false);
		_position += bytesRead;
		return bytesRead;
	}

	/// <summary>
	/// Reads a sequence of bytes from the given <paramref name="position"/> of the current stream.
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
	public int ReadAt(long position, byte[] buffer, int offset, int count)
	{
		return ReadAtAsync(position, buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
	}

	/// <summary>
	/// Asynchronously reads a sequence of bytes from the given <paramref name="position"/> of the current stream.
	/// </summary>
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
	public async Task<int> ReadAtAsync(long position, byte[] buffer, int offset, int count, CancellationToken cancellationToken)
	{
		var (i, startPos) = GetChunkIndex(position);
		if (i == -1)
			return 0;

		var bytesRead = 0u;

		if (_cacheAsIs)
		{
			var poolBuffer = ArrayPool<byte>.Shared.Rent(_fileManifest.Manifest.Options.ChunkDownloadBufferSize);

			try
			{
				while (true)
				{
					var chunkPart = _fileManifest.ChunkParts[i];
					var chunk = _fileManifest.Manifest.Chunks[chunkPart.Guid];

					await chunk.ReadDataAsIsAsync(poolBuffer, _fileManifest.Manifest, cancellationToken).ConfigureAwait(false);

					var chunkOffset = chunkPart.Offset + startPos;
					var chunkBytes = chunkPart.Size - startPos;
					var bytesLeft = (uint)count - bytesRead;

					if (bytesLeft <= chunkBytes)
					{
						Unsafe.CopyBlockUnaligned(ref buffer[bytesRead + offset], ref poolBuffer[chunkOffset], bytesLeft);
						bytesRead += bytesLeft;
						break;
					}

					Unsafe.CopyBlockUnaligned(ref buffer[bytesRead + offset], ref poolBuffer[chunkOffset], chunkBytes);
					bytesRead += chunkBytes;
					startPos = 0;

					if (++i == _fileManifest.ChunkParts.Length)
						break;
				}
			}
			finally
			{
				ArrayPool<byte>.Shared.Return(poolBuffer);
			}
		}
		else
		{
			while (true)
			{
				var chunkPart = _fileManifest.ChunkParts[i];
				var chunk = _fileManifest.Manifest.Chunks[chunkPart.Guid];

				var chunkOffset = (int)(chunkPart.Offset + startPos);
				var chunkBytes = (int)(chunkPart.Size - startPos);
				var bytesLeft = count - (int)bytesRead;

				if (bytesLeft <= chunkBytes)
				{
					await chunk.ReadDataAsync(buffer, (int)bytesRead + offset, bytesLeft, chunkOffset, _fileManifest.Manifest, cancellationToken).ConfigureAwait(false);
					bytesRead += (uint)bytesLeft;
					break;
				}

				await chunk.ReadDataAsync(buffer, (int)bytesRead + offset, chunkBytes, chunkOffset, _fileManifest.Manifest, cancellationToken).ConfigureAwait(false);
				bytesRead += (uint)chunkBytes;
				startPos = 0;

				if (++i == _fileManifest.ChunkParts.Length)
					break;
			}
		}

		return (int)bytesRead;
	}

	/// <summary>
	/// Asynchronously reads a sequence of bytes from the given <paramref name="position"/> of the current stream.
	/// </summary>
	/// <param name="position">The position to begin reading from.</param>
	/// <param name="buffer">The region of memory to write the data into.</param>
	/// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
	/// <returns>The total number of bytes written into the <paramref name="buffer"/>.</returns>
	public Task<int> ReadAtAsync(long position, Memory<byte> buffer, CancellationToken cancellationToken = default)
	{
		if (cancellationToken.IsCancellationRequested)
			return Task.FromCanceled<int>(cancellationToken);

		try
		{
			return
				MemoryMarshal.TryGetArray(buffer, out ArraySegment<byte> destinationArray) ?
					ReadAtAsync(position, destinationArray.Array!, destinationArray.Offset, destinationArray.Count, cancellationToken) :
					throw new NotSupportedException("Failed to get memory array");
		}
		catch (OperationCanceledException oce)
		{
			return Task.FromCanceled<int>(oce.CancellationToken);
		}
		catch (Exception exception)
		{
			return Task.FromException<int>(exception);
		}
	}

	private long _lastChunkPartPosition;
	private uint _lastChunkPartSize;
	private int _lastChunkPartIndex;

	private (int Index, uint ChunkPos) GetChunkIndexNew(long position)
	{
		lock (_fileManifest)
		{
			var maxPosition = _lastChunkPartPosition + _lastChunkPartSize;
			if (maxPosition < position && position >= _lastChunkPartPosition)
			{
				return (_lastChunkPartIndex, (uint)(_lastChunkPartPosition - position));
			}

			var chunkPartPosition = 0L;

			for (var i = 0; i < _fileManifest.ChunkParts.Length; i++)
			{
				var chunkPart = _fileManifest.ChunkParts[i];

				if (chunkPartPosition >= position)
				{
					_lastChunkPartPosition = chunkPartPosition;
					_lastChunkPartSize = chunkPart.Size;
					_lastChunkPartIndex = i;
					return (i, (uint)(chunkPartPosition - position));
				}

				chunkPartPosition += chunkPart.Size;
			}

			return (-1, 0);
		}
	}

	private (int Index, uint ChunkPos) GetChunkIndex(long position)
	{
		for (var i = 0; i < _fileManifest.ChunkParts.Length; i++)
		{
			var chunkPart = _fileManifest.ChunkParts[i];

			if (position < chunkPart.Size)
				return (i, (uint)position);

			position -= chunkPart.Size;
		}

		return (-1, 0);
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
			SeekOrigin.Current => offset + _position,
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
	/// <summary>Not supported</summary>
	public override void Flush()
		=> throw new NotSupportedException();
}

/// <summary>
/// Event for save progress
/// </summary>
/// <typeparam name="TState">Type of <see cref="UserState"/></typeparam>
public class SaveProgressChangedEventArgs<TState> : EventArgs
{
	internal SaveProgressChangedEventArgs(TState? userState, long bytesSaved, long totalBytesToSave, int progressPercentage)
	{
		UserState = userState;
		BytesSaved = bytesSaved;
		TotalBytesToSave = totalBytesToSave;
		ProgressPercentage = progressPercentage;
	}

	/// <summary/>
	public TState? UserState { get; }
	/// <summary/>
	public long BytesSaved { get; }
	/// <summary/>
	public long TotalBytesToSave { get; }
	/// <summary/>
	public int ProgressPercentage { get; }
}
