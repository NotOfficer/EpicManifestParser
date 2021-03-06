﻿using System.Collections.Generic;
using System.IO;
using System.Resources;
using System.Text.Json;

namespace EpicManifestParser.Objects
{
	public readonly struct FileManifest
	{
		private readonly Manifest _manifest;
		public string Name { get; }
		public string Hash { get; }
		public List<FileChunkPart> ChunkParts { get; }
		public List<string> InstallTags { get; }

		internal FileManifest(ref Utf8JsonReader reader, Manifest manifest)
		{
			_manifest = manifest;
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

				switch (reader.GetString())
				{
					case "Filename":
					{
						reader.Read();
						Name = reader.GetString();
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
							InstallTags.Add(reader.GetString());
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

		public Stream GetStream()
		{
			if (_manifest.Options.ChunkBaseUri == null)
			{
				throw new MissingManifestResourceException("missing ChunkBaseUri");
			}

			return new FileManifestStream(this, _manifest);
		}
	}
}