using System.Buffers;
using System.Security.Cryptography;

using Microsoft.Win32.SafeHandles;

namespace EpicManifestParser.UE;
// ReSharper disable UseSymbolAlias

internal static class FHashHelpers
{
    // large reads: far fewer I/Os than HashDataAsync, which reads in 4 KiB pieces
    private const int HashFileBufferSize = 1024 * 1024;

    /// <summary>
    /// Hashes a file with one pooled buffer and positional asynchronous reads, without a <see cref="FileStream"/>.
    /// </summary>
    /// <typeparam name="T">The hash struct, its size must match the digest size of <paramref name="algorithm"/>.</typeparam>
    public static async Task<T> ComputeFileHashAsync<T>(string filePath, HashAlgorithmName algorithm, CancellationToken cancellationToken)
        where T : struct, IFHash<T>, IEquatable<T>, ISpanFormattable, IUtf8SpanFormattable
    {
        using SafeFileHandle handle = File.OpenHandle(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using IncrementalHash hash = IncrementalHash.CreateHash(algorithm);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(HashFileBufferSize);

        try
        {
            long offset = 0;
            while (true)
            {
                int read = await RandomAccess.ReadAsync(handle, buffer.AsMemory(0, HashFileBufferSize), offset, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;

                hash.AppendData(buffer, 0, read);
                offset += read;
            }

            return GetHashAndReset<T>(hash);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static T GetHashAndReset<T>(IncrementalHash hash)
        where T : struct, IFHash<T>, IEquatable<T>, ISpanFormattable, IUtf8SpanFormattable
    {
        T result = default;
        hash.GetHashAndReset(result.GetSpan());
        return result;
    }

    public static string ToString(ReadOnlySpan<byte> hash, string? format)
    {
        if (format is null || format.Length == 0 || format == "X")
            return Convert.ToHexString(hash);
        if (format == "x")
            return Convert.ToHexStringLower(hash);
        throw new FormatException("the provided format is not valid");
    }

    public static bool TryFormat(ReadOnlySpan<byte> hash, Span<char> destination, out int charsWritten, ReadOnlySpan<char> format)
    {
        if (format.IsEmpty || format is "X")
            return Convert.TryToHexString(hash, destination, out charsWritten);
        if (format is "x")
            return Convert.TryToHexStringLower(hash, destination, out charsWritten);
        throw new FormatException("the provided format is not valid");
    }

    public static bool TryFormat(ReadOnlySpan<byte> hash, Span<byte> utf8Destination, out int bytesWritten, ReadOnlySpan<char> format)
    {
        if (format.IsEmpty || format is "X")
            return Convert.TryToHexString(hash, utf8Destination, out bytesWritten);
        if (format is "x")
            return Convert.TryToHexStringLower(hash, utf8Destination, out bytesWritten);
        throw new FormatException("the provided format is not valid");
    }
}
