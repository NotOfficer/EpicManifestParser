using System.Runtime.CompilerServices;

namespace EpicManifestParser.UE;

// https://github.com/NotOfficer/UnrealEngine/blob/1436d646b9b11ffe9a46b04e9617dba689b56d35/Engine/Source/Runtime/Online/BuildPatchServices/Private/Common/Crypto.h?plain=1#L9C1-L21C4
[InlineArray(AES256_GCM_AuthTagSizeInBytes)]
internal struct FAESAuthTag
{
    private byte _element;

    public const int AES256_GCM_InitializationVectorSizeInBytes = 12;
    public const int AES256_GCM_AuthTagSizeInBytes = 16;
    public const int AES256_GCM_KeySizeInBytes = 32;
}

// https://github.com/NotOfficer/UnrealEngine/blob/1436d646b9b11ffe9a46b04e9617dba689b56d35/Engine/Source/Runtime/Online/BuildPatchServices/Private/Data/ManifestData.h?plain=1#L63C16-L63C16
internal sealed class FManifestHeader
{
    // https://github.com/NotOfficer/UnrealEngine/blob/1436d646b9b11ffe9a46b04e9617dba689b56d35/Engine/Source/Runtime/Online/BuildPatchServices/Private/Data/ManifestData.cpp?plain=1#L22C25-L22C25
    public const uint32 Magic = 0x44BEC00C;

    /// <summary>
    /// The version of this header and manifest data format, driven by the feature level.
    /// </summary>
    public readonly EFeatureLevel Version;
    /// <summary>
    /// The size of this header.
    /// </summary>
    public readonly int32 HeaderSize;
    /// <summary>
    /// The size of this data compressed.
    /// </summary>
    public readonly int32 DataSizeCompressed;
    /// <summary>
    /// The size of this data uncompressed.
    /// </summary>
    public readonly int32 DataSizeUncompressed;
    /// <summary>
    /// How the chunk data is stored.
    /// </summary>
    public readonly EManifestStorageFlags StoredAs;
    /// <summary>
    /// The SHA1 hash for the manifest data that follows.
    /// </summary>
    public readonly FSHAHash SHAHash;
    /// <summary>
    /// An ID that identifies the encryption secret required to decrypt certain manifest fields.
    /// </summary>
    public readonly FGuid? EncryptionSecretId;
    /// <summary>
    /// Without having the associated key, only chunk use, file hashes, and sizes can be understood.
    /// </summary>
    public readonly FAESAuthTag? EncryptionAuthTag;

    /// <summary>
    /// The deflate format cannot expand a byte to more than 1032 bytes (a 258 byte match in 2 bits),
    /// so a larger uncompressed size cannot be real.
    /// </summary>
    private const int32 MaxZlibExpansion = 1032;

    // https://github.com/NotOfficer/UnrealEngine/blob/1436d646b9b11ffe9a46b04e9617dba689b56d35/Engine/Source/Runtime/Online/BuildPatchServices/Private/Data/ManifestData.cpp?plain=1#L401C61-L401C61
    /// <param name="reader">A reader at the start of the input.</param>
    /// <exception cref="InvalidDataException">The input is shorter than the header or the header size is invalid.</exception>
    /// <exception cref="FileLoadException">The magic does not match.</exception>
    internal FManifestHeader(ref ManifestReader reader)
    {
        int32 originalSize = (int32)ManifestHeaderVersionSizes[(int32)EFeatureLevel.Original];
        if (reader.Length - reader.Position < originalSize)
            throw new InvalidDataException($"Invalid manifest header: the input has {reader.Length - reader.Position} bytes, the header alone needs {originalSize}");

        uint32 magic = reader.Read<uint32>();
        if (magic != Magic)
            throw new FileLoadException($"Invalid manifest header magic: 0x{magic:X}");

        HeaderSize = reader.Read<int32>();
        if (HeaderSize < originalSize || HeaderSize > reader.Length)
            throw new InvalidDataException($"Invalid manifest header: a header size of {HeaderSize} bytes is less than {originalSize} or more than the {reader.Length} byte input");

        DataSizeUncompressed = reader.Read<int32>();
        DataSizeCompressed = reader.Read<int32>();
        SHAHash = reader.Read<FSHAHash>();
        StoredAs = reader.Read<EManifestStorageFlags>();

        if (HeaderSize > originalSize)
        {
            RequireHeaderSize(EFeatureLevel.StoredAsBinaryData);
            Version = reader.Read<EFeatureLevel>();
        }
        else
        {
            Version = EFeatureLevel.StoredAsCompressedUClass;
        }

        if (Version >= EFeatureLevel.ChunkEncryptionSupport)
        {
            RequireHeaderSize(EFeatureLevel.ChunkEncryptionSupport);
            EncryptionSecretId = reader.Read<FGuid>();
            EncryptionAuthTag = reader.Read<FAESAuthTag>();
        }

        reader.SetPosition(HeaderSize);
    }

    private void RequireHeaderSize(EFeatureLevel featureLevel)
    {
        uint32 required = ManifestHeaderVersionSizes[(int32)featureLevel];
        if ((uint32)HeaderSize < required)
            throw new InvalidDataException($"Invalid manifest header: a header size of {HeaderSize} bytes is less than the {required} bytes of a {featureLevel} header");
    }

    /// <summary>
    /// Checks the data sizes against the input, before anything is allocated for the data.
    /// </summary>
    /// <param name="dataAvailable">The number of input bytes behind the header.</param>
    /// <param name="maxBufferSize">The largest supported buffer for the (uncompressed) data.</param>
    /// <exception cref="InvalidDataException">
    /// The compressed size is negative or larger than the input, or the uncompressed size is negative, larger than
    /// <paramref name="maxBufferSize"/> or more than zlib can inflate from the compressed size.
    /// </exception>
    internal void CheckDataSizes(int32 dataAvailable, int32 maxBufferSize)
    {
        if (DataSizeCompressed < 0 || DataSizeCompressed > dataAvailable)
            throw new InvalidDataException($"Invalid manifest header: a data size of {DataSizeCompressed} bytes is negative or more than the {dataAvailable} bytes behind the header");
        if (DataSizeUncompressed < 0)
            throw new InvalidDataException($"Invalid manifest header: the uncompressed data size of {DataSizeUncompressed} bytes is negative");

        if (!StoredAs.HasFlag(EManifestStorageFlags.Compressed))
        {
            if (DataSizeCompressed > maxBufferSize)
                throw new InvalidDataException($"Invalid manifest header: a data size of {DataSizeCompressed} bytes is more than the supported {maxBufferSize} bytes");
            return;
        }

        if (DataSizeUncompressed > (int64)DataSizeCompressed * MaxZlibExpansion)
            throw new InvalidDataException($"Invalid manifest header: {DataSizeUncompressed} uncompressed bytes cannot be inflated from {DataSizeCompressed} compressed bytes");
        if (DataSizeUncompressed > maxBufferSize)
            throw new InvalidDataException($"Invalid manifest header: an uncompressed data size of {DataSizeUncompressed} bytes is more than the supported {maxBufferSize} bytes");
    }

    // https://github.com/NotOfficer/UnrealEngine/blob/1436d646b9b11ffe9a46b04e9617dba689b56d35/Engine/Source/Runtime/Online/BuildPatchServices/Private/Data/ManifestData.cpp?plain=1#L27C1-L40C4
    // The constant minimum sizes for each version of a header struct. Must be updated.
    // If new member variables are added the version MUST be bumped and handled properly here,
    // and these values must never change.
    private static ReadOnlySpan<uint32> ManifestHeaderVersionSizes =>
    [
        // EFeatureLevel::Original is 37B (32b Magic, 32b HeaderSize, 32b DataSizeUncompressed, 32b DataSizeCompressed, 160b SHA1, 8b StoredAs)
        // This remained the same all up to including EFeatureLevel::StoresPrerequisiteIds.
        37, 37, 37, 37, 37, 37, 37, 37, 37, 37, 37, 37, 37, 37,
        // EFeatureLevel::StoredAsBinaryData is 41B, (296b Original, 32b Version).
        // This remained the same all up to including EFeatureLevel::StoresUninstallActions.
        41, 41, 41, 41, 41, 41, 41, 41,
        // EFeatureLevel::ChunkEncryptionSupport added a 128b SecretId GUID, plus 128b AuthTag
        73, 73, 73
    ];
}
