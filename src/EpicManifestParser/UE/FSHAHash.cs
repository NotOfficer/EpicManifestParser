using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using CommunityToolkit.HighPerformance.Buffers;

namespace EpicManifestParser.UE;
// ReSharper disable InconsistentNaming
// ReSharper disable UseSymbolAlias

/// <summary>
/// UE FSHAHash struct
/// </summary>
[InlineArray(20)]
public struct FSHAHash : IFHash<FSHAHash>, IEquatable<FSHAHash>, ISpanFormattable, IUtf8SpanFormattable
{
    private byte _element;

    /// <summary>
    /// The size of the hash/struct.
    /// </summary>
    public static int Size => 20;

    /// <inheritdoc/>
    [UnscopedRef]
    public Span<byte> GetSpan() => this;

    /// <inheritdoc/>
    public bool Equals(FSHAHash other)
        => ((ReadOnlySpan<byte>)this).SequenceEqual(other);

    /// <inheritdoc/>
    public override bool Equals(object? obj)
        => obj is FSHAHash other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hashCode = new HashCode();
        hashCode.AddBytes(this);
        return hashCode.ToHashCode();
    }

    /// <inheritdoc/>
    public static bool operator ==(FSHAHash left, FSHAHash right)
        => left.Equals(right);

    /// <inheritdoc/>
    public static bool operator !=(FSHAHash left, FSHAHash right)
        => !left.Equals(right);

    /// <inheritdoc/>
    public static FSHAHash Compute(ReadOnlySpan<byte> data)
    {
        FSHAHash result = default;
        SHA1.TryHashData(data, result, out _);
        return result;
    }

    /// <inheritdoc/>
    public static FSHAHash Compute(ReadOnlySpan<char> text)
        => Compute(MemoryMarshal.AsBytes(text));

    /// <inheritdoc/>
    public static async Task<FSHAHash> ComputeAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        using var memoryOwner = MemoryOwner<byte>.Allocate(Size);
        await SHA1.HashDataAsync(stream, memoryOwner.Memory, cancellationToken).ConfigureAwait(false);
        return MemoryMarshal.Read<FSHAHash>(memoryOwner.Span);
    }

    /// <inheritdoc/>
    public static Task<FSHAHash> ComputeAsync(string filePath, CancellationToken cancellationToken = default)
        => FHashHelpers.ComputeFileHashAsync<FSHAHash>(filePath, HashAlgorithmName.SHA1, cancellationToken);

    /// <inheritdoc cref="IFHash{T}.ToString()"/>
    public override string ToString() => Convert.ToHexString(this);

    /// <inheritdoc/>
    public string ToString(bool upperCase) => upperCase
        ? Convert.ToHexString(this)
        : Convert.ToHexStringLower(this);

    /// <inheritdoc cref="IFHash{T}.ToString(string?,IFormatProvider?)"/>
    public string ToString(string? format, IFormatProvider? formatProvider = null)
        => FHashHelpers.ToString(this, format);

    /// <inheritdoc cref="IFHash{T}.TryFormat(Span{char},out int,ReadOnlySpan{char},IFormatProvider?)"/>
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default, IFormatProvider? provider = null)
        => FHashHelpers.TryFormat(this, destination, out charsWritten, format);

    /// <inheritdoc cref="IFHash{T}.TryFormat(Span{byte},out int,ReadOnlySpan{char},IFormatProvider?)"/>
    public bool TryFormat(Span<byte> utf8Destination, out int bytesWritten, ReadOnlySpan<char> format = default, IFormatProvider? provider = null)
        => FHashHelpers.TryFormat(this, utf8Destination, out bytesWritten, format);
}
