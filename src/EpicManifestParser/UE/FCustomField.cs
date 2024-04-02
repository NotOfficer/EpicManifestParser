using GenericReader;

namespace EpicManifestParser.UE;

public class FCustomField
{
	public string Name { get; internal set; } = "";
	public string Value { get; internal set; } = "";

	internal FCustomField() { }

	internal static FCustomField[] ReadCustomFields(GenericBufferReader reader)
	{
		var startPos = reader.Position;
		var dataSize = reader.Read<int32>();
		var dataVersion = reader.Read<EChunkDataListVersion>();
		var elementCount = reader.Read<int32>();

		var fields = new FCustomField[elementCount];
		var fieldsSpan = fields.AsSpan();

		if (dataVersion >= EChunkDataListVersion.Original)
		{
			for (var i = 0; i < elementCount; i++)
			{
				var field = new FCustomField();
				field.Name = reader.ReadFString();
				fieldsSpan[i] = field;
			}
			for (var i = 0; i < elementCount; i++)
				fieldsSpan[i].Value = reader.ReadFString();
		}
		else
		{
			var defaultField = new FCustomField();
			fieldsSpan.Fill(defaultField);
		}

		reader.Position = startPos + dataSize;
		return fields;
	}
}
