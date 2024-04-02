using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;

using GenericReader;

using ZlibngDotNet;

namespace EpicManifestParser.UE;

public class FChunkInfo
{
	/// <summary>
	/// The GUID for this data.
	/// </summary>
	public FGuid Guid { get; internal set; }
	/// <summary>
	/// The FRollingHash hashed value for this chunk data.
	/// </summary>
	public uint64 Hash { get; internal set; }
	/// <summary>
	/// The FSHA hashed value for this chunk data.
	/// </summary>
	public FSHAHash ShaHash { get; internal set; }
	/// <summary>
	/// The group number this chunk divides into.
	/// </summary>
	public uint8 GroupNumber { get; internal set; }
	/// <summary>
	/// The window size for this chunk.
	/// </summary>
	public uint32 WindowSize { get; internal set; }
	/// <summary>
	/// The file download size for this chunk.
	/// </summary>
	public int64 FileSize { get; internal set; }

	internal string? CachePath { get; set; }

	public Uri GetUri(FBuildPatchAppManifest manifest) =>
		new($"{manifest.Options.ChunkBaseUrl}{manifest.GetChunkSubdir()}/{GroupNumber:D2}/{Hash:X16}_{Guid}.chunk", UriKind.Absolute);

	internal static FChunkInfo[] ReadChunkDataList(GenericBufferReader reader, Dictionary<FGuid, FChunkInfo> chunksDict)
	{
		var startPos = reader.Position;
		var dataSize = reader.Read<int32>();
		var dataVersion = reader.Read<EChunkDataListVersion>();
		var elementCount = reader.Read<int32>();

		var chunks = new FChunkInfo[elementCount];
		var chunksSpan = chunks.AsSpan();

		chunksDict.EnsureCapacity(elementCount);

		if (dataVersion >= EChunkDataListVersion.Original)
		{
			for (var i = 0; i < elementCount; i++)
			{
				var chunk = new FChunkInfo();
				chunk.Guid = reader.Read<FGuid>();
				chunksSpan[i] = chunk;

				chunksDict.Add(chunk.Guid, chunk);
				//ref var lookupChunk = ref CollectionsMarshal.GetValueRefOrAddDefault(chunksDict, chunk.Guid, out var exists);
				//if (!exists)
				//	lookupChunk = chunk;
			}
			for (var i = 0; i < elementCount; i++)
				chunksSpan[i].Hash = reader.Read<uint64>();
			for (var i = 0; i < elementCount; i++)
				chunksSpan[i].ShaHash = reader.Read<FSHAHash>();
			for (var i = 0; i < elementCount; i++)
				chunksSpan[i].GroupNumber = reader.Read<uint8>();
			for (var i = 0; i < elementCount; i++)
				chunksSpan[i].WindowSize = reader.Read<uint32>();
			for (var i = 0; i < elementCount; i++)
				chunksSpan[i].FileSize = reader.Read<int64>();
		}
		else
		{
			var defaultChunk = new FChunkInfo();
			chunksSpan.Fill(defaultChunk);
		}

		reader.Position = startPos + dataSize;
		return chunks;
	}

	public async Task<int> ReadDataAsync(byte[] destination, FBuildPatchAppManifest manifest, CancellationToken cancellationToken = default)
	{
		var fileSize = 0;
		var shouldCache = manifest.Options.ChunkCacheDirectory is not null;
		string? cachePath = null;

		if (shouldCache)
		{
			if (CachePath is not null)
			{
				using var fileHandle = File.OpenHandle(CachePath);
				fileSize = (int)RandomAccess.GetLength(fileHandle);
				await RandomAccess.ReadAsync(fileHandle, destination.AsMemory(0, fileSize), 0, cancellationToken).ConfigureAwait(false);
			}

			cachePath = Path.Combine(manifest.Options.ChunkCacheDirectory!, $"v2_{Hash:X16}_{Guid}.chunk");
			if (File.Exists(cachePath))
			{
				CachePath = cachePath;
				using var fileHandle = File.OpenHandle(CachePath);
				fileSize = (int)RandomAccess.GetLength(fileHandle);
				await RandomAccess.ReadAsync(fileHandle, destination.AsMemory(0, fileSize), 0, cancellationToken).ConfigureAwait(false);
			}
		}

		if (fileSize == 0)
		{
			using var _ = await manifest.ChunksLocker.LockAsync(Guid, cancellationToken).ConfigureAwait(false);
			if (shouldCache && File.Exists(cachePath))
			{
				CachePath = cachePath;
				using var fileHandle = File.OpenHandle(CachePath);
				fileSize = (int)RandomAccess.GetLength(fileHandle);
				await RandomAccess.ReadAsync(fileHandle, destination.AsMemory(0, fileSize), 0, cancellationToken).ConfigureAwait(false);
			}

			var uri = GetUri(manifest);
			var destMs = new MemoryStream(destination, 0, destination.Length, true);
			using var res = await manifest.Options.Client!.GetAsync(uri, cancellationToken).ConfigureAwait(false);
			await res.Content.CopyToAsync(destMs, cancellationToken).ConfigureAwait(false);
			fileSize = (int)destMs.Position;

			if (shouldCache)
			{
				using var fileHandle = File.OpenHandle(cachePath!, FileMode.Create, FileAccess.Write, FileShare.None, FileOptions.None, fileSize);
				await RandomAccess.WriteAsync(fileHandle, new ReadOnlyMemory<byte>(destination, 0, fileSize), 0, cancellationToken).ConfigureAwait(false);
				RandomAccess.FlushToDisk(fileHandle);
				CachePath = cachePath;
			}
		}

		var reader = new GenericBufferReader(new Memory<byte>(destination, 0, fileSize));
		var header = new FChunkHeader(reader);

		if (header.StoredAs == EChunkStorageFlags.None)
		{
			Unsafe.CopyBlockUnaligned(ref destination[0], ref destination[reader.Position], (uint)header.DataSizeCompressed);
			return header.DataSizeCompressed;
		}

		if (header.StoredAs.HasFlag(EChunkStorageFlags.Encrypted))
		{
			throw new NotSupportedException("encrypted chunks are not supported");
		}

		if (!header.StoredAs.HasFlag(EChunkStorageFlags.Compressed))
			throw new UnreachableException("unknown/new chunk ChunkStorageFlag");

		// cant uncompress in-place
		var poolBuffer = ArrayPool<byte>.Shared.Rent(header.DataSizeCompressed);

		try
		{
			Unsafe.CopyBlockUnaligned(ref poolBuffer[0], ref destination[reader.Position], (uint)header.DataSizeCompressed);

			var result = manifest.Options.Zlibng!.Uncompress(
				destination.AsSpan(0, header.DataSizeUncompressed),
				poolBuffer.AsSpan(0, header.DataSizeCompressed),
				out int bytesWritten);

			if (result != ZlibngCompressionResult.Ok || bytesWritten != header.DataSizeUncompressed)
				throw new FileLoadException("failed to uncompress chunk data");
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(poolBuffer);
		}

		return header.DataSizeUncompressed;
	}
}
