using ZlibngDotNet;

namespace EpicManifestParser.ZlibngDotNetDecompressor;

/// <summary>
/// A decompressor using <see cref="Zlibng"/>.
/// </summary>
public static class ManifestZlibngDotNetDecompressor
{
	/// <summary>
	/// Decompresses data buffer into destination buffer.
	/// </summary>
	/// <returns><see langword="true"/> if the decompression was successful; otherwise, <see langword="false"/>.</returns>
	public static bool Decompress(object? state, byte[] source, int sourceOffset, int sourceLength, byte[] destination, int destinationOffset, int destinationLength)
	{
		var zlibng = (Zlibng)state!;

		var result = zlibng.Uncompress(destination.AsSpan(destinationOffset, destinationLength),
			source.AsSpan(sourceOffset, sourceLength), out int bytesWritten);

		return result == ZlibngCompressionResult.Ok && bytesWritten == destinationLength;
	}
}
