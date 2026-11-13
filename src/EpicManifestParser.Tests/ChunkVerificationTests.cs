using System.Buffers;
using System.Buffers.Binary;
using System.Net;

using EpicManifestParser.Tests.Fixtures;
using EpicManifestParser.UE;

using OffiUtils;

namespace EpicManifestParser.Tests;

/// <summary>
/// A downloaded chunk is checked against the manifest (header structure, GUID, size, SHA1) before it is returned or cached,
/// and a response cannot be larger than its chunk can be.
/// </summary>
public class ChunkVerificationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const byte Guard = 0xEE;

    public static TheoryData<string, bool> ModesAndSync => new()
    {
        { "cache-directory", false },
        { "cache-directory", true },
        { "memory-cache", false },
        { "memory-cache", true },
        { "no-cache", false },
        { "no-cache", true }
    };

    [Theory]
    [MemberData(nameof(ModesAndSync))]
    public async Task A_chunk_with_wrong_content_is_downloaded_again(string mode, bool sync)
    {
        using var dir = new TempDirectory();
        var cdn = new FakeCdn();
        using HttpClient client = cdn.CreateClient();
        FFileManifest file = TestData.GetFile(TestData.Parse(client, o => Configure(o, mode, dir.Path)));
        FChunkPart part = file.ChunkParts[0];
        FGuid guid = part.Chunk.Guid;
        // structurally valid and consistent in itself (the header has the SHA1 of the wrong data), only the manifest disagrees
        cdn.Behavior = (request, _) => Task.FromResult(request.Chunk == guid && request.Attempt == 1 ? FakeCdn.Ok(WrongChunkFile(guid, compress: true)) : null);

        byte[] actual = await ReadAsync(file, sync, 0, (int)part.Size);

        Assert.Equal(TestData.ExpectedFile(file).AsSpan(0, (int)part.Size).ToArray(), actual);
        Assert.Equal(2, cdn.RequestsFor(guid));
        if (mode == "cache-directory")
            CacheDirectoryTests.AssertCacheDirectoryIsConsistent(dir.Path, file);
    }

    [Theory]
    [MemberData(nameof(ModesAndSync))]
    public async Task A_chunk_that_never_verifies_throws_and_is_neither_returned_nor_cached(string mode, bool sync)
    {
        using var dir = new TempDirectory();
        var cdn = new FakeCdn();
        using HttpClient client = cdn.CreateClient();
        FBuildPatchAppManifest manifest = TestData.Parse(client, o =>
        {
            Configure(o, mode, dir.Path);
            o.MaxDownloadRetries = 2;
        });
        FFileManifest file = TestData.GetFile(manifest);
        FChunkPart part = file.ChunkParts[0];
        FGuid guid = part.Chunk.Guid;
        cdn.Behavior = (request, _) => Task.FromResult(request.Chunk == guid ? FakeCdn.Ok(WrongChunkFile(guid, compress: false)) : null);
        byte[] buffer = new byte[part.Size];
        buffer.AsSpan().Fill(Guard);

        InvalidDataException ex = await Assert.ThrowsAsync<InvalidDataException>(() => ReadIntoAsync(file, sync, 0, buffer));

        Assert.Contains("SHA1", ex.Message);
        Assert.Equal(3, cdn.RequestsFor(guid));
        Assert.Equal(3, ex.Data["Attempts"]);
        Assert.True(buffer.AsSpan().IndexOfAnyExcept(Guard) < 0, "unverified data was copied to the caller");
        Assert.Empty(dir.Files);
        Assert.False(manifest.ChunkMemoryCache?.Contains(guid) ?? false, "unverified data is in the memory cache");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_header_GUID_must_be_the_requested_chunk(bool verifyHashes)
    {
        var cdn = new FakeCdn();
        using HttpClient client = cdn.CreateClient();
        FFileManifest file = TestData.GetFile(TestData.Parse(client, o =>
        {
            o.MaxDownloadRetries = 1;
            o.VerifyChunkHashes = verifyHashes;
        }));
        FChunkPart part = file.ChunkParts[0];
        FGuid guid = part.Chunk.Guid;
        FGuid other = TestData.ChunkGuids.First(g => g != guid);
        // the right data, only the GUID in the header is another one
        cdn.Behavior = (request, _) => Task.FromResult(request.Chunk == guid
            ? FakeCdn.Ok(TestData.BuildChunkFile(new TestData.ChunkFileSpec(other, TestData.GetChunkData(guid), false)))
            : null);

        InvalidDataException ex = await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync(file, false, 0, (int)part.Size));

        Assert.Contains("was requested", ex.Message);
        Assert.Equal(2, cdn.RequestsFor(guid));
    }

    [Fact]
    public async Task Without_hash_verification_wrong_content_of_the_right_size_is_returned()
    {
        var cdn = new FakeCdn();
        using HttpClient client = cdn.CreateClient();
        FFileManifest file = TestData.GetFile(TestData.Parse(client, o => o.VerifyChunkHashes = false));
        FChunkPart part = file.ChunkParts[0];
        FGuid guid = part.Chunk.Guid;
        cdn.Behavior = (request, _) => Task.FromResult(request.Chunk == guid ? FakeCdn.Ok(WrongChunkFile(guid, compress: true)) : null);

        byte[] actual = await ReadAsync(file, false, 0, (int)part.Size);

        Assert.Equal(WrongData(guid).AsSpan((int)part.Offset, (int)part.Size).ToArray(), actual);
        Assert.Equal(1, cdn.RequestsFor(guid));
        Assert.True(new ManifestParseOptions().VerifyChunkHashes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_data_must_not_be_smaller_than_the_manifest_says(bool compress)
    {
        var cdn = new FakeCdn();
        using HttpClient client = cdn.CreateClient();
        FFileManifest file = TestData.GetFile(TestData.Parse(client, o => o.MaxDownloadRetries = 0));
        FChunkPart part = file.ChunkParts[0];
        FGuid guid = part.Chunk.Guid;
        byte[] data = TestData.GetChunkData(guid);
        // a header that agrees with its data, including the SHA1
        cdn.Behavior = (request, _) => Task.FromResult(request.Chunk == guid
            ? FakeCdn.Ok(TestData.BuildChunkFile(new TestData.ChunkFileSpec(guid, data[..^1], compress)))
            : null);

        InvalidDataException ex = await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync(file, false, 0, (int)part.Size));

        Assert.Contains("the manifest says", ex.Message);
    }

    public static TheoryData<string, bool, bool> ModesAndSyncAndCompression
    {
        get
        {
            var data = new TheoryData<string, bool, bool>();
            foreach ((string mode, bool sync) in new[] { ("cache-directory", false), ("cache-directory", true), ("memory-cache", false), ("no-cache", true) })
            {
                data.Add(mode, sync, false);
                data.Add(mode, sync, true);
            }
            return data;
        }
    }

    // UE's ValidateAndRead accepts a chunk file with more data than the manifest's window and uses the first window bytes
    [Theory]
    [MemberData(nameof(ModesAndSyncAndCompression))]
    public async Task A_chunk_file_with_more_data_than_the_manifest_says_is_cut_to_the_manifest_size(string mode, bool sync, bool compress)
    {
        using var dir = new TempDirectory();
        var cdn = new FakeCdn();
        using HttpClient client = cdn.CreateClient();
        FBuildPatchAppManifest manifest = TestData.Parse(client, o =>
        {
            Configure(o, mode, dir.Path);
            o.MaxDownloadRetries = 0;
        });
        FFileManifest file = TestData.GetFile(manifest);
        FChunkPart part = file.ChunkParts[0];
        FGuid guid = part.Chunk.Guid;
        byte[] data = TestData.GetChunkData(guid);
        byte[] larger = new byte[data.Length + 4096];
        data.CopyTo(larger, 0);
        larger.AsSpan(data.Length).Fill(0x5A);
        // the header has the SHA1 of all of its data, which is not the one of the manifest
        cdn.Behavior = (request, _) => Task.FromResult(request.Chunk == guid
            ? FakeCdn.Ok(TestData.BuildChunkFile(new TestData.ChunkFileSpec(guid, larger, compress)))
            : null);

        byte[] actual = await ReadAsync(file, sync, 0, (int)part.Size);
        byte[] again = await ReadAsync(file, sync, 0, (int)part.Size);

        byte[] expected = TestData.ExpectedFile(file).AsSpan(0, (int)part.Size).ToArray();
        Assert.Equal(expected, actual);
        Assert.Equal(expected, again);
        if (mode == "cache-directory")
        {
            // what is cached has the size the manifest records, so a later run adopts the file
            Assert.Equal(part.Chunk.DataSizeUncompressed, new FileInfo(TestData.CacheFilePath(dir.Path, part.Chunk)).Length);
            CacheDirectoryTests.AssertCacheDirectoryIsConsistent(dir.Path, file);
        }
        if (mode != "no-cache")
            Assert.Equal(1, cdn.RequestsFor(guid));
    }

    [Fact]
    public async Task A_larger_chunk_file_is_checked_against_the_SHA1_of_its_header()
    {
        var cdn = new FakeCdn();
        using HttpClient client = cdn.CreateClient();
        FFileManifest file = TestData.GetFile(TestData.Parse(client, o => o.MaxDownloadRetries = 0));
        FChunkPart part = file.ChunkParts[0];
        FGuid guid = part.Chunk.Guid;
        byte[] larger = new byte[TestData.GetChunkData(guid).Length + 4096];
        TestData.GetChunkData(guid).CopyTo(larger, 0);
        // the header still has the SHA1 of the window alone
        cdn.Behavior = (request, _) => Task.FromResult(request.Chunk == guid
            ? FakeCdn.Ok(TestData.BuildChunkFile(new TestData.ChunkFileSpec(guid, larger, false) { Sha = TestData.GetChunkSha(guid) }))
            : null);

        InvalidDataException ex = await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync(file, false, 0, (int)part.Size));

        Assert.Contains("SHA1", ex.Message);
    }

    [Fact]
    public async Task A_chunk_file_cannot_have_more_data_than_the_limit()
    {
        var cdn = new FakeCdn();
        using HttpClient client = cdn.CreateClient();
        FFileManifest file = TestData.GetFile(TestData.Parse(client, o => o.MaxDownloadRetries = 0));
        FChunkPart part = file.ChunkParts[0];
        FGuid guid = part.Chunk.Guid;
        Assert.Equal(FChunkInfo.MaxUnknownDataSize, part.Chunk.MaxDataSize);
        // compresses to a few KiB, so only the data size is over the limit, not the response
        byte[] tooLarge = new byte[FChunkInfo.MaxUnknownDataSize + 1];
        cdn.Behavior = (request, _) => Task.FromResult(request.Chunk == guid
            ? FakeCdn.Ok(TestData.BuildChunkFile(new TestData.ChunkFileSpec(guid, tooLarge, true)))
            : null);

        InvalidDataException ex = await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync(file, false, 0, (int)part.Size));

        Assert.Contains("limit", ex.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_forged_uncompressed_size_is_rejected_before_a_buffer_is_rented(bool sizeKnown)
    {
        using HttpClient client = new FakeCdn().CreateClient();
        FBuildPatchAppManifest manifest = TestData.Parse(client);
        // a chunk of the manifest (1 MiB) or one that no chunk list describes (size 0: the 16 MiB cap)
        FChunkInfo chunk = sizeKnown ? TestData.GetFile(manifest).ChunkParts[0].Chunk : new FChunkInfo { Guid = new FGuid(1, 2, 3, 4) };
        Assert.Equal(sizeKnown, chunk.DataSizeUncompressed != 0);
        byte[] file = TestData.BuildChunkFile(new TestData.ChunkFileSpec(chunk.Guid, new byte[4096], true) { DataSizeUncompressed = 1 << 30 });
        byte[] rented = ArrayPool<byte>.Shared.Rent(file.Length);
        file.CopyTo(rented, 0);
        var body = new RentedBuffer(rented, 0, file.Length);

        long before = GC.GetAllocatedBytesForCurrentThread();
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => FChunkInfo.DecodeChunk((chunk, manifest.Options), body));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        // when the decoder throws, the body is the caller's to return
        body.Return();

        Assert.Contains(sizeKnown ? "the manifest says" : "limit", ex.Message);
        Assert.True(allocated < 1024 * 1024, $"{allocated} bytes allocated");
    }

    public static TheoryData<uint, int?, bool> HeaderVersions => new()
    {
        // 41 bytes, no SHA1 in the header: the one of the manifest is compared
        { 1, null, true },
        { 2, null, true },
        { 3, null, true },
        // version 4 adds the encryption fields (98 bytes), a later version at least those: fields that are not read are skipped
        { 4, 98, true },
        { 5, 98, true },
        { 5, 120, true },
        { 4, 66, false },
        { 4, 97, false },
        { 5, 66, false },
        // the header size of an older version: the SHA1, hash type and size would be read from the data
        { 3, 41, false },
        { 3, 62, false },
        { 2, 41, false },
        // EChunkVersion::Invalid
        { 0, null, false }
    };

    [Theory]
    [MemberData(nameof(HeaderVersions))]
    public async Task Every_field_of_the_header_version_lies_inside_the_header(uint version, int? headerSize, bool valid)
    {
        var cdn = new FakeCdn();
        using HttpClient client = cdn.CreateClient();
        FFileManifest file = TestData.GetFile(TestData.Parse(client, o => o.MaxDownloadRetries = 0));
        FChunkPart part = file.ChunkParts[0];
        FGuid guid = part.Chunk.Guid;
        // raw: the uncompressed size versions 1 and 2 imply does not matter
        cdn.Behavior = (request, _) => Task.FromResult(request.Chunk == guid
            ? FakeCdn.Ok(TestData.BuildChunkFile(new TestData.ChunkFileSpec(guid, TestData.GetChunkData(guid), false) { Version = version, HeaderSize = headerSize }))
            : null);
        byte[] expected = TestData.ExpectedFile(file).AsSpan(0, (int)part.Size).ToArray();

        if (valid)
        {
            Assert.Equal(expected, await ReadAsync(file, false, 0, (int)part.Size));
        }
        else
        {
            InvalidDataException ex = await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync(file, false, 0, (int)part.Size));
            Assert.Contains("version", ex.Message);
        }
    }

    [Theory]
    [InlineData("shorter-than-any-header")]
    [InlineData("header-size-beyond-the-response")]
    [InlineData("header-size-negative")]
    public async Task Truncated_or_oversized_headers_are_rejected(string corruption)
    {
        var cdn = new FakeCdn();
        using HttpClient client = cdn.CreateClient();
        FFileManifest file = TestData.GetFile(TestData.Parse(client, o => o.MaxDownloadRetries = 1));
        FChunkPart part = file.ChunkParts[0];
        FGuid guid = part.Chunk.Guid;
        cdn.Behavior = (request, _) =>
        {
            if (request.Chunk != guid)
                return Task.FromResult<HttpResponseMessage?>(null);

            byte[] body = (byte[])request.Body.Clone();
            switch (corruption)
            {
                case "shorter-than-any-header":
                    body = body[..40];
                    break;
                case "header-size-beyond-the-response":
                    BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(8), body.Length + 1);
                    break;
                case "header-size-negative":
                    BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(8), -1);
                    break;
            }
            return Task.FromResult<HttpResponseMessage?>(FakeCdn.Ok(body));
        };

        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync(file, false, 0, (int)part.Size));

        Assert.Equal(2, cdn.RequestsFor(guid));
    }

    [Theory]
    // the header has the SHA1 of the real data, the served data is wrong
    [InlineData(TestData.HashTypeSha1, false, "SHA1")]
    // the header has the SHA1 of the served data: nothing else to compare with
    [InlineData(TestData.HashTypeSha1, true, null)]
    // only a rolling hash, as in a version 1 header: UE's CheckChunkHash compares it
    [InlineData(1, false, "rolling hash")]
    [InlineData(1, true, null)]
    // both: only the SHA1 counts, as in UE
    [InlineData(3, true, null)]
    // neither: UE's ValidateAndRead rejects the header
    [InlineData(0, true, "no hash type")]
    public async Task Without_a_hash_in_the_manifest_the_hash_of_the_header_is_compared(byte hashType, bool headerHashesServedData, string? rejection)
    {
        var cdn = new FakeCdn();
        using HttpClient client = cdn.CreateClient();
        FFileManifest file = TestData.GetFile(TestData.Parse(client, o => o.MaxDownloadRetries = 0));
        FChunkPart part = file.ChunkParts[0];
        FGuid guid = part.Chunk.Guid;
        part.Chunk.ShaHash = default;
        byte[] wrong = WrongData(guid);
        byte[] hashed = headerHashesServedData ? wrong : TestData.GetChunkData(guid);
        cdn.Behavior = (request, _) => Task.FromResult(request.Chunk == guid
            ? FakeCdn.Ok(TestData.BuildChunkFile(new TestData.ChunkFileSpec(guid, wrong, true)
            {
                HashType = hashType,
                Sha = FSHAHash.Compute(hashed),
                RollingHash = FRollingHash.GetHashForDataSet(hashed)
            }))
            : null);

        if (rejection is null)
        {
            Assert.Equal(wrong.AsSpan((int)part.Offset, (int)part.Size).ToArray(), await ReadAsync(file, false, 0, (int)part.Size));
        }
        else
        {
            InvalidDataException ex = await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync(file, false, 0, (int)part.Size));
            Assert.Contains(rejection, ex.Message);
        }
    }

    [Fact]
    public async Task A_chunk_no_chunk_list_describes_is_checked_against_its_header()
    {
        var cdn = new FakeCdn();
        using HttpClient client = cdn.CreateClient();
        FFileManifest file = TestData.CreateFile(TestData.Parse(client, o => o.MaxDownloadRetries = 0), [(0, 100, 1000)]);
        FChunkInfo chunk = file.ChunkParts[0].Chunk;
        Assert.Equal(0u, chunk.DataSizeUncompressed);
        Assert.Equal(default, chunk.ShaHash);
        // the data was changed after the header got its SHA1
        byte[] wrong = WrongData(chunk.Guid);
        cdn.Behavior = (request, _) => Task.FromResult(request.Chunk == chunk.Guid
            ? FakeCdn.Ok(TestData.BuildChunkFile(new TestData.ChunkFileSpec(chunk.Guid, wrong, false) { Sha = TestData.GetChunkSha(chunk.Guid) }))
            : null);

        InvalidDataException ex = await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync(file, false, 0, 1000));

        Assert.Contains("SHA1", ex.Message);
    }

    [Theory]
    [InlineData(FChunkInfo.MaxUnknownDataSize, true)]
    [InlineData(FChunkInfo.MaxUnknownDataSize + 1, false)]
    public async Task A_chunk_without_a_size_in_the_manifest_may_have_at_most_16_MiB(int size, bool accepted)
    {
        Assert.Equal(16 * 1024 * 1024, FChunkInfo.MaxUnknownDataSize);
        var cdn = new FakeCdn();
        using HttpClient client = cdn.CreateClient();
        FFileManifest file = TestData.CreateFile(TestData.Parse(client, o => o.MaxDownloadRetries = 0), [(0, 0, 100)]);
        FGuid guid = file.ChunkParts[0].Chunk.Guid;
        byte[] data = new byte[size];
        new Random(size).NextBytes(data);
        byte[] chunkFile = TestData.BuildChunkFile(new TestData.ChunkFileSpec(guid, data, false));
        cdn.Behavior = (request, _) => Task.FromResult(request.Chunk == guid ? FakeCdn.Ok(chunkFile) : null);

        if (accepted)
        {
            Assert.Equal(data[..100], await ReadAsync(file, false, 0, 100));
        }
        else
        {
            InvalidDataException ex = await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync(file, false, 0, 100));
            Assert.Contains("limit", ex.Message);
        }
    }

    [Fact]
    public async Task A_Content_Length_beyond_what_the_chunk_can_have_is_rejected_before_a_buffer_is_rented()
    {
        var cdn = new FakeCdn();
        using HttpClient client = cdn.CreateClient();
        FFileManifest file = TestData.GetFile(TestData.Parse(client, o => o.MaxDownloadRetries = 1));
        FChunkPart part = file.ChunkParts[0];
        FGuid guid = part.Chunk.Guid;
        cdn.Behavior = (request, _) =>
        {
            if (request.Chunk != guid)
                return Task.FromResult<HttpResponseMessage?>(null);

            // announces 1.5 GiB, which used to be rented in full before the first byte arrived
            var content = new StreamContent(new MemoryStream(request.Body));
            content.Headers.ContentLength = 3L << 29;
            return Task.FromResult<HttpResponseMessage?>(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        };

        long before = GC.GetTotalAllocatedBytes(true);
        InvalidDataException ex = await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync(file, false, 0, (int)part.Size));
        long allocated = GC.GetTotalAllocatedBytes(true) - before;

        Assert.Contains("Content-Length", ex.Message);
        Assert.Equal(2, cdn.RequestsFor(guid));
        // other tests run at the same time; a rented 1.5 GiB buffer would be far above this
        Assert.True(allocated < 512L * 1024 * 1024, $"{allocated} bytes allocated");
    }

    [Fact]
    public async Task A_body_without_Content_Length_is_rejected_once_it_grows_past_what_the_chunk_can_have()
    {
        var cdn = new FakeCdn();
        using HttpClient client = cdn.CreateClient();
        FFileManifest file = TestData.GetFile(TestData.Parse(client, o => o.MaxDownloadRetries = 0));
        FChunkPart part = file.ChunkParts[0];
        FGuid guid = part.Chunk.Guid;
        // a chunk file may carry more data than the manifest's window, up to MaxUnknownDataSize
        Assert.Equal(FChunkInfo.MaxUnknownDataSize + FChunkInfo.MaxUnknownDataSize / 8 + 64 * 1024, part.Chunk.MaxDownloadSize);
        byte[] body = new byte[part.Chunk.MaxDownloadSize + 1];
        TestData.GetChunkFile(guid).CopyTo(body, 0);
        cdn.Behavior = (request, _) => Task.FromResult(request.Chunk == guid ? FakeCdn.NoContentLength(body) : null);

        InvalidDataException ex = await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync(file, false, 0, (int)part.Size));

        Assert.Contains("exceeds the limit", ex.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DownloadAsync_accepts_bodies_up_to_maxResponseSize(bool contentLength)
    {
        byte[] blob = new byte[300 * 1024];
        new Random(9).NextBytes(blob);
        var cdn = new FakeCdn
        {
            Behavior = contentLength ? null : static (request, _) => Task.FromResult<HttpResponseMessage?>(FakeCdn.NoContentLength(request.Body))
        };
        cdn.AddBlob("/blob", blob);
        using HttpClient client = cdn.CreateClient();
        var options = new ManifestParseOptions { Client = client, MaxDownloadRetries = 0 };
        var uri = new Uri("https://cdn.test/blob");

        RentedBuffer body = await HttpDownloader.DownloadAsync(options, uri, 1000, blob.Length, 0, static (_, rented) => rented, Ct);
        Assert.Equal(blob, body.Span.ToArray());
        body.Return();

        InvalidDataException ex = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            (await HttpDownloader.DownloadAsync(options, uri, 1000, blob.Length - 1, 0, static (_, rented) => rented, Ct)).Return());
        Assert.Contains("limit", ex.Message);
    }

    [Fact]
    public async Task DownloadAsync_without_a_limit_reads_bodies_larger_than_any_chunk()
    {
        // manifests are downloaded unbounded: larger than the limit of a chunk of unknown size, without Content-Length
        byte[] blob = new byte[20 * 1024 * 1024];
        new Random(10).NextBytes(blob);
        var cdn = new FakeCdn { Behavior = static (request, _) => Task.FromResult<HttpResponseMessage?>(FakeCdn.NoContentLength(request.Body)) };
        cdn.AddBlob("/manifest", blob);
        using HttpClient client = cdn.CreateClient();
        var options = new ManifestParseOptions { Client = client, MaxDownloadRetries = 0 };

        RentedBuffer body = await HttpDownloader.DownloadAsync(options, new Uri("https://cdn.test/manifest"), 1024 * 1024, HttpDownloader.Unbounded, 0,
            static (_, rented) => rented, Ct);
        try
        {
            Assert.True(blob.AsSpan().SequenceEqual(body.Span));
        }
        finally
        {
            body.Return();
        }
    }

    [Fact]
    public void Header_fields_are_parsed()
    {
        FGuid guid = TestData.ChunkGuids.First();
        byte[] data = TestData.GetChunkData(guid);
        byte[] file = TestData.BuildChunkFile(new TestData.ChunkFileSpec(guid, data, true));

        FChunkHeader header = FChunkHeader.Parse(file);

        Assert.Equal(guid, header.Guid);
        Assert.Equal(FSHAHash.Compute(data), header.ShaHash);
        Assert.Equal(EChunkHashFlags.Sha1, header.HashType);
        Assert.Equal(EChunkStorageFlags.Compressed, header.StoredAs);
        Assert.Equal(66, header.HeaderSize);
        Assert.Equal(file.Length - 66, header.DataSizeCompressed);
        Assert.Equal(data.Length, header.DataSizeUncompressed);

        // version 1: rolling hash only, 1 MiB
        FChunkHeader original = FChunkHeader.Parse(TestData.BuildChunkFile(new TestData.ChunkFileSpec(guid, data, false) { Version = 1 }));
        Assert.Equal(EChunkHashFlags.RollingPoly64, original.HashType);
        Assert.Equal(1024 * 1024, original.DataSizeUncompressed);
        Assert.Equal(41, original.HeaderSize);
    }

    [Fact]
    public void The_real_chunk_fixture_passes_every_check()
    {
        // a chunk file from Epic's CDN (header version 3, zlib)
        byte[] chunkFile = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "files", "chunk_compressed.bin"));
        FChunkHeader header = FChunkHeader.Parse(chunkFile);
        var chunk = new FChunkInfo { Guid = header.Guid, DataSizeUncompressed = (uint)header.DataSizeUncompressed, ShaHash = header.ShaHash };
        var options = new ManifestParseOptions { Decompressor = DecompressorBuilder.Default.Build() };
        byte[] rented = ArrayPool<byte>.Shared.Rent(chunkFile.Length);
        chunkFile.CopyTo(rented, 0);

        RentedBuffer data = FChunkInfo.DecodeChunk((chunk, options), new RentedBuffer(rented, 0, chunkFile.Length));
        try
        {
            Assert.Equal(1024 * 1024, data.Length);
            Assert.Equal(header.ShaHash, FSHAHash.Compute(data.Span));
            // the port of UE's rolling hash gives the hash Epic's build tool wrote into the header
            Assert.Equal(header.RollingHash, FRollingHash.GetHashForDataSet(data.Span));
        }
        finally
        {
            data.Return();
        }
    }

    /// <summary>The data of a chunk with one byte changed.</summary>
    private static byte[] WrongData(FGuid guid)
    {
        byte[] data = (byte[])TestData.GetChunkData(guid).Clone();
        data[data.Length / 2] ^= 0x5A;
        return data;
    }

    /// <summary>A valid chunk file of <see cref="WrongData"/>: the right GUID and size, a header SHA1 that matches the data.</summary>
    private static byte[] WrongChunkFile(FGuid guid, bool compress) =>
        TestData.BuildChunkFile(new TestData.ChunkFileSpec(guid, WrongData(guid), compress));

    private static async Task<byte[]> ReadAsync(FFileManifest file, bool sync, long position, int count)
    {
        byte[] buffer = new byte[count];
        await ReadIntoAsync(file, sync, position, buffer);
        return buffer;
    }

    private static async Task ReadIntoAsync(FFileManifest file, bool sync, long position, byte[] buffer)
    {
        FFileManifestStream stream = file.GetStream();
        int read = sync
            ? await Task.Run(() => stream.ReadAt(position, buffer.AsSpan()), Ct)
            : await stream.ReadAtAsync(position, buffer, Ct);
        Assert.Equal(buffer.Length, read);
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
            case "no-cache":
                options.ChunkMemoryCacheCapacity = 0;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
    }
}
