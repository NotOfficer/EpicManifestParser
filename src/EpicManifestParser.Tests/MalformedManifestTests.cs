using System.Buffers.Binary;
using System.IO.Compression;

using EpicManifestParser.Tests.Fixtures;
using EpicManifestParser.UE;

namespace EpicManifestParser.Tests;

/// <summary>
/// Crafted binary manifests with a valid SHA1: each fails with an <see cref="InvalidDataException"/> that names the header or
/// section, before anything large is allocated. Only a negative length prefix is reported by the reader with its own message.
/// </summary>
public class MalformedManifestTests
{
    private const long MaxAllocation = 1024 * 1024;

    private static InvalidDataException AssertInvalid(byte[] manifest, string expectedInMessage)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        var ex = Assert.Throws<InvalidDataException>(() => FBuildPatchAppManifest.Deserialize(manifest));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Contains(expectedInMessage, ex.Message);
        Assert.True(allocated < MaxAllocation, $"allocated {allocated} bytes: {ex.Message}");
        return ex;
    }

    private static byte[] Wrap(ManifestWriter data) => ManifestWriter.Wrap(data.ToArray());

    [Fact]
    public void Valid_crafted_manifest_parses()
    {
        FBuildPatchAppManifest manifest = FBuildPatchAppManifest.Deserialize(Wrap(ManifestWriter.Valid()));

        Assert.Equal("BuildId", manifest.Meta.BuildId);
        Assert.Equal(2, manifest.ChunkList.Count);
        FFileManifest file = Assert.Single(manifest.Files);
        Assert.Equal("Engine/File.bin", file.FileName);
        Assert.Equal(200, file.FileSize);
        Assert.Equal([0L, 100L], file.ChunkParts.Select(p => p.FileOffset));
        Assert.Equal([ManifestWriter.ChunkA, ManifestWriter.ChunkB], file.ChunkParts.Select(p => p.Chunk.Guid));
        FCustomField field = Assert.Single(manifest.CustomFields);
        Assert.Equal(("Key", "Value"), (field.Name, field.Value));
    }

    [Fact]
    public void Valid_crafted_manifest_parses_compressed()
    {
        byte[] data = ManifestWriter.Valid().ToArray();
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, true))
            zlib.Write(data);

        // the header hash is the one of the uncompressed data
        byte[] manifest = ManifestWriter.Wrap(data, storedAs: 1, dataSizeUncompressed: data.Length);
        byte[] stored = compressed.ToArray();
        byte[] file = new byte[41 + stored.Length];
        manifest.AsSpan(0, 41).CopyTo(file);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(12), stored.Length);
        stored.CopyTo(file, 41);

        Assert.Equal("Engine/File.bin", Assert.Single(FBuildPatchAppManifest.Deserialize(file).Files).FileName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(36)]
    public void Input_shorter_than_the_header(int length)
    {
        byte[] input = Wrap(ManifestWriter.Valid()).AsSpan(0, length).ToArray();
        AssertInvalid(input, "header");
    }

    [Fact]
    public void Header_claiming_64_MiB_without_data()
    {
        // 41 bytes, compressed, 64 MiB uncompressed from 0 bytes
        AssertInvalid(ManifestWriter.Wrap([], storedAs: 1, dataSizeUncompressed: 64 * 1024 * 1024), "header");
        // stored raw, 64 MiB that are not there
        AssertInvalid(ManifestWriter.Wrap([], dataSizeCompressed: 64 * 1024 * 1024), "header");
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(10, -1)]
    [InlineData(11, 10)]
    [InlineData(int.MaxValue, 10)]
    public void Header_data_sizes_out_of_range(int dataSizeCompressed, int dataSizeUncompressed)
    {
        AssertInvalid(ManifestWriter.Wrap(new byte[10], storedAs: 1, dataSizeCompressed: dataSizeCompressed, dataSizeUncompressed: dataSizeUncompressed), "header");
    }

    [Fact]
    public void Uncompressed_size_zlib_cannot_produce()
    {
        // deflate expands a byte to at most 1032 bytes
        AssertInvalid(ManifestWriter.Wrap(new byte[10], storedAs: 1, dataSizeUncompressed: 10 * 1032 + 1), "cannot be inflated");
    }

    [Theory]
    [InlineData(20)]
    [InlineData(39)]
    [InlineData(100_000)]
    public void Header_size_out_of_range(int headerSize)
    {
        byte[] manifest = Wrap(ManifestWriter.Valid());
        BinaryPrimitives.WriteInt32LittleEndian(manifest.AsSpan(4), headerSize);
        AssertInvalid(manifest, "header size");
    }

    [Fact]
    public void Encryption_header_without_room_for_the_secret()
    {
        // a ChunkEncryptionSupport header is 73 bytes
        byte[] manifest = Wrap(ManifestWriter.Valid());
        BinaryPrimitives.WriteInt32LittleEndian(manifest.AsSpan(37), (int)EFeatureLevel.ChunkEncryptionSupport);
        AssertInvalid(manifest, "ChunkEncryptionSupport header");
    }

    [Fact]
    public void Unknown_storage_flags()
    {
        AssertInvalid(ManifestWriter.Wrap(ManifestWriter.Valid().ToArray(), storedAs: 0x80), "storage flags");
    }

    [Theory]
    [InlineData(2)]
    [InlineData(-1)]
    [InlineData(10_000_000)]
    public void Section_size_out_of_range(int metaDataSize)
    {
        var data = new ManifestWriter().Section(w => w.UInt8(0).Int32((int)EFeatureLevel.StoresUninstallActions), metaDataSize);
        AssertInvalid(Wrap(data), "meta section");
    }

    [Fact]
    public void Missing_section()
    {
        AssertInvalid(Wrap(new ManifestWriter().Meta()), "chunk data list section: it is missing");
    }

    [Fact]
    public void Chunk_count_of_20_million_in_a_tiny_manifest()
    {
        var data = new ManifestWriter().Meta().Section(w => w.UInt8(0).Int32(20_000_000));
        byte[] manifest = Wrap(data);
        Assert.True(manifest.Length < 200);
        AssertInvalid(manifest, "chunk data list section: an element count of 20000000");
    }

    [Fact]
    public void Negative_chunk_count()
    {
        AssertInvalid(Wrap(new ManifestWriter().Meta().ChunkList([ManifestWriter.ChunkA], count: -1)), "chunk data list section");
    }

    [Fact]
    public void Chunk_count_one_more_than_the_section_holds()
    {
        AssertInvalid(Wrap(new ManifestWriter().Meta().ChunkList([ManifestWriter.ChunkA], count: 2)), "chunk data list section");
    }

    [Fact]
    public void Duplicate_chunk_guid()
    {
        var data = new ManifestWriter().Meta().ChunkList([ManifestWriter.ChunkA, ManifestWriter.ChunkA])
            .FileList("a", [ManifestWriter.ChunkA]).CustomFields();
        AssertInvalid(Wrap(data), $"chunk {ManifestWriter.ChunkA} is listed twice");
    }

    [Fact]
    public void Chunk_part_of_a_chunk_that_is_not_in_the_chunk_list()
    {
        var data = new ManifestWriter().Meta().ChunkList([ManifestWriter.ChunkA])
            .FileList("a", [ManifestWriter.ChunkA, ManifestWriter.ChunkB]).CustomFields();
        AssertInvalid(Wrap(data), $"chunk part section: chunk {ManifestWriter.ChunkB} is not in the chunk data list");
    }

    [Fact]
    public void File_count_too_large()
    {
        var data = new ManifestWriter().Meta().ChunkList([]).Section(w => w.UInt8(0).Int32(int.MaxValue).Zeros(40));
        AssertInvalid(Wrap(data), "file manifest list section: an element count of 2147483647");
    }

    [Fact]
    public void Install_tag_count_too_large()
    {
        // 100 million tags would be an 800 MB array, the reader checks the count against the data before it allocates
        var data = new ManifestWriter().Meta().ChunkList([ManifestWriter.ChunkA])
            .FileList("a", [ManifestWriter.ChunkA], installTagCount: 100_000_000);
        var ex = AssertInvalid(Wrap(data), "file manifest list section: a value lies outside the manifest data");
        Assert.IsType<EndOfStreamException>(ex.InnerException);
    }

    [Fact]
    public void Negative_install_tag_count()
    {
        // a negative length prefix is rejected by the reader itself, its message does not name the section
        var data = new ManifestWriter().Meta().ChunkList([ManifestWriter.ChunkA])
            .FileList("a", [ManifestWriter.ChunkA], installTagCount: -1);
        AssertInvalid(Wrap(data), "negative length (-1)");
    }

    [Fact]
    public void Chunk_part_count_too_large()
    {
        var data = new ManifestWriter().Meta().ChunkList([ManifestWriter.ChunkA])
            .FileList("a", [ManifestWriter.ChunkA], partCount: 50_000_000);
        AssertInvalid(Wrap(data), "file manifest list section: an element count of 50000000");
    }

    [Theory]
    [InlineData(10)]
    [InlineData(-5)]
    [InlineData(100_000)]
    public void Chunk_part_size_out_of_range(int partDataSize)
    {
        var data = new ManifestWriter().Meta().ChunkList([ManifestWriter.ChunkA]).Section(w => w
            .UInt8(0).Int32(1).FString("a").FString("").Zeros(20).UInt8(0).Int32(0)
            .Int32(1).Int32(partDataSize).Guid(ManifestWriter.ChunkA).UInt32(0).UInt32(100));
        AssertInvalid(Wrap(data.CustomFields()), "chunk part section");
    }

    [Fact]
    public void Chunk_part_larger_than_28_bytes_is_skipped_to_its_end()
    {
        // newer versions may append fields to a part
        var data = new ManifestWriter().Meta().ChunkList([ManifestWriter.ChunkA])
            .FileList("a", [ManifestWriter.ChunkA], partDataSize: 40).CustomFields();
        FFileManifest file = Assert.Single(FBuildPatchAppManifest.Deserialize(Wrap(data)).Files);
        Assert.Equal(100, file.FileSize);
    }

    [Fact]
    public void Custom_field_count_too_large()
    {
        var data = ManifestWriter.Valid().ToArray();
        // the custom fields are the last section: data size, version, count
        var customFields = new ManifestWriter().CustomFields(("Key", "Value")).ToArray();
        int countOffset = data.Length - customFields.Length + 5;
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(countOffset), 30_000_000);
        AssertInvalid(ManifestWriter.Wrap(data), "custom fields section: an element count of 30000000");
    }

    [Fact]
    public void Encrypted_data_length_too_large()
    {
        var data = ManifestWriter.Valid().Section(w => w.UInt8(0).Int32(1 << 30).Zeros(16));
        byte[] manifest = ManifestWriter.Wrap(data.ToArray(), EFeatureLevel.ManifestEncryptionSupport, encryptionSecretId: new FGuid(1, 2, 3, 4));
        // the reader checks the length against the data before it allocates the 1 GiB array
        var ex = AssertInvalid(manifest, "encrypted data section: a value lies outside the manifest data");
        Assert.IsType<EndOfStreamException>(ex.InnerException);
    }

    [Fact]
    public void Encrypted_data_is_read()
    {
        var data = ManifestWriter.Valid().Section(w => w.UInt8(0).Int32(3).UInt8(1).UInt8(2).UInt8(3));
        byte[] manifest = ManifestWriter.Wrap(data.ToArray(), EFeatureLevel.ManifestEncryptionSupport, encryptionSecretId: new FGuid(1, 2, 3, 4));
        Assert.Equal(new byte[] { 1, 2, 3 }, FBuildPatchAppManifest.Deserialize(manifest).EncryptedData!.Data);
    }

    [Fact]
    public void String_longer_than_the_data()
    {
        // AppName claims 1000 bytes, the data ends after its length
        var data = new ManifestWriter().Section(w => w.UInt8(0).Int32((int)EFeatureLevel.StoresUninstallActions).UInt8(0).UInt32(1).Int32(1000));
        var ex = AssertInvalid(Wrap(data), "meta section: a value lies outside");
        Assert.IsType<EndOfStreamException>(ex.InnerException);
    }

    [Fact]
    public void Value_that_starts_behind_the_data()
    {
        // only the version: the feature level would be read behind the data
        var ex = AssertInvalid(Wrap(new ManifestWriter().Section(w => w.UInt8(0))), "meta section: a value lies outside");
        Assert.IsType<EndOfStreamException>(ex.InnerException);
    }

    [Fact]
    public void Reads_past_the_section_end()
    {
        // a meta section that claims only its header, followed by the real fields
        byte[] data = new ManifestWriter().Meta().ChunkList([]).FileList("a", []).CustomFields().ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(data, 5);
        AssertInvalid(ManifestWriter.Wrap(data), "meta section: its data runs");
    }
}
