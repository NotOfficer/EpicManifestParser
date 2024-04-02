using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using EpicManifestParser.UE;

namespace EpicManifestParser.Api;
// ReSharper disable UseSymbolAlias

public class ManifestInfo
{
	public required List<ManifestInfoElement> Elements { get; set; }

	public static ManifestInfo? Deserialize(ReadOnlySpan<byte> utf8Json)
		=> JsonSerializer.Deserialize(utf8Json, EpicManifestParserJsonContext.Default.ManifestInfo);

	public static ManifestInfo? Deserialize(Stream utf8Json)
		=> JsonSerializer.Deserialize(utf8Json, EpicManifestParserJsonContext.Default.ManifestInfo);

	public static ValueTask<ManifestInfo?> DeserializeAsync(Stream utf8Json, CancellationToken cancellationToken = default)
		=> JsonSerializer.DeserializeAsync(utf8Json, EpicManifestParserJsonContext.Default.ManifestInfo, cancellationToken);

	public static ManifestInfo? DeserializeFile(string path)
	{
		using var fs = File.OpenRead(path);
		return JsonSerializer.Deserialize(fs, EpicManifestParserJsonContext.Default.ManifestInfo);
	}

	public static ValueTask<ManifestInfo?> DeserializeFileAsync(string path, CancellationToken cancellationToken = default)
	{
		using var fs = File.OpenRead(path);
		return JsonSerializer.DeserializeAsync(fs, EpicManifestParserJsonContext.Default.ManifestInfo, cancellationToken);
	}
}

public class ManifestInfoElement
{
	public required string AppName { get; set; }
	public required string LabelName { get; set; }
	public required string BuildVersion { get; set; }
	public FSHAHash Hash { get; set; }
	public bool UseSignedUrl { get; set; }
	public Dictionary<string, string>? Metadata { get; set; }
	public required List<ManifestInfoElementManifest> Manifests { get; set; }
}

public class ManifestInfoElementManifest
{
	public required Uri Uri { get; set; }
	public List<ManifestInfoElementManifestQueryParams> QueryParams { get; set; } = [];
}

public class ManifestInfoElementManifestQueryParams
{
	public required string Name { get; set; }
	public required string Value { get; set; }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, Converters = [ typeof(FSHAHashConverter) ])]
[JsonSerializable(typeof(ManifestInfo))]
public partial class EpicManifestParserJsonContext : JsonSerializerContext;

public static class ManifestInfoExtensions
{
	public static Task<ManifestInfo?> ReadManifestInfoAsync(this HttpContent content, CancellationToken cancellationToken = default)
		=> content.ReadFromJsonAsync(EpicManifestParserJsonContext.Default.ManifestInfo, cancellationToken);
}
