using EpicManifestParser.Tests.Fixtures;
using EpicManifestParser.UE;

namespace EpicManifestParser.Tests;

public class SaveToTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> MemoryStreamScenarios =>
    [
        "empty",
        "presized",
        "after-prefix",
        "overwrite-middle",
        "gap-behind-position",
        "longer-than-file",
        "fixed-exact",
        "fixed-larger",
        "fixed-with-offset",
        "fixed-not-visible",
        "fixed-too-small",
        "read-only",
        "subclass"
    ];

    /// <summary>
    /// After <see cref="FFileManifestStream.SaveToAsync(Stream,int?,CancellationToken)"/> the stream must look exactly as if the
    /// chunk parts had been written through <see cref="MemoryStream.Write(byte[],int,int)"/>: content, length, position, exceptions.
    /// </summary>
    [Theory]
    [MemberData(nameof(MemoryStreamScenarios))]
    public async Task SaveToAsync_MemoryStream_behaves_like_Write(string scenario)
    {
        var cdn = new FakeCdn();
        using HttpClient client = cdn.CreateClient();
        FFileManifest file = TestData.GetFile(TestData.Parse(client));
        byte[] expected = TestData.ExpectedFile(file);
        int size = expected.Length;

        MemoryStream actual = Create(scenario, size);
        MemoryStream reference = Create(scenario, size);

        Exception? referenceError = Record.Exception(() =>
        {
            foreach (FChunkPart part in file.ChunkParts)
                reference.Write(expected, (int)part.FileOffset, (int)part.Size);
        });
        Exception? actualError = await Record.ExceptionAsync(() => file.GetStream().SaveToAsync(actual, cancellationToken: Ct));

        Assert.Equal(referenceError?.GetType(), actualError?.GetType());
        Assert.Equal(reference.Length, actual.Length);
        Assert.Equal(reference.Position, actual.Position);
        Assert.True(reference.ToArray().AsSpan().SequenceEqual(actual.ToArray()));
    }

    [Fact]
    public async Task SaveToAsync_MemoryStream_with_offset_buffer_leaves_bytes_outside_the_stream_untouched()
    {
        var cdn = new FakeCdn();
        using HttpClient client = cdn.CreateClient();
        FFileManifest file = TestData.GetFile(TestData.Parse(client));
        byte[] expected = TestData.ExpectedFile(file);

        byte[] buffer = new byte[expected.Length + 30];
        buffer.AsSpan().Fill(0xCC);
        var ms = new MemoryStream(buffer, 7, expected.Length + 10, true, true) { Position = 3 };

        await file.GetStream().SaveToAsync(ms, cancellationToken: Ct);

        Assert.Equal(expected.Length + 10, ms.Length);
        Assert.Equal(3 + expected.Length, ms.Position);
        Assert.True(buffer.AsSpan(7 + 3, expected.Length).SequenceEqual(expected));
        Assert.All(buffer[..10], b => Assert.Equal(0xCC, b));
        Assert.All(buffer[(10 + expected.Length)..], b => Assert.Equal(0xCC, b));
    }

    [Fact]
    public async Task SaveToAsync_MemoryStream_reports_progress()
    {
        var cdn = new FakeCdn();
        using HttpClient client = cdn.CreateClient();
        FFileManifest file = TestData.GetFile(TestData.Parse(client));
        long lastTotal = 0;
        object state = new();

        var ms = new MemoryStream();
        await file.GetStream().SaveToAsync(ms, e =>
        {
            Assert.Same(state, e.UserState);
            lastTotal = Math.Max(lastTotal, e.TotalBytesWritten);
        }, state, cancellationToken: Ct);

        Assert.Equal(file.FileSize, lastTotal);
        Assert.Equal(TestData.ExpectedFile(file), ms.ToArray());
    }

    private static MemoryStream Create(string scenario, int size)
    {
        byte[] Pattern(int length)
        {
            byte[] bytes = new byte[length];
            new Random(length).NextBytes(bytes);
            return bytes;
        }

        MemoryStream WithContent(int length, long position)
        {
            var ms = new MemoryStream();
            ms.Write(Pattern(length));
            ms.Position = position;
            return ms;
        }

        return scenario switch
        {
            "empty" => new MemoryStream(),
            "presized" => new MemoryStream(size),
            "after-prefix" => WithContent(10, 10),
            "overwrite-middle" => WithContent(100, 50),
            "gap-behind-position" => WithContent(10, 20),
            "longer-than-file" => WithContent(size + 100, 0),
            "fixed-exact" => new MemoryStream(new byte[size], 0, size, true, true),
            "fixed-larger" => new MemoryStream(Pattern(size + 16), 0, size + 16, true, true),
            "fixed-with-offset" => new MemoryStream(Pattern(size + 30), 7, size + 10, true, true) { Position = 3 },
            "fixed-not-visible" => new MemoryStream(new byte[size + 16]),
            "fixed-too-small" => new MemoryStream(new byte[size - 1], 0, size - 1, true, true),
            "read-only" => new MemoryStream(new byte[size], false),
            "subclass" => new DerivedMemoryStream(),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
    }

    private sealed class DerivedMemoryStream : MemoryStream;
}
