using System.Text.Json;

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

		public override string ToString()
		{
			return $"S:{Size}, O:{Offset} | {Guid}";
		}
	}
}