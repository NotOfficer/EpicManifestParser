using System.Buffers;
using System.Diagnostics;
using System.Net;

using EpicManifestParser.UE;
using EpicManifestParser.ZlibngDotNetDecompressor;

using ZlibngDotNet;

namespace EpicManifestParser.Tests;

public class UncompressTests : IAsyncLifetime
{
	private string _zlibngFilePath = null!;
	private Zlibng _zlibng = null!;
	private byte[] _uncompressPoolBuffer = null!;

	public async Task InitializeAsync()
	{
		_zlibngFilePath = await DownloadAsync();
		_zlibng = new Zlibng(_zlibngFilePath);
		_uncompressPoolBuffer = ArrayPool<byte>.Shared.Rent(10000000);
	}

	[Fact]
	public async Task Uncompress_Chunk_Default()
	{
		var chunkBuffer = await File.ReadAllBytesAsync("files/chunk_compressed.bin");
		FChunkInfo.Test_ZlibStream(_uncompressPoolBuffer, chunkBuffer);
	}

	[Fact]
	public async Task Uncompress_Chunk_Zlibng()
	{
		var chunkBuffer = await File.ReadAllBytesAsync("files/chunk_compressed.bin");
		FChunkInfo.Test_Zlibng(_uncompressPoolBuffer, chunkBuffer, _zlibng, ManifestZlibngDotNetDecompressor.Decompress);
	}

	public Task DisposeAsync()
	{
		ArrayPool<byte>.Shared.Return(_uncompressPoolBuffer);
		_zlibng.Dispose();
		File.Delete(_zlibngFilePath);
		return Task.CompletedTask;
	}

	public static async Task<string> DownloadAsync()
	{
		if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
		{
			throw new PlatformNotSupportedException("this test is not supported on the current platform");
		}

		const string baseUrl = "https://github.com/NotOfficer/Zlib-ng.NET/releases/download/1.0.0/";
		string url;

		if (OperatingSystem.IsWindows())
		{
			url = baseUrl + "zlib-ng2.dll";
		}
		else if (OperatingSystem.IsLinux())
		{
			url = baseUrl + "libz-ng.so";
		}
		else
		{
			throw new UnreachableException();
		}

		using var client = new HttpClient(new SocketsHttpHandler
		{
			UseProxy = false,
			UseCookies = true,
			AutomaticDecompression = DecompressionMethods.All
		});
		using var response = await client.GetAsync(url);
		response.EnsureSuccessStatusCode();
		var filePath = Path.GetTempFileName();
		await using var fs = File.Create(filePath);
		await response.Content.CopyToAsync(fs);
		return filePath;
	}
}
