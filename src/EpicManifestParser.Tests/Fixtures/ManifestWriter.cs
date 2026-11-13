using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

using EpicManifestParser.UE;

namespace EpicManifestParser.Tests.Fixtures;

/// <summary>
/// Writes binary manifest data the way UE serializes it, so that tests can build small valid manifests and corrupt single values.
/// </summary>
internal sealed class ManifestWriter
{
    private readonly List<byte> _data = [];

    public int Position => _data.Count;

    public ManifestWriter UInt8(byte value)
    {
        _data.Add(value);
        return this;
    }

    public ManifestWriter Int32(int value) => Bytes(BitConverter.GetBytes(value));
    public ManifestWriter UInt32(uint value) => Bytes(BitConverter.GetBytes(value));
    public ManifestWriter UInt64(ulong value) => Bytes(BitConverter.GetBytes(value));
    public ManifestWriter Int64(long value) => Bytes(BitConverter.GetBytes(value));
    public ManifestWriter Guid(FGuid value) => Bytes(value.AsSpan());
    public ManifestWriter Zeros(int count) => Bytes(new byte[count]);

    public ManifestWriter Bytes(ReadOnlySpan<byte> bytes)
    {
        _data.AddRange(bytes);
        return this;
    }

    /// <summary>An ANSI FString: length including the terminator, then the characters and the terminator.</summary>
    public ManifestWriter FString(string value)
    {
        if (value.Length == 0)
            return Int32(0);
        byte[] bytes = Encoding.ASCII.GetBytes(value);
        return Int32(bytes.Length + 1).Bytes(bytes).UInt8(0);
    }

    /// <summary>A section: its data size (the real one unless <paramref name="dataSize"/> overrides it), then <paramref name="body"/>.</summary>
    public ManifestWriter Section(Action<ManifestWriter> body, int? dataSize = null)
    {
        int start = _data.Count;
        Int32(0);
        body(this);
        BinaryPrimitives.WriteInt32LittleEndian(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_data)[start..], dataSize ?? _data.Count - start);
        return this;
    }

    public byte[] ToArray() => _data.ToArray();

    // ---- the sections of a minimal manifest ----

    /// <summary>FManifestMeta, version SerialisesBuildId.</summary>
    public ManifestWriter Meta(string appName = "App", string buildVersion = "1.0-CL-1", string? buildId = "BuildId") => Section(w =>
    {
        w.UInt8(buildId is null ? (byte)0 : (byte)1)
            .Int32((int)EFeatureLevel.StoresUninstallActions)
            .UInt8(0) // bIsFileData
            .UInt32(1) // AppID
            .FString(appName).FString(buildVersion).FString("App.exe").FString("")
            .Int32(0) // PrereqIds
            .FString("").FString("").FString("");
        if (buildId is not null)
            w.FString(buildId);
    });

    /// <summary>FChunkDataList, version Original: each chunk has a 1 MiB window and the given file size.</summary>
    public ManifestWriter ChunkList(ReadOnlySpan<FGuid> guids, int? count = null)
    {
        FGuid[] array = guids.ToArray();
        return Section(w =>
        {
            w.UInt8(0).Int32(count ?? array.Length);
            foreach (FGuid guid in array) w.Guid(guid);
            foreach (FGuid _ in array) w.UInt64(0x1234);
            foreach (FGuid _ in array) w.Zeros(FSHAHash.Size);
            foreach (FGuid _ in array) w.UInt8(7);
            foreach (FGuid _ in array) w.UInt32(1024 * 1024);
            foreach (FGuid _ in array) w.Int64(1000);
        });
    }

    /// <summary>
    /// FFileManifestList, version Original, one file with one part of 100 bytes per chunk GUID. The counts of install tags
    /// and parts can be overridden without writing the elements.
    /// </summary>
    public ManifestWriter FileList(string fileName, ReadOnlySpan<FGuid> partChunks, int? installTagCount = null, int? partCount = null, int partDataSize = 28)
    {
        FGuid[] array = partChunks.ToArray();
        return Section(w =>
        {
            w.UInt8(0).Int32(1)
                .FString(fileName)
                .FString("") // SymlinkTarget
                .Zeros(FSHAHash.Size)
                .UInt8(0) // FileMetaFlags
                .Int32(installTagCount ?? 0) // InstallTags
                .Int32(partCount ?? array.Length);
            foreach (FGuid guid in array)
            {
                w.Int32(partDataSize).Guid(guid).UInt32(0).UInt32(100);
                if (partDataSize > 28)
                    w.Zeros(partDataSize - 28);
            }
        });
    }

    /// <summary>FCustomFields, version Original.</summary>
    public ManifestWriter CustomFields(params (string Name, string Value)[] fields) => Section(w =>
    {
        w.UInt8(0).Int32(fields.Length);
        foreach ((string name, _) in fields) w.FString(name);
        foreach ((_, string value) in fields) w.FString(value);
    });

    public static readonly FGuid ChunkA = new(0xAAAAAAAA, 1, 2, 3);
    public static readonly FGuid ChunkB = new(0xBBBBBBBB, 1, 2, 3);

    /// <summary>The data of a valid manifest with one file over two chunks.</summary>
    public static ManifestWriter Valid() => new ManifestWriter()
        .Meta()
        .ChunkList([ChunkA, ChunkB])
        .FileList("Engine/File.bin", [ChunkA, ChunkB])
        .CustomFields(("Key", "Value"));

    /// <summary>
    /// Wraps manifest data in an uncompressed binary manifest with a header of <paramref name="featureLevel"/>; the sizes and
    /// the SHA1 are those of <paramref name="data"/> unless overridden.
    /// </summary>
    public static byte[] Wrap(ReadOnlySpan<byte> data, EFeatureLevel featureLevel = EFeatureLevel.StoresUninstallActions, byte storedAs = 0,
        int? dataSizeCompressed = null, int? dataSizeUncompressed = null, FGuid encryptionSecretId = default)
    {
        int headerSize = featureLevel >= EFeatureLevel.ChunkEncryptionSupport ? 73 : 41;
        byte[] file = new byte[headerSize + data.Length];
        Span<byte> header = file;
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x44BEC00C);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], headerSize);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], dataSizeUncompressed ?? data.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], dataSizeCompressed ?? data.Length);
        SHA1.HashData(data, header[16..36]);
        header[36] = storedAs;
        BinaryPrimitives.WriteInt32LittleEndian(header[37..], (int)featureLevel);
        if (headerSize == 73)
            encryptionSecretId.AsSpan().CopyTo(header[41..]);
        data.CopyTo(file.AsSpan(headerSize));
        return file;
    }
}
