using System.Runtime.CompilerServices;

namespace EpicManifestParser.UE;

// Port of operator<<(FArchive&, FChunkHeader&), ChunkData.cpp:
// https://github.com/EpicGames/UnrealEngine/blob/release/Engine/Source/Runtime/Online/BuildPatchServices/Private/Data/ChunkData.cpp
internal readonly struct FChunkHeader
{
    internal const int32 DefaultDataSizeUncompressed = 1024 * 1024;
    public const uint32 Magic = 0xB1FE3AA2;

    /// <summary>
    /// The version of this header data.
    /// </summary>
    public readonly EChunkVersion Version;
    /// <summary>
    /// The size of this header.
    /// </summary>
    public readonly int32 HeaderSize;
    /// <summary>
    /// The GUID for this data.
    /// </summary>
    public readonly FGuid Guid;
    /// <summary>
    /// The size of this data compressed.
    /// </summary>
    public readonly int32 DataSizeCompressed;
    /// <summary>
    /// The size of this data uncompressed, 1 MiB before <see cref="EChunkVersion.StoresDataSizeUncompressed"/>.
    /// </summary>
    public readonly int32 DataSizeUncompressed;
    /// <summary>
    /// How the chunk data is stored.
    /// </summary>
    public readonly EChunkStorageFlags StoredAs;
    /// <summary>
    /// What type of hash we are using, only <see cref="EChunkHashFlags.RollingPoly64"/> before <see cref="EChunkVersion.StoresShaAndHashType"/>.
    /// </summary>
    public readonly EChunkHashFlags HashType;
    /// <summary>
    /// The FRollingHash hashed value for this chunk data.
    /// </summary>
    public readonly uint64 RollingHash;
    /// <summary>
    /// The FSHA hashed value for this chunk data, valid if <see cref="HashType"/> has <see cref="EChunkHashFlags.Sha1"/>.
    /// </summary>
    public readonly FSHAHash ShaHash;

    /// <inheritdoc cref="FChunkHeader(ref ManifestReader)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static FChunkHeader Parse(ManifestData data)
    {
        var reader = new ManifestReader(data);
        return new FChunkHeader(ref reader);
    }

    /// <summary>
    /// Reads the header at the position of <paramref name="reader"/> and moves it to the data behind the header.
    /// </summary>
    /// <remarks>
    /// Every field of the declared version must lie inside <see cref="HeaderSize"/> and inside the data: 41, 62, 66 and 98 bytes
    /// for the versions 1 to 4, and at least the 98 bytes of <see cref="EChunkVersion.StoresEncryptionData"/> for later ones.
    /// The encryption fields of version 4 are not read (encrypted chunks are not supported), they and the fields of later
    /// versions are skipped up to the header size.
    /// </remarks>
    /// <exception cref="FileLoadException">The magic is invalid.</exception>
    /// <exception cref="InvalidDataException">The data is too short for the header of its version, the version is invalid, or the header size is.</exception>
    internal FChunkHeader(ref ManifestReader reader)
    {
        int32 startPos = reader.Position;
        int32 archiveSizeLeft = reader.Length - startPos;

        if (archiveSizeLeft < ChunkHeaderVersionSizes[(int32)EChunkVersion.Original])
            throw new InvalidDataException($"A chunk of {archiveSizeLeft} bytes is shorter than the smallest chunk header");

        uint32 magic = reader.Read<uint32>();
        if (magic != Magic)
            throw new FileLoadException($"invalid chunk magic: 0x{magic:X}");

        Version = reader.Read<EChunkVersion>();
        HeaderSize = reader.Read<int32>();
        DataSizeCompressed = reader.Read<int32>();
        Guid = reader.Read<FGuid>();
        RollingHash = reader.Read<uint64>();
        StoredAs = reader.Read<EChunkStorageFlags>();
        HashType = EChunkHashFlags.RollingPoly64;
        DataSizeUncompressed = DefaultDataSizeUncompressed;

        if (Version == EChunkVersion.Invalid)
            throw new InvalidDataException("Invalid chunk header version 0");

        // versions after the latest known one append fields, all of them begin with the ones of the latest
        int32 versionSize = (int32)ChunkHeaderVersionSizes[(int32)Math.Min((uint32)Version, (uint32)EChunkVersion.Latest)];
        if (HeaderSize < versionSize || HeaderSize > archiveSizeLeft)
            throw new InvalidDataException($"Chunk header of version {(uint32)Version} needs {versionSize} bytes, it claims {HeaderSize} in a {archiveSizeLeft} byte chunk");

        if (Version >= EChunkVersion.StoresShaAndHashType)
        {
            ShaHash = reader.Read<FSHAHash>();
            HashType = reader.Read<EChunkHashFlags>();

            if (Version >= EChunkVersion.StoresDataSizeUncompressed)
                DataSizeUncompressed = reader.Read<int32>();
        }

        reader.Position = startPos + HeaderSize;
    }

    private static ReadOnlySpan<uint32> ChunkHeaderVersionSizes =>
    [
        // Dummy for indexing.
        0,
        // Original is 41 bytes (32b Magic, 32b Version, 32b HeaderSize, 32b DataSizeCompressed, 4x32b GUID, 64b Hash, 8b StoredAs).
        41,
        // StoresShaAndHashType is 62 bytes (328b Original, 160b SHA1, 8b HashType).
        62,
        // StoresDataSizeUncompressed is 66 bytes (496b StoresShaAndHashType, 32b DataSizeUncompressed).
        66,
        // StoresEncryptionData is 98 bytes (528b StoresDataSizeUncompressed, 4x32b GUID, 128b AuthTag).
        98
    ];
}
