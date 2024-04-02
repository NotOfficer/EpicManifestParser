using ZlibngDotNet;

namespace EpicManifestParser;

// ReSharper disable UseSymbolAlias
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
	/// Your own (optional) <see cref="HttpClient"/> used for downloading, must not have a <see cref="HttpClient.BaseAddress"/>
	/// </summary>
	public HttpClient? Client { get; set; }

	/// <summary>
	/// Buffer size for downloading chunks, defaults to 2097152 bytes (2 MiB)
	/// </summary>
	public int ChunkDownloadBufferSize { get; set; } = 2097152;

	/// <summary>
	/// Optional for caching chunks, very recommended.
	/// </summary>
	public string? ChunkCacheDirectory { get; set; }

	/// <summary>
	/// Required for:
	/// - deserializing compressed binary manifests
	/// - downloading compressed chunks
	/// </summary>
	public Zlibng? Zlibng { get; set; }
}
