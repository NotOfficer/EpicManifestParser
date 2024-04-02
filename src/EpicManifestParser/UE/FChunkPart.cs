using GenericReader;

namespace EpicManifestParser.UE;

public readonly struct FChunkPart
{
	// The GUID of the chunk containing this part.
	public FGuid Guid { get; }
	// The offset of the first byte into the chunk.
	public uint32 Offset { get; }
	// The size of this part.
	public uint32 Size { get; }

	internal FChunkPart(FGuid guid, uint32 offset, uint32 size)
	{
		Guid = guid;
		Offset = offset;
		Size = size;
	}

	internal FChunkPart(IGenericReader reader)
	{
		var startPos = reader.Position;
		var dataSize = reader.Read<int32>();

		Guid = reader.Read<FGuid>();
		Offset = reader.Read<uint32>();
		Size = reader.Read<uint32>();

		reader.Position = startPos + dataSize;
	}
}
