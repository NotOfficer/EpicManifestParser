using EpicManifestParser.UE;

namespace EpicManifestParser.Tests;

public class DeserializeTests
{
	[Fact]
	public async Task Deserialize_Binary_Manifest()
	{
		var manifestBuffer = await File.ReadAllBytesAsync("files/manifest.bin");
		var manifest = FBuildPatchAppManifest.Deserialize(manifestBuffer);
		Assert.NotNull(manifest);

		// TODO: more assertions
	}

	[Fact]
	public async Task Deserialize_Json_Manifest()
	{
		var manifestBuffer = await File.ReadAllBytesAsync("files/manifest.json");
		var manifest = FBuildPatchAppManifest.Deserialize(manifestBuffer);
		Assert.NotNull(manifest);

		// TODO: more assertions
	}
}
