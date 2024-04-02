using System.Buffers;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using EpicManifestParser.UE;
using Microsoft.Win32.SafeHandles;

namespace EpicManifestParser;
// ReSharper disable UseSymbolAlias

public class FFileManifestStream : Stream
{
	private readonly FFileManifest _fileManifest;

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

	internal FFileManifestStream(FFileManifest fileManifest)
	{
		if (string.IsNullOrEmpty(fileManifest.Manifest.Options.ChunkBaseUrl))
			throw new ArgumentException("missing DistrubutionPoint");
		if (fileManifest.Manifest.ManifestMeta.bIsFileData)
			throw new NotSupportedException("filedata manifests are not supported");

		_fileManifest = fileManifest;
	}

	// TODO: make concurrent
	public async Task SaveToAsync(Stream destination, CancellationToken cancellationToken = default)
	{
		var poolBuffer = ArrayPool<byte>.Shared.Rent(_fileManifest.Manifest.Options.ChunkDownloadBufferSize);

		try
		{
			foreach (var fileChunkPart in _fileManifest.ChunkParts)
			{
				var chunk = _fileManifest.Manifest.Chunks[fileChunkPart.Guid];
				await chunk.ReadDataAsync(poolBuffer, _fileManifest.Manifest, cancellationToken).ConfigureAwait(false);
				await destination.WriteAsync(new ReadOnlyMemory<byte>(poolBuffer, (int)fileChunkPart.Offset, (int)fileChunkPart.Size), cancellationToken).ConfigureAwait(false);
			}
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(poolBuffer);
		}

		await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
	}

	public async Task SaveBytesAsync(byte[] destination, int maxDegreeOfParallelism = 16, CancellationToken cancellationToken = default)
	{
		var parallelOptions = new ParallelOptions
		{
			MaxDegreeOfParallelism = maxDegreeOfParallelism,
			CancellationToken = cancellationToken
		};
		await Parallel.ForEachAsync(EnumerateChunksWithOffset(destination), parallelOptions, async (tuple, token) =>
		{
			var poolBuffer = ArrayPool<byte>.Shared.Rent(tuple.Manifest.Options.ChunkDownloadBufferSize);

			try
			{
				await tuple.Chunk.ReadDataAsync(poolBuffer, tuple.Manifest, token).ConfigureAwait(false);
				Unsafe.CopyBlockUnaligned(ref tuple.Buffer[tuple.Offset], ref poolBuffer[tuple.ChunkPartOffset], tuple.ChunkPartSize);
			}
			finally
			{
				ArrayPool<byte>.Shared.Return(poolBuffer);
			}
		}).ConfigureAwait(false);
	}

	public async Task<byte[]> SaveBytesAsync(int maxDegreeOfParallelism = 16, CancellationToken cancellationToken = default)
	{
		var destination = new byte[Length];
		await SaveBytesAsync(destination, maxDegreeOfParallelism, cancellationToken).ConfigureAwait(false);
		return destination;
	}

	private IEnumerable<(FBuildPatchAppManifest Manifest, FChunkInfo Chunk, uint ChunkPartOffset, uint ChunkPartSize, byte[] Buffer, long Offset)>
		EnumerateChunksWithOffset(byte[] buffer)
	{
		var offset = 0L;

		foreach (var fileChunkPart in _fileManifest.ChunkParts)
		{
			var chunk = _fileManifest.Manifest.Chunks[fileChunkPart.Guid];
			yield return (_fileManifest.Manifest, chunk, fileChunkPart.Offset, fileChunkPart.Size, buffer, offset);
			offset += fileChunkPart.Size;
		}
	}

	public async Task SaveFileAsync(string path, int maxDegreeOfParallelism = 16, CancellationToken cancellationToken = default)
	{
		using var destination = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, FileOptions.Asynchronous, Length);

		var parallelOptions = new ParallelOptions
		{
			MaxDegreeOfParallelism = maxDegreeOfParallelism,
			CancellationToken = cancellationToken
		};
		await Parallel.ForEachAsync(EnumerateChunksWithOffset(destination), parallelOptions, async (tuple, token) =>
		{
			var poolBuffer = ArrayPool<byte>.Shared.Rent(tuple.Manifest.Options.ChunkDownloadBufferSize);

			try
			{
				await tuple.Chunk.ReadDataAsync(poolBuffer, tuple.Manifest, token).ConfigureAwait(false);
				await RandomAccess.WriteAsync(tuple.Handle
					, new ReadOnlyMemory<byte>(poolBuffer, tuple.ChunkPartOffset, tuple.ChunkPartSize)
					, tuple.Offset, cancellationToken).ConfigureAwait(false);
			}
			finally
			{
				ArrayPool<byte>.Shared.Return(poolBuffer);
			}
		}).ConfigureAwait(false);

		RandomAccess.FlushToDisk(destination);
	}

	private IEnumerable<(FBuildPatchAppManifest Manifest, FChunkInfo Chunk, int ChunkPartOffset, int ChunkPartSize, SafeFileHandle Handle, long Offset)>
		EnumerateChunksWithOffset(SafeFileHandle handle)
	{
		var offset = 0L;

		foreach (var fileChunkPart in _fileManifest.ChunkParts)
		{
			var chunk = _fileManifest.Manifest.Chunks[fileChunkPart.Guid];
			yield return (_fileManifest.Manifest, chunk, (int)fileChunkPart.Offset, (int)fileChunkPart.Size, handle, offset);
			offset += fileChunkPart.Size;
		}
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

	public async Task<int> ReadAtAsync(long position, byte[] buffer, int offset, int count, CancellationToken cancellationToken)
	{
		var (i, startPos) = GetChunkIndex(position);
		if (i == -1)
			return 0;

		var bytesRead = 0u;
		var poolBuffer = ArrayPool<byte>.Shared.Rent(_fileManifest.Manifest.Options.ChunkDownloadBufferSize);

		try
		{
			while (true)
			{
				var chunkPart = _fileManifest.ChunkParts[i];
				var chunk = _fileManifest.Manifest.Chunks[chunkPart.Guid];

				await chunk.ReadDataAsync(poolBuffer, _fileManifest.Manifest, cancellationToken).ConfigureAwait(false);

				var chunkOffset = chunkPart.Offset + startPos;
				//chunkStream.Position = chunkOffset;
				var chunkBytes = chunkPart.Size - startPos;
				var bytesLeft = (uint)count - bytesRead;

				if (bytesLeft <= chunkBytes)
				{
					Unsafe.CopyBlockUnaligned(ref buffer[bytesRead + offset], ref poolBuffer[chunkOffset], (uint)bytesLeft);
					bytesRead += bytesLeft;
					break;
				}

				Unsafe.CopyBlockUnaligned(ref buffer[bytesRead + offset], ref poolBuffer[chunkOffset], chunkBytes);
				//await RandomAccess.ReadAsync(chunkHandle, buffer.AsMemory(bytesRead + offset, chunkBytes), chunkOffset, cancellationToken).ConfigureAwait(false);
				bytesRead += chunkBytes;
				startPos = 0;

				if (++i == _fileManifest.ChunkParts.Length)
				{
					break;
				}
			}
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(poolBuffer);
		}

		return (int)bytesRead;
	}

	public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
	{
		if (cancellationToken.IsCancellationRequested)
			return ValueTask.FromCanceled<int>(cancellationToken);

		try
		{
			return new ValueTask<int>(
				MemoryMarshal.TryGetArray(buffer, out ArraySegment<byte> destinationArray) ?
					ReadAsync(destinationArray.Array!, destinationArray.Offset, destinationArray.Count, cancellationToken) :
					throw new NotSupportedException("failed to get memory array"));
		}
		catch (OperationCanceledException oce)
		{
			return new ValueTask<int>(Task.FromCanceled<int>(oce.CancellationToken));
		}
		catch (Exception exception)
		{
			return ValueTask.FromException<int>(exception);
		}
	}

	private (int Index, uint ChunkPos) GetChunkIndex(long position)
	{
		for (var i = 0; i < _fileManifest.ChunkParts.Length; i++)
		{
			var chunk = _fileManifest.ChunkParts[i];

			if (position < chunk.Size)
				return (i, (uint)position);

			position -= chunk.Size;
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
