using System;
using System.Text.Json;
using GenericReader;

namespace EpicManifestParser.Objects
{
	public readonly struct FileChunkPart
	{
		public string Guid { get; }
		public int Size { get; }
		public int Offset { get; }

		internal FileChunkPart(ref Utf8JsonReader reader)
		{
			Guid = null;
			Size = Offset = 0;

			if (reader.TokenType != JsonTokenType.StartObject)
			{
				return;
			}

			while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
			{
				if (reader.TokenType != JsonTokenType.PropertyName)
				{
					continue;
				}

				switch (reader.GetString())
				{
					case "Guid":
					{
						reader.Read();
						Guid = reader.GetString();
						break;
					}
					case "Size":
					{
						reader.Read();
						Size = Utilities.StringBlobTo<int>(reader.ValueSpan);
						break;
					}
					case "Offset":
					{
						reader.Read();
						Offset = Utilities.StringBlobTo<int>(reader.ValueSpan);
						break;
					}
				}
			}
		}

		internal FileChunkPart(ref GenericBufferReader reader)
		{
			reader.Position += 4;
			var hex = reader.ReadBytes(16);
			Array.Reverse(hex, 0, 4);
			Array.Reverse(hex, 4, 4);
			Array.Reverse(hex, 8, 4);
			Array.Reverse(hex, 12, 4);
			Guid = BitConverter.ToString(hex).Replace("-", "");
			Offset = reader.Read<int>();
			Size = reader.Read<int>();
		}

		public override string ToString()
		{
			return $"S:{Size}, O:{Offset} | {Guid}";
		}
	}
}