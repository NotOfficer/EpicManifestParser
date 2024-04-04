
using System.Diagnostics;
using System.Net;

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;

using EpicManifestParser;
using EpicManifestParser.Api;
using EpicManifestParser.UE;

using ZlibngDotNet;

//BenchmarkDotNet.Running.BenchmarkRunner.Run<Benchmarks>();
//return;

var client = new HttpClient(new HttpClientHandler
{
	UseCookies = false,
	UseProxy = false,
	AutomaticDecompression = DecompressionMethods.Deflate | DecompressionMethods.GZip,
	MaxConnectionsPerServer = 256
})
{
	DefaultRequestVersion = new Version(1, 1),
	DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
	Timeout = TimeSpan.FromSeconds(30)
};

var zlibng = new Zlibng(Benchmarks.ZlibngPath);

var options = new ManifestParseOptions
{
	Zlibng = zlibng,
	Client = client,
	ChunkBaseUrl = "http://cloudflare.epicgamescdn.com/Builds/Fortnite/CloudDir/",
	ChunkCacheDirectory = Directory.CreateDirectory(Path.Combine(Benchmarks.DownloadsDir, "chunks_v2")).FullName,
	ManifestCacheDirectory = Directory.CreateDirectory(Path.Combine(Benchmarks.DownloadsDir, "manifests_v2")).FullName
};

using var manifestResponse = await client.GetAsync("https://media.wtf/XlQk.json");
var manifestInfo1 = await manifestResponse.Content.ReadManifestInfoAsync();
var manifestInfo2 = await ManifestInfo.DeserializeFileAsync(Benchmarks.ManifestInfoPath);

var manifestInfoTuple = await manifestInfo2!.DownloadAndParseAsync(options);
var parseResult = manifestInfoTuple.InfoElement.TryParseVersionAndCL(out var infoVersion, out var infoCl);

var randomGuid = FGuid.Random();
var chunkGuid = new FGuid("A76EAD354E9F6F06D0E75CAC2AB1B56C");

var manifestBuffer = await File.ReadAllBytesAsync(Benchmarks.ManifestPath);

var sw = Stopwatch.StartNew();
var manifest = FBuildPatchAppManifest.Deserialize(manifestBuffer, options);
sw.Stop();
Console.WriteLine(Math.Round(sw.Elapsed.TotalMilliseconds, 0));

{
	var fileManifest = manifest.FileManifestList.First(x =>
		x.FileName.EndsWith("/pakchunk0optional-WindowsClient.ucas", StringComparison.Ordinal));
	var fileManifestFileName = Path.GetFileName(fileManifest.FileName);
	var fileManifestStream = fileManifest.GetStream(false);

	await fileManifestStream.SaveFileAsync(Path.Combine(Benchmarks.DownloadsDir, fileManifestFileName));

	var fileBuffer = await fileManifestStream.SaveBytesAsync();
	Console.WriteLine(FSHAHash.Compute(fileBuffer));

	await fileManifestStream.SaveToAsync(new MemoryStream(fileBuffer, true), ProgressCallback, fileManifestFileName);
	Console.WriteLine(FSHAHash.Compute(fileBuffer));

	static void ProgressCallback(SaveProgressChangedEventArgs<string> eventArgs)
	{
		Console.WriteLine($"{eventArgs.UserState!}: {eventArgs.ProgressPercentage}% ({eventArgs.BytesSaved}/{eventArgs.TotalBytesToSave})");
	}
}

Console.ReadLine();

[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
[MemoryDiagnoser(false), BaselineColumn]
public class Benchmarks
{
	public static string DownloadsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

	public static string ManifestPath = Path.Combine(DownloadsDir, "jDihDvwDD4VfI5Ss7Uy-BNIY91lqSw.manifest");
	//public static string ManifestPath = Path.Combine(DownloadsDir, "apk_manifest.json");
	//public static string ManifestPath = Path.Combine(DownloadsDir, "last_pc_manifest.json");
	public static string ZlibngPath = Path.Combine(DownloadsDir, "zlib-ng2.dll");
	public static string ManifestInfoPath = Path.Combine(DownloadsDir, "manifestinfo.json");

	private byte[] _manifestBuffer = null!;
	private Zlibng _zlibng = null!;
	private byte[] _manifestInfoBuffer = null!;
	private FBuildPatchAppManifest _manifest = null!;
	private FFileManifestStream _fileManifestStream1 = null!;
	private FFileManifestStream _fileManifestStream2 = null!;
	private byte[] _fileBuffer = null!;
	private MemoryStream _fileMs = null!;
	private string _filePath = null!;

	[GlobalSetup]
	public void Setup()
	{
		_manifestBuffer = File.ReadAllBytes(ManifestPath);
		_zlibng = new Zlibng(ZlibngPath);
		_manifestInfoBuffer = File.ReadAllBytes(ManifestInfoPath);

		_manifest = FBuildPatchAppManifest.Deserialize(_manifestBuffer, options =>
		{
			options.Zlibng = _zlibng;
			//options.ChunkBaseUrl = "http://download.epicgames.com/Builds/Fortnite/CloudDir/";            // 20-21 ms
			//options.ChunkBaseUrl = "http://cloudflare.epicgamescdn.com/Builds/Fortnite/CloudDir/";       // 34-36 ms
			options.ChunkBaseUrl = "http://fastly-download.epicgames.com/Builds/Fortnite/CloudDir/";     // 19-20 ms
			//options.ChunkBaseUrl = "http://epicgames-download1.akamaized.net/Builds/Fortnite/CloudDir/"; // 27-28 ms
			options.ChunkCacheDirectory = Directory.CreateDirectory(Path.Combine(DownloadsDir, "chunks_v2")).FullName;
		});
		var fileManifest = _manifest.FileManifestList.First(x =>
			x.FileName.EndsWith("/pakchunk0optional-WindowsClient.ucas", StringComparison.Ordinal));
		_filePath = Path.Combine(DownloadsDir, Path.GetFileName(fileManifest.FileName));
		_fileBuffer = new byte[fileManifest.FileSize];
		_fileMs = new MemoryStream(_fileBuffer, true);
		_fileManifestStream1 = fileManifest.GetStream();
		_fileManifestStream2 = fileManifest.GetStream(false);
	}

	//[Benchmark]
	//public FBuildPatchAppManifest FBuildPatchAppManifest_Deserialize()
	//{
	//	return FBuildPatchAppManifest.Deserialize(_manifestBuffer, options =>
	//	{
	//		options.Zlibng = _zlibng;
	//	});
	//}

	//[Benchmark]
	//public ManifestInfo? ManifestInfo_Deserialize()
	//{
	//	return ManifestInfo.Deserialize(_manifestInfoBuffer);
	//}

	[BenchmarkCategory("Buffer"), Benchmark(Baseline = true)]
	public async Task FFileManifestStream_SaveBuffer()
	{
		await _fileManifestStream2.SaveBytesAsync(_fileBuffer);
	}

	[BenchmarkCategory("Buffer"), Benchmark]
	public async Task FFileManifestStream_SaveBuffer_AsIs()
	{
		await _fileManifestStream1.SaveBytesAsync(_fileBuffer);
	}

	[BenchmarkCategory("File"), Benchmark(Baseline = true)]
	public async Task FFileManifestStream_SaveFile()
	{
		await _fileManifestStream2.SaveFileAsync(_filePath);
	}

	[BenchmarkCategory("File"), Benchmark]
	public async Task FFileManifestStream_SaveFile_AsIs()
	{
		await _fileManifestStream1.SaveFileAsync(_filePath);
	}

	[BenchmarkCategory("Stream"), Benchmark(Baseline = true)]
	public async Task FFileManifestStream_SaveStream()
	{
		_fileMs.Position = 0;
		await _fileManifestStream2.SaveToAsync(_fileMs);
	}

	[BenchmarkCategory("Stream"), Benchmark]
	public async Task FFileManifestStream_SaveStream_AsIs()
	{
		_fileMs.Position = 0;
		await _fileManifestStream1.SaveToAsync(_fileMs);
	}
}
