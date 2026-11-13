using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;

using EpicManifestParser.UE;

using OffiUtils;

namespace EpicManifestParser.Tests;

public class UncompressTests
{
    // chunk header version 3 (StoresDataSizeUncompressed): the SHA1 of the decompressed data at 41, the hash type at 61,
    // the uncompressed size at 62
    private const int ShaOffset = 41;
    private const int HashTypeOffset = 61;
    private const int DataSizeUncompressedOffset = 62;
    private const byte HashTypeSha1 = 2;

    [Fact]
    public async Task Uncompress_Chunk_Default()
    {
        byte[] chunkBuffer = await File.ReadAllBytesAsync("files/chunk_compressed.bin", TestContext.Current.CancellationToken);
        Assert.True((chunkBuffer[HashTypeOffset] & HashTypeSha1) != 0, "the fixture must carry an SHA1");
        int dataSize = BinaryPrimitives.ReadInt32LittleEndian(chunkBuffer.AsSpan(DataSizeUncompressedOffset));
        Assert.Equal(1024 * 1024, dataSize);

        byte[] uncompressPoolBuffer = ArrayPool<byte>.Shared.Rent(10000000);
        try
        {
            uncompressPoolBuffer.AsSpan().Fill(0xCC);
            FChunkInfo.TestDecompress(uncompressPoolBuffer, chunkBuffer, DecompressorBuilder.Default.Build());

            byte[] sha1 = SHA1.HashData(uncompressPoolBuffer.AsSpan(0, dataSize));
            Assert.Equal(chunkBuffer.AsSpan(ShaOffset, sha1.Length).ToArray(), sha1);
            // nothing written behind the chunk data
            Assert.Equal(0xCC, uncompressPoolBuffer[dataSize]);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(uncompressPoolBuffer);
        }
    }
}
