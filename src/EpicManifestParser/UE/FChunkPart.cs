using System.Runtime.InteropServices;

namespace EpicManifestParser.UE;

/// <summary>
/// UE FChunkPart struct
/// </summary>
public readonly struct FChunkPart
{
    /*
    /// <summary>
    /// The GUID of the chunk containing this part.
    /// </summary>
    public FGuid Guid { get; }
    */
    /// <summary>
    /// The chunk containing this part.
    /// </summary>
    public FChunkInfo Chunk { get; }
    /// <summary>
    /// The offset of the first byte into the chunk.
    /// </summary>
    public uint32 Offset { get; }
    /// <summary>
    /// The size of this part.
    /// </summary>
    public uint32 Size { get; }

    /// <summary>
    /// The offset of the first byte into the file.
    /// </summary>
    public int64 FileOffset { get; }

    internal FChunkPart(FGuid guid, uint32 offset, uint32 size, int64 fileOffset, Dictionary<FGuid, FChunkInfo> chunks)
    {
        ref FChunkInfo? lookupChunk = ref CollectionsMarshal.GetValueRefOrAddDefault(chunks, guid, out bool exists);
        if (!exists)
        {
            lookupChunk = new FChunkInfo
            {
                Guid = guid
            };
        }

        Chunk = lookupChunk!;
        Offset = offset;
        Size = size;
        FileOffset = fileOffset;
    }

    /// <summary>
    /// The size of a serialized chunk part: data size, GUID, offset and size.
    /// </summary>
    internal const int32 SerializedSize = sizeof(int32) + FGuid.Size + sizeof(uint32) + sizeof(uint32);

    /// <param name="reader">The reader at the chunk part.</param>
    /// <param name="fileOffset">The offset of the part in its file.</param>
    /// <param name="chunks">The chunks of the manifest.</param>
    /// <param name="limit">The end of the enclosing file manifest list.</param>
    /// <exception cref="InvalidDataException">The part does not fit or its chunk is not in <paramref name="chunks"/>.</exception>
    internal FChunkPart(ref ManifestReader reader, int64 fileOffset, IReadOnlyDictionary<FGuid, FChunkInfo> chunks, int32 limit)
    {
        // the data size is at least SerializedSize, so the reads below stay inside the part
        int32 end = reader.BeginSection(ManifestSection.ChunkPart, SerializedSize, limit);

        FGuid guid = reader.Read<FGuid>();
        if (!chunks.TryGetValue(guid, out FChunkInfo? chunk))
            ManifestSection.ThrowUnknownChunk(guid);

        Chunk = chunk;
        Offset = reader.Read<uint32>();
        Size = reader.Read<uint32>();
        FileOffset = fileOffset;

        reader.Position = end;
    }
}
