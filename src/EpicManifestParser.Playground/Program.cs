using System.Diagnostics;

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;

using EpicManifestParser;
using EpicManifestParser.Api;
using EpicManifestParser.UE;

using OffiUtils;

#if !DEBUG
if (true)
{
    var config = ManualConfig.CreateEmpty()
        .AddLogger(new BenchmarkDotNet.Loggers.ConsoleLogger(unicodeSupport: true))
        .AddColumnProvider(BenchmarkDotNet.Columns.DefaultColumnProviders.Instance);

    BenchmarkDotNet.Running.BenchmarkRunner.Run<Benchmarks>(config);
    return;
}
#endif

//await TestEncryptedManifest();
await Test1();

return;

static async Task TestEncryptedManifest()
{
    Console.WriteLine("Loading manifest bytes...");
    //var manifestBuffer = await File.ReadAllBytesAsync(Path.Combine(Benchmarks.DownloadsDir, "0230d0150e9f45d49dce401e1103c9fc_Windows_1.0.0.82.manifest"));
    byte[] manifestBuffer = await File.ReadAllBytesAsync(Path.Combine(Benchmarks.DownloadsDir, "0230d0150e9f45d49dce401e1103c9fc_Windows_1.0.0.89.manifest"));
    Console.WriteLine("Deserializing manifest...");
    var manifest = FBuildPatchAppManifest.Deserialize(manifestBuffer);
}

static async Task Test1()
{
    var options = new ManifestParseOptions
    {
        // use https for HTTP3 performance gains
        ChunkBaseUrl = "https://egdownload.fastly-edge.com/Builds/Fortnite/CloudDir/",
        ChunkCacheDirectory = Directory.CreateDirectory(Path.Combine(Benchmarks.DownloadsDir, "chunks_v2")).FullName,
        ManifestCacheDirectory = Directory.CreateDirectory(Path.Combine(Benchmarks.DownloadsDir, "manifests_v2")).FullName,
        Decompressor = DecompressorBuilder.Default.Build()
    };

    var client = options.Client = ManifestParseOptions.CreateDefaultClient();

    // ++Fortnite+Release-41.00-CL-54618515-Windows UcaaeP2Bi8ObrwuT60SrYiVf3NGxXA.manifest
    using var manifestResponse = await client.GetAsync("https://media.wtf/EcyB.json");
    var manifestInfo1 = await manifestResponse.Content.ReadManifestInfoAsync();
    //var manifestInfo2 = await ManifestInfo.DeserializeFileAsync(Benchmarks.ManifestInfoPath);

    var manifestInfoTuple = await manifestInfo1!.DownloadAndParseAsync(options);
    bool parseResult = manifestInfoTuple.InfoElement.TryParseVersionAndCL(out var infoVersion, out int infoCl);

    //var randomGuid = FGuid.Random();
    //var chunkGuid = new FGuid("A76EAD354E9F6F06D0E75CAC2AB1B56C");

    var sw = Stopwatch.StartNew();

    //var manifest = await FBuildPatchAppManifest.DeserializeFileAsync(Benchmarks.ManifestPath, options);
    //sw.Stop();
    //Console.WriteLine(Math.Round(sw.Elapsed.TotalMilliseconds, 0));

    var manifest = manifestInfoTuple.Manifest;

    {
        var fileManifest = manifest.Files.First(x =>
            x.FileName.EndsWith("/pakchunk0optional-WindowsClient.ucas", StringComparison.Ordinal));
        string fileManifestFileName = Path.GetFileName(fileManifest.FileName);
        var fileManifestStream = fileManifest.GetStream();

        await fileManifestStream.SaveFileAsync(Path.Combine(Benchmarks.DownloadsDir, fileManifestFileName));

        byte[] fileBuffer = await fileManifestStream.SaveBytesAsync();
        Console.WriteLine($"{fileManifest.SHA1Hash} / {FSHAHash.Compute(fileBuffer)}");

        sw.Restart();
        fileBuffer = new byte[fileManifest.FileSize];
        await fileManifestStream.SaveBytesAsync(fileBuffer, ProgressCallback, fileManifestFileName);
        //await fileManifestStream.SaveToAsync(new MemoryStream(fileBuffer, 0, fileBuffer.Length, true, true), ProgressCallback, fileManifestFileName);
        sw.Stop();
        Console.WriteLine($"{fileManifest.SHA1Hash} / {FSHAHash.Compute(fileBuffer)}");
    }

    Console.ReadLine();
}

static void ProgressCallback(SaveProgressChangedEventArgs eventArgs)
{
    string text = (string)eventArgs.UserState!;
    Console.WriteLine($"{text}: {eventArgs.ProgressPercentage}% ({eventArgs.TotalBytesWritten}/{eventArgs.TotalBytesToWrite} | {eventArgs.BytesWritten})");
}

static string ToHumanReadable(long bytes)
{
    double size = bytes;
    int unit = 0;

    while (size >= 1024 && unit < 4)
    {
        size /= 1024;
        unit++;
    }

    return $"{size:0.##} {GetUnit(unit)}";

    static string GetUnit(int idx) => idx switch
    {
        0 => "B",
        1 => "KB",
        2 => "MB",
        3 => "GB",
        4 => "TB",
        _ => ""
    };
}

[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
[BaselineColumn]
[MemoryDiagnoser(false)]
[SimpleJob(RuntimeMoniker.Net10_0)]
[HideColumns("Error", "StdDev", "RatioSD")]
public class Benchmarks
{
    public static string DownloadsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    public static string ManifestPath = Path.Combine(DownloadsDir, "1je7ZywtIIpGkkWcFE6eff_sKRZ9LQ.manifest");
    public static string ZlibngPath = Path.Combine(DownloadsDir, "zlib-ng2.dll");
    public static string ManifestInfoPath = Path.Combine(DownloadsDir, "manifestinfo.json");

    public static string TestChunkPath = Path.Combine(DownloadsDir, "8ED2116F187190BA_996E9BFD428888C4627AE6B1153404C3.chunk");

    private const string TestChunkUrlFormat =
        "https://{0}/Builds/Fortnite/CloudDir/ChunksV4/45/2A39A5346809495C_C74EA3B44F9943B475BB72A41F8E68DD.chunk";

    private byte[] _manifestBuffer = null!;
    private byte[] _testChunkBuffer = null!;
    private byte[] _testTempBuffer = null!;
    private byte[] _manifestInfoBuffer = null!;
    private HttpClient _client = null!;
    private FBuildPatchAppManifest _manifest = null!;
    private FFileManifestStream _fileManifestStream1 = null!;
    private FFileManifestStream _fileManifestStream2 = null!;
    private byte[] _fileBuffer = null!;
    private MemoryStream _fileMs = null!;
    private string _filePath = null!;
    private FGuid _guid;

    private IDecompressor _decompressor = null!;

    [GlobalSetup]
    public void Setup()
    {
        _guid = FGuid.Random();
        _testTempBuffer = new byte[10000000];
        _testChunkBuffer = File.ReadAllBytes(TestChunkPath);

        _manifestBuffer = File.ReadAllBytes(ManifestPath);
        _manifestInfoBuffer = File.ReadAllBytes(ManifestInfoPath);

        _decompressor = DecompressorBuilder.Default.Build();
        _client = ManifestParseOptions.CreateDefaultClient();
        _manifest = FBuildPatchAppManifest.Deserialize(_manifestBuffer, options =>
        {
            options.ChunkBaseUrl = "https://egdownload.fastly-edge.com/Builds/Fortnite/CloudDir/";
            options.ChunkCacheDirectory = Directory.CreateDirectory(Path.Combine(DownloadsDir, "chunks_v2")).FullName;
            options.Client = _client;
            options.Decompressor = _decompressor;
        });
        var fileManifest = _manifest.Files.First(x =>
            x.FileName.EndsWith("/pakchunk0optional-WindowsClient.ucas", StringComparison.Ordinal));
        _filePath = Path.Combine(DownloadsDir, Path.GetFileName(fileManifest.FileName));
        _fileBuffer = new byte[fileManifest.FileSize];
        _fileMs = new MemoryStream(_fileBuffer, 0, _fileBuffer.Length, true, true);
        _fileManifestStream1 = fileManifest.GetStream();
        _fileManifestStream2 = fileManifest.GetStream();
    }

    [Params(
        "egs-cloudfront-chunks.epicgamescdn.com",
        "epicgames-download1.akamaized.net",
        "egdownload.fastly-edge.com"
        )]
    public string ChunkHost { get; set; }

    [Params(HttpCompletionOption.ResponseContentRead, HttpCompletionOption.ResponseHeadersRead)]
    public HttpCompletionOption CompletionOption { get; set; }

    [Benchmark]
    public async Task<int> DownloadChunk()
    {
        var chunkUri = new Uri(string.Format(TestChunkUrlFormat, ChunkHost), UriKind.Absolute);
        using var res = await _client.GetAsync(chunkUri, CompletionOption).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
        var destMs = new MemoryStream(_testTempBuffer, 0, _testTempBuffer.Length, true, true);
        await res.Content.CopyToAsync(destMs).ConfigureAwait(false);
        int responseSize = (int)destMs.Position;

        var header = FChunkHeader.Parse(new Span<byte>(_testTempBuffer, 0, responseSize));
        return header.DataSizeUncompressed;
    }

    /*[Benchmark, BenchmarkCategory("Deserialize")]
    public FBuildPatchAppManifest FBuildPatchAppManifest_Deserialize()
    {
        return FBuildPatchAppManifest.Deserialize(_manifestBuffer);
    }

    [Benchmark, BenchmarkCategory("Deserialize")]
    public ManifestInfo? ManifestInfo_Deserialize()
    {
        return ManifestInfo.Deserialize(_manifestInfoBuffer);
    }*/

    /*[BenchmarkCategory("SaveBuffer"), Benchmark(Baseline = true)]
    public async Task FFileManifestStream_SaveBuffer()
    {
        await _fileManifestStream2.SaveBytesAsync(_fileBuffer);
    }

    [BenchmarkCategory("SaveBuffer"), Benchmark]
    public async Task FFileManifestStream_SaveBuffer_AsIs()
    {
        await _fileManifestStream1.SaveBytesAsync(_fileBuffer);
    }*/

    //[BenchmarkCategory("SaveFile"), Benchmark(Baseline = true)]
    //public async Task FFileManifestStream_SaveFile()
    //{
    //    await _fileManifestStream2.SaveFileAsync(_filePath);
    //}

    //[BenchmarkCategory("SaveFile"), Benchmark]
    //public async Task FFileManifestStream_SaveFile_AsIs()
    //{
    //    await _fileManifestStream1.SaveFileAsync(_filePath);
    //}

    /*[BenchmarkCategory("SaveStream"), Benchmark(Baseline = true)]
    public async Task FFileManifestStream_SaveStream()
    {
        _fileMs.Position = 0;
        await _fileManifestStream2.SaveToAsync(_fileMs);
    }

    [BenchmarkCategory("SaveStream"), Benchmark]
    public async Task FFileManifestStream_SaveStream_AsIs()
    {
        _fileMs.Position = 0;
        await _fileManifestStream1.SaveToAsync(_fileMs);
    }*/
}
