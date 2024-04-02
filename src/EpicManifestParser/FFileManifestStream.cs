using System.Buffers;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using EpicManifestParser.UE;

using Microsoft.Win32.SafeHandles;

using OffiUtils;

namespace EpicManifestParser;
// ReSharper disable UseSymbolAlias

public class FFileManifestStream : Stream, IRandomAccessStream
{
	private readonly FFileManifest _fileManifest;
	private readonly bool _cacheAsIs;

	public override bool CanRead => true;
	public override bool CanSeek => true;
	public override bool CanWrite => false;
	public override long Length => _fileManifest.FileSize;
	
	private long _position;
	public override long Position
	{
		get => _position;
		set
		{
			if (value > Length || value < 0)
				throw new ArgumentOutOfRangeException(nameof(value), value, null);

			_position = value;
		}
	}

	public string FileName => _fileManifest.FileName;

	internal FFileManifestStream(FFileManifest fileManifest, bool cacheAsIs = true)
	{
		if (string.IsNullOrEmpty(fileManifest.Manifest.Options.ChunkBaseUrl))
			throw new ArgumentException("missing DistrubutionPoint");
		if (fileManifest.Manifest.ManifestMeta.bIsFileData)
			throw new NotSupportedException("filedata manifests are not supported");

		_fileManifest = fileManifest;
		_cacheAsIs = cacheAsIs;
	}

	private class DownloadState
	{
		public readonly byte[] DestinationBuffer;
		public readonly SafeFileHandle DestinationHandle;
		public readonly FBuildPatchAppManifest Manifest;

		private readonly object? _userState;
		private readonly Action<SaveProgressChangedEventArgs>? _callback;
		private readonly long _totalBytesToSave;
		private long _bytesSaved;

		public DownloadState(SafeFileHandle destinationHandle, FBuildPatchAppManifest manifest, long totalBytesToSave, object? userState, Action<SaveProgressChangedEventArgs>? callback)
		{
			DestinationHandle = destinationHandle;
			DestinationBuffer = null!;
			Manifest = manifest;
			_userState = userState;
			_callback = callback;
			_totalBytesToSave = totalBytesToSave;
		}

		public DownloadState(byte[] destinationBuffer, FBuildPatchAppManifest manifest, long totalBytesToSave, object? userState, Action<SaveProgressChangedEventArgs>? callback)
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
				var eventArgs = new SaveProgressChangedEventArgs(progress, _userState, _bytesSaved, _totalBytesToSave);
				_callback(eventArgs);
			}
		}
	}

	// TODO: make concurrent
	public async Task SaveToAsync(Stream destination, Action<SaveProgressChangedEventArgs>? progressCallback,
		object? userState = null, CancellationToken cancellationToken = default)
	{
		var downloadState = new DownloadState((byte[])null!, _fileManifest.Manifest, Length, userState, progressCallback);

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
					//await RandomAccess.WriteAsync(tuple.State.DestinationHandle,
					//	new ReadOnlyMemory<byte>(poolBuffer, 0, (int)tuple.ChunkPartSize),
					//	tuple.Offset, token).ConfigureAwait(false);
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

	public Task SaveToAsync(Stream destination, CancellationToken cancellationToken = default)
	{
		return SaveToAsync(destination, null, null, cancellationToken);
	}

	public async Task SaveBytesAsync(byte[] destination, Action<SaveProgressChangedEventArgs>? progressCallback,
		object? userState = null, int maxDegreeOfParallelism = 16, CancellationToken cancellationToken = default)
	{
		var downloadState = new DownloadState(destination, _fileManifest.Manifest, Length, userState, progressCallback);
		var parallelOptions = new ParallelOptions
		{
			MaxDegreeOfParallelism = maxDegreeOfParallelism,
			CancellationToken = cancellationToken
		};
		await Parallel.ForEachAsync(EnumerateChunksWithOffset(downloadState), parallelOptions, _cacheAsIs ? SaveAsIsAsync : SaveAsync).ConfigureAwait(false);
		return;

		static async ValueTask SaveAsync((DownloadState State, FChunkInfo Chunk, uint ChunkPartOffset, uint ChunkPartSize, long Offset) tuple, CancellationToken token)
		{
			await tuple.Chunk.ReadDataAsync(tuple.State.DestinationBuffer, (int)tuple.Offset, (int)tuple.ChunkPartSize,
				(int)tuple.ChunkPartOffset, tuple.State.Manifest, token).ConfigureAwait(false);
			tuple.State.OnBytesWritten(tuple.ChunkPartSize);
		}

		static async ValueTask SaveAsIsAsync((DownloadState State, FChunkInfo Chunk, uint ChunkPartOffset, uint ChunkPartSize, long Offset) tuple, CancellationToken token)
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

	public Task SaveBytesAsync(byte[] destination, int maxDegreeOfParallelism = 16, CancellationToken cancellationToken = default)
	{
		return SaveBytesAsync(destination, null, null, maxDegreeOfParallelism, cancellationToken);
	}

	public async Task<byte[]> SaveBytesAsync(Action<SaveProgressChangedEventArgs> progressCallback, object? userState = null, int maxDegreeOfParallelism = 16, CancellationToken cancellationToken = default)
	{
		var destination = new byte[Length];
		await SaveBytesAsync(destination, progressCallback, userState, maxDegreeOfParallelism, cancellationToken).ConfigureAwait(false);
		return destination;
	}

	public async Task<byte[]> SaveBytesAsync(int maxDegreeOfParallelism = 16, CancellationToken cancellationToken = default)
	{
		var destination = new byte[Length];
		await SaveBytesAsync(destination, maxDegreeOfParallelism, cancellationToken).ConfigureAwait(false);
		return destination;
	}

	private IEnumerable<(DownloadState State, FChunkInfo Chunk, uint ChunkPartOffset, uint ChunkPartSize, long Offset)>
		EnumerateChunksWithOffset(DownloadState state)
	{
		var offset = 0L;

		foreach (var fileChunkPart in _fileManifest.ChunkParts)
		{
			var chunk = _fileManifest.Manifest.Chunks[fileChunkPart.Guid];
			yield return (state, chunk, fileChunkPart.Offset, fileChunkPart.Size, offset);
			offset += fileChunkPart.Size;
		}
	}

	public async Task SaveFileAsync(string path, Action<SaveProgressChangedEventArgs>? progressCallback, object? userState = null, int maxDegreeOfParallelism = 16, CancellationToken cancellationToken = default)
	{
		using var destination = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, FileOptions.Asynchronous, Length);
		var downloadState = new DownloadState(destination, _fileManifest.Manifest, Length, userState, progressCallback);

		var parallelOptions = new ParallelOptions
		{
			MaxDegreeOfParallelism = maxDegreeOfParallelism,
			CancellationToken = cancellationToken
		};
		await Parallel.ForEachAsync(EnumerateChunksWithOffset(downloadState), parallelOptions, _cacheAsIs ? SaveAsIsAsync : SaveAsync).ConfigureAwait(false);
		RandomAccess.FlushToDisk(destination);
		return;

		static async ValueTask SaveAsync((DownloadState State, FChunkInfo Chunk, uint ChunkPartOffset, uint ChunkPartSize, long Offset) tuple, CancellationToken token)
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

		static async ValueTask SaveAsIsAsync((DownloadState State, FChunkInfo Chunk, uint ChunkPartOffset, uint ChunkPartSize, long Offset) tuple, CancellationToken token)
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

	public Task SaveFileAsync(string path, int maxDegreeOfParallelism = 16, CancellationToken cancellationToken = default)
	{
		return SaveFileAsync(path, null, null, maxDegreeOfParallelism, cancellationToken);
	}

	public override int Read(byte[] buffer, int offset, int count)
	{
		return ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
	}

	public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
	{
		if (cancellationToken.IsCancellationRequested)
			return await Task.FromCanceled<int>(cancellationToken).ConfigureAwait(false);

		var bytesRead = await ReadAtAsync(_position, buffer, offset, count, cancellationToken).ConfigureAwait(false);
		_position += bytesRead;
		return bytesRead;
	}

	public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
	{
		var bytesRead = await ReadAtAsync(_position, buffer, cancellationToken).ConfigureAwait(false);
		_position += bytesRead;
		return bytesRead;
	}

	public int ReadAt(long position, byte[] buffer, int offset, int count)
	{
		return ReadAtAsync(position, buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
	}

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

	public Task<int> ReadAtAsync(long position, Memory<byte> buffer, CancellationToken cancellationToken = default)
	{
		if (cancellationToken.IsCancellationRequested)
			return Task.FromCanceled<int>(cancellationToken);

		try
		{
			return
				MemoryMarshal.TryGetArray(buffer, out ArraySegment<byte> destinationArray) ?
					ReadAtAsync(position, destinationArray.Array!, destinationArray.Offset, destinationArray.Count, cancellationToken) :
					throw new NotSupportedException("failed to get memory array");
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

	public override long Seek(long offset, SeekOrigin origin)
	{
		Position = origin switch
		{
			SeekOrigin.Begin => offset,
			SeekOrigin.Current => offset + _position,
			SeekOrigin.End => Length + offset,
			_ => throw new ArgumentOutOfRangeException(nameof(offset), offset, null)
		};
		return _position;
	}

	public override void SetLength(long value)
		=> throw new NotSupportedException();
	public override void Write(byte[] buffer, int offset, int count)
		=> throw new NotSupportedException();
	public override void Flush()
		=> throw new NotSupportedException();
}

public class SaveProgressChangedEventArgs : ProgressChangedEventArgs
{
	internal SaveProgressChangedEventArgs(int progressPercentage, object? userState, long bytesSaved, long totalBytesToSave) : base(progressPercentage, userState)
	{
		BytesSaved = bytesSaved;
		TotalBytesToSave = totalBytesToSave;
	}
 
	public long BytesSaved { get; }
	public long TotalBytesToSave { get; }
}
