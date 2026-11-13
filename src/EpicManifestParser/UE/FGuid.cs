using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

using EpicManifestParser.Json;

namespace EpicManifestParser.UE;

/// <summary>
/// UE FGuid struct
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct FGuid : IEquatable<FGuid>, ISpanFormattable, IUtf8SpanFormattable
{
    /// <summary>
    /// The size of the FGuid/struct.
    /// </summary>
    public const int Size = sizeof(uint32) * 4;

    private readonly uint32 A;
    private readonly uint32 B;
    private readonly uint32 C;
    private readonly uint32 D;

    /// <summary>
    /// Creates a hex string of the guid.
    /// </summary>
    public string GetHexString(bool upperCase = true) => upperCase
        ? $"{A:X8}{B:X8}{C:X8}{D:X8}"
        : $"{A:x8}{B:x8}{C:x8}{D:x8}";

    /// <summary>
    /// Creates a string of the guid.
    /// </summary>
    public string GetGuidString() => $"{A:x8}-{B >> 16:x4}-{B & 0xffff:x4}-{C >> 16:x4}-{C & 0xffff:x4}{D:x8}";

    /// <summary>
    /// Creates a FGuid from values.
    /// </summary>
    /// <param name="a">A value.</param>
    /// <param name="b">B value.</param>
    /// <param name="c">C value.</param>
    /// <param name="d">D value.</param>
    public FGuid(uint32 a, uint32 b, uint32 c, uint32 d)
    {
        A = a;
        B = b;
        C = c;
        D = d;
    }

    /// <summary>
    /// Parses a FGuid from a string.
    /// </summary>
    /// <param name="guid">The FGuid string.</param>
    public FGuid(string guid) : this(guid.AsSpan()) { }

    /// <summary>
    /// Parses a FGuid from a string.
    /// </summary>
    /// <param name="guid">The FGuid string.</param>
    public FGuid(ReadOnlySpan<char> guid)
    {
        if (guid.Length != 32)
            throw new ArgumentOutOfRangeException(nameof(guid), "guid has to be 32 characters long, other parsing is not implemented");
        A = uint32.Parse(guid[  .. 8], NumberStyles.AllowHexSpecifier);
        B = uint32.Parse(guid[8 ..16], NumberStyles.AllowHexSpecifier);
        C = uint32.Parse(guid[16..24], NumberStyles.AllowHexSpecifier);
        D = uint32.Parse(guid[24..32], NumberStyles.AllowHexSpecifier);
    }

    /// <summary>
    /// Parses a FGuid from a UTF8 string.
    /// </summary>
    /// <param name="utf8Guid">The UTF8 FGuid string.</param>
    public FGuid(ReadOnlySpan<byte> utf8Guid)
    {
        if (utf8Guid.Length != 32)
            throw new ArgumentOutOfRangeException(nameof(utf8Guid), "guid has to be 32 characters long, other parsing is not implemented");
        A = uint32.Parse(utf8Guid[  .. 8], NumberStyles.AllowHexSpecifier);
        B = uint32.Parse(utf8Guid[8 ..16], NumberStyles.AllowHexSpecifier);
        C = uint32.Parse(utf8Guid[16..24], NumberStyles.AllowHexSpecifier);
        D = uint32.Parse(utf8Guid[24..32], NumberStyles.AllowHexSpecifier);
    }

    /// <summary>
    /// Parses 32 hex digits, without the exceptions of the constructors.
    /// </summary>
    internal static bool TryParse(ReadOnlySpan<char> hex, out FGuid guid)
    {
        Span<byte> bytes = stackalloc byte[Size];
        if (hex.Length != Size * 2 || Convert.FromHexString(hex, bytes, out _, out int bytesWritten) != OperationStatus.Done || bytesWritten != Size)
        {
            guid = default;
            return false;
        }

        guid = FromBigEndian(bytes);
        return true;
    }

    /// <inheritdoc cref="TryParse(ReadOnlySpan{char}, out FGuid)"/>
    internal static bool TryParse(ReadOnlySpan<byte> utf8Hex, out FGuid guid)
    {
        Span<byte> bytes = stackalloc byte[Size];
        if (utf8Hex.Length != Size * 2 || Convert.FromHexString(utf8Hex, bytes, out _, out int bytesWritten) != OperationStatus.Done || bytesWritten != Size)
        {
            guid = default;
            return false;
        }

        guid = FromBigEndian(bytes);
        return true;
    }

    // each component is written as 8 hex digits, most significant first
    private static FGuid FromBigEndian(ReadOnlySpan<byte> bytes) => new(
        BinaryPrimitives.ReadUInt32BigEndian(bytes),
        BinaryPrimitives.ReadUInt32BigEndian(bytes[4..]),
        BinaryPrimitives.ReadUInt32BigEndian(bytes[8..]),
        BinaryPrimitives.ReadUInt32BigEndian(bytes[12..]));

    /// <summary>
    /// Creates a random FGuid.
    /// </summary>
    public static FGuid Random()
    {
        Unsafe.SkipInit(out FGuid result);
        RandomNumberGenerator.Fill(result.GetSpan());
        return result;
    }

    /// <summary>
    /// Checks for validity.
    /// </summary>
    public bool IsValid() => (A | B | C | D) != 0;

    /// <summary>
    /// Gets the data of the guid.
    /// </summary>
    /// <remarks>The span refers to this instance and cannot outlive it.</remarks>
    [UnscopedRef]
    public ReadOnlySpan<byte> AsSpan() =>
        MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<uint32, byte>(ref Unsafe.AsRef(in A)), Size);

    /// <summary>
    /// Gets the values of the guid.
    /// </summary>
    /// <remarks>The span refers to this instance and cannot outlive it.</remarks>
    [UnscopedRef]
    public ReadOnlySpan<uint32> AsIntSpan() =>
        MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in A), 4);

    [UnscopedRef]
    internal Span<byte> GetSpan() =>
        MemoryMarshal.CreateSpan(ref Unsafe.As<uint32, byte>(ref Unsafe.AsRef(in A)), Size);

    /// <inheritdoc />
    public bool Equals(FGuid other)
    {
        return A == other.A && B == other.B && C == other.C && D == other.D;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        return obj is FGuid other && Equals(other);
    }
    
    /// <inheritdoc />
    public override int GetHashCode()
    {
        return HashCode.Combine(A, B, C, D);
    }
    
    /// <inheritdoc cref="Equals(FGuid)"/>
    public static bool operator ==(FGuid left, FGuid right)
    {
        return left.Equals(right);
    }

    /// <inheritdoc cref="Equals(FGuid)"/>
    public static bool operator !=(FGuid left, FGuid right)
    {
        return !left.Equals(right);
    }
    
    /// <inheritdoc />
    public override string ToString() => GetHexString();
    
    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider)
    {
        FormattableString formattable = $"{A:X8}{B:X8}{C:X8}{D:X8}";
        return formattable.ToString(formatProvider);
    }
    
    /// <inheritdoc />
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider = null)
    {
        return destination.TryWrite(provider, $"{A:X8}{B:X8}{C:X8}{D:X8}", out charsWritten);
    }
    
    /// <inheritdoc />
    public bool TryFormat(Span<byte> utf8Destination, out int bytesWritten, ReadOnlySpan<char> format, IFormatProvider? provider = null)
    {
        return Utf8.TryWrite(utf8Destination, provider, $"{A:X8}{B:X8}{C:X8}{D:X8}", out bytesWritten);
    }
}

/// <summary>
/// Converts <see cref="FGuid"/> from and to JSON.
/// </summary>
public sealed class FGuidConverter : JsonConverter<FGuid>
{
    /// <inheritdoc/>
    /// <remarks>An empty string is the default (invalid) <see cref="FGuid"/>.</remarks>
    /// <exception cref="JsonException">The value is not a string of 32 hex digits or an empty string.</exception>
    public override FGuid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException($"Expected a string for an {nameof(FGuid)}, got {reader.TokenType}.");
        if (reader.HasValueSequence ? reader.ValueSequence.IsEmpty : reader.ValueSpan.IsEmpty)
            return default;

        // CopyString unescapes and handles values that span several segments, unlike ValueSpan
        Span<byte> utf8Guid = stackalloc byte[FGuid.Size * 2];
        if (!reader.TryCopyExactString(utf8Guid) || !FGuid.TryParse(utf8Guid, out FGuid guid))
            throw new JsonException($"Expected an {nameof(FGuid)} of {utf8Guid.Length} hex digits.");

        return guid;
    }
    
    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, FGuid value, JsonSerializerOptions options)
    {
        Span<byte> guidUtf8 = stackalloc byte[FGuid.Size * 2]; // 2 chars per byte

        if (value.TryFormat(guidUtf8, out _, default))
        {
            writer.WriteStringValue(guidUtf8);
            return;
        }

        writer.WriteStringValue(string.Empty);
    }
}
