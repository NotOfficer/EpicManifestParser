using System;
using System.Net.Http;
using System.Threading.Tasks;

using EpicManifestParser.Objects;

namespace EpicManifestParser.Test
{
	internal class Program
	{
		private static async Task Main()
		{
			using var client = new HttpClient();
			Console.WriteLine("Getting manifest info...");
			await using var infoStream = await client.GetStreamAsync("https://cdn.notofficer.de/323685.json"); // ++Fortnite+Release-13.20-CL-13777676-Windows example
			var manifestInfo = new ManifestInfo(infoStream);
			Console.WriteLine("Downloading manifest...");
			var manifestData = await manifestInfo.DownloadManifestDataAsync();
			Console.WriteLine("Parsing manifest...");
			var manfiest = new Manifest(manifestData);
			Console.WriteLine($"Manifest parsed in: {manfiest.ParseTime.Milliseconds}ms");
			Console.WriteLine();
			Console.WriteLine("Running benchmark...");
			var time = new TimeSpan();
			const int runs = 50;

			for (var i = 0; i < runs; i++)
			{
				var m = new Manifest(manifestData);
				time += m.ParseTime;
			}

			var avg = time / runs;
			Console.WriteLine($"Manifests parsed in: {time.Milliseconds}ms ({avg.Milliseconds}ms avg) of {runs} runs");
		}
	}
}