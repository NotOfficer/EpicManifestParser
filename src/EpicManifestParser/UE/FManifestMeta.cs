using System.Buffers;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace EpicManifestParser.UE;

// https://github.com/NotOfficer/UnrealEngine/blob/1436d646b9b11ffe9a46b04e9617dba689b56d35/Engine/Source/Runtime/Online/BuildPatchServices/Private/Data/ManifestData.h?plain=1#L91C15-L91C15
/// <summary>
/// UE FManifestMeta struct
/// </summary>
public sealed class FManifestMeta
{
    /// <summary>
    /// The feature level support this build was created with, regardless of the serialised format.
    /// </summary>
    public EFeatureLevel FeatureLevel { get; internal set; } = EFeatureLevel.Invalid;
    /// <summary>
    /// Whether this is a legacy 'nochunks' build.
    /// </summary>
    public bool bIsFileData { get; internal set; }
    /// <summary>
    /// The app id provided at generation.
    /// </summary>
    public uint32 AppID { get; internal set; }
    /// <summary>
    /// The app name string provided at generation.
    /// </summary>
    public string AppName { get; internal set; } = "";
    /// <summary>
    /// The build version string provided at generation.
    /// </summary>
    public string BuildVersion { get; internal set; } = "";
    /// <summary>
    /// The file in this manifest designated the application executable of the build.
    /// Can be an obfuscated string for encrypted manifests.
    /// </summary>
    public string LaunchExe { get; internal set; } = "";
    /// <summary>
    /// The command line required when launching the application executable.
    /// Can be an obfuscated string for encrypted manifests.
    /// </summary>
    public string LaunchCommand { get; internal set; } = "";
    /// <summary>
    /// The set of prerequisite ids for dependencies that this build's prerequisite installer will apply.
    /// Can be an obfuscated strings for encrypted manifests.
    /// </summary>
    public string[] PrereqIds { get; internal set; } = [];
    /// <summary>
    /// A display string for the prerequisite provided at generation.
    /// Can be an obfuscated string for encrypted manifests.
    /// </summary>
    public string PrereqName { get; internal set; } = "";
    /// <summary>
    /// The file in this manifest designated the launch executable of the prerequisite installer.
    /// Can be an obfuscated string for encrypted manifests.
    /// </summary>
    public string PrereqPath { get; internal set; } = "";
    /// <summary>
    /// The command line required when launching the prerequisite installer.
    /// Can be an obfuscated string for encrypted manifests.
    /// </summary>
    public string PrereqArgs { get; internal set; } = "";
    /// <summary>
    /// The path to the uninstall custom action executable.
    /// Can be an obfuscated string for encrypted manifests.
    /// </summary>
    public string UninstallActionPath { get; internal set; } = "";
    /// <summary>
    /// The arguments to the uninstall custom action executable.
    /// Can be an obfuscated string for encrypted manifests.
    /// </summary>
    public string UninstallActionArgs { get; internal set; } = "";
    /// <summary>
    /// A unique build id generated at original chunking time to identify an exact build.
    /// </summary>
    public string BuildId { get; internal set; } = "";

    /// <summary>
    /// The chunk sub-directory name.
    /// </summary>
    public string ChunkSubdir { get; internal set; } = "";

    internal FManifestMeta() { }
    // https://github.com/NotOfficer/UnrealEngine/blob/1436d646b9b11ffe9a46b04e9617dba689b56d35/Engine/Source/Runtime/Online/BuildPatchServices/Private/Data/ManifestData.cpp?plain=1#L494C57-L494C57
    internal FManifestMeta(ref ManifestReader reader)
    {
        int32 end = reader.BeginSection(ManifestSection.Meta, ManifestSection.HeaderSize, reader.Length);
        EManifestMetaVersion dataVersion = reader.Read<EManifestMetaVersion>();

        if (dataVersion >= EManifestMetaVersion.Original)
        {
            FeatureLevel = reader.Read<EFeatureLevel>();
            ChunkSubdir = GetChunkSubdir(FeatureLevel);

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

        if (dataVersion >= EManifestMetaVersion.SerialisesUnistallActions)
        {
            UninstallActionPath = reader.ReadFString();
            UninstallActionArgs = reader.ReadFString();
        }

        reader.EndSection(ManifestSection.Meta, end);
    }

    // Port of FBuildPatchUtils::GetBackwardsCompatibleBuildId, BuildPatchUtil.cpp lines 166-192:
    // https://github.com/EpicGames/UnrealEngine/blob/8c31706601135aadf2f957fb76e2af46f04a8ef9/Engine/Source/Runtime/Online/BuildPatchServices/Private/BuildPatchUtil.cpp#L166-L192
    /// <summary>
    /// The build id of a manifest that does not store one (JSON, or binary below <see cref="EManifestMetaVersion.SerialisesBuildId"/>):
    /// the SHA1 of the app id (4 bytes, little endian) and the UTF-8 app name, build version, launch exe and launch command,
    /// as URL safe base64 without padding.
    /// </summary>
    internal static string GetBackwardsCompatibleBuildId(FManifestMeta meta)
    {
        int32 length = sizeof(uint32)
                       + Encoding.UTF8.GetByteCount(meta.AppName)
                       + Encoding.UTF8.GetByteCount(meta.BuildVersion)
                       + Encoding.UTF8.GetByteCount(meta.LaunchExe)
                       + Encoding.UTF8.GetByteCount(meta.LaunchCommand);

        byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            Span<byte> data = buffer.AsSpan(0, length);
            BinaryPrimitives.WriteUInt32LittleEndian(data, meta.AppID);
            int32 written = sizeof(uint32);
            written += Encoding.UTF8.GetBytes(meta.AppName, data[written..]);
            written += Encoding.UTF8.GetBytes(meta.BuildVersion, data[written..]);
            written += Encoding.UTF8.GetBytes(meta.LaunchExe, data[written..]);
            written += Encoding.UTF8.GetBytes(meta.LaunchCommand, data[written..]);

            Span<byte> hash = stackalloc byte[SHA1.HashSizeInBytes];
            SHA1.HashData(data[..written], hash);
            // UE base64 encodes, replaces '+' with '-' and '/' with '_' and removes the '=' padding: base64url
            return Base64Url.EncodeToString(hash);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    internal static string GetChunkSubdir(EFeatureLevel featureLevel) => featureLevel switch
    {
        < EFeatureLevel.DataFileRenames => "Chunks",
        < EFeatureLevel.ChunkCompressionSupport => "ChunksV2",
        < EFeatureLevel.VariableSizeChunksWithoutWindowSizeChunkInfo => "ChunksV3",
        < EFeatureLevel.ChunkEncryptionSupport => "ChunksV4",
        _ => "ChunksV5"
    };
}
