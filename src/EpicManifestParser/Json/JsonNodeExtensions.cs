using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;

namespace EpicManifestParser.Json;

/// <summary>
/// Reads manifest values straight from the <see cref="JsonNode"/> DOM. Nothing goes through <see cref="JsonSerializer"/>, so
/// JSON manifests also parse without reflection (trimmed and AOT apps, <c>JsonSerializerIsReflectionEnabledByDefault=false</c>).
/// </summary>
/// <remarks>
/// A missing or <c>null</c> value gives the default. A value of the wrong type or format throws <see cref="InvalidDataException"/>.
/// </remarks>
internal static class JsonNodeExtensions
{
    /// <summary>
    /// Room for an escaped value after unescaping. The longest valid value is a 60 character blob (<see cref="FSHAHash"/>),
    /// a longer one is invalid anyway.
    /// </summary>
    private const int ScratchSize = 64;

    extension(JsonNode? node)
    {
        /// <summary>
        /// Parses a blob string (see <see cref="BlobString"/>).
        /// </summary>
        /// <returns><paramref name="defaultValue"/> if the value is missing or <c>null</c>.</returns>
        /// <exception cref="InvalidDataException">The value is not a blob string of at most <c>sizeof(T)</c> bytes.</exception>
        public T GetBlob<T>(T defaultValue = default) where T : unmanaged
        {
            if (node is null)
                return defaultValue;
            if (!node.TryGetBlob(out T value))
                throw InvalidValue(node, $"a blob string of at most {Unsafe.SizeOf<T>()} bytes");
            return value;
        }

        /// <summary>
        /// Parses a blob string (see <see cref="BlobString"/>).
        /// </summary>
        /// <returns><see langword="false"/> if the value is missing, <c>null</c> or malformed.</returns>
        public bool TryGetBlob<T>(out T value) where T : unmanaged
        {
            Span<byte> scratch = stackalloc byte[ScratchSize];
            if (node is not null && TryGetUtf8String(node, scratch, out ReadOnlySpan<byte> utf8))
                return BlobString.TryParse(utf8, out value);

            value = default;
            return false;
        }

        /// <summary>
        /// Parses a GUID of 32 hex digits.
        /// </summary>
        /// <exception cref="InvalidDataException">The value is missing, <c>null</c> or not a GUID.</exception>
        public FGuid GetFGuid()
        {
            Span<byte> scratch = stackalloc byte[ScratchSize];
            if (node is not null && TryGetUtf8String(node, scratch, out ReadOnlySpan<byte> utf8) && FGuid.TryParse(utf8, out FGuid guid))
                return guid;

            throw InvalidValue(node, $"a GUID of {FGuid.Size * 2} hex digits");
        }

        /// <summary>
        /// Parses an SHA1 hash of 40 hex digits.
        /// </summary>
        /// <exception cref="InvalidDataException">The value is missing, <c>null</c> or not a hash.</exception>
        public FSHAHash GetShaHex()
        {
            Span<byte> scratch = stackalloc byte[ScratchSize];
            FSHAHash hash = default;
            if (node is not null && TryGetUtf8String(node, scratch, out ReadOnlySpan<byte> utf8) && utf8.Length == FSHAHash.Size * 2
                && Convert.FromHexString(utf8, hash.GetSpan(), out _, out int bytesWritten) == OperationStatus.Done && bytesWritten == FSHAHash.Size)
            {
                return hash;
            }

            throw InvalidValue(node, $"an SHA1 hash of {FSHAHash.Size * 2} hex digits");
        }

        /// <returns><paramref name="defaultValue"/> if the value is missing or <c>null</c>.</returns>
        /// <exception cref="InvalidDataException">The value is not a string.</exception>
        public string GetString(string defaultValue = "")
        {
            if (node is null)
                return defaultValue;
            if (node is JsonValue value && value.TryGetValue(out string? text))
                return text;

            throw InvalidValue(node, "a string");
        }

        /// <returns><see langword="false"/> if the value is missing or not a boolean (as in UE).</returns>
        public bool GetBool() => node.TryGetBool(out bool value) && value;

        /// <returns><see langword="false"/> if the value is missing or not a boolean.</returns>
        public bool TryGetBool(out bool value)
        {
            if (node is JsonValue jsonValue && jsonValue.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
                return jsonValue.TryGetValue(out value);

            value = false;
            return false;
        }

        /// <returns>An empty array if the value is missing or not an array (as in UE).</returns>
        /// <exception cref="InvalidDataException">An element is not a string.</exception>
        public string[] GetStringArray()
        {
            if (node is not JsonArray array || array.Count == 0)
                return [];

            string[] result = new string[array.Count];
            for (int i = 0; i < result.Length; i++)
                result[i] = array[i].GetString();
            return result;
        }
    }

    /// <summary>
    /// The UTF-8 text of a string value: a view of the JSON document if the value is not escaped, otherwise the unescaped value
    /// in <paramref name="scratch"/>.
    /// </summary>
    /// <returns><see langword="false"/> if the node is not a string or its unescaped value does not fit in <paramref name="scratch"/>.</returns>
    private static bool TryGetUtf8String(JsonNode node, Span<byte> scratch, out ReadOnlySpan<byte> utf8)
    {
        utf8 = default;
        if (node is not JsonValue value)
            return false;

        if (value.TryGetValue(out JsonElement element))
        {
            if (element.ValueKind != JsonValueKind.String)
                return false;

            // the raw value includes the quotes
            ReadOnlySpan<byte> raw = JsonMarshal.GetRawUtf8Value(element);
            raw = raw[1..^1];
            if (!raw.Contains((byte)'\\'))
            {
                utf8 = raw;
                return true;
            }
        }

        // escaped, or a node that is not backed by a document
        if (!value.TryGetValue(out string? text) || Utf8.FromUtf16(text, scratch, out _, out int bytesWritten) != OperationStatus.Done)
            return false;

        utf8 = scratch[..bytesWritten];
        return true;
    }

    private static InvalidDataException InvalidValue(JsonNode? node, string expected) => node is null
        ? new InvalidDataException($"Invalid JSON manifest: expected {expected}, the value is missing or null")
        : new InvalidDataException($"Invalid JSON manifest: expected {expected} at {node.GetPath()}, got {node.GetValueKind()}");
}
