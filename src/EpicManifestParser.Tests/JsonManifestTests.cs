using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using EpicManifestParser.UE;

namespace EpicManifestParser.Tests;

/// <summary>
/// <c>files/manifest.json</c> is the EpicGamesLauncher 2.9.2 manifest. The test project runs with
/// <c>JsonSerializerIsReflectionEnabledByDefault=false</c>, so every JSON parse here also checks that no reflection is needed.
/// </summary>
public class JsonManifestTests
{
    private static readonly byte[] Launcher = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "files", "manifest.json"));

    private const string PrereqHash = "F7123446F160ACFFAC0B36F87BF0CE5E3502C7C7";
    private const string UnknownGuid = "0123456789ABCDEF0123456789ABCDEF";

    private static JsonObject LoadJson() => JsonNode.Parse(Launcher)!.AsObject();
    private static byte[] ToBytes(JsonNode node) => Encoding.UTF8.GetBytes(node.ToJsonString());
    private static FBuildPatchAppManifest Parse(JsonNode node) => FBuildPatchAppManifest.DeserializeJson(ToBytes(node));

    /// <summary>A blob string decoded independently of the library: three decimal digits per byte.</summary>
    private static byte[] Blob(string blob) => Enumerable.Range(0, blob.Length / 3).Select(i => byte.Parse(blob.AsSpan(i * 3, 3))).ToArray();

    [Fact]
    public void Test_run_has_reflection_disabled()
    {
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
    }

    [Fact]
    public void Launcher_manifest_values()
    {
        FBuildPatchAppManifest manifest = FBuildPatchAppManifest.DeserializeJson(Launcher);

        Assert.Equal(EFeatureLevel.StoresChunkDataShaHashes, manifest.Meta.FeatureLevel);
        Assert.Equal("EpicGamesLauncher", manifest.Meta.AppName);
        Assert.False(manifest.Meta.bIsFileData);
        Assert.Equal(3430, manifest.Files.Count);
        Assert.Equal(246, manifest.ChunkList.Count);
        Assert.Equal(239_239_382, manifest.TotalBuildSize);
        Assert.Equal(manifest.Files.Sum(f => f.FileSize), manifest.TotalBuildSize);
        Assert.Equal(107_838_099, manifest.TotalDownloadSize);
        Assert.All(manifest.ChunkList, chunk => Assert.Equal(1048576u, chunk.DataSizeUncompressed));

        // the legacy values UE derives: backwards compatible build id, prerequisite id from the hash of the installer
        Assert.Equal("Wl3wr2K1ey8IXs6P4sz6GNQ4DfQ", manifest.Meta.BuildId);
        Assert.Equal([PrereqHash], manifest.Meta.PrereqIds);

        JsonObject json = LoadJson();
        foreach (FChunkInfo chunk in manifest.ChunkList)
        {
            string key = chunk.Guid.ToString();
            Assert.Equal(Blob(json["DataGroupList"]![key]!.GetValue<string>())[0], chunk.GroupNumber);
            Assert.Equal(BitConverter.ToUInt64(Blob(json["ChunkHashList"]![key]!.GetValue<string>())), chunk.Hash);
            Assert.Equal(BitConverter.ToInt64(Blob(json["ChunkFilesizeList"]![key]!.GetValue<string>())), chunk.FileSize);
            Assert.Equal(json["ChunkShaList"]![key]!.GetValue<string>(), chunk.ShaHash.ToString(), ignoreCase: true);
        }
    }

    [Fact]
    public void Unreferenced_chunk_list_entries_are_skipped()
    {
        JsonObject json = LoadJson();
        json["ChunkHashList"]![UnknownGuid] = "001002003004005006007008";
        json["ChunkShaList"]![UnknownGuid] = new string('A', 40);
        json["DataGroupList"]![UnknownGuid] = "042";
        json["ChunkFilesizeList"]![UnknownGuid] = "001000000000000000000000";

        FBuildPatchAppManifest manifest = Parse(json);

        Assert.Equal(246, manifest.ChunkList.Count);
        Assert.False(manifest.Chunks.ContainsKey(new FGuid(UnknownGuid)));
        Assert.Equal(107_838_099, manifest.TotalDownloadSize);
    }

    [Fact]
    public void Chunk_hash_list_without_used_entries_means_file_data()
    {
        // as in UE, the bIsFileData fallback only counts entries of referenced chunks
        JsonObject json = LoadJson();
        json.Remove("bIsFileData");
        Assert.False(Parse(json).Meta.bIsFileData);

        json["ChunkHashList"] = new JsonObject { [UnknownGuid] = "001002003004005006007008" };
        Assert.True(Parse(json).Meta.bIsFileData);
    }

    [Fact]
    public void File_data_manifest_keeps_its_window_sizes()
    {
        JsonObject json = LoadJson();
        json["bIsFileData"] = true;

        FBuildPatchAppManifest manifest = Parse(json);

        Assert.True(manifest.Meta.bIsFileData);
        Assert.All(manifest.ChunkList, chunk => Assert.Equal(0u, chunk.DataSizeUncompressed));
    }

    [Fact]
    public void Missing_FileManifestList()
    {
        JsonObject json = LoadJson();
        json.Remove("FileManifestList");
        Assert.Contains("FileManifestList", Assert.Throws<InvalidDataException>(() => Parse(json)).Message);

        json["FileManifestList"] = new JsonObject();
        Assert.Contains("FileManifestList", Assert.Throws<InvalidDataException>(() => Parse(json)).Message);
    }

    [Fact]
    public void Root_that_is_not_an_object()
    {
        Assert.Throws<InvalidDataException>(() => FBuildPatchAppManifest.DeserializeJson("[1, 2]"u8));
        Assert.Throws<InvalidDataException>(() => FBuildPatchAppManifest.DeserializeJson("null"u8));
    }

    [Fact]
    public void An_escaped_lone_surrogate_is_invalid_data()
    {
        // the DOM reads strings lazily and throws an InvalidOperationException for text that is not valid UTF-16
        Assert.Throws<InvalidDataException>(() =>
            FBuildPatchAppManifest.DeserializeJson("""{"AppID":"\uD80000000000000","FileManifestList":[]}"""u8));
        Assert.Throws<InvalidDataException>(() =>
            FBuildPatchAppManifest.DeserializeJson("""{"AppNameString":"\uD800","FileManifestList":[]}"""u8));
        Assert.Throws<InvalidDataException>(() =>
            FBuildPatchAppManifest.DeserializeJson("""{"FileManifestList":[],"CustomFields":{"\uD800":"value"}}"""u8));
    }

    [Fact]
    public void A_duplicate_property_is_invalid_json()
    {
        Assert.ThrowsAny<JsonException>(() =>
            FBuildPatchAppManifest.DeserializeJson("""{"AppID":"000000000000","AppID":"000000000000","FileManifestList":[]}"""u8));
        Assert.ThrowsAny<JsonException>(() =>
            FBuildPatchAppManifest.DeserializeJson("""{"FileManifestList":[],"CustomFields":{"a":"1","a":"2"}}"""u8));
    }

    [Fact]
    public void Missing_DataGroupList_derives_the_group_from_the_deprecated_crc()
    {
        JsonObject json = LoadJson();
        json.Remove("DataGroupList");

        FBuildPatchAppManifest manifest = Parse(json);

        // known answers of UE's FCrc::MemCrc_DEPRECATED (slicing-by-8 with UE's tables) modulo 100
        Assert.Equal(33, manifest.Chunks[new FGuid("80CF85434A18C0DC4BD482909F40DF8D")].GroupNumber);
        Assert.Equal(9, manifest.Chunks[new FGuid("D5AB468E4970DB4EF3C13B8456EF84A5")].GroupNumber);
        Assert.Equal(72, manifest.Chunks[new FGuid("C755B7194673726F943A19808B3FB98A")].GroupNumber);
        Assert.All(manifest.ChunkList, chunk => Assert.Equal(FChunkInfo.GetDeprecatedGroupNumber(chunk.Guid), chunk.GroupNumber));
    }

    [Fact]
    public void Deprecated_crc_is_crc32_bzip2()
    {
        Assert.Equal(0xFC891918u, FChunkInfo.MemCrcDeprecated("123456789"u8));
        Assert.Equal(0xACBDD4CDu, FChunkInfo.MemCrcDeprecated(new FGuid("80CF85434A18C0DC4BD482909F40DF8D").AsSpan()));
    }

    [Fact]
    public void PrereqIds_are_read_when_present()
    {
        JsonObject json = LoadJson();
        json["PrereqIds"] = new JsonArray("Id1", "Id2");
        Assert.Equal(["Id1", "Id2"], Parse(json).Meta.PrereqIds);

        json["PrereqIds"] = new JsonArray();
        Assert.Empty(Parse(json).Meta.PrereqIds);

        // present but null: UE reads it as an empty array, no fallback
        json["PrereqIds"] = null;
        Assert.Empty(Parse(json).Meta.PrereqIds);
    }

    [Fact]
    public void PrereqIds_fall_back_to_the_prerequisite_installer()
    {
        JsonObject json = LoadJson();
        Assert.False(json.ContainsKey("PrereqIds"));

        // backslashes and a different case still find the file, as in UE
        json["PrereqPath"] = @"portal\extras\redist\LAUNCHERPREREQSETUP_X64.exe";
        Assert.Equal([PrereqHash], Parse(json).Meta.PrereqIds);

        json["PrereqPath"] = "Portal/Extras/Redist/Missing.exe";
        Assert.Empty(Parse(json).Meta.PrereqIds);
    }

    public static TheoryData<string> MalformedValues => new()
    {
        "FileHash blob length", "FileHash blob byte", "FileHash number", "part Guid", "part Offset number", "Filename number",
        "ChunkHashList key", "ChunkShaList value", "DataGroupList array", "InstallTags element",
    };

    [Theory]
    [MemberData(nameof(MalformedValues))]
    public void Malformed_values(string what)
    {
        JsonObject json = LoadJson();
        JsonObject file = json["FileManifestList"]![0]!.AsObject();
        JsonObject part = file["FileChunkParts"]![0]!.AsObject();
        switch (what)
        {
            case "FileHash blob length": file["FileHash"] = "12"; break;
            case "FileHash blob byte": file["FileHash"] = "256"; break;
            case "FileHash number": file["FileHash"] = 5; break;
            case "part Guid": part["Guid"] = "xyz"; break;
            case "part Offset number": part["Offset"] = 5; break;
            case "Filename number": file["Filename"] = 5; break;
            case "ChunkHashList key": json["ChunkHashList"]!["nope"] = "001002003004005006007008"; break;
            case "ChunkShaList value": json["ChunkShaList"]![json["ChunkShaList"]!.AsObject().First().Key] = "zz"; break;
            case "DataGroupList array": json["DataGroupList"] = new JsonArray(); break;
            case "InstallTags element": file["InstallTags"] = new JsonArray(1); break;
        }

        Assert.Throws<InvalidDataException>(() => Parse(json));
    }

    [Fact]
    public void Escaped_values_are_unescaped()
    {
        string text = Encoding.UTF8.GetString(Launcher);
        FBuildPatchAppManifest expected = FBuildPatchAppManifest.DeserializeJson(Launcher);

        // escape the first character of every chunk part GUID and of every file hash: \u00XX
        string escaped = Regex.Replace(text, """
                                             "(Guid|FileHash)":"([0-9A-Fa-f])
                                             """,
            m => $"\"{m.Groups[1].Value}\":\"\\u00{(int)m.Groups[2].Value[0]:X2}");
        Assert.Contains("""
                        "Guid":"\u00
                        """, escaped);
        Assert.Contains("""
                        "FileHash":"\u00
                        """, escaped);

        FBuildPatchAppManifest actual = FBuildPatchAppManifest.DeserializeJson(Encoding.UTF8.GetBytes(escaped));

        Assert.Equal(expected.Files.Select(f => f.SHA1Hash), actual.Files.Select(f => f.SHA1Hash));
        Assert.Equal(expected.ChunkList.Select(c => c.Guid.ToString()).Order(), actual.ChunkList.Select(c => c.Guid.ToString()).Order());
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("7B", true)]
    [InlineData("20090D0A7B", true)]
    [InlineData("EFBBBF7B", true)]
    [InlineData("EFBBBF200A7B", true)]
    [InlineData("61", false)]
    [InlineData("6162", false)]
    [InlineData("EFBB", false)]
    [InlineData("EFBBBF", false)]
    [InlineData("0CC0BE44", false)]
    [InlineData("200D0A", false)]
    public void IsJson(string hex, bool expected)
    {
        Assert.Equal(expected, FBuildPatchAppManifest.IsJson(Convert.FromHexString(hex)));
    }

    [Fact]
    public void Bom_and_leading_whitespace()
    {
        byte[] data = [.. "\uFEFF\r\n\t "u8, .. Launcher];

        Assert.True(FBuildPatchAppManifest.IsJson(data));
        Assert.Equal(3430, FBuildPatchAppManifest.Deserialize(data).Files.Count);
        Assert.Equal(3430, FBuildPatchAppManifest.DeserializeJson(data).Files.Count);
    }
}
