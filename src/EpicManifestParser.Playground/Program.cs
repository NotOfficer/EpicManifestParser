
using System.Diagnostics;
using System.Net;

using BenchmarkDotNet.Attributes;

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

var testChunk = manifest.ChunkDataList[69];
var testBuffer = new byte[testChunk.WindowSize];
await testChunk.ReadDataAsync(testBuffer, manifest);
sw.Restart();
await testChunk.ReadDataAsync(testBuffer, manifest);
sw.Stop();
Console.WriteLine(Math.Round(sw.Elapsed.TotalMilliseconds, 0));

{
	var fileManifest = manifest.FileManifestList.First(x =>
		x.Filename.EndsWith("/pakchunk0optional-WindowsClient.ucas", StringComparison.Ordinal));
	var fileManifestFileName = Path.GetFileName(fileManifest.Filename);
	var fileManifestStream = fileManifest.GetStream();

	//await fileManifestStream.SaveFileAsync(Path.Combine(Benchmarks.DownloadsDir, "pakchunk0optional-WindowsClient.ucas"));

	var fileBuffer = await fileManifestStream.SaveBytesAsync();
	FSHAHash.TryCompute(fileBuffer, out var hash);
	Console.WriteLine(hash);
	await fileManifestStream.SaveToAsync(new MemoryStream(fileBuffer, true), ProgressCallback, fileManifestFileName);

	static void ProgressCallback(SaveProgressChangedEventArgs eventArgs)
	{
		var fileName = (string)eventArgs.UserState!;
		Console.WriteLine($"{fileName}: {eventArgs.ProgressPercentage}% ({eventArgs.BytesSaved}/{eventArgs.TotalBytesToSave})");
	}
}

var chunkUrl = $"http://cloudflare.epicgamescdn.com/Builds/Fortnite/CloudDir/ChunksV4/{testChunk.GroupNumber:D2}/{testChunk.Hash:X16}_{testChunk.Guid}.chunk";
Console.WriteLine(chunkUrl);
Console.ReadLine();

[MemoryDiagnoser(false)]
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
	private FFileManifestStream _fileManifestStream = null!;
	private byte[] _fileBuffer = null!;

	[GlobalSetup]
	public void Setup()
	{
		_manifestBuffer = File.ReadAllBytes(ManifestPath);
		_zlibng = new Zlibng(ZlibngPath);
		_manifestInfoBuffer = File.ReadAllBytes(ManifestInfoPath);

		var manifest = FBuildPatchAppManifest.Deserialize(_manifestBuffer, options =>
		{
			options.Zlibng = _zlibng;
			options.ChunkBaseUrl = "http://epicgames-download1.akamaized.net/Builds/Fortnite/CloudDir/";
			options.ChunkCacheDirectory = Directory.CreateDirectory(Path.Combine(DownloadsDir, "chunks_v2")).FullName;
		});
		var fileManifest = manifest.FileManifestList.First(x =>
			x.Filename.EndsWith("/pakchunk0optional-WindowsClient.ucas", StringComparison.Ordinal));
		_fileManifestStream = fileManifest.GetStream();
		_fileBuffer = new byte[fileManifest.FileSize];
	}

	[Benchmark]
	public FBuildPatchAppManifest FBuildPatchAppManifest_Deserialize()
	{
		return FBuildPatchAppManifest.Deserialize(_manifestBuffer, options =>
		{
			options.Zlibng = _zlibng;
		});
	}

	[Benchmark]
	public ManifestInfo? ManifestInfo_Deserialize()
	{
		return ManifestInfo.Deserialize(_manifestInfoBuffer);
	}

	[Benchmark]
	public async Task FFileManifestStream_Save()
	{
		await _fileManifestStream.SaveBytesAsync(_fileBuffer);
	}
}
