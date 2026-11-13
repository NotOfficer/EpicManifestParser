using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace EpicManifestParser.Api;
// ReSharper disable UseSymbolAlias

/// <summary/>
public class ManifestInfo
{
    /// <summary/>
    public required List<ManifestInfoElement> Elements { get; set; }

    /// <summary>
    /// Parses the UTF-8 encoded text representing a single JSON value into a <see cref="ManifestInfo"/>.
    /// </summary>
    /// <returns>A <see cref="ManifestInfo"/> representation of the JSON value.</returns>
    /// <param name="utf8Json">JSON text to parse.</param>
    /// <exception cref="JsonException">
    /// The JSON is invalid,
    /// <see cref="ManifestInfo"/> is not compatible with the JSON,
    /// or when there is remaining data in the buffer.
    /// </exception>
    public static ManifestInfo? Deserialize(ReadOnlySpan<byte> utf8Json)
        => JsonSerializer.Deserialize(utf8Json, EpicManifestParserJsonContext.Default.ManifestInfo);

    /// <summary>
    /// Reads the UTF-8 encoded text representing a single JSON value into a <see cref="ManifestInfo"/>.
    /// The Stream will be read to completion.
    /// </summary>
    /// <returns>A <see cref="ManifestInfo"/> representation of the JSON value.</returns>
    /// <param name="utf8Json">JSON data to parse.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="utf8Json"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="JsonException">
    /// The JSON is invalid,
    /// <see cref="ManifestInfo"/> is not compatible with the JSON,
    /// or when there is remaining data in the Stream.
    /// </exception>
    public static ManifestInfo? Deserialize(Stream utf8Json)
        => JsonSerializer.Deserialize(utf8Json, EpicManifestParserJsonContext.Default.ManifestInfo);

    /// <summary>
    /// Reads the UTF-8 encoded text representing a single JSON value into a <see cref="ManifestInfo"/>.
    /// The Stream will be read to completion.
    /// </summary>
    /// <returns>A <see cref="ManifestInfo"/> representation of the JSON value.</returns>
    /// <param name="utf8Json">JSON data to parse.</param>
    /// <param name="cancellationToken">
    /// The <see cref="CancellationToken"/> that can be used to cancel the read operation.
    /// </param>
    /// <exception cref="System.ArgumentNullException">
    /// <paramref name="utf8Json"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="JsonException">
    /// The JSON is invalid,
    /// <see cref="ManifestInfo"/> is not compatible with the JSON,
    /// or when there is remaining data in the Stream.
    /// </exception>
    public static ValueTask<ManifestInfo?> DeserializeAsync(Stream utf8Json, CancellationToken cancellationToken = default)
        => JsonSerializer.DeserializeAsync(utf8Json, EpicManifestParserJsonContext.Default.ManifestInfo, cancellationToken);

    /// <summary>
    /// Reads the UTF-8 encoded text representing a single JSON value into a <see cref="ManifestInfo"/>.
    /// The Stream will be read to completion.
    /// </summary>
    /// <returns>A <see cref="ManifestInfo"/> representation of the JSON value.</returns>
    /// <param name="path">JSON file to parse.</param>
    /// <exception cref="JsonException">
    /// The JSON is invalid,
    /// <see cref="ManifestInfo"/> is not compatible with the JSON,
    /// or when there is remaining data in the Stream.
    /// </exception>
    /// <inheritdoc cref="File.OpenRead(string)"/>
    public static ManifestInfo? DeserializeFile(string path)
    {
        using FileStream fs = File.OpenRead(path);
        return JsonSerializer.Deserialize(fs, EpicManifestParserJsonContext.Default.ManifestInfo);
    }

    /// <summary>
    /// Reads the UTF-8 encoded text representing a single JSON value into a <see cref="ManifestInfo"/>.
    /// The Stream will be read to completion.
    /// </summary>
    /// <returns>A <see cref="ManifestInfo"/> representation of the JSON value.</returns>
    /// <param name="path">JSON file to parse.</param>
    /// <param name="cancellationToken">
    /// The <see cref="CancellationToken"/> that can be used to cancel the read operation.
    /// </param>
    /// <exception cref="JsonException">
    /// The JSON is invalid,
    /// <see cref="ManifestInfo"/> is not compatible with the JSON,
    /// or when there is remaining data in the Stream.
    /// </exception>
    /// <inheritdoc cref="File.OpenRead(string)"/>
    public static async ValueTask<ManifestInfo?> DeserializeFileAsync(string path, CancellationToken cancellationToken = default)
    {
        // the serializer reads through its own buffer
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 0, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (fs.ConfigureAwait(false))
        {
            return await JsonSerializer.DeserializeAsync(fs, EpicManifestParserJsonContext.Default.ManifestInfo, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <param name="elementPredicate">Predicate to select a specific element in <see cref="Elements"/></param>
    /// <param name="elementDownloadPredicate">Predicate to select a specific download in <see cref="ManifestInfoElement.Manifests"/></param>
    /// <param name="cancellationToken">
    /// The <see cref="CancellationToken"/> that can be used to cancel the read operation.
    /// </param>
    /// <param name="optionsBuilder">Builder for options for parsing and/or caching the manifest</param>
    /// <inheritdoc cref="DownloadAndParseAsync(ManifestParseOptions, Predicate&lt;ManifestInfoElement&gt;?, Predicate&lt;ManifestInfoElementDownload&gt;?, CancellationToken)"/>
    public Task<(FBuildPatchAppManifest Manifest, ManifestInfoElement InfoElement)> DownloadAndParseAsync(
        Predicate<ManifestInfoElement>? elementPredicate = null, Predicate<ManifestInfoElementDownload>? elementDownloadPredicate = null,
        CancellationToken cancellationToken = default, Action<ManifestParseOptions>? optionsBuilder = null)
    {
        var options = new ManifestParseOptions();
        optionsBuilder?.Invoke(options);
        return DownloadAndParseAsync(options, elementPredicate, elementDownloadPredicate, cancellationToken);
    }

    /// <summary>
    /// Downloads and parses the manifest.
    /// </summary>
    /// <param name="options">Options for parsing and/or caching the manifest</param>
    /// <param name="elementPredicate">Predicate to select a specific element in <see cref="Elements"/></param>
    /// <param name="elementDownloadPredicate">Predicate to select a specific download in <see cref="ManifestInfoElement.Manifests"/></param>
    /// <param name="cancellationToken">
    /// The <see cref="CancellationToken"/> that can be used to cancel the read operation.
    /// </param>
    /// <returns>
    /// The parsed <see cref="FBuildPatchAppManifest"/> manifest and the selected <see cref="ManifestInfoElement"/> info element in a <see cref="ValueTuple"/>
    /// </returns>
    /// <remarks>
    /// With <see cref="ManifestParseOptions.ManifestCacheDirectory"/> the manifest is read from and written to
    /// <c>{ManifestCacheDirectory}/{file name of the URI's path}</c>; the directory is created when it is missing. A failed write of
    /// the cache file does not fail the call, neither does a cache file that cannot be read or parsed: the manifest is downloaded
    /// then. A downloaded manifest that fails to parse, invalid JSON included, counts as a corrupt response and is downloaded
    /// again, up to <see cref="ManifestParseOptions.MaxDownloadRetries"/> times.
    /// </remarks>
    /// <exception cref="InvalidOperationException">When a predicate fails.</exception>
    /// <exception cref="HttpRequestException">
    /// When the manifest data fails to download, after <see cref="ManifestParseOptions.MaxDownloadRetries"/> retries for transient errors.
    /// </exception>
    /// <exception cref="TimeoutException">
    /// The last attempt received no data for <see cref="ManifestParseOptions.DownloadIdleTimeout"/>.
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// The last download was truncated or the manifest is corrupt (invalid JSON included), see
    /// <see cref="FBuildPatchAppManifest.Deserialize(ManifestRoData,ManifestParseOptions?)"/>.
    /// </exception>
    /// <exception cref="FileLoadException">The last download is a binary manifest with an invalid magic or data that fails to decompress.</exception>
    /// <exception cref="NotSupportedException">The manifest is a binary manifest below <see cref="EFeatureLevel.StoredAsBinaryData"/> (not retried).</exception>
    /// <exception cref="OperationCanceledException">The <paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<(FBuildPatchAppManifest Manifest, ManifestInfoElement InfoElement)> DownloadAndParseAsync(
        ManifestParseOptions options, Predicate<ManifestInfoElement>? elementPredicate = null,
        Predicate<ManifestInfoElementDownload>? elementDownloadPredicate = null, CancellationToken cancellationToken = default)
    {
        ManifestInfoElement element;
        if (elementPredicate is null)
            element = Elements[0];
        else
            element = Elements.Find(elementPredicate) ?? throw new InvalidOperationException("Could not find ManifestInfoElement based on predicate");

        ManifestInfoElementDownload elementDownload;
        if (elementDownloadPredicate is null)
            elementDownload = element.Manifests[0];
        else
            elementDownload = element.Manifests.Find(elementDownloadPredicate) ?? throw new InvalidOperationException("Could not find ManifestInfoElement based on predicate");

        string? cachePath = GetCachePath(options.ManifestCacheDirectory, elementDownload.Uri);

        if (cachePath is not null && await TryReadCachedManifestAsync(cachePath, options, cancellationToken).ConfigureAwait(false) is { } cached)
            return (cached, element);

        {
            Uri manifestUri = AppendQueryParams(elementDownload.Uri, elementDownload.QueryParams);

            FBuildPatchAppManifest manifest;
            RentedBuffer manifestBuffer;

            try
            {
                // parsed inside the retry loop: a response that fails to parse is corrupt and downloaded again;
                // the body comes back with the manifest so that it can be cached
                (manifest, manifestBuffer) = await HttpDownloader.DownloadAsync(options, manifestUri, ManifestDownloadBufferSize,
                    HttpDownloader.Unbounded, options, static (parseOptions, body) => (ParseDownloadedManifest(body.Span, parseOptions), body),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException httpEx)
            {
                httpEx.Data["ManifestUri"] = manifestUri;
                httpEx.Data["ElementManifest"] = elementDownload;
                httpEx.Data["Element"] = element;
                throw;
            }

            try
            {
                if (cachePath is not null)
                {
                    // temp file + rename: a cancelled or failed write never leaves a truncated manifest behind;
                    // a failed write is not an error of the download, the manifest is downloaded again next time
                    await AtomicFile.TryWriteAsync(cachePath, manifestBuffer.Memory).ConfigureAwait(false);
                }

                return (manifest, element);
            }
            finally
            {
                manifestBuffer.Return();
            }
        }
    }

    // initial buffer when the response has no Content-Length, grows as needed
    private const int ManifestDownloadBufferSize = 1024 * 1024;

    /// <summary>
    /// The manifest in the cache file, or <see langword="null"/> when there is none that can be used.
    /// </summary>
    /// <remarks>
    /// Several calls may share the cache file: while one of them replaces or deletes it, another one may not be able to open it.
    /// That only means downloading the manifest. A file that does not parse is deleted: older versions wrote it non-atomically,
    /// so a cancelled run could leave it truncated, and the download rewrites it atomically.
    /// </remarks>
    private static async Task<FBuildPatchAppManifest?> TryReadCachedManifestAsync(string cachePath, ManifestParseOptions options,
        CancellationToken cancellationToken)
    {
        byte[] data;
        try
        {
            data = await File.ReadAllBytesAsync(cachePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // missing, or in use by another call that replaces or deletes it right now
            return null;
        }

        try
        {
            return FBuildPatchAppManifest.Deserialize(data, options);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AtomicFile.TryDelete(cachePath);
            return null;
        }
    }

    /// <summary>
    /// Parses a downloaded manifest. Invalid JSON is a corrupt response like a corrupt binary manifest: as an
    /// <see cref="InvalidDataException"/> it is downloaded again, a <see cref="JsonException"/> would not be.
    /// </summary>
    private static FBuildPatchAppManifest ParseDownloadedManifest(ManifestRoData data, ManifestParseOptions options)
    {
        try
        {
            return FBuildPatchAppManifest.Deserialize(data, options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Invalid JSON manifest: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// <paramref name="uri"/> with <paramref name="queryParams"/> appended: after an existing query, before a fragment, as
    /// <c>name=value</c>, or the name alone when the value is <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// Epic's API delivers names and values ready for the URL, so they are appended as they are. An Akamai <c>ak_token</c> is
    /// <c>exp=…~hmac=…</c>, and Akamai rejects it with the <c>=</c> escaped as <c>%3D</c>. <see cref="Uri"/> escapes only what a
    /// URI cannot contain, such as spaces, quotes and non-ASCII characters.
    /// </remarks>
    internal static Uri AppendQueryParams(Uri uri, List<ManifestInfoElementDownloadQueryParams>? queryParams)
    {
        if (queryParams is not { Count: not 0 })
            return uri;

        string url = uri.IsAbsoluteUri ? uri.AbsoluteUri : uri.OriginalString;
        int fragmentStart = url.IndexOf('#');
        if (fragmentStart < 0)
            fragmentStart = url.Length;
        ReadOnlySpan<char> beforeFragment = url.AsSpan(0, fragmentStart);

        var builder = new DefaultInterpolatedStringHandler(0, 0, CultureInfo.InvariantCulture, stackalloc char[512]);
        builder.AppendFormatted(beforeFragment);

        // no separator right after a bare '?' or a trailing '&'
        string separator = !beforeFragment.Contains('?') ? "?" : beforeFragment[^1] is '?' or '&' ? "" : "&";
        foreach (ManifestInfoElementDownloadQueryParams queryParam in queryParams)
        {
            builder.AppendLiteral(separator);
            builder.AppendFormatted(queryParam.Name);
            if (queryParam.Value is { } value)
            {
                builder.AppendLiteral("=");
                builder.AppendFormatted(value);
            }
            separator = "&";
        }

        builder.AppendFormatted(url.AsSpan(fragmentStart));
        return new Uri(builder.ToStringAndClear(), UriKind.RelativeOrAbsolute);
    }

    /// <returns>
    /// The path of the cache file in <paramref name="cacheDirectory"/>, or <see langword="null"/> without a directory or when the
    /// URI's path has no file name.
    /// </returns>
    private static string? GetCachePath(string? cacheDirectory, Uri uri)
    {
        if (cacheDirectory is null)
            return null;

        ReadOnlySpan<char> fileName = GetCacheFileName(uri);
        return fileName.IsEmpty ? null : Path.Join(cacheDirectory.AsSpan(), fileName);
    }

    /// <summary>
    /// The name of the cache file of a manifest: the last segment of the URI's path. A query string or fragment, such as the token
    /// of a signed URL, is not part of it.
    /// </summary>
    internal static ReadOnlySpan<char> GetCacheFileName(Uri uri)
    {
        ReadOnlySpan<char> path;
        if (uri.IsAbsoluteUri)
        {
            path = uri.AbsolutePath;
        }
        else
        {
            path = uri.OriginalString;
            int end = path.IndexOfAny('?', '#');
            if (end >= 0)
                path = path[..end];
        }

        return path[(path.LastIndexOf('/') + 1)..];
    }
}

/// <summary/>
public class ManifestInfoElement
{
    /// <summary/>
    public required string AppName { get; set; }
    /// <summary/>
    public required string LabelName { get; set; }
    /// <summary/>
    public required string BuildVersion { get; set; }
    /// <summary/>
    public FSHAHash Hash { get; set; }
    /// <summary/>
    public bool UseSignedUrl { get; set; }
    /// <summary/>
    public Dictionary<string, object>? Metadata { get; set; }
    /// <summary/>
    public required List<ManifestInfoElementDownload> Manifests { get; set; }
    /// <summary/>
    public List<ManifestInfoElementDownload>? SelectiveDownload { get; set; }
    /// <summary/>
    public bool IsPreloaded { get; set; }

    /// <inheritdoc cref="ManifestExtensions.TryParseVersionAndCL"/>
    public bool TryParseVersionAndCL([NotNullWhen(true)] out Version? version, out int cl) =>
        ManifestExtensions.TryParseVersionAndCL(BuildVersion, out version, out cl);
}

/// <summary/>
public class ManifestInfoElementDownload
{
    /// <summary/>
    public required Uri Uri { get; set; }
    /// <summary/>
    public List<ManifestInfoElementDownloadQueryParams>? QueryParams { get; set; }
}

/// <summary/>
public class ManifestInfoElementDownloadQueryParams
{
    /// <summary/>
    public required string Name { get; set; }
    /// <summary/>
    public required string Value { get; set; }
}


/// <summary>
/// Source generated JSON parsers for <see cref="ManifestInfo"/>
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, Converters = [ typeof(FHashConverter<FSHAHash>) ])]
[JsonSerializable(typeof(ManifestInfo))]
public partial class EpicManifestParserJsonContext : JsonSerializerContext;


/// <summary>
/// Extension methods for manifest related things
/// </summary>
public static partial class ManifestExtensions
{
    [GeneratedRegex(@"(\d+(?:\.\d+)+)-CL-(\d+)", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    internal static partial Regex VersionAndClRegex();

    /// <summary>
    /// Attempts to parse <paramref name="version"/> and <paramref name="cl"/> from the <paramref name="buildVersion"/>.
    /// </summary>
    /// <param name="buildVersion"></param>
    /// <param name="version"></param>
    /// <param name="cl"></param>
    /// <returns>
    /// <see langword="true"/> if <paramref name="buildVersion" /> was successfully parsed; otherwise, <see langword="false"/>
    /// with <paramref name="version"/> <see langword="null"/> and <paramref name="cl"/> <c>-1</c>, also when the version has more
    /// than four components or a number does not fit in an <see cref="int"/>.
    /// </returns>
    public static bool TryParseVersionAndCL(string buildVersion, [NotNullWhen(true)] out Version? version, out int cl)
    {
        version = null;
        cl = -1;
        if (string.IsNullOrEmpty(buildVersion))
            return false;

        Match match = VersionAndClRegex().Match(buildVersion);
        if (!match.Success
            || !Version.TryParse(match.Groups[1].ValueSpan, out Version? parsedVersion)
            || !int.TryParse(match.Groups[2].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out int parsedCl))
        {
            return false;
        }

        version = parsedVersion;
        cl = parsedCl;
        return true;
    }

    /// <summary>
    /// Reads the HTTP content and returns the value that results from deserializing the content as JSON in an asynchronous operation.
    /// </summary>
    /// <param name="content">The content to read from.</param>
    /// <param name="cancellationToken">A cancellation token that can be used by other objects or threads to receive notice of cancellation.</param>
    /// <returns>The task object representing the asynchronous operation.</returns>
    public static Task<ManifestInfo?> ReadManifestInfoAsync(this HttpContent content, CancellationToken cancellationToken = default)
        => content.ReadFromJsonAsync(EpicManifestParserJsonContext.Default.ManifestInfo, cancellationToken);
}
