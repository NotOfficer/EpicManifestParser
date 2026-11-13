using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;

using EpicManifestParser.Json;

namespace EpicManifestParser.UE;
// ReSharper disable UseSymbolAlias

/// <summary>
/// Converts a <see cref="IFHash{T}"/> value from and to JSON.
/// </summary>
public sealed class FHashConverter<T> : JsonConverter<T>
    where T : struct, IFHash<T>, IEquatable<T>, ISpanFormattable, IUtf8SpanFormattable
{
    /// <inheritdoc/>
    /// <exception cref="JsonException">The value is not a string of <c>2 * T.Size</c> hex digits.</exception>
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException($"Expected string, got {reader.TokenType}.");

        Span<byte> utf8Hex = stackalloc byte[T.Size * 2];

        // CopyString throws an ArgumentException for a longer value
        if (!reader.TryCopyExactString(utf8Hex))
            throw new JsonException($"Expected {utf8Hex.Length} hex characters.");

        T result = default;

        OperationStatus status = Convert.FromHexString(
            utf8Hex,
            result.GetSpan(),
            out int bytesConsumed,
            out int bytesWritten);

        if (status != OperationStatus.Done ||
            bytesConsumed != utf8Hex.Length ||
            bytesWritten != T.Size)
        {
            throw new JsonException("Invalid hash hex value.");
        }

        return result;
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        Span<byte> utf8Hex = stackalloc byte[T.Size * 2];

        if (!(value as IUtf8SpanFormattable).TryFormat(utf8Hex, out int bytesWritten, default, null) ||
            bytesWritten != utf8Hex.Length)
        {
            throw new JsonException("Failed to format SHA hash.");
        }

        writer.WriteStringValue(utf8Hex);
    }
}
