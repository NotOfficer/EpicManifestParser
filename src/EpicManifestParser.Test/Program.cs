﻿using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
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
			var manfiest = new Manifest(manifestData, new ManifestOptions
			{
				ChunkBaseUri = new Uri("http://epicgames-download1.akamaized.net/Builds/Fortnite/CloudDir/ChunksV3/", UriKind.Absolute), //required for downloading (fortnite)
				ChunkCacheDirectory = Directory.CreateDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "FortniteChunks")) // optional
			});
			Console.WriteLine($"Manifest parsed in: {manfiest.ParseTime.TotalMilliseconds:0}ms");

			var testPak = manfiest.FileManifests.Find(x => x.Name == "FortniteGame/Content/Paks/pakchunk1006-WindowsClient.pak");
			await using var pakStream = testPak.GetStream();
			using var sha1 = SHA1.Create();
			Console.WriteLine("Downloading 24 MB pak & computing hash...");
			var hash = sha1.ComputeHash(pakStream);
			var hashString = BitConverter.ToString(hash).Replace("-", null);
			Console.WriteLine("Result: {0} ({1} / {2})", testPak.Hash == hashString ? "match" : "different", hashString, testPak.Hash);

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
			Console.WriteLine($"Parsed {runs} Manifests in: {time.TotalMilliseconds:0}ms ({avg.TotalMilliseconds:0}ms avg)");
		}
	}
}