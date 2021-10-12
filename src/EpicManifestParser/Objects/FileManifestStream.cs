using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Ionic.Zlib;

namespace EpicManifestParser.Objects
{
	public class FileManifestStream : Stream
	{
		public string FileName { get; }

		private readonly List<FileChunkPart> _fileChunkParts;
		private readonly Dictionary<string, FileChunk> _chunks;
		private readonly string _chunkCacheDir;
		private readonly HttpClient _client;

		internal FileManifestStream(FileManifest fileManifest, Manifest manifest)
		{
			FileName = fileManifest.Name;

			foreach (var chunkPart in fileManifest.ChunkParts)
			{
				Length += chunkPart.Size;
			}

			_fileChunkParts = fileManifest.ChunkParts;
			_chunks = manifest.Chunks;
			_chunkCacheDir = manifest.Options.ChunkCacheDirectory?.FullName;
			_client = new HttpClient(new HttpClientHandler
			{
				UseProxy = false,
				UseCookies = false,
				AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
			});
		}

		public override bool CanRead { get; } = true;
		public override bool CanSeek { get; } = true;
		public override bool CanWrite { get; } = false;
		public override long Length { get; }

		private long _position;
		public override long Position
		{
			get => _position;
			set
			{
				if (value > Length || value < 0)
				{
					throw new ArgumentOutOfRangeException();
				}

				_position = value;
			}
		}

		public override void Flush()
		{
			throw new NotSupportedException();
		}

		public override void SetLength(long value)
		{
			throw new NotSupportedException();
		}

		public override void Write(byte[] buffer, int offset, int count)
		{
			throw new NotSupportedException();
		}

		public override int Read(byte[] buffer, int offset, int count)
		{
			return ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
		}

		private async Task<Stream> GetChunkStreamAsync(FileChunk chunk, CancellationToken cancellationToken)
		{
			var cachePath = _chunkCacheDir == null ? null : Path.Combine(_chunkCacheDir, chunk.Filename);

			if (cachePath != null && File.Exists(cachePath))
			{
				return new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read);
			}

			using var message = new HttpRequestMessage(HttpMethod.Get, chunk.Uri);
			using var response = await _client.SendAsync(message, cancellationToken).ConfigureAwait(false);
			await using var chunkStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);

			using var reader = new BinaryReader(chunkStream);
			chunkStream.Position = 8L;
			var headerSize = reader.ReadInt32();
			chunkStream.Position = 40L;
			var isCompressed = reader.ReadByte() == 1;
			chunkStream.Position = headerSize;

			var outStream = cachePath == null ? new MemoryStream() : (Stream)new FileStream(cachePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);

			if (isCompressed)
			{
				await using var decompressionStream = new ZlibStream(chunkStream, CompressionMode.Decompress, CompressionLevel.Default, false);
				await decompressionStream.CopyToAsync(outStream, cancellationToken).ConfigureAwait(false);
			}
			else
			{
				await chunkStream.CopyToAsync(outStream, cancellationToken).ConfigureAwait(false);
			}

			outStream.Position = 0L;
			return outStream;
		}

		public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
		{
			var (i, startPos) = GetChunkIndex(_position);

			if (i == -1)
			{
				return 0;
			}

			var bytesRead = 0;

			while (true)
			{
				var chunkPart = _fileChunkParts[i];
				var chunk = _chunks[chunkPart.Guid];
				await using var chunkStream = await GetChunkStreamAsync(chunk, cancellationToken).ConfigureAwait(false);

				var chunkOffset = chunkPart.Offset + startPos;
				chunkStream.Position = chunkOffset;
				var chunkBytes = chunkPart.Size - startPos;
				var bytesLeft = count - bytesRead;

				if (bytesLeft <= chunkBytes)
				{
					await chunkStream.ReadAsync(buffer, bytesRead + offset, bytesLeft, cancellationToken).ConfigureAwait(false);
					bytesRead += bytesLeft;
					break;
				}

				await chunkStream.ReadAsync(buffer, bytesRead + offset, chunkBytes, cancellationToken).ConfigureAwait(false);
				bytesRead += chunkBytes;
				startPos = 0;

				if (++i == _fileChunkParts.Count)
				{
					break;
				}
			}

			_position += bytesRead;
			return bytesRead;
		}

		public override long Seek(long offset, SeekOrigin origin)
		{
			Position = origin switch
			{
				SeekOrigin.Begin => offset,
				SeekOrigin.Current => offset + _position,
				SeekOrigin.End => Length + offset,
				_ => throw new ArgumentOutOfRangeException()
			};
			return _position;
		}

		private (int Index, int ChunkPos) GetChunkIndex(long position)
		{
			for (var i = 0; i < _fileChunkParts.Count; i++)
			{
				var size = _fileChunkParts[i].Size;

				if (position < size)
				{
					return (i, (int)position);
				}

				position -= size;
			}

			return (-1, -1);
		}
	}
}
