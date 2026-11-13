using System.Runtime.InteropServices;

namespace EpicManifestParser.Json;

/// <summary>
/// The "blob" string of JSON manifests (UE <c>FString::ToBlob</c>/<c>FromBlob</c>): three decimal digits per byte of the
/// value's memory, in memory order.
/// </summary>
internal static class BlobString
{
    /// <summary>
    /// Parses a blob string of at most <c>sizeof(T)</c> bytes, the bytes that are not in the string stay zero (as in UE, which
    /// also accepts an empty string). Unlike UE, which uses <c>Atoi</c>, only three digits of up to 255 are accepted per byte.
    /// </summary>
    /// <param name="utf8">The UTF-8 text of the string.</param>
    /// <param name="value">The parsed value.</param>
    /// <returns><see langword="false"/> if the string is malformed or longer than <c>3 * sizeof(T)</c>.</returns>
    public static bool TryParse<T>(ReadOnlySpan<byte> utf8, out T value) where T : unmanaged
    {
        value = default;
        Span<byte> destination = MemoryMarshal.AsBytes(new Span<T>(ref value));

        if (utf8.Length % 3 != 0 || utf8.Length / 3 > destination.Length)
            return false;

        for (int i = 0, j = 0; i < utf8.Length; i += 3, j++)
        {
            uint32 hundreds = (uint32)(utf8[i] - '0');
            uint32 tens = (uint32)(utf8[i + 1] - '0');
            uint32 ones = (uint32)(utf8[i + 2] - '0');
            if (hundreds > 9 || tens > 9 || ones > 9)
                return false;

            uint32 b = hundreds * 100 + tens * 10 + ones;
            if (b > uint8.MaxValue)
                return false;

            destination[j] = (uint8)b;
        }

        return true;
    }
}
