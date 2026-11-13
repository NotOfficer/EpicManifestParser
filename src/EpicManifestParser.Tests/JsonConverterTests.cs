using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;
using System.Text.Json;

using EpicManifestParser.UE;

namespace EpicManifestParser.Tests;

public class JsonConverterTests
{
    private static readonly JsonSerializerOptions Options = new();
    private const string GuidHex = "0123456789ABCDEF0123456789abcdef";
    private static readonly FGuid Guid = new(0x01234567, 0x89ABCDEF, 0x01234567, 0x89ABCDEF);
    private const string ShaHex = "00112233445566778899AABBCCDDEEFF00112233";

    [Theory]
    [InlineData(nameof(FGuid.AsSpan), BindingFlags.Public)]
    [InlineData(nameof(FGuid.AsIntSpan), BindingFlags.Public)]
    [InlineData("GetSpan", BindingFlags.NonPublic)]
    public void FGuid_span_accessors_are_UnscopedRef(string name, BindingFlags visibility)
    {
        // without the attribute a span over a temporary, such as MakeGuid().AsSpan(), could be returned from a method
        MethodInfo? method = typeof(FGuid).GetMethod(name, BindingFlags.Instance | visibility, []);
        Assert.NotNull(method);
        Assert.True(method.IsDefined(typeof(UnscopedRefAttribute)));
    }

    [Fact]
    public void FGuid_spans_view_the_components()
    {
        var guid = new FGuid(1, 2, 3, 4);
        Assert.Equal([1u, 2u, 3u, 4u], guid.AsIntSpan().ToArray());
        Assert.Equal(FGuid.Size, guid.AsSpan().Length);
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(guid.AsSpan()[4..]));
    }

    private static FGuid ReadGuid(ReadOnlySequence<byte> json)
    {
        var reader = new Utf8JsonReader(json);
        Assert.True(reader.Read());
        return new FGuidConverter().Read(ref reader, typeof(FGuid), Options);
    }

    private static FSHAHash ReadSha(ReadOnlySequence<byte> json)
    {
        var reader = new Utf8JsonReader(json);
        Assert.True(reader.Read());
        return new FHashConverter<FSHAHash>().Read(ref reader, typeof(FSHAHash), Options);
    }

    private static ReadOnlySequence<byte> Json(string json) => new(Encoding.UTF8.GetBytes(json));

    /// <summary>The JSON split into segments of <paramref name="segmentLength"/> bytes, so that a value spans several of them.</summary>
    private static ReadOnlySequence<byte> Segmented(string json, int segmentLength)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        var first = new Segment(bytes.AsMemory(0, Math.Min(segmentLength, bytes.Length)));
        Segment last = first;
        for (int offset = segmentLength; offset < bytes.Length; offset += segmentLength)
            last = last.Append(bytes.AsMemory(offset, Math.Min(segmentLength, bytes.Length - offset)));
        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory) => Memory = memory;

        public Segment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new Segment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }

    [Fact]
    public void FGuidConverter_reads_plain_escaped_and_segmented_values()
    {
        Assert.Equal(Guid, ReadGuid(Json($"\"{GuidHex}\"")));
        Assert.Equal(Guid, ReadGuid(Json($"\"\\u0030\\u0031{GuidHex[2..]}\"")));
        Assert.Equal(Guid, ReadGuid(Segmented($"\"{GuidHex}\"", 5)));
        Assert.Equal(Guid, ReadGuid(Segmented($"\"\\u0030{GuidHex[1..]}\"", 3)));
    }

    [Fact]
    public void Converters_reject_an_escaped_lone_surrogate()
    {
        // not valid text: Utf8JsonReader.CopyString throws an InvalidOperationException for it
        string guid = $"\"\\uD800{new string('0', 31)}\"";
        Assert.Throws<JsonException>(() => ReadGuid(Json(guid)));
        Assert.Throws<JsonException>(() => ReadGuid(Segmented(guid, 4)));

        string sha = $"\"\\uD800{new string('0', 39)}\"";
        Assert.Throws<JsonException>(() => ReadSha(Json(sha)));
        Assert.Throws<JsonException>(() => ReadSha(Segmented(sha, 4)));
    }

    [Fact]
    public void FGuidConverter_reads_an_empty_string_as_the_default_guid()
    {
        Assert.Equal(default, ReadGuid(Json("\"\"")));
        Assert.Equal(default, ReadGuid(Segmented("\"\"", 1)));
    }

    [Theory]
    [InlineData("\"0123\"")]
    [InlineData("\"0123456789ABCDEF0123456789ABCDE\"")]
    [InlineData("\"0123456789ABCDEF0123456789ABCDEF0\"")]
    [InlineData("\"0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF\"")]
    [InlineData("\"0123456789ABCDEF0123456789ABCDEG\"")]
    [InlineData("\"\\u00300123456789ABCDEF0123456789ABCDEF\"")]
    [InlineData("123")]
    [InlineData("null")]
    [InlineData("{}")]
    public void FGuidConverter_rejects_invalid_values(string json)
    {
        Assert.Throws<JsonException>(() => ReadGuid(Json(json)));
        Assert.Throws<JsonException>(() => ReadGuid(Segmented(json, 4)));
    }

    [Fact]
    public void FGuidConverter_round_trips()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            new FGuidConverter().Write(writer, Guid, Options);

        Assert.Equal(Guid, ReadGuid(new ReadOnlySequence<byte>(stream.ToArray())));
    }

    [Fact]
    public void FHashConverter_reads_plain_escaped_and_segmented_values()
    {
        FSHAHash expected = default;
        Convert.FromHexString(ShaHex).CopyTo(expected.GetSpan());

        Assert.Equal(expected, ReadSha(Json($"\"{ShaHex}\"")));
        Assert.Equal(expected, ReadSha(Json($"\"\\u0030{ShaHex[1..]}\"")));
        Assert.Equal(expected, ReadSha(Segmented($"\"{ShaHex}\"", 7)));
    }

    [Theory]
    [InlineData("\"00112233445566778899AABBCCDDEEFF0011223\"")]
    [InlineData("\"00112233445566778899AABBCCDDEEFF001122334\"")]
    [InlineData("\"00112233445566778899AABBCCDDEEFF0011223300112233445566778899AABBCCDDEEFF00112233\"")]
    [InlineData("\"00112233445566778899AABBCCDDEEFF0011223X\"")]
    [InlineData("1")]
    public void FHashConverter_rejects_invalid_values(string json)
    {
        // a longer string made CopyString throw an ArgumentException
        Assert.Throws<JsonException>(() => ReadSha(Json(json)));
        Assert.Throws<JsonException>(() => ReadSha(Segmented(json, 6)));
    }
}
