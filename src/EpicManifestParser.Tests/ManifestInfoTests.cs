using System.Net;

using EpicManifestParser.Api;
using EpicManifestParser.Tests.Fixtures;
using EpicManifestParser.UE;

namespace EpicManifestParser.Tests;

public class ManifestInfoTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string ManifestPath = "/manifests/test.manifest";

    private const string Json =
        """
        {
          "elements": [
            {
              "appName": "Fortnite",
              "labelName": "Live-Windows",
              "buildVersion": "++Fortnite+Release-43.00-CL-59596594-Windows",
              "hash": "03c5790a5c61421d2ed856146c37cf90df6cf38f",
              "useSignedUrl": false,
              "isPreloaded": false,
              "manifests": [
                {
                  "uri": "https://cdn.test/manifests/test.manifest",
                  "queryParams": [ { "name": "token", "value": "abc" } ]
                }
              ]
            }
          ]
        }
        """;

    [Fact]
    public async Task DeserializeFileAsync_returns_the_parsed_object()
    {
        using var dir = new TempDirectory();
        string path = Path.Combine(dir.Path, "info.json");
        await File.WriteAllTextAsync(path, Json, Ct);

        for (int i = 0; i < 20; i++)
        {
            ManifestInfo? info = await ManifestInfo.DeserializeFileAsync(path, Ct);

            Assert.NotNull(info);
            ManifestInfoElement element = Assert.Single(info.Elements);
            Assert.Equal("Fortnite", element.AppName);
            Assert.Equal("++Fortnite+Release-43.00-CL-59596594-Windows", element.BuildVersion);
            Assert.Equal("03C5790A5C61421D2ED856146C37CF90DF6CF38F", element.Hash.ToString());
            ManifestInfoElementDownload download = Assert.Single(element.Manifests);
            Assert.Equal(new Uri("https://cdn.test/manifests/test.manifest"), download.Uri);
            Assert.Equal("token", Assert.Single(download.QueryParams!).Name);
        }

        FileAssert.IsClosed(path);
    }

    [Fact]
    public async Task DownloadAndParseAsync_retries_and_writes_the_manifest_cache_atomically()
    {
        using var dir = new TempDirectory();
        var cdn = new FakeCdn
        {
            Behavior = static (request, _) => Task.FromResult(request.Attempt == 1 ? FakeCdn.Status(HttpStatusCode.ServiceUnavailable) : null)
        };
        cdn.AddBlob(ManifestPath, TestData.ManifestBytes);
        using HttpClient client = cdn.CreateClient();
        ManifestInfo info = ManifestInfo.Deserialize(System.Text.Encoding.UTF8.GetBytes(Json))!;
        var options = new ManifestParseOptions
        {
            Client = client,
            ManifestCacheDirectory = dir.Path,
            DownloadRetryBaseDelay = TimeSpan.Zero
        };

        (FBuildPatchAppManifest manifest, _) = await info.DownloadAndParseAsync(options, cancellationToken: Ct);

        Assert.NotNull(manifest.FindFile(TestData.FileName));
        Assert.Equal(2, cdn.Requests);
        string cacheFile = Assert.Single(dir.Files);
        Assert.Equal("test.manifest", Path.GetFileName(cacheFile));
        Assert.Equal(TestData.ManifestBytes, await File.ReadAllBytesAsync(cacheFile, Ct));

        // served from the cache
        (FBuildPatchAppManifest cached, _) = await info.DownloadAndParseAsync(options, cancellationToken: Ct);
        Assert.Equal(manifest.Files.Count, cached.Files.Count);
        Assert.Equal(2, cdn.Requests);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("half")]
    [InlineData("last-byte-missing")]
    public async Task DownloadAndParseAsync_replaces_a_cached_manifest_that_does_not_parse(string truncation)
    {
        using var dir = new TempDirectory();
        string cacheFile = Path.Combine(dir.Path, "test.manifest");
        await File.WriteAllBytesAsync(cacheFile, Truncate(TestData.ManifestBytes, truncation), Ct);
        var cdn = new FakeCdn();
        cdn.AddBlob(ManifestPath, TestData.ManifestBytes);
        using HttpClient client = cdn.CreateClient();

        (FBuildPatchAppManifest manifest, _) = await CreateInfo().DownloadAndParseAsync(CreateOptions(client, dir.Path), cancellationToken: Ct);

        Assert.NotNull(manifest.FindFile(TestData.FileName));
        Assert.Equal(1, cdn.Requests);
        Assert.Equal(cacheFile, Assert.Single(dir.Files));
        Assert.Equal(TestData.ManifestBytes, await File.ReadAllBytesAsync(cacheFile, Ct));
    }

    [Fact]
    public async Task DownloadAndParseAsync_uses_a_valid_cached_manifest_without_a_request()
    {
        using var dir = new TempDirectory();
        await File.WriteAllBytesAsync(Path.Combine(dir.Path, "test.manifest"), TestData.ManifestBytes, Ct);
        var cdn = new FakeCdn();
        cdn.AddBlob(ManifestPath, TestData.ManifestBytes);
        using HttpClient client = cdn.CreateClient();

        (FBuildPatchAppManifest manifest, _) = await CreateInfo().DownloadAndParseAsync(CreateOptions(client, dir.Path), cancellationToken: Ct);

        Assert.NotNull(manifest.FindFile(TestData.FileName));
        Assert.Equal(0, cdn.Requests);
    }

    [Fact]
    public async Task DownloadAndParseAsync_retries_a_download_that_does_not_parse_and_throws_when_none_does()
    {
        using var dir = new TempDirectory();
        byte[] truncated = Truncate(TestData.ManifestBytes, "half");
        await File.WriteAllBytesAsync(Path.Combine(dir.Path, "test.manifest"), truncated, Ct);
        var cdn = new FakeCdn();
        // complete as far as HTTP is concerned (Content-Length matches), but not a manifest
        cdn.AddBlob(ManifestPath, truncated);
        using HttpClient client = cdn.CreateClient();
        ManifestParseOptions options = CreateOptions(client, dir.Path);
        options.MaxDownloadRetries = 2;

        Exception ex = await Assert.ThrowsAnyAsync<Exception>(() => CreateInfo().DownloadAndParseAsync(options, cancellationToken: Ct));

        Assert.True(ex is InvalidDataException or FileLoadException, $"{ex.GetType().Name}: {ex.Message}");
        // the cached file is read once and discarded, the download is a corrupt response like any other
        Assert.Equal(3, cdn.Requests);
        Assert.Equal(3, ex.Data["Attempts"]);
        // the broken cache file is gone and the broken download was not cached
        Assert.Empty(dir.Files);
    }

    [Theory]
    [InlineData("half")]
    [InlineData("last-byte-missing")]
    public async Task DownloadAndParseAsync_downloads_again_when_a_download_does_not_parse(string truncation)
    {
        using var dir = new TempDirectory();
        byte[] truncated = Truncate(TestData.ManifestBytes, truncation);
        var cdn = new FakeCdn
        {
            Behavior = (request, _) => Task.FromResult(request.Attempt == 1 ? FakeCdn.Ok(truncated) : null)
        };
        cdn.AddBlob(ManifestPath, TestData.ManifestBytes);
        using HttpClient client = cdn.CreateClient();

        (FBuildPatchAppManifest manifest, _) = await CreateInfo().DownloadAndParseAsync(CreateOptions(client, dir.Path), cancellationToken: Ct);

        Assert.NotNull(manifest.FindFile(TestData.FileName));
        Assert.Equal(2, cdn.Requests);
        Assert.Equal(TestData.ManifestBytes, await File.ReadAllBytesAsync(Path.Combine(dir.Path, "test.manifest"), Ct));
    }

    [Fact]
    public async Task DownloadAndParseAsync_downloads_again_when_a_JSON_manifest_does_not_parse()
    {
        byte[] json = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "files", "manifest.json"), Ct);
        // a response cut short: invalid JSON is a corrupt response like a truncated binary manifest
        var cdn = new FakeCdn { Behavior = static (request, _) => Task.FromResult(request.Attempt == 1 ? FakeCdn.Ok("{\"FileManifestList\":["u8.ToArray()) : null) };
        cdn.AddBlob(ManifestPath, json);
        using HttpClient client = cdn.CreateClient();

        (FBuildPatchAppManifest manifest, _) = await CreateInfo().DownloadAndParseAsync(
            new ManifestParseOptions { Client = client, DownloadRetryBaseDelay = TimeSpan.Zero }, cancellationToken: Ct);

        Assert.NotEmpty(manifest.Files);
        Assert.Equal(2, cdn.Requests);
    }

    [Fact]
    public async Task DownloadAndParseAsync_reports_invalid_JSON_as_invalid_data_when_no_retry_is_left()
    {
        var cdn = new FakeCdn();
        cdn.AddBlob(ManifestPath, "{\"FileManifestList\":["u8.ToArray());
        using HttpClient client = cdn.CreateClient();
        var options = new ManifestParseOptions { Client = client, MaxDownloadRetries = 0 };

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => CreateInfo().DownloadAndParseAsync(options, cancellationToken: Ct));

        Assert.IsAssignableFrom<System.Text.Json.JsonException>(ex.InnerException);
        Assert.Equal(1, cdn.Requests);
    }

    [Fact]
    public async Task DownloadAndParseAsync_downloads_when_the_cache_file_cannot_be_opened()
    {
        using var dir = new TempDirectory();
        string cacheFile = Path.Combine(dir.Path, "test.manifest");
        await File.WriteAllBytesAsync(cacheFile, TestData.ManifestBytes, Ct);
        var cdn = new FakeCdn();
        cdn.AddBlob(ManifestPath, TestData.ManifestBytes);
        using HttpClient client = cdn.CreateClient();

        // held like by another call that replaces the file right now
        using (new FileStream(cacheFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            (FBuildPatchAppManifest manifest, _) = await CreateInfo().DownloadAndParseAsync(CreateOptions(client, dir.Path), cancellationToken: Ct);
            Assert.NotNull(manifest.FindFile(TestData.FileName));
        }

        Assert.Equal(1, cdn.Requests);
        Assert.Equal(TestData.ManifestBytes, await File.ReadAllBytesAsync(cacheFile, Ct));
    }

    [Fact]
    public async Task Concurrent_calls_recover_a_corrupt_cache_file_without_failing()
    {
        using var dir = new TempDirectory();
        await File.WriteAllBytesAsync(Path.Combine(dir.Path, "test.manifest"), Truncate(TestData.ManifestBytes, "half"), Ct);
        var cdn = new FakeCdn();
        cdn.AddBlob(ManifestPath, TestData.ManifestBytes);
        using HttpClient client = cdn.CreateClient();
        ManifestInfo info = CreateInfo();

        // each call may find the corrupt file, delete it, or replace it while another one reads it
        (FBuildPatchAppManifest Manifest, ManifestInfoElement)[] results = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => info.DownloadAndParseAsync(CreateOptions(client, dir.Path), cancellationToken: Ct), Ct)));

        // which call wins the file is open: a later run reads or replaces whatever is left
        Assert.All(results, result => Assert.NotNull(result.Manifest.FindFile(TestData.FileName)));
    }

    [Theory]
    [InlineData("https://cdn.test/manifests/test.manifest?token=abc")]
    [InlineData("https://cdn.test/manifests/test.manifest#fragment")]
    [InlineData("https://cdn.test/manifests/test.manifest?token=a/b&x=y#fragment")]
    public async Task DownloadAndParseAsync_names_the_cache_file_after_the_path_of_the_uri(string uri)
    {
        using var dir = new TempDirectory();
        var cdn = new FakeCdn();
        cdn.AddBlob(ManifestPath, TestData.ManifestBytes);
        using HttpClient client = cdn.CreateClient();
        ManifestInfo info = CreateInfo();
        info.Elements[0].Manifests[0].Uri = new Uri(uri);
        info.Elements[0].Manifests[0].QueryParams = null;

        await info.DownloadAndParseAsync(CreateOptions(client, dir.Path), cancellationToken: Ct);
        (FBuildPatchAppManifest cached, _) = await info.DownloadAndParseAsync(CreateOptions(client, dir.Path), cancellationToken: Ct);

        Assert.Equal(Path.Combine(dir.Path, "test.manifest"), Assert.Single(dir.Files));
        Assert.NotNull(cached.FindFile(TestData.FileName));
        Assert.Equal(1, cdn.Requests);
    }

    [Theory]
    [InlineData("https://cdn.test/a/b.manifest?x=1", "b.manifest")]
    [InlineData("https://cdn.test/b.manifest", "b.manifest")]
    [InlineData("https://cdn.test/a/", "")]
    [InlineData("b.manifest?x=1#y", "b.manifest")]
    public void The_cache_file_name_is_the_last_segment_of_the_path(string uri, string expected)
    {
        Assert.Equal(expected, ManifestInfo.GetCacheFileName(new Uri(uri, UriKind.RelativeOrAbsolute)).ToString());
    }

    private const string Base = "https://cdn.test/a/b.manifest";

    /// <summary>
    /// What Flurl's <c>AppendQueryParam(name, value, isEncoded: true, NullValueHandling.NameOnly)</c> made of these, which the
    /// library used before; only after a trailing <c>&amp;</c> Flurl added a second one. Names and values are not escaped:
    /// Akamai rejects its <c>ak_token</c> with the <c>=</c> as <c>%3D</c>. Only <see cref="Uri"/> escapes what a URI cannot contain.
    /// </summary>
    public static TheoryData<string, string, string?, string> QueryParamCases => new()
    {
        { Base, "t", "exp=1~hmac=ab", Base + "?t=exp=1~hmac=ab" },
        { Base, "t", null, Base + "?t" },
        { Base, "t", "", Base + "?t=" },
        { Base, "t", "a b", Base + "?t=a%20b" },
        { Base, "t", "a+b", Base + "?t=a+b" },
        { Base, "t", "a%20b", Base + "?t=a%20b" },
        { Base, "t", "a%zz", Base + "?t=a%25zz" },
        { Base, "t", "a&b=c", Base + "?t=a&b=c" },
        { Base, "t", "a#b", Base + "?t=a#b" },
        { Base, "t", "a?b", Base + "?t=a?b" },
        { Base, "t", "ä€", Base + "?t=%C3%A4%E2%82%AC" },
        { Base, "t", "a/b:c@d", Base + "?t=a/b:c@d" },
        { Base, "n m", "v", Base + "?n%20m=v" },
        { Base, "n&m", "v", Base + "?n&m=v" },
        { Base, "t", "a\"<>b", Base + "?t=a%22%3C%3Eb" },
        { Base + "?x=1", "t", "exp=1~hmac=ab", Base + "?x=1&t=exp=1~hmac=ab" },
        { Base + "?x=1", "t", null, Base + "?x=1&t" },
        { Base + "?x=1", "t", "a b", Base + "?x=1&t=a%20b" },
        { Base + "#frag", "t", "exp=1~hmac=ab", Base + "?t=exp=1~hmac=ab#frag" },
        { Base + "#frag", "t", null, Base + "?t#frag" },
        { Base + "#frag", "t", "a#b", Base + "?t=a#b#frag" },
        { Base + "?", "t", "exp=1~hmac=ab", Base + "?t=exp=1~hmac=ab" },
        { Base + "?", "t", null, Base + "?t" },
        { Base + "?x=1#frag", "t", "exp=1~hmac=ab", Base + "?x=1&t=exp=1~hmac=ab#frag" },
        { Base + "?x=1#frag", "t", null, Base + "?x=1&t#frag" },
        { Base + "?x=1&", "t", "exp=1~hmac=ab", Base + "?x=1&t=exp=1~hmac=ab" },
        { "b.manifest", "t", "exp=1~hmac=ab", "b.manifest?t=exp=1~hmac=ab" },
        { "b.manifest", "t", "a b", "b.manifest?t=a b" }
    };

    [Theory]
    [MemberData(nameof(QueryParamCases))]
    public void Query_params_are_appended_as_they_are(string uri, string name, string? value, string expected)
    {
        Uri result = ManifestInfo.AppendQueryParams(new Uri(uri, UriKind.RelativeOrAbsolute), [new() { Name = name, Value = value! }]);

        Assert.Equal(expected, result.IsAbsoluteUri ? result.AbsoluteUri : result.OriginalString);
    }

    [Fact]
    public void Several_query_params_are_appended_in_order()
    {
        Uri result = ManifestInfo.AppendQueryParams(new Uri(Base),
        [
            new() { Name = "a", Value = "1" },
            new() { Name = "b", Value = null! },
            new() { Name = "ak_token", Value = "exp=1794485819~hmac=0123abcd" }
        ]);

        Assert.Equal(Base + "?a=1&b&ak_token=exp=1794485819~hmac=0123abcd", result.AbsoluteUri);
    }

    [Fact]
    public void Without_query_params_the_uri_is_used_as_it_is()
    {
        var uri = new Uri(Base + "?x=1");

        Assert.Same(uri, ManifestInfo.AppendQueryParams(uri, null));
        Assert.Same(uri, ManifestInfo.AppendQueryParams(uri, []));
    }

    [Fact]
    public async Task DownloadAndParseAsync_requests_the_manifest_with_its_query_params()
    {
        var cdn = new FakeCdn();
        cdn.AddBlob(ManifestPath, TestData.ManifestBytes);
        using HttpClient client = cdn.CreateClient();
        ManifestInfo info = CreateInfo();
        info.Elements[0].Manifests[0].QueryParams = [new() { Name = "ak_token", Value = "exp=1794485819~hmac=0123abcd" }];

        (FBuildPatchAppManifest manifest, _) = await info.DownloadAndParseAsync(new ManifestParseOptions { Client = client }, cancellationToken: Ct);

        Assert.NotNull(manifest.FindFile(TestData.FileName));
        Uri requested = Assert.Single(cdn.RequestUris);
        Assert.Equal(ManifestPath + "?ak_token=exp=1794485819~hmac=0123abcd", requested.PathAndQuery);
    }

    [Fact]
    public async Task DownloadAndParseAsync_creates_a_missing_cache_directory()
    {
        using var dir = new TempDirectory();
        string cacheDirectory = Path.Combine(dir.Path, "not", "there");
        var cdn = new FakeCdn();
        cdn.AddBlob(ManifestPath, TestData.ManifestBytes);
        using HttpClient client = cdn.CreateClient();

        await CreateInfo().DownloadAndParseAsync(CreateOptions(client, cacheDirectory), cancellationToken: Ct);
        (FBuildPatchAppManifest cached, _) = await CreateInfo().DownloadAndParseAsync(CreateOptions(client, cacheDirectory), cancellationToken: Ct);

        Assert.Equal(TestData.ManifestBytes, await File.ReadAllBytesAsync(Path.Combine(cacheDirectory, "test.manifest"), Ct));
        Assert.NotNull(cached.FindFile(TestData.FileName));
        Assert.Equal(1, cdn.Requests);
    }

    [Theory]
    [InlineData("directory-at-the-file-path")]
    [InlineData("file-at-the-directory-path")]
    public async Task DownloadAndParseAsync_returns_the_manifest_when_the_cache_cannot_be_written(string obstacle)
    {
        using var dir = new TempDirectory();
        string cacheDirectory = dir.Path;
        if (obstacle == "directory-at-the-file-path")
        {
            Directory.CreateDirectory(Path.Combine(dir.Path, "test.manifest"));
        }
        else
        {
            cacheDirectory = Path.Combine(dir.Path, "manifests");
            await File.WriteAllBytesAsync(cacheDirectory, [1, 2, 3], Ct);
        }
        var cdn = new FakeCdn();
        cdn.AddBlob(ManifestPath, TestData.ManifestBytes);
        using HttpClient client = cdn.CreateClient();

        (FBuildPatchAppManifest manifest, _) = await CreateInfo().DownloadAndParseAsync(CreateOptions(client, cacheDirectory), cancellationToken: Ct);

        Assert.NotNull(manifest.FindFile(TestData.FileName));
        Assert.Equal(1, cdn.Requests);
        Assert.Empty(Directory.GetFiles(dir.Path, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task DownloadAndParseAsync_cancellation_does_not_discard_the_cached_manifest()
    {
        using var dir = new TempDirectory();
        string cacheFile = Path.Combine(dir.Path, "test.manifest");
        byte[] truncated = Truncate(TestData.ManifestBytes, "half");
        await File.WriteAllBytesAsync(cacheFile, truncated, Ct);
        var cdn = new FakeCdn();
        cdn.AddBlob(ManifestPath, TestData.ManifestBytes);
        using HttpClient client = cdn.CreateClient();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateInfo().DownloadAndParseAsync(CreateOptions(client, dir.Path), cancellationToken: cts.Token));

        Assert.Equal(0, cdn.Requests);
        Assert.Equal(truncated, await File.ReadAllBytesAsync(cacheFile, Ct));
    }

    [Fact]
    public async Task DownloadAndParseAsync_does_not_retry_404()
    {
        var cdn = new FakeCdn();
        using HttpClient client = cdn.CreateClient();
        ManifestInfo info = ManifestInfo.Deserialize(System.Text.Encoding.UTF8.GetBytes(Json))!;
        var options = new ManifestParseOptions { Client = client, DownloadRetryBaseDelay = TimeSpan.Zero };

        HttpRequestException ex = await Assert.ThrowsAsync<HttpRequestException>(() => info.DownloadAndParseAsync(options, cancellationToken: Ct));

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        Assert.Equal(1, cdn.Requests);
        Assert.Equal(1, ex.Data["Attempts"]);
        Assert.NotNull(ex.Data["ManifestUri"]);
        Assert.NotNull(ex.Data["Element"]);
    }

    private static ManifestInfo CreateInfo() => ManifestInfo.Deserialize(System.Text.Encoding.UTF8.GetBytes(Json))!;

    private static ManifestParseOptions CreateOptions(HttpClient client, string cacheDirectory) => new()
    {
        Client = client,
        ManifestCacheDirectory = cacheDirectory,
        DownloadRetryBaseDelay = TimeSpan.Zero
    };

    /// <summary>What a cancelled <c>File.WriteAllBytesAsync</c> of an older version could leave behind.</summary>
    private static byte[] Truncate(byte[] data, string truncation) => truncation switch
    {
        "empty" => [],
        "half" => data[..(data.Length / 2)],
        "last-byte-missing" => data[..^1],
        _ => throw new ArgumentOutOfRangeException(nameof(truncation))
    };
}
