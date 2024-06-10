using System.Net;

using EpicManifestParser.Api;

using ZlibngDotNet;

namespace EpicManifestParser;
// ReSharper disable UseSymbolAlias

/// <summary>
/// Options/Configuration for parsing manifests
/// </summary>
public class ManifestParseOptions
{
	/// <summary>
	/// Required for downloading, must end with a slash!
	/// </summary>
	/// <remarks>
	/// Example: <code>http://epicgames-download1.akamaized.net/Builds/Fortnite/CloudDir/</code><br/>
	/// Distributionpoints can be found here: <see href="https://launcher-public-service-prod06.ol.epicgames.com/launcher/api/public/distributionpoints">here</see>
	/// </remarks>
	public string? ChunkBaseUrl { get; set; }

	/// <summary>
	/// Your own (optional) <see cref="HttpClient"/> used for downloading, must not have a <see cref="HttpClient.BaseAddress"/> !
	/// </summary>
	public HttpClient? Client { get; set; }

	/// <summary>
	/// Buffer size for downloading chunks, defaults to 2097152 bytes (2 MiB).
	/// </summary>
	public int ChunkDownloadBufferSize { get; set; } = 2097152;

	/// <summary>
	/// Optional for caching chunks, very recommended.
	/// </summary>
	public string? ChunkCacheDirectory { get; set; }

	/// <summary>
	/// Whether or not to cache the chunks 1:1 as they were downloaded.
	/// </summary>
	public bool CacheChunksAsIs { get; set; }

	/// <summary>
	/// Optional for caching manifests when using <see cref="ManifestInfo.DownloadAndParseAsync(ManifestParseOptions, Predicate&lt;ManifestInfoElement&gt;?, Predicate&lt;ManifestInfoElementManifest&gt;?, CancellationToken)"/>.
	/// </summary>
	public string? ManifestCacheDirectory { get; set; }

	/// <summary>
	/// Required for:<br/>
	/// - deserializing compressed binary manifests<br/>
	/// - downloading compressed chunks<br/>
	/// </summary>
	public Zlibng? Zlibng { get; set; }

	internal void CreateDefaultClient()
	{
		Client ??= new HttpClient(new SocketsHttpHandler
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
	}
}
