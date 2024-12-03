using System.Diagnostics;

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;

using EpicManifestParser;
using EpicManifestParser.Api;
using EpicManifestParser.UE;
using EpicManifestParser.ZlibngDotNetDecompressor;

using OffiUtils;

using ZlibngDotNet;

var zlibng = new Zlibng(Benchmarks.ZlibngPath);

await TestLauncherManifest(zlibng);
return;

static async Task<byte[]> TestLauncherManifest(Zlibng? zlibng = null)
{
	var options = new ManifestParseOptions
	{
		ChunkBaseUrl = "http://download.epicgames.com/Builds/UnrealEngineLauncher/CloudDir/",
		//ChunkCacheDirectory = Directory.CreateDirectory(Path.Combine(Benchmarks.DownloadsDir, "chunks_v2")).FullName,
		//ManifestCacheDirectory = Directory.CreateDirectory(Path.Combine(Benchmarks.DownloadsDir, "manifests_v2")).FullName,
	};

	if (zlibng is not null)
	{
		Console.WriteLine($"Zlib-ng version: {zlibng.GetVersionString()}");
		options.Decompressor = ManifestZlibngDotNetDecompressor.Decompress;
		options.DecompressorState = zlibng;
	}

	Console.WriteLine("Loading manifest bytes...");
	var manifestBuffer = await File.ReadAllBytesAsync(Path.Combine(Benchmarks.DownloadsDir, "EpicGamesLauncher2.9.2-2874913-Portal-Release-Live-Windows.manifest"));
	Console.WriteLine("Deserializing manifest...");
	var manifest = FBuildPatchAppManifest.Deserialize(manifestBuffer, options);

	var fileManifest = manifest.FindFile("Portal/Binaries/Win64/EpicGamesLauncher.exe")!;
	var stream = fileManifest.GetStream();

	var fileBytes = new byte[stream.Length];

#if !DEBUG
	await Task.Delay(TimeSpan.FromSeconds(10));

	for (var i = 0; i < 10_000; i++)
	{
		await stream.SaveBytesAsync(fileBytes);
	}
#else
	var fileName = fileManifest.FileName.CutAfterLast('/')!;
	Console.WriteLine($"Saving {fileName}...");

	try
	{
		await stream.SaveBytesAsync(fileBytes, ProgressCallback, fileName);
	}
	catch (Exception ex)
	{
		var uri = ex.Data["Uri"];
		var headers = ex.Data["Headers"];
		return [];
	}
	
	Console.WriteLine($"Hashes match: {fileManifest.FileHash == FSHAHash.Compute(fileBytes)}");
#endif

	return fileBytes;
}

//BenchmarkDotNet.Running.BenchmarkRunner.Run<Benchmarks>();
//return;

var options = new ManifestParseOptions
{
	//ChunkBaseUrl = "http://fastly-download.epicgames.com/Builds/Fortnite/Content/CloudDir/",
	ChunkBaseUrl = "http://fastly-download.epicgames.com/Builds/Fortnite/CloudDir/",
	ChunkCacheDirectory = Directory.CreateDirectory(Path.Combine(Benchmarks.DownloadsDir, "chunks_v2")).FullName,
	ManifestCacheDirectory = Directory.CreateDirectory(Path.Combine(Benchmarks.DownloadsDir, "manifests_v2")).FullName,
	CacheChunksAsIs = false,

	Decompressor = ManifestZlibngDotNetDecompressor.Decompress,
	DecompressorState = zlibng
};

var client = options.CreateDefaultClient();

using var manifestResponse = await client.GetAsync("https://media.wtf/XlQk.json");
var manifestInfo1 = await manifestResponse.Content.ReadManifestInfoAsync();
var manifestInfo2 = await ManifestInfo.DeserializeFileAsync(Benchmarks.ManifestInfoPath);

//var manifestInfoTuple = await manifestInfo2!.DownloadAndParseAsync(options);
//var parseResult = manifestInfoTuple.InfoElement.TryParseVersionAndCL(out var infoVersion, out var infoCl);

//var randomGuid = FGuid.Random();
//var chunkGuid = new FGuid("A76EAD354E9F6F06D0E75CAC2AB1B56C");

var manifestBuffer = await File.ReadAllBytesAsync(Benchmarks.ManifestPath);

var sw = Stopwatch.StartNew();
var manifest = FBuildPatchAppManifest.Deserialize(manifestBuffer, options);
sw.Stop();
Console.WriteLine(Math.Round(sw.Elapsed.TotalMilliseconds, 0));

{
	var fileManifest = manifest.Files.First(x =>
		x.FileName.EndsWith("/pakchunk0optional-WindowsClient.ucas", StringComparison.Ordinal));
	var fileManifestFileName = Path.GetFileName(fileManifest.FileName);
	var fileManifestStream = fileManifest.GetStream();

	await fileManifestStream.SaveFileAsync(Path.Combine(Benchmarks.DownloadsDir, fileManifestFileName));

	var fileBuffer = await fileManifestStream.SaveBytesAsync();
	Console.WriteLine($"{fileManifest.FileHash} / {FSHAHash.Compute(fileBuffer)}");

	sw.Restart();
	fileBuffer = new byte[fileManifest.FileSize];
	await fileManifestStream.SaveBytesAsync(fileBuffer, ProgressCallback, fileManifestFileName);
	//await fileManifestStream.SaveToAsync(new MemoryStream(fileBuffer, 0, fileBuffer.Length, true, true), ProgressCallback, fileManifestFileName);
	sw.Stop();
	Console.WriteLine($"{fileManifest.FileHash} / {FSHAHash.Compute(fileBuffer)}");
}

Console.ReadLine();

static void ProgressCallback(SaveProgressChangedEventArgs eventArgs)
{
	var text = (string)eventArgs.UserState!;
	Console.WriteLine($"{text}: {eventArgs.ProgressPercentage}% ({eventArgs.BytesSaved}/{eventArgs.TotalBytesToSave})");
}

[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
[BaselineColumn]
[MemoryDiagnoser(false)]
[SimpleJob(RuntimeMoniker.Net90, baseline: true)]
[SimpleJob(RuntimeMoniker.Net80)]
public class Benchmarks
{
	public static string DownloadsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

	public static string ManifestPath = Path.Combine(DownloadsDir, "Kauyq4jGHB-SuyDjakmArJ1VU6QYJw.manifest");
	public static string ZlibngPath = Path.Combine(DownloadsDir, "zlib-ng2.dll");
	public static string ManifestInfoPath = Path.Combine(DownloadsDir, "manifestinfo.json");

	public static string TestChunkPath = Path.Combine(DownloadsDir, "8ED2116F187190BA_996E9BFD428888C4627AE6B1153404C3.chunk");

	private byte[] _manifestBuffer = null!;
	private byte[] _testChunkBuffer = null!;
	private byte[] _testTempBuffer = null!;
	private Zlibng _zlibng = null!;
	private byte[] _manifestInfoBuffer = null!;
	private FBuildPatchAppManifest _manifest = null!;
	private FFileManifestStream _fileManifestStream1 = null!;
	private FFileManifestStream _fileManifestStream2 = null!;
	private byte[] _fileBuffer = null!;
	private MemoryStream _fileMs = null!;
	private string _filePath = null!;
	private FGuid _guid;

	[GlobalSetup]
	public void Setup()
	{
		_guid = FGuid.Random();
		_testTempBuffer = new byte[10000000];
		_zlibng = new Zlibng(ZlibngPath);
		_testChunkBuffer = File.ReadAllBytes(TestChunkPath);

		_manifestBuffer = File.ReadAllBytes(ManifestPath);
		_manifestInfoBuffer = File.ReadAllBytes(ManifestInfoPath);

		_manifest = FBuildPatchAppManifest.Deserialize(_manifestBuffer, options =>
		{
			//options.ChunkBaseUrl = "http://download.epicgames.com/Builds/Fortnite/CloudDir/";            // 20-21 ms
			//options.ChunkBaseUrl = "http://cloudflare.epicgamescdn.com/Builds/Fortnite/CloudDir/";       // 34-36 ms
			options.ChunkBaseUrl = "http://fastly-download.epicgames.com/Builds/Fortnite/CloudDir/";     // 19-20 ms
			//options.ChunkBaseUrl = "http://epicgames-download1.akamaized.net/Builds/Fortnite/CloudDir/"; // 27-28 ms
			options.ChunkCacheDirectory = Directory.CreateDirectory(Path.Combine(DownloadsDir, "chunks_v2")).FullName;
		});
		var fileManifest = _manifest.Files.First(x =>
			x.FileName.EndsWith("/pakchunk0optional-WindowsClient.ucas", StringComparison.Ordinal));
		_filePath = Path.Combine(DownloadsDir, Path.GetFileName(fileManifest.FileName));
		_fileBuffer = new byte[fileManifest.FileSize];
		_fileMs = new MemoryStream(_fileBuffer, true);
		_fileManifestStream1 = fileManifest.GetStream(true);
		_fileManifestStream2 = fileManifest.GetStream(false);
	}

	[Benchmark(Baseline = true), BenchmarkCategory("Uncompress")]
	public byte[] FChunkInfo_Uncompress_Zlibng()
	{
		FChunkInfo.Test_Zlibng(_testTempBuffer, _testChunkBuffer, _zlibng, ManifestZlibngDotNetDecompressor.Decompress);
		return _testTempBuffer;
	}

	[Benchmark, BenchmarkCategory("Uncompress")]
	public byte[] FChunkInfo_Uncompress_ZlibStream()
	{
		FChunkInfo.Test_ZlibStream(_testTempBuffer, _testChunkBuffer);
		return _testTempBuffer;
	}

	[Benchmark, BenchmarkCategory("Deserialize")]
	public FBuildPatchAppManifest FBuildPatchAppManifest_Deserialize()
	{
		return FBuildPatchAppManifest.Deserialize(_manifestBuffer);
	}

	[Benchmark, BenchmarkCategory("Deserialize")]
	public ManifestInfo? ManifestInfo_Deserialize()
	{
		return ManifestInfo.Deserialize(_manifestInfoBuffer);
	}

	[BenchmarkCategory("SaveBuffer"), Benchmark(Baseline = true)]
	public async Task FFileManifestStream_SaveBuffer()
	{
		await _fileManifestStream2.SaveBytesAsync(_fileBuffer);
	}

	[BenchmarkCategory("SaveBuffer"), Benchmark]
	public async Task FFileManifestStream_SaveBuffer_AsIs()
	{
		await _fileManifestStream1.SaveBytesAsync(_fileBuffer);
	}

	//[BenchmarkCategory("SaveFile"), Benchmark(Baseline = true)]
	//public async Task FFileManifestStream_SaveFile()
	//{
	//	await _fileManifestStream2.SaveFileAsync(_filePath);
	//}

	//[BenchmarkCategory("SaveFile"), Benchmark]
	//public async Task FFileManifestStream_SaveFile_AsIs()
	//{
	//	await _fileManifestStream1.SaveFileAsync(_filePath);
	//}

	[BenchmarkCategory("SaveStream"), Benchmark(Baseline = true)]
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
	}
}
