using System.Collections.Generic;
using System.Text.Json;

namespace EpicManifestParser.Objects
{
	public readonly struct FileManifest
	{
		public string Name { get; }
		public string Hash { get; }
		public List<FileChunkPart> ChunkParts { get; }
		public List<string> InstallTags { get; }

		internal FileManifest(ref Utf8JsonReader reader)
		{
			Name = Hash = null;
			ChunkParts = null;
			InstallTags = null;

			if (reader.TokenType != JsonTokenType.StartObject)
			{
				return;
			}

			while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
			{
				if (reader.TokenType != JsonTokenType.PropertyName)
				{
					reader.Skip();
					continue;
				}

				switch (reader.GetPString())
				{
					case "Filename":
					{
						reader.Read();
						Name = reader.GetPString();
						break;
					}
					case "FileHash":
					{
						reader.Read();
						Hash = Utilities.StringBlobToHexString(reader.ValueSpan);
						break;
					}
					case "FileChunkParts":
					{
						reader.Read();

						if (reader.TokenType != JsonTokenType.StartArray)
						{
							break;
						}

						ChunkParts = new List<FileChunkPart>();

						while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
						{
							ChunkParts.Add(new FileChunkPart(ref reader));
						}

						break;
					}
					case "InstallTags":
					{
						reader.Read();

						if (reader.TokenType != JsonTokenType.StartArray)
						{
							break;
						}

						InstallTags = new List<string>(1); // wasn't ever bigger than 1 ¯\_(ツ)_/¯

						while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
						{
							InstallTags.Add(reader.GetPString());
						}

						break;
					}
				}
			}
		}

		public override string ToString()
		{
			return Name;
		}
	}
}