using EpicManifestParser.UE;

namespace EpicManifestParser.Tests;

/// <summary>
/// The chunk path depends on the manifest's feature level, as in UE's <c>BuildPatchUtils::GetChunkNewFilename</c>
/// (BuildPatchUtil.cpp). The base64url values below are worked out by hand from the little-endian bytes.
/// </summary>
public class ChunkUrlTests
{
    private const string BaseUrl = "https://cdn.test/CloudDir/";
    private static readonly FGuid Guid = new(0xAAAAAAAA, 1, 2, 3);
    private const ulong Hash = 0x1234;

    private static FChunkInfo Chunk(FGuid? secret = null) => new()
    {
        Guid = Guid,
        Hash = Hash,
        GroupNumber = 94,
        EncryptionSecretId = secret
    };

    private static FBuildPatchAppManifest Manifest(EFeatureLevel featureLevel) => new()
    {
        Options = new ManifestParseOptions { ChunkBaseUrl = BaseUrl },
        Meta = new FManifestMeta { FeatureLevel = featureLevel, ChunkSubdir = FManifestMeta.GetChunkSubdir(featureLevel) }
    };

    [Fact]
    public void Before_DataFileRenames_the_name_is_the_GUID_under_Chunks()
    {
        string group = FChunkInfo.GetDeprecatedGroupNumber(Guid).ToString("D2");

        Assert.Equal($"{BaseUrl}Chunks/{group}/AAAAAAAA000000010000000200000003.chunk", Chunk().GetUrl(Manifest(EFeatureLevel.StartStoringVersion)));
    }

    [Theory]
    [InlineData(EFeatureLevel.DataFileRenames, "ChunksV2")]
    [InlineData(EFeatureLevel.StoresUninstallActions, "ChunksV4")]
    [InlineData(EFeatureLevel.ChunkEncryptionSupport, "ChunksV5")]
    public void Up_to_ChunksStoredBySecret_the_name_is_hex_hash_and_GUID(EFeatureLevel featureLevel, string subdir)
    {
        Assert.Equal($"{BaseUrl}{subdir}/94/0000000000001234_AAAAAAAA000000010000000200000003.chunk", Chunk().GetUrl(Manifest(featureLevel)));
    }

    [Theory]
    [InlineData(EFeatureLevel.ChunksStoredBySecret)]
    [InlineData(EFeatureLevel.ManifestEncryptionSupport)]
    public void From_ChunksStoredBySecret_the_name_is_base64url_under_the_secret(EFeatureLevel featureLevel)
    {
        Assert.Equal($"{BaseUrl}ChunksV5/plain/94/NBIAAAAAAAA_qqqqqgEAAAACAAAAAwAAAA.chunk", Chunk().GetUrl(Manifest(featureLevel)));
        // an invalid (all zero) secret ID is no secret
        Assert.Equal($"{BaseUrl}ChunksV5/plain/94/NBIAAAAAAAA_qqqqqgEAAAACAAAAAwAAAA.chunk", Chunk(default(FGuid)).GetUrl(Manifest(featureLevel)));
        Assert.Equal($"{BaseUrl}ChunksV5/AQAAAAIAAAADAAAABAAAAA/94/NBIAAAAAAAA_qqqqqgEAAAACAAAAAwAAAA.chunk",
            Chunk(new FGuid(1, 2, 3, 4)).GetUrl(Manifest(featureLevel)));
    }
}
