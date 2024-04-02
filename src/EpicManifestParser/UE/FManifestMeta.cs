using GenericReader;

namespace EpicManifestParser.UE;

public class FManifestMeta
{
	// The feature level support this build was created with, regardless of the serialised format.
	public EFeatureLevel FeatureLevel { get; internal set; } = EFeatureLevel.Invalid;
	// Whether this is a legacy 'nochunks' build.
	public bool bIsFileData { get; internal set; }
	// The app id provided at generation.
	public uint32 AppID { get; internal set; }
	// The app name string provided at generation.
	public string AppName { get; internal set; } = "";
	// The build version string provided at generation.
	public string BuildVersion { get; internal set; } = "";
	// The file in this manifest designated the application executable of the build.
	public string LaunchExe { get; internal set; } = "";
	// The command line required when launching the application executable.
	public string LaunchCommand { get; internal set; } = "";
	// The set of prerequisite ids for dependencies that this build's prerequisite installer will apply.
	public string[] PrereqIds { get; internal set; } = [];
	// A display string for the prerequisite provided at generation.
	public string PrereqName { get; internal set; } = "";
	// The file in this manifest designated the launch executable of the prerequisite installer.
	public string PrereqPath { get; internal set; } = "";
	// The command line required when launching the prerequisite installer.
	public string PrereqArgs { get; internal set; } = "";
	// A unique build id generated at original chunking time to identify an exact build.
	public string BuildId { get; internal set; } = "";

	public string UninstallExe { get; internal set; } = "";
	public string UninstallCommand { get; internal set; } = "";

	internal FManifestMeta() { }
	internal FManifestMeta(GenericBufferReader reader)
	{
		var startPos = reader.Position;
		var dataSize = reader.Read<int32>();
		var dataVersion = reader.Read<EManifestMetaVersion>();

		if (dataVersion >= EManifestMetaVersion.Original)
		{
			FeatureLevel = reader.Read<EFeatureLevel>();
			bIsFileData = reader.Read<uint8>() == 1;
			AppID = reader.Read<uint32>();
			AppName = reader.ReadFString();
			BuildVersion = reader.ReadFString();
			LaunchExe = reader.ReadFString();
			LaunchCommand = reader.ReadFString();
			PrereqIds = reader.ReadFStringArray();
			PrereqName = reader.ReadFString();
			PrereqPath = reader.ReadFString();
			PrereqArgs = reader.ReadFString();
		}

		BuildId = dataVersion >= EManifestMetaVersion.SerialisesBuildId
			? reader.ReadFString()
			: GetBackwardsCompatibleBuildId(this);

		// not to be found in UE, maybe fn specific?
		if (FeatureLevel > EFeatureLevel.UsesBuildTimeGeneratedBuildId)
		{
			UninstallExe = reader.ReadFString();
			UninstallCommand = reader.ReadFString();
		}

		reader.Position = startPos + dataSize;
	}

	public static string GetBackwardsCompatibleBuildId(in FManifestMeta meta)
	{
		// TODO: https://github.com/EpicGames/UnrealEngine/blob/a937fa584fbd6d69b7cf9c527907040c9dbf54fc/Engine/Source/Runtime/Online/BuildPatchServices/Private/BuildPatchUtil.cpp#L166
		return "";
	}
}
