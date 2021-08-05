using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using EpicManifestParser.Enums;
using GenericReader;
using Ionic.Zlib;

namespace EpicManifestParser.Objects
{
	public sealed class Manifest
	{
		public int ManifestFileVersion { get; private set; }
		public bool IsFileData { get; private set; }
		public int AppId { get; private set; }
		public string AppName { get; private set; }
		public string BuildVersion { get; private set; }
		public Version Version { get; private set; }
		public int CL { get; private set; }
		public string LaunchExe { get; private set; }
		public string LaunchCommand { get; private set; }
		public List<string> PrereqIds { get; private set; }
		public string PrereqName { get; private set; }
		public string PrereqPath { get; private set; }
		public string PrereqArgs { get; private set; }
		public List<FileManifest> FileManifests { get; private set; }
		public Dictionary<string, string> ChunkHashes { get; private set; }
		public Dictionary<string, string> ChunkShas { get; private set; }
		public Dictionary<string, byte> DataGroups { get; private set; }
		public Dictionary<string, long> ChunkFilesizes { get; private set; }
		public Dictionary<string, string> CustomFields { get; private set; }
		public Dictionary<string, FileChunk> Chunks { get; }
		public TimeSpan ParseTime { get; }

		internal ManifestOptions Options { get; }

		private const uint _MANIFEST_HEADER_MAGIC = 0x44BEC00Cu;

		public Manifest(byte[] data, ManifestOptions options = null)
		{
			Options = options ??= new ManifestOptions();
			if (options.ChunkCacheDirectory is {Exists: false})
			{
				options.ChunkCacheDirectory.Create();
			}

			var sw = Stopwatch.StartNew();
			var magic = BitConverter.ToUInt32(data, 0);
			if (magic == _MANIFEST_HEADER_MAGIC)
			{
				ParseData(data);
			}
			else
			{
				ParseJson(data);
			}
			
			var buildMatch = Regex.Match(BuildVersion, @"(\d+(?:\.\d+)+)-CL-(\d+)", RegexOptions.Singleline);
			if (buildMatch.Success)
			{
				Version = Version.Parse(buildMatch.Groups[1].Value);
				CL = int.Parse(buildMatch.Groups[2].Value);
			}

			Chunks = new Dictionary<string, FileChunk>(ChunkFilesizes.Count);
			foreach (var (guid, size) in ChunkFilesizes)
			{
				var chunk = new FileChunk(guid, size, ChunkHashes[guid], ChunkShas[guid], DataGroups[guid], options.ChunkBaseUri);
				Chunks.Add(guid, chunk);
			}

			sw.Stop();
			ParseTime = sw.Elapsed;
		}

		public (int Count, long Size) DeleteUnusedChunks()
		{
			if (!(Options.ChunkCacheDirectory is {Exists: true}))
			{
				return default;
			}

			var chunkMap = Chunks.Values.Select(x => x.Filename).ToHashSet(StringComparer.OrdinalIgnoreCase);
			var deletedCount = 0;
			var deletedSize = 0L;

			foreach (var chunk in Options.ChunkCacheDirectory.EnumerateFiles("*.chunk"))
			{
				if (chunkMap.Contains(chunk.Name))
				{
					continue;
				}

				chunk.Delete();
				deletedCount++;
				deletedSize += chunk.Length;
			}

			return (deletedCount, deletedSize);
		}

		private void ParseJson(byte[] buffer)
		{
			var reader = new Utf8JsonReader(buffer, true, new JsonReaderState());
			while (reader.Read())
			{
				if (reader.TokenType != JsonTokenType.PropertyName)
				{
					continue;
				}

				switch (reader.GetString())
				{
					case "ManifestFileVersion":
					{
						reader.Read();
						ManifestFileVersion = Utilities.StringBlobTo<int>(reader.ValueSpan);
						break;
					}
					case "bIsFileData":
					{
						reader.Read();
						IsFileData = reader.GetBoolean();
						break;
					}
					case "AppID":
					{
						reader.Read();
						AppId = Utilities.StringBlobTo<int>(reader.ValueSpan);
						break;
					}
					case "AppNameString":
					{
						reader.Read();
						AppName = reader.GetString();
						break;
					}
					case "BuildVersionString":
					{
						reader.Read();
						BuildVersion = reader.GetString();
						break;
					}
					case "LaunchExeString":
					{
						reader.Read();
						LaunchExe = reader.GetString();
						break;
					}
					case "LaunchCommand":
					{
						reader.Read();
						LaunchCommand = reader.GetString();
						break;
					}
					case "PrereqIds":
					{
						reader.Read();

						if (reader.TokenType != JsonTokenType.StartArray)
						{
							break;
						}

						PrereqIds = new List<string>();
						while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
						{
							PrereqIds.Add(reader.GetString());
						}

						break;
					}
					case "PrereqName":
					{
						reader.Read();
						PrereqName = reader.GetString();
						break;
					}
					case "PrereqPath":
					{
						reader.Read();
						PrereqPath = reader.GetString();
						break;
					}
					case "PrereqArgs":
					{
						reader.Read();
						PrereqArgs = reader.GetString();
						break;
					}
					case "FileManifestList":
					{
						reader.Read();

						if (reader.TokenType != JsonTokenType.StartArray)
						{
							break;
						}

						FileManifests = new List<FileManifest>();
						while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
						{
							FileManifests.Add(new FileManifest(ref reader, this));
						}

						break;
					}
					case "ChunkHashList":
					{
						reader.Read();

						if (reader.TokenType != JsonTokenType.StartObject)
						{
							break;
						}

						ChunkHashes = new Dictionary<string, string>();
						while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
						{
							var guid = reader.GetString();
							reader.Read();
							var hash = Utilities.StringBlobToHexString(reader.ValueSpan, true);
							ChunkHashes.Add(guid, hash);
						}

						break;
					}
					case "ChunkShaList":
					{
						reader.Read();

						if (reader.TokenType != JsonTokenType.StartObject)
						{
							break;
						}

						ChunkShas = new Dictionary<string, string>();
						while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
						{
							var guid = reader.GetString();
							reader.Read();
							var sha = reader.GetString();
							ChunkShas.Add(guid, sha);
						}

						break;
					}
					case "DataGroupList":
					{
						reader.Read();

						if (reader.TokenType != JsonTokenType.StartObject)
						{
							break;
						}

						DataGroups = new Dictionary<string, byte>();
						while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
						{
							var guid = reader.GetString();
							reader.Read();
							var b = Utilities.GetByte(reader.ValueSpan);
							DataGroups.Add(guid, b);
						}

						break;
					}
					case "ChunkFilesizeList":
					{
						reader.Read();

						if (reader.TokenType != JsonTokenType.StartObject)
						{
							break;
						}

						ChunkFilesizes = new Dictionary<string, long>();
						while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
						{
							var guid = reader.GetString();
							reader.Read();
							var size = Utilities.StringBlobTo<long>(reader.ValueSpan);
							ChunkFilesizes.Add(guid, size);
						}

						break;
					}
					case "CustomFields":
					{
						reader.Read();

						if (reader.TokenType != JsonTokenType.StartObject)
						{
							break;
						}

						CustomFields = new Dictionary<string, string>();
						while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
						{
							var key = reader.GetString();
							reader.Read();
							var value = reader.GetString();
							CustomFields.Add(key, value);
						}

						break;
					}
				}
			}
		}

		private void ParseData(byte[] buffer)
		{
			var reader = new GenericBufferReader(buffer) { Position = 4 };
			var headerSize = reader.Read<int>();
			var dataSizeUncompressed = reader.Read<int>();
			var dataSizeCompressed = reader.Read<int>();
			reader.Position += 20; // SHAHash.Hash
			var storedAs = reader.Read<EManifestStorageFlags>();
			var version = reader.Read<EFeatureLevel>();
			reader.Seek(headerSize, SeekOrigin.Begin);

			var data = new byte[dataSizeUncompressed];
			switch (storedAs)
			{
				case EManifestStorageFlags.Compressed:
				{
					var compressed = reader.ReadBytes(dataSizeCompressed);
					using var compressedStream = new MemoryStream(compressed) {Position = 0};
					using var zlib = new ZlibStream(compressedStream, CompressionMode.Decompress);
					zlib.Read(data, 0, dataSizeUncompressed);
					break;
				}
				case EManifestStorageFlags.Encrypted:
					throw new NotImplementedException("Encrypted Manifests are not supported yet");
				default:
					data = reader.ReadBytes(dataSizeUncompressed);
					break;
			}
			reader.Dispose();

			var manifest = new GenericBufferReader(data);
			var startPos = (int)manifest.Position;
			var dataSize = manifest.Read<int>();
			var dataVersion = manifest.Read<EManifestMetaVersion>();
			if (dataVersion >= EManifestMetaVersion.Original)
			{
				var featureLevel = manifest.Read<EFeatureLevel>();
				// var builder = new UriBuilder(Options.ChunkBaseUri);
				// builder.Path += featureLevel switch
				// {
				// 	EFeatureLevel.DataFileRenames => "Chunks/",
				// 	EFeatureLevel.ChunkCompressionSupport => "ChunksV2/",
				// 	EFeatureLevel.VariableSizeChunksWithoutWindowSizeChunkInfo => "ChunksV3/",
				// 	_ => "ChunksV4/"
				// };
				// Options.ChunkBaseUri = builder.Uri;
				IsFileData = manifest.ReadByte() != 0x00;
				AppId = manifest.Read<int>();
				AppName = manifest.ReadFString();
				BuildVersion = manifest.ReadFString();
				LaunchExe = manifest.ReadFString();
				LaunchCommand = manifest.ReadFString();
				PrereqIds = manifest.ReadArray(() => manifest.ReadFString()).ToList();
				PrereqName = manifest.ReadFString();
				PrereqPath = manifest.ReadFString();
				PrereqArgs = manifest.ReadFString();
			}

			if (dataVersion >= EManifestMetaVersion.SerialisesBuildId)
				BuildVersion = manifest.ReadFString();

			manifest.Seek(startPos + dataSize, SeekOrigin.Begin);
			startPos = (int)manifest.Position;
			dataSize = manifest.Read<int>();
			dataVersion = manifest.Read<EManifestMetaVersion>();
			if (dataVersion >= EManifestMetaVersion.Original)
			{
				var count = manifest.Read<int>();
				ChunkFilesizes = new Dictionary<string, long>(count);
				ChunkHashes = new Dictionary<string, string>(count);
				ChunkShas = new Dictionary<string, string>(count);
				DataGroups = new Dictionary<string, byte>(count);

				for (var i = 0; i < count; i++) // Guid
				{
					var hex = manifest.ReadBytes(16);
					Array.Reverse(hex, 0, 4);
					Array.Reverse(hex, 4, 4);
					Array.Reverse(hex, 8, 4);
					Array.Reverse(hex, 12, 4);
					var guid = BitConverter.ToString(hex).Replace("-", "");
					ChunkFilesizes.Add(guid, 0);
					ChunkHashes.Add(guid, string.Empty);
					ChunkShas.Add(guid, string.Empty);
					DataGroups.Add(guid, 0x00);
				}

				foreach (var key in ChunkHashes.Keys.ToList()) // Hash
				{
					var hex = manifest.ReadBytes(8);
					Array.Reverse(hex);
					ChunkHashes[key] = BitConverter.ToString(hex).Replace("-", "");
				}

				foreach (var key in ChunkShas.Keys.ToList()) // ShaHash
				{
					ChunkShas[key] = BitConverter.ToString(manifest.ReadBytes(20)).Replace("-", "");
				}

				foreach (var key in DataGroups.Keys.ToList()) // GroupNumber
				{
					DataGroups[key] = manifest.ReadByte();
				}

				manifest.Position += count * 4; // WindowSize

				foreach (var key in ChunkFilesizes.Keys.ToList()) // FileSize
				{
					ChunkFilesizes[key] = manifest.Read<long>();
				}
			}

			manifest.Seek(startPos + dataSize, SeekOrigin.Begin);
			startPos = (int)manifest.Position;
			dataSize = manifest.Read<int>();
			dataVersion = manifest.Read<EManifestMetaVersion>();
			if (dataVersion >= EManifestMetaVersion.Original)
			{
				var count = manifest.Read<int>();
				FileManifests = new List<FileManifest>(count);

				for (var i = 0; i < count; i++) // Filename
				{
					var filename = manifest.ReadFString();
					FileManifests.Add(new FileManifest(this) { Name = filename });
				}

				for (var i = 0; i < count; i++) // SymlinkTarget
				{
					manifest.ReadFString();
				}

				foreach (var file in FileManifests) // FileHash
				{
					file.Hash = BitConverter.ToString(manifest.ReadBytes(20)).Replace("-", "");
				}

				manifest.Position += count; // FileList

				foreach (var file in FileManifests) // InstallTags
				{
					file.InstallTags = manifest.ReadArray(() => manifest.ReadFString()).ToList();
				}

				foreach (var file in FileManifests) // ChunkParts
				{
					file.ChunkParts = manifest.ReadArray(() => new FileChunkPart(ref manifest)).ToList();
				}
			}

			manifest.Seek(startPos + dataSize, SeekOrigin.Begin);
			startPos = (int)manifest.Position;
			dataSize = manifest.Read<int>();
			dataVersion = manifest.Read<EManifestMetaVersion>();
			if (dataVersion >= EManifestMetaVersion.Original)
			{
				var count = manifest.Read<int>();
				CustomFields = new Dictionary<string, string>(count);

				for (var i = 0; i < count; i++)
				{
					CustomFields.Add(manifest.ReadFString(), string.Empty);
				}

				foreach (var key in CustomFields.Keys.ToList())
				{
					CustomFields[key] = manifest.ReadFString();
				}
			}
		}
	}
}