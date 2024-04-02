using System.Buffers;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

using AsyncKeyedLock;

using EpicManifestParser.Json;

using GenericReader;

using ZlibngDotNet;

namespace EpicManifestParser.UE;

public class FBuildPatchAppManifest
{
	public FManifestMeta ManifestMeta { get; internal set; } = null!;
	public IReadOnlyList<FChunkInfo> ChunkDataList { get; internal set; } = null!;
	public IReadOnlyList<FFileManifest> FileManifestList { get; internal set; } = null!;
	public IReadOnlyList<FCustomField> CustomFields { get; internal set; } = null!;
	public IReadOnlyDictionary<FGuid, FChunkInfo> Chunks { get; internal set; } = null!;

	public int64 TotalBuildSize { get; internal set; }
	public int64 TotalDownloadSize { get; internal set; }

	internal ManifestParseOptions Options { get; init; } = null!;
	internal AsyncKeyedLocker<FGuid> ChunksLocker { get; set; } = null!;

	internal FBuildPatchAppManifest() { }

	public string GetChunkSubdir() => ManifestMeta.FeatureLevel switch
	{
		> EFeatureLevel.StoredAsBinaryData => "ChunksV4",
		> EFeatureLevel.StoresDataGroupNumbers => "ChunksV3",
		> EFeatureLevel.StartStoringVersion => "ChunksV2",
		_ => "Chunks"
	};

	/// <summary>
	/// Helper function to decide whether the passed in data is a JSON string we expect to deserialize a manifest from
	/// </summary>
	/// <param name="dataInput"></param>
	/// <returns></returns>
	public static bool IsJson(byte[] dataInput)
	{
		// The best we can do is look for the mandatory first character open curly brace,
		// it will be within the first 4 characters (may have BOM)
		for (var idx = 0; idx < 4 && idx < dataInput.Length; ++idx)
		{
			if (dataInput[idx] == '{')
			{
				return true;
			}
		}
		return false;
	}

	public static FBuildPatchAppManifest Deserialize(byte[] dataInput, Action<ManifestParseOptions>? optionsBuilder = null)
	{
		var options = new ManifestParseOptions();
		optionsBuilder?.Invoke(options);
		return Deserialize(dataInput, options);
	}

	public static FBuildPatchAppManifest DeserializeJson(byte[] dataInput, Action<ManifestParseOptions>? optionsBuilder = null)
	{
		var options = new ManifestParseOptions();
		optionsBuilder?.Invoke(options);
		return DeserializeJson(dataInput, options);
	}

	public static FBuildPatchAppManifest DeserializeBinary(byte[] dataInput, Action<ManifestParseOptions>? optionsBuilder = null)
	{
		var options = new ManifestParseOptions();
		optionsBuilder?.Invoke(options);
		return DeserializeBinary(dataInput, options);
	}

	public static FBuildPatchAppManifest Deserialize(byte[] dataInput, ManifestParseOptions options)
	{
		return IsJson(dataInput) ? DeserializeJson(dataInput, options) : DeserializeBinary(dataInput, options);
	}

	public static FBuildPatchAppManifest DeserializeJson(byte[] dataInput, ManifestParseOptions options)
	{
		var reader = JsonNode.Parse(dataInput)!.AsObject();

		var featureLevel = reader["ManifestFileVersion"].GetBlob(EFeatureLevel.CustomFields);
		if (featureLevel == EFeatureLevel.BrokenJsonVersion)
			featureLevel = EFeatureLevel.StoresChunkFileSizes;

		var meta = new FManifestMeta
		{
			FeatureLevel = featureLevel,
			AppID = reader["AppID"].GetBlob<uint32>(),
			AppName = reader["AppNameString"].GetString(),
			BuildVersion = reader["BuildVersionString"].GetString(),
			LaunchExe = reader["LaunchExeString"].GetString(),
			LaunchCommand = reader["LaunchCommand"].GetString(),
			PrereqName = reader["PrereqName"].GetString(),
			PrereqPath = reader["PrereqPath"].GetString(),
			PrereqArgs = reader["PrereqArgs"].GetString(),
			UninstallExe = "",
			UninstallCommand = "",
		};

		var jsonFileManifestList = reader["FileManifestList"]!.AsArray();
		var fileManifests = new FFileManifest[jsonFileManifestList.Count];
		var fileManifestsSpan = fileManifests.AsSpan();

		//var allDataGuids = new HashSet<FGuid>();
		var mutableChunkInfoLookup = new Dictionary<FGuid, FChunkInfo>();

		for (var i = 0; i < fileManifestsSpan.Length; i++)
		{
			var jsonFileManifest = jsonFileManifestList[i]!;
			var fileManifest = fileManifestsSpan[i] = new FFileManifest
			{
				FileName = jsonFileManifest["Filename"].GetString(),
				FileHash = jsonFileManifest["FileHash"].GetBlob<FSHAHash>(),
				InstallTags = jsonFileManifest["InstallTags"].Parse<string[]>([]),
				SymlinkTarget = jsonFileManifest["SymlinkTarget"].GetString()
			};
			var jsonFileChunkParts = jsonFileManifest["FileChunkParts"]!.AsArray();
			fileManifest.ChunkParts = new FChunkPart[jsonFileChunkParts.Count];
			var chunkPartsSpan = fileManifest.ChunkParts.AsSpan();
			for (var j = 0; j < chunkPartsSpan.Length; j++)
			{
				var jsonFileChunkPart = jsonFileChunkParts[j]!;
				var chunkPartGuid = jsonFileChunkPart["Guid"].GetFGuid();
				var chunkPartOffset = jsonFileChunkPart["Offset"].GetBlob<uint32>();
				var chunkPartSize = jsonFileChunkPart["Size"].GetBlob<uint32>();
				chunkPartsSpan[j] = new FChunkPart(chunkPartGuid, chunkPartOffset, chunkPartSize);

				ref var lookupChunk = ref CollectionsMarshal.GetValueRefOrAddDefault(mutableChunkInfoLookup, chunkPartGuid, out var exists);
				if (!exists)
				{
					lookupChunk = new FChunkInfo
					{
						Guid = chunkPartGuid
					};
				}
			}

			if (jsonFileManifest["bIsUnixExecutable"].Get<bool>())
				fileManifest.FileMetaFlags |= EFileMetaFlags.UnixExecutable;
			if (jsonFileManifest["bIsReadOnly"].Get<bool>())
				fileManifest.FileMetaFlags |= EFileMetaFlags.ReadOnly;
			if (jsonFileManifest["bIsCompressed"].Get<bool>())
				fileManifest.FileMetaFlags |= EFileMetaFlags.Compressed;
		}

		var chunkList = new FChunkInfo[mutableChunkInfoLookup.Count];
		var chunkListSpan = chunkList.AsSpan();
		var chunkIndex = 0;
		foreach (var chunk in mutableChunkInfoLookup.Values)
		{
			chunkListSpan[chunkIndex++] = chunk;
		}

		var hasChunkHashList = false;
		var jsonChunkHashListNode = reader["ChunkHashList"];
		if (jsonChunkHashListNode is not null)
		{
			var jsonChunkHashList = jsonChunkHashListNode.AsObject();

			foreach (var (guidString, jsonChunkHash) in jsonChunkHashList)
			{
				var guid = new FGuid(guidString);
				var chunkHash = jsonChunkHash.GetBlob<uint64>();
				mutableChunkInfoLookup[guid].Hash = chunkHash;
			}

			hasChunkHashList = true;
		}

		var jsonChunkShaListNode = reader["ChunkShaList"];
		if (jsonChunkShaListNode is not null)
		{
			var jsonChunkShaList = jsonChunkShaListNode.AsObject();

			foreach (var (guidString, jsonSha) in jsonChunkShaList)
			{
				var guid = new FGuid(guidString);
				var chunkSha = jsonSha.GetSha();
				mutableChunkInfoLookup[guid].ShaHash = chunkSha;
			}
		}

		var prereqIds = reader["PrereqIds"].Deserialize<string[]>();
		if (prereqIds is null)
		{
			// TODO: https://github.com/EpicGames/UnrealEngine/blob/8c31706601135aadf2f957fb76e2af46f04a8ef9/Engine/Source/Runtime/Online/BuildPatchServices/Private/BuildPatchManifest.cpp#L602
			meta.PrereqIds = [];
		}
		else
		{
			meta.PrereqIds = prereqIds;
		}

		var jsonDataGroupListNode = reader["DataGroupList"];
		if (jsonDataGroupListNode is not null)
		{
			var jsonDataGroupList = jsonDataGroupListNode.AsObject();

			foreach (var (guidString, jsonDataGroup) in jsonDataGroupList)
			{
				var guid = new FGuid(guidString);
				var dataGroup = jsonDataGroup.GetBlob<uint8>();
				mutableChunkInfoLookup[guid].GroupNumber = dataGroup;
			}
		}
		else
		{
			// TODO: https://github.com/EpicGames/UnrealEngine/blob/8c31706601135aadf2f957fb76e2af46f04a8ef9/Engine/Source/Runtime/Online/BuildPatchServices/Private/BuildPatchManifest.cpp#L635
			//       https://github.com/EpicGames/UnrealEngine/blob/8c31706601135aadf2f957fb76e2af46f04a8ef9/Engine/Source/Runtime/Core/Private/Misc/Crc.cpp#L592
		}

		var hasChunkFilesizeList = false;
		var jsonChunkFilesizeListNode = reader["ChunkFilesizeList"];
		if (jsonChunkFilesizeListNode is not null)
		{
			var jsonChunkFilesizeList = jsonChunkFilesizeListNode.AsObject();

			foreach (var (guidString, jsonFileSize) in jsonChunkFilesizeList)
			{
				var guid = new FGuid(guidString);
				var fileSize = jsonFileSize.GetBlob<int64>();
				mutableChunkInfoLookup[guid].FileSize = fileSize;
			}

			hasChunkFilesizeList = true;
		}

		if (!hasChunkFilesizeList)
		{
			// Missing chunk list, version before we saved them compressed. Assume original fixed chunk size of 1 MiB.
			foreach (var chunk in chunkListSpan)
			{
				chunk.FileSize = 1048576;
			}
		}

		if (reader.TryGetPropertyValue("bIsFileData", out var jsonIsFileData))
		{
			meta.bIsFileData = jsonIsFileData.Get<bool>();
		}
		else
		{
			meta.bIsFileData = !hasChunkHashList;
		}

		FCustomField[]? customFields = null;
		var jsonCustomFieldsNode = reader["CustomFields"];
		if (jsonCustomFieldsNode is not null)
		{
			var jsonCustomFields = jsonCustomFieldsNode.AsObject();
			customFields = new FCustomField[jsonCustomFields.Count];
			var customFieldIndex = 0;

			foreach (var (name, jsonValue) in jsonCustomFields)
			{
				customFields[customFieldIndex++] = new FCustomField
				{
					Name = name,
					Value = jsonValue.GetString()
				};
			}
		}

		meta.BuildId = FManifestMeta.GetBackwardsCompatibleBuildId(meta);

		var manifest = new FBuildPatchAppManifest
		{
			ManifestMeta = meta,
			ChunkDataList = chunkList,
			FileManifestList = fileManifests,
			CustomFields = customFields ?? [],
			Chunks = mutableChunkInfoLookup,
			Options = options
		};
		manifest.PostSetup();

		// FileDataList.OnPostLoad();
		{
			Array.Sort(fileManifests);
			for (var i = 0; i < fileManifestsSpan.Length; i++)
			{
				var file = fileManifestsSpan[i];
				file.Manifest = manifest;
				foreach (var chunkPart in file.ChunkParts)
				{
					file.FileSize += chunkPart.Size;
				}
			}
		}

		return manifest;
	}

	//inline unsigned int8* Align(const unsigned int8* Val, const uniform int Alignment)
	//{
	//	return (unsigned int8*)(((unsigned int64)Val + Alignment - 1) & ~(Alignment - 1));
	//}

	public static FBuildPatchAppManifest DeserializeBinary(byte[] dataInput, ManifestParseOptions options)
	{
		var fileReader = new GenericBufferReader(dataInput);
		byte[]? manifestRawDataBuffer = null;

		try
		{
			var header = new FManifestHeader(fileReader);

			if (header.Version < EFeatureLevel.StoredAsBinaryData)
				throw new NotSupportedException();

			if (header.StoredAs.HasFlag(EManifestStorageFlags.Encrypted))
				throw new NotSupportedException();

			Memory<byte> manifestRawData;

			if (header.StoredAs.HasFlag(EManifestStorageFlags.Compressed))
			{
				if (options.Zlibng is null)
					throw new FileLoadException("data is compressed and zlib-ng instance was null");

				manifestRawDataBuffer = ArrayPool<byte>.Shared.Rent(header.DataSizeCompressed + header.DataSizeUncompressed);
				manifestRawData = manifestRawDataBuffer.AsMemory(header.DataSizeCompressed, header.DataSizeUncompressed);
				var manifestCompressedData = manifestRawDataBuffer.AsSpan(0, header.DataSizeCompressed);
				fileReader.Read(manifestCompressedData);

				var result = options.Zlibng.Uncompress2(manifestRawData.Span, manifestCompressedData, out int32 bytesWritten, out int32 bytesConsumed);
				if (result != ZlibngCompressionResult.Ok || bytesWritten != header.DataSizeUncompressed || bytesConsumed != header.DataSizeCompressed)
					throw new FileLoadException("Failed to uncompress data");
			}
			else
			{
				manifestRawDataBuffer = ArrayPool<byte>.Shared.Rent(header.DataSizeCompressed);
				manifestRawData = manifestRawDataBuffer.AsMemory(0, header.DataSizeCompressed);
				fileReader.Read(manifestRawData.Span);
			}

			{
				var result = FSHAHash.TryCompute(manifestRawData.Span, out var hash, out var bytesWritten);
				if (!result || bytesWritten != FSHAHash.Size)
					throw new InvalidDataException("failed to calculate hash");
				if (header.SHAHash != hash)
					throw new InvalidDataException($"hash does not match. expected: {header.SHAHash}, actual: {hash}");
			}

			var reader = new GenericBufferReader(manifestRawData);
			var chunks = new Dictionary<FGuid, FChunkInfo>();
			var manifest = new FBuildPatchAppManifest
			{
				Chunks = chunks,
				Options = options
			};
			manifest.ManifestMeta = new FManifestMeta(reader);
			manifest.ChunkDataList = FChunkInfo.ReadChunkDataList(reader, chunks);
			manifest.FileManifestList = FFileManifest.ReadFileDataList(reader, manifest);
			manifest.CustomFields = FCustomField.ReadCustomFields(reader);
			manifest.PostSetup();
			return manifest;
		}
		finally
		{
			if (manifestRawDataBuffer is not null)
				ArrayPool<byte>.Shared.Return(manifestRawDataBuffer);
		}
	}

	private void PostSetup()
	{
		foreach (var file in FileManifestList)
		{
			TotalBuildSize += file.FileSize;
		}

		foreach (var chunk in ChunkDataList)
		{
			TotalDownloadSize += chunk.FileSize;
		}

		if (!string.IsNullOrEmpty(Options.ChunkBaseUrl))
		{
			ChunksLocker = new AsyncKeyedLocker<FGuid>(lockerOptions =>
			{
				lockerOptions.MaxCount = 1;
				lockerOptions.PoolSize = 128;
				lockerOptions.PoolInitialFill = 64;
			});

			Options.CreateDefaultClient();
		}
	}
}
