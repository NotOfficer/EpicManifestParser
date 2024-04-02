using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

using OffiUtils;

namespace EpicManifestParser.UE;

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = Size)]
public readonly struct FSHAHash : IEquatable<FSHAHash>
{
	internal const int Size = 20;
	private readonly long Hash_00_07;
	private readonly long Hash_08_15;
	private readonly int Hash_16_19;

	public bool Equals(FSHAHash other)
	{
		return Hash_00_07 == other.Hash_00_07 && Hash_08_15 == other.Hash_08_15 && Hash_16_19 == other.Hash_16_19;
	}

	public override bool Equals(object? obj)
	{
		return obj is FSHAHash other && Equals(other);
	}

	public override int GetHashCode()
	{
		return HashCode.Combine(Hash_00_07, Hash_08_15, Hash_16_19);
	}

	public static bool operator ==(FSHAHash left, FSHAHash right)
	{
		return left.Equals(right);
	}

	public static bool operator !=(FSHAHash left, FSHAHash right)
	{
		return !left.Equals(right);
	}

	public ReadOnlySpan<byte> AsSpan() =>
		MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<long, byte>(ref Unsafe.AsRef(in Hash_00_07)), Size);

	internal Span<byte> GetSpan() =>
		MemoryMarshal.CreateSpan(ref Unsafe.As<long, byte>(ref Unsafe.AsRef(in Hash_00_07)), Size);

	public static bool TryCompute(ReadOnlySpan<byte> source, out FSHAHash hash)
	{
		Unsafe.SkipInit(out FSHAHash _hash);
		var result = SHA1.TryHashData(source, _hash.GetSpan(), out _);
		hash = _hash;
		return result;
	}

	public static bool TryCompute(ReadOnlySpan<byte> source, out FSHAHash hash, out int bytesWritten)
	{
		Unsafe.SkipInit(out FSHAHash _hash);
		var result = SHA1.TryHashData(source, _hash.GetSpan(), out bytesWritten);
		hash = _hash;
		return result;
	}

	public override string ToString() => StringUtils.BytesToHexUpper(AsSpan());
}

public sealed class FSHAHashConverter : JsonConverter<FSHAHash>
{
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

	public override void Write(Utf8JsonWriter writer, FSHAHash value, JsonSerializerOptions options)
	{
		throw new NotSupportedException();
	}
}
