using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EpicManifestParser.Objects
{
	public sealed class Manifest
	{
		public int ManifestFileVersion { get; }
		public bool IsFileData { get; }
		public int AppId { get; }
		public string AppName { get; }
		public string BuildVersion { get; }
		public Version Version { get; }
		public int CL { get; }
		public string LaunchExe { get; }
		public string LaunchCommand { get; }
		public List<string> PrereqIds { get; }
		public string PrereqName { get; }
		public string PrereqPath { get; }
		public string PrereqArgs { get; }
		public List<FileManifest> FileManifests { get; }
		public Dictionary<string, string> ChunkHashes { get; }
		public Dictionary<string, string> ChunkShas { get; }
		public Dictionary<string, byte> DataGroups { get; }
		public Dictionary<string, long> ChunkFilesizes { get; }
		//public Dictionary<string, object> CustomFields { get; }
		public TimeSpan ParseTime { get; }

		public Manifest(byte[] data)
		{
			var sw = Stopwatch.StartNew();
			var reader = new Utf8JsonReader(data, true, new JsonReaderState());

			while (reader.Read())
			{
				if (reader.TokenType != JsonTokenType.PropertyName)
				{
					continue;
				}

				switch (reader.GetPString())
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
						AppName = reader.GetPString();
						break;
					}
					case "BuildVersionString":
					{
						reader.Read();
						BuildVersion = reader.GetPString();

						var buildMatch = Regex.Match(BuildVersion, @"(\d+(?:\.\d+)+)-CL-(\d+)", RegexOptions.Singleline);

						if (buildMatch.Success)
						{
							Version = Version.Parse(buildMatch.Groups[1].Value);
							CL = int.Parse(buildMatch.Groups[2].Value);
						}

						break;
					}
					case "LaunchExeString":
					{
						reader.Read();
						LaunchExe = reader.GetPString();
						break;
					}
					case "LaunchCommand":
					{
						reader.Read();
						LaunchCommand = reader.GetPString();
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
							PrereqIds.Add(reader.GetPString());
						}

						break;
					}
					case "PrereqName":
					{
						reader.Read();
						PrereqName = reader.GetPString();
						break;
					}
					case "PrereqPath":
					{
						reader.Read();
						PrereqPath = reader.GetPString();
						break;
					}
					case "PrereqArgs":
					{
						reader.Read();
						PrereqArgs = reader.GetPString();
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
							FileManifests.Add(new FileManifest(ref reader));
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
							var guid = reader.GetPString();
							reader.Read();
							var hash = Utilities.StringBlobToHexString(reader.ValueSpan);
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
							var guid = reader.GetPString();
							reader.Read();
							var sha = reader.GetPString();
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
							var guid = reader.GetPString();
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
							var guid = reader.GetPString();
							reader.Read();
							var size = Utilities.StringBlobTo<long>(reader.ValueSpan);
							ChunkFilesizes.Add(guid, size);
						}

						break;
					}
				}
			}

			sw.Stop();
			ParseTime = sw.Elapsed;
		}
	}
}