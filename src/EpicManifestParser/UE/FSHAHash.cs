using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

using OffiUtils;

namespace EpicManifestParser.UE;
// ReSharper disable InconsistentNaming
// ReSharper disable UseSymbolAlias

/// <summary>
/// UE FSHAHash struct
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = Size)]
public readonly struct FSHAHash : IEquatable<FSHAHash>
{
	/// <summary>
	/// The size of the hash/struct.
	/// </summary>
	public const int Size = 20;

	private readonly long Hash_00_07;
	private readonly long Hash_08_15;
	private readonly int Hash_16_19;

	/// <inheritdoc/>
	public bool Equals(FSHAHash other)
	{
		return Hash_00_07 == other.Hash_00_07 && Hash_08_15 == other.Hash_08_15 && Hash_16_19 == other.Hash_16_19;
	}

	/// <inheritdoc/>
	public override bool Equals(object? obj)
	{
		return obj is FSHAHash other && Equals(other);
	}

	/// <inheritdoc/>
	public override int GetHashCode()
	{
		return HashCode.Combine(Hash_00_07, Hash_08_15, Hash_16_19);
	}

	/// <inheritdoc cref="Equals(FSHAHash)"/>
	public static bool operator ==(FSHAHash left, FSHAHash right)
	{
		return left.Equals(right);
	}

	/// <inheritdoc cref="Equals(FSHAHash)"/>
	public static bool operator !=(FSHAHash left, FSHAHash right)
	{
		return !left.Equals(right);
	}

	/// <summary>
	/// Gets the data of the hash.
	/// </summary>
	/// <returns>Hash data in a read-only span</returns>
	public ReadOnlySpan<byte> AsSpan() =>
		MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<long, byte>(ref Unsafe.AsRef(in Hash_00_07)), Size);

	internal Span<byte> GetSpan() =>
		MemoryMarshal.CreateSpan(ref Unsafe.As<long, byte>(ref Unsafe.AsRef(in Hash_00_07)), Size);

	/// <summary>
	/// Computes the hash of data using the SHA1 algorithm.
	/// </summary>
	/// <param name="source">The data to hash</param>
	/// <returns>The computed hash</returns>
	public static FSHAHash Compute(ReadOnlySpan<byte> source)
	{
		Unsafe.SkipInit(out FSHAHash hash);
		SHA1.TryHashData(source, hash.GetSpan(), out _);
		return hash;
	}

	/// <summary>
	/// Creates a hex string of the hash.
	/// </summary>
	/// <returns>An upper-case hex <see cref="string"/> of the hash</returns>
	public override string ToString() => StringUtils.BytesToHexUpper(AsSpan());

	/// <returns>A hex <see cref="string"/> of the hash</returns>
	/// <inheritdoc cref="ToString()"/>
	public string ToString(bool upperCase) => upperCase
		? StringUtils.BytesToHexUpper(AsSpan())
		: StringUtils.BytesToHexLower(AsSpan());
}

/// <summary>
/// Converts <see cref="FSHAHash"/> from JSON.
/// </summary>
public sealed class FSHAHashConverter : JsonConverter<FSHAHash>
{
	/// <inheritdoc/>
	public override FSHAHash Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
	{
		Unsafe.SkipInit(out FSHAHash result);
		var resultSpan = result.GetSpan();
		var span = reader.ValueSpan;

		for (var i = 0; i < resultSpan.Length; i++)
		{
			resultSpan[i] = byte.Parse(span.Slice(i * 2, 2), NumberStyles.AllowHexSpecifier);
		}

		return result;
	}

	/// <summary>
	/// Not supported
	/// </summary>
	/// <exception cref="NotSupportedException"></exception>
	public override void Write(Utf8JsonWriter writer, FSHAHash value, JsonSerializerOptions options)
	{
		throw new NotSupportedException();
	}
}
