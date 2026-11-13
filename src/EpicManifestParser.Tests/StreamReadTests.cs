using EpicManifestParser.Tests.Fixtures;
using EpicManifestParser.UE;

namespace EpicManifestParser.Tests;

public class StreamReadTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const int Offset = 3;
    private const byte Guard = 0xEE;

    public static TheoryData<string> Modes => ["cache-directory", "memory-cache", "no-cache"];

    private delegate ValueTask<int> ReadMethod(FFileManifestStream stream, long position, byte[] buffer, int count);

    /// <summary>Every overload reads <c>count</c> bytes at <c>position</c> into <c>buffer[Offset..]</c>.</summary>
    private static readonly (string Name, bool UsesPosition, ReadMethod Read)[] Overloads =
    [
        ("Read(Span)", true, static (s, p, b, c) => { s.Position = p; return ValueTask.FromResult(s.Read(b.AsSpan(Offset, c))); }),
        ("Read(byte[])", true, static (s, p, b, c) => { s.Position = p; return ValueTask.FromResult(s.Read(b, Offset, c)); }),
        ("ReadAsync(Memory)", true, static (s, p, b, c) => { s.Position = p; return s.ReadAsync(b.AsMemory(Offset, c), Ct); }),
        ("ReadAsync(byte[])", true, static (s, p, b, c) => { s.Position = p; return new ValueTask<int>(s.ReadAsync(b, Offset, c, Ct)); }),
        ("ReadAt(Span)", false, static (s, p, b, c) => ValueTask.FromResult(s.ReadAt(p, b.AsSpan(Offset, c)))),
        ("ReadAt(byte[])", false, static (s, p, b, c) => ValueTask.FromResult(s.ReadAt(p, b, Offset, c))),
        ("ReadAtAsync(Memory)", false, static (s, p, b, c) => s.ReadAtAsync(p, b.AsMemory(Offset, c), Ct)),
        ("ReadAtAsync(byte[])", false, static (s, p, b, c) => new ValueTask<int>(s.ReadAtAsync(p, b, Offset, c, Ct)))
    ];

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task All_read_overloads_return_the_same_data(string mode)
    {
        using var dir = new TempDirectory();
        var cdn = new FakeCdn();
        using HttpClient client = cdn.CreateClient();
        FFileManifest file = TestData.GetFile(TestData.Parse(client, o => Configure(o, mode, dir.Path)));
        byte[] expected = TestData.ExpectedFile(file);
        IReadOnlyList<FChunkPart> parts = file.ChunkParts;
        long length = file.FileSize;

        (string Name, long Position, int Count)[] cases =
        [
            ("inside the first part", 10, 100),
            ("across a part boundary", parts[1].FileOffset - 50, 100),
            ("over three parts", parts[1].FileOffset - 10, (int)parts[1].Size + 20),
            ("exactly to the end", length - 100, 100),
            ("crossing the end", length - 10, 100),
            ("at the end", length, 10),
            ("past the end", length + 5, 10),
            ("whole file", 0, (int)length),
            ("empty buffer", 123, 0)
        ];

        foreach ((string caseName, long position, int count) in cases)
        {
            int expectedCount = (int)Math.Clamp(length - position, 0, count);

            foreach ((string name, bool usesPosition, ReadMethod read) in Overloads)
            {
                if (usesPosition && position > length)
                    continue;

                FFileManifestStream stream = file.GetStream();
                byte[] buffer = new byte[Offset + count + 5];
                buffer.AsSpan().Fill(Guard);

                int bytesRead = await read(stream, position, buffer, count);

                string context = $"{name}, {caseName} (position {position}, count {count}, mode {mode})";
                Assert.True(expectedCount == bytesRead, $"{context}: read {bytesRead}, expected {expectedCount}");
                Assert.True(buffer.AsSpan(Offset, bytesRead).SequenceEqual(expected.AsSpan((int)Math.Min(position, length), bytesRead)), $"{context}: wrong data");
                Assert.True(buffer.AsSpan(0, Offset).IndexOfAnyExcept(Guard) < 0, $"{context}: wrote before the buffer");
                Assert.True(buffer.AsSpan(Offset + bytesRead).IndexOfAnyExcept(Guard) < 0, $"{context}: wrote past the bytes read");
                Assert.Equal(usesPosition ? position + bytesRead : 0, stream.Position);
            }
        }
    }

    [Fact]
    public void Position_validation_and_read_only_members()
    {
        using HttpClient client = new FakeCdn().CreateClient();
        FFileManifestStream stream = TestData.GetFile(TestData.Parse(client)).GetStream();

        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Position = stream.Length + 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Position = -1);
        stream.Position = stream.Length;
        Assert.Equal(0, stream.Read(new byte[10]));
        Assert.Equal(stream.Length - 5, stream.Seek(-5, SeekOrigin.End));
        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Read(new byte[10], 5, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => stream.ReadAt(0, new byte[10], -1, 5));

        stream.Flush(); // read-only streams treat Flush as a no-op
        Assert.False(stream.CanWrite);
        Assert.Throws<NotSupportedException>(() => stream.Write(new byte[1], 0, 1));
        Assert.Throws<NotSupportedException>(() => stream.SetLength(0));
    }

    [Fact]
    public async Task A_negative_position_is_rejected_by_every_positional_read()
    {
        var cdn = new FakeCdn();
        using HttpClient client = cdn.CreateClient();
        FFileManifestStream stream = TestData.GetFile(TestData.Parse(client)).GetStream();
        byte[] buffer = new byte[16];

        // 0 used to come back, which a read loop takes for the end of the stream
        Assert.Throws<ArgumentOutOfRangeException>(() => stream.ReadAt(-1, buffer.AsSpan()));
        Assert.Throws<ArgumentOutOfRangeException>(() => stream.ReadAt(long.MinValue, buffer, 0, buffer.Length));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => stream.ReadAtAsync(-1, buffer.AsMemory(), Ct).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => stream.ReadAtAsync(-1, buffer, 0, buffer.Length, Ct));
        // also for an empty buffer, the position is wrong either way
        Assert.Throws<ArgumentOutOfRangeException>(() => stream.ReadAt(-1, Span<byte>.Empty));
        Assert.Equal(0, cdn.Requests);
    }

    public static TheoryData<uint, string> WrappingOffsets => new()
    {
        // uint.MaxValue + 1 wrapped to 0 in 32 bits and returned the start of the chunk
        { uint.MaxValue, "ReadAt" },
        { uint.MaxValue, "ReadAtAsync" },
        { uint.MaxValue, "ReadAt-multi-part" },
        { uint.MaxValue, "ReadAtAsync-multi-part" },
        // above int.MaxValue without any addition: the cast made it negative
        { 0x8000_0000, "SaveBytesAsync" },
        { 0x8000_0000, "SaveFileAsync" },
        { 0x8000_0000, "SaveToAsync-stream" }
    };

    [Theory]
    [MemberData(nameof(WrappingOffsets))]
    public async Task A_part_offset_beyond_any_chunk_throws_instead_of_wrapping(uint partOffset, string method)
    {
        using var dir = new TempDirectory();
        var cdn = new FakeCdn();
        using HttpClient client = cdn.CreateClient();
        FBuildPatchAppManifest manifest = TestData.Parse(client);
        // part 0 starts at partOffset in chunk 0, part 1 is a normal part of chunk 1
        FFileManifest file = TestData.CreateFile(manifest, [(0, 0, 16), (1, 0, 16)]);
        var chunks = file.ChunkParts.ToDictionary(p => p.Chunk.Guid, p => p.Chunk);
        file.ChunkPartsArray[0] = new FChunkPart(file.ChunkParts[0].Chunk.Guid, partOffset, 16, 0, chunks);
        FFileManifestStream stream = file.GetStream();
        byte[] buffer = new byte[32];

        Func<Task> read = method switch
        {
            // one byte into the part: Offset + 1
            "ReadAt" => () => Task.Run(() => stream.ReadAt(1, buffer.AsSpan(0, 1)), Ct),
            "ReadAtAsync" => () => stream.ReadAtAsync(1, buffer.AsMemory(0, 1), Ct).AsTask(),
            "ReadAt-multi-part" => () => Task.Run(() => stream.ReadAt(1, buffer.AsSpan(0, 20)), Ct),
            "ReadAtAsync-multi-part" => () => stream.ReadAtAsync(1, buffer.AsMemory(0, 20), Ct).AsTask(),
            "SaveBytesAsync" => () => stream.SaveBytesAsync(buffer, cancellationToken: Ct),
            "SaveFileAsync" => () => stream.SaveFileAsync(Path.Combine(dir.Path, "file.bin"), cancellationToken: Ct),
            "SaveToAsync-stream" => () => stream.SaveToAsync(new RecordingStream(), cancellationToken: Ct),
            _ => throw new ArgumentOutOfRangeException(nameof(method))
        };

        InvalidDataException ex = await Assert.ThrowsAsync<InvalidDataException>(read);

        Assert.Contains("beyond any chunk", ex.Message);
        Assert.Equal(0, cdn.RequestsFor(file.ChunkParts[0].Chunk.Guid));
        Assert.Empty(dir.Files);
    }

    [Theory]
    [InlineData(TestData.ChunkBaseUrl)]
    [InlineData("https://cdn.test/CloudDir")]
    public async Task ChunkBaseUrl_works_with_and_without_a_trailing_slash(string baseUrl)
    {
        var cdn = new FakeCdn();
        using HttpClient client = cdn.CreateClient();
        FFileManifest file = TestData.GetFile(TestData.Parse(client, o => o.ChunkBaseUrl = baseUrl));
        FChunkInfo chunk = file.ChunkParts[0].Chunk;

        Assert.Equal($"{TestData.ChunkBaseUrl}{file.Manifest.Meta.ChunkSubdir}/{chunk.GroupNumber:D2}/{chunk.Hash:X16}_{chunk.Guid}.chunk", chunk.GetUrl(file.Manifest));
        Assert.Equal(TestData.ExpectedFile(file), await file.GetStream().SaveBytesAsync(cancellationToken: Ct));
    }

    [Fact]
    public async Task CopyToAsync_and_ReadExactly_use_the_span_and_memory_overloads()
    {
        using HttpClient client = new FakeCdn().CreateClient();
        FFileManifest file = TestData.GetFile(TestData.Parse(client));
        byte[] expected = TestData.ExpectedFile(file);

        var ms = new MemoryStream();
        await file.GetStream().CopyToAsync(ms, Ct);
        Assert.Equal(expected, ms.ToArray());

        byte[] exact = new byte[expected.Length];
        file.GetStream().ReadExactly(exact);
        Assert.Equal(expected, exact);
    }

    [Theory]
    [InlineData("cache-directory")]
    [InlineData("memory-cache-capacity-2")]
    [InlineData("no-cache")]
    public async Task Concurrent_ReadAt_and_ReadAtAsync_return_correct_data(string mode)
    {
        using var dir = new TempDirectory();
        using HttpClient client = new FakeCdn().CreateClient();
        FFileManifest file = TestData.GetFile(TestData.Parse(client, o => Configure(o, mode, dir.Path)));
        FFileManifestStream stream = file.GetStream();
        byte[] expected = TestData.ExpectedFile(file);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(async () =>
        {
            var rng = new Random(worker);
            byte[] buffer = new byte[256 * 1024];
            for (int i = 0; i < 40; i++)
            {
                long position = rng.NextInt64(expected.Length);
                int count = rng.Next(1, buffer.Length);
                int expectedCount = (int)Math.Min(count, expected.Length - position);

                int bytesRead = i % 2 == 0
                    ? stream.ReadAt(position, buffer.AsSpan(0, count))
                    : await stream.ReadAtAsync(position, buffer.AsMemory(0, count), Ct);

                Assert.Equal(expectedCount, bytesRead);
                Assert.True(buffer.AsSpan(0, bytesRead).SequenceEqual(expected.AsSpan((int)position, bytesRead)));
            }
        }, Ct)));
    }

    private static void Configure(ManifestParseOptions options, string mode, string directory)
    {
        switch (mode)
        {
            case "cache-directory":
                options.ChunkCacheDirectory = directory;
                break;
            case "memory-cache":
                break;
            case "memory-cache-capacity-2":
                options.ChunkMemoryCacheCapacity = 2;
                break;
            case "no-cache":
                options.ChunkMemoryCacheCapacity = 0;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
    }
}
