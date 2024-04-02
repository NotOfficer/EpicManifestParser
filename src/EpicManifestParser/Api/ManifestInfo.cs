using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

using EpicManifestParser.UE;

using Flurl;

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

	public Task<(FBuildPatchAppManifest Manifest, ManifestInfoElement InfoElement)> DownloadAndParseAsync(
		Action<ManifestParseOptions>? optionsBuilder = null, Predicate<ManifestInfoElement>? elementPredicate = null,
		Predicate<ManifestInfoElementManifest>? elementManifestPredicate = null, CancellationToken cancellationToken = default)
	{
		var options = new ManifestParseOptions();
		optionsBuilder?.Invoke(options);
		return DownloadAndParseAsync(options, elementPredicate, elementManifestPredicate, cancellationToken);
	}

	public async Task<(FBuildPatchAppManifest Manifest, ManifestInfoElement InfoElement)> DownloadAndParseAsync(
		ManifestParseOptions options, Predicate<ManifestInfoElement>? predicate = null,
		Predicate<ManifestInfoElementManifest>? elementManifestPredicate = null, CancellationToken cancellationToken = default)
	{
		ManifestInfoElement element;
		if (predicate is null)
			element = Elements[0];
		else
			element = Elements.Find(predicate) ?? throw new FileLoadException("could not find ManifestInfoElement based on predicate");

		ManifestInfoElementManifest elementManifest;
		if (elementManifestPredicate is null)
			elementManifest = element.Manifests[0];
		else
			elementManifest = element.Manifests.Find(elementManifestPredicate) ?? throw new FileLoadException("could not find ManifestInfoElement based on predicate");

		string? cachePath = null;

		if (options.ManifestCacheDirectory is not null)
		{
			var fileName =
				elementManifest.Uri.OriginalString[(elementManifest.Uri.OriginalString.LastIndexOf('/') + 1)..];
			cachePath = Path.Combine(options.ManifestCacheDirectory, fileName);
			if (File.Exists(cachePath))
			{
				var manifestBuffer = await File.ReadAllBytesAsync(cachePath, cancellationToken).ConfigureAwait(false);
				var manifest = FBuildPatchAppManifest.Deserialize(manifestBuffer, options);
				return (manifest, element);
			}
		}

		{
			Uri manifestUri;

			if (elementManifest.QueryParams is { Count: not 0 })
			{
				var url = new Url(elementManifest.Uri);
				foreach (var queryParam in elementManifest.QueryParams)
					url.AppendQueryParam(queryParam.Name, queryParam.Value, NullValueHandling.NameOnly);
				manifestUri = url.ToUri();
			}
			else
			{
				manifestUri = elementManifest.Uri;
			}

			options.CreateDefaultClient();
			var manifestBuffer = await options.Client!.GetByteArrayAsync(manifestUri, cancellationToken).ConfigureAwait(false);
			var manifest = FBuildPatchAppManifest.Deserialize(manifestBuffer, options);

			if (cachePath is not null)
				await File.WriteAllBytesAsync(cachePath, manifestBuffer, cancellationToken).ConfigureAwait(false);

			return (manifest, element);
		}
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

	public bool TryParseVersionAndCL([NotNullWhen(true)] out Version? version, out int cl) =>
		ManifestExtensions.TryParseVersionAndCL(BuildVersion, out version, out cl);
}

public class ManifestInfoElementManifest
{
	public required Uri Uri { get; set; }
	public List<ManifestInfoElementManifestQueryParams>? QueryParams { get; set; }
}

public class ManifestInfoElementManifestQueryParams
{
	public required string Name { get; set; }
	public required string Value { get; set; }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, Converters = [ typeof(FSHAHashConverter) ])]
[JsonSerializable(typeof(ManifestInfo))]
public partial class EpicManifestParserJsonContext : JsonSerializerContext;

public static partial class ManifestExtensions
{
	[GeneratedRegex(@"(\d+(?:\.\d+)+)-CL-(\d+)", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
	internal static partial Regex VersionAndClRegexGen();
	internal static Regex VersionAndClRegex = VersionAndClRegexGen();

	public static bool TryParseVersionAndCL(string buildVersion, [NotNullWhen(true)] out Version? version, out int cl)
	{
		version = null;
		cl = -1;
		if (string.IsNullOrEmpty(buildVersion))
			return false;

		var match = VersionAndClRegex.Match(buildVersion);
		if (!match.Success)
			return false;

		version = Version.Parse(match.Groups[1].ValueSpan);
		cl = int.Parse(match.Groups[2].ValueSpan);
		return true;
	}

	public static Task<ManifestInfo?> ReadManifestInfoAsync(this HttpContent content, CancellationToken cancellationToken = default)
		=> content.ReadFromJsonAsync(EpicManifestParserJsonContext.Default.ManifestInfo, cancellationToken);
}
