using System.Diagnostics;
using System.Text.Json;

namespace EpicManifestParser.Json;

internal static class Utf8JsonReaderExtensions
{
    extension(ref Utf8JsonReader reader)
    {
        /// <summary>
        /// Copies the unescaped value of the current string token into <paramref name="destination"/> if it has exactly
        /// <c>destination.Length</c> bytes. Unlike <see cref="Utf8JsonReader.CopyString(Span{byte})"/> it does not throw for
        /// a longer value, and unlike <see cref="Utf8JsonReader.ValueSpan"/> it handles escaped and segmented values.
        /// </summary>
        /// <returns><see langword="false"/> if the value is shorter or longer.</returns>
        public bool TryCopyExactString(scoped Span<byte> destination)
        {
            Debug.Assert(reader.TokenType is JsonTokenType.String or JsonTokenType.PropertyName);
            Debug.Assert(destination.Length <= 256, "the escaped value is unescaped on the stack");

            long rawLength = reader.HasValueSequence ? reader.ValueSequence.Length : reader.ValueSpan.Length;
            if (!reader.ValueIsEscaped)
                return rawLength == destination.Length && TryCopyString(ref reader, destination, out int copied) && copied == destination.Length;

            // an escape sequence takes at most 6 bytes (\uXXXX) and unescapes to at least one,
            // so a longer escaped value cannot match and a shorter one fits on the stack
            if (rawLength > destination.Length * 6L)
                return false;

            Span<byte> unescaped = stackalloc byte[(int)rawLength];
            if (!TryCopyString(ref reader, unescaped, out int written) || written != destination.Length)
                return false;

            unescaped[..written].CopyTo(destination);
            return true;
        }
    }

    /// <summary>
    /// <see cref="Utf8JsonReader.CopyString(Span{byte})"/> without its <see cref="InvalidOperationException"/> for a value that is
    /// not valid text, such as an escaped lone surrogate (<c>\uD800</c>).
    /// </summary>
    private static bool TryCopyString(ref Utf8JsonReader reader, scoped Span<byte> destination, out int written)
    {
        try
        {
            written = reader.CopyString(destination);
            return true;
        }
        catch (InvalidOperationException)
        {
            written = 0;
            return false;
        }
    }
}
