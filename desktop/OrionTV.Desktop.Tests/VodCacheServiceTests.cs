using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using OrionTV.Desktop.App;

namespace OrionTV.Desktop.Tests;

public sealed class VodCacheServiceTests
{
    [Fact]
    [Trait("Category", "NetworkIntegration")]
    public async Task CanPrebufferConfiguredRealHlsUrlWhenProvided()
    {
        var url = Environment.GetEnvironmentVariable("ORION_TEST_HLS_URL");
        if (string.IsNullOrWhiteSpace(url))
        {
            Assert.NotEqual("1", Environment.GetEnvironmentVariable("ORION_REQUIRE_HLS_TEST"));
            return;
        }

        using var service = new VodCacheService();
        var root = Path.Combine(Path.GetTempPath(), "orion-vod-cache-integration-" + Guid.NewGuid().ToString("N"));
        try
        {
            var session = await service.StartAsync(url, "Network Integration", root, 15);
            Assert.NotNull(session);
            _ = await session!.Ready.WaitAsync(TimeSpan.FromSeconds(90));
            Assert.True(session.Progress.DownloadedSeconds >= 15 || session.Progress.IsComplete);
            Assert.True(session.Progress.CompletedSegments > 0);
            await service.CloseAsync(session, deleteFiles: true, continueDownload: false);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task DownloadsSegmentsBeforeServingLocalPlaybackAndDeletesSessionCache()
    {
        await using var upstream = new FakeHlsServer();
        using var service = new VodCacheService();
        var root = Path.Combine(Path.GetTempPath(), "orion-vod-cache-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var session = await service.StartAsync(
                upstream.PlaylistUrl,
                "Test Episode",
                root,
                15);

            Assert.NotNull(session);
            var localPlaylistUrl = await session!.Ready.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.StartsWith("http://127.0.0.1:", localPlaylistUrl);

            using var http = new HttpClient();
            var playlist = await http.GetStringAsync(localPlaylistUrl);
            Assert.Contains("/vod/", playlist);
            Assert.Contains("segment_00000.ts", playlist);
            var firstSegmentUrl = playlist.Split('\n')
                .First(line => line.StartsWith("http://", StringComparison.Ordinal) && line.Contains("segment_00000.ts"));
            var firstSegment = await http.GetByteArrayAsync(firstSegmentUrl);
            Assert.Equal(1024, firstSegment.Length);

            await session.DownloadTask.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(session.Progress.IsComplete);
            Assert.Equal(3, session.Progress.CompletedSegments);
            Assert.True(File.Exists(session.OfflinePlaylistPath));
            Assert.Contains("segment_00002.ts", await File.ReadAllTextAsync(session.OfflinePlaylistPath));
            Assert.True(File.Exists(session.MergedFilePath));
            Assert.True(new FileInfo(session.MergedFilePath).Length >= 3 * 1024);

            await service.CloseAsync(session, deleteFiles: true, continueDownload: false);
            Assert.False(Directory.Exists(session.Directory));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task DownloadsSegmentsConcurrentlyWhileCommittingThemInOrder()
    {
        await using var upstream = new FakeHlsServer(delayAllSegments: true);
        using var service = new VodCacheService();
        var root = Path.Combine(Path.GetTempPath(), "orion-vod-cache-parallel-" + Guid.NewGuid().ToString("N"));
        try
        {
            var session = await service.StartAsync(
                upstream.PlaylistUrl,
                "Parallel Episode",
                root,
                15);

            Assert.NotNull(session);
            await session!.DownloadTask.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(session.Progress.IsComplete);
            Assert.InRange(upstream.MaxConcurrentSegmentRequests, 2, 4);
            Assert.Equal(3, session.Progress.CompletedSegments);
            Assert.Equal(3 * 1024, new FileInfo(session.MergedFilePath).Length);
            var merged = await File.ReadAllBytesAsync(session.MergedFilePath);
            Assert.Equal(FakeTsSegment(1), merged[..1024]);
            Assert.Equal(FakeTsSegment(2), merged[1024..2048]);
            Assert.Equal(FakeTsSegment(3), merged[2048..]);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task RetriesSegmentWhoseBodyStallsInsteadOfHangingTheDownload()
    {
        // 第二个分片第一次请求时：响应头和一半数据到了，之后连接既不关闭也不再来数据。
        await using var upstream = new FakeHlsServer(stallSecondSegmentBodyOnce: true);
        using var service = new VodCacheService { BodyStallTimeout = TimeSpan.FromMilliseconds(500) };
        var root = Path.Combine(Path.GetTempPath(), "orion-vod-cache-stall-" + Guid.NewGuid().ToString("N"));
        try
        {
            var session = await service.StartAsync(
                upstream.PlaylistUrl,
                "Stalled Episode",
                root,
                15);

            Assert.NotNull(session);
            await session!.DownloadTask.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(session.Progress.IsComplete);
            Assert.Null(session.Progress.Error);
            Assert.True(upstream.SegmentRequestCount(2) >= 2);
            // 断流那次只收到的半个分片不能混进成品。
            var merged = await File.ReadAllBytesAsync(session.MergedFilePath);
            Assert.Equal(3 * 1024, merged.Length);
            Assert.Equal(FakeTsSegment(2), merged[1024..2048]);

            await service.CloseAsync(session, deleteFiles: true, continueDownload: false);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("<html><body><h1>403 Forbidden</h1></body></html>")]
    [InlineData("")]
    public async Task RetriesSegmentWhoseOkResponseIsNotMediaInsteadOfMergingIt(string badBody)
    {
        // 防盗链/地区限制/代理出口异常：第二个分片第一次拿到的是 200 + 错误页（或空正文），不是视频数据。
        await using var upstream = new FakeHlsServer(
            secondSegmentBadResponses: 1,
            badSegmentBody: badBody);
        using var service = new VodCacheService();
        var root = Path.Combine(Path.GetTempPath(), "orion-vod-cache-badbody-" + Guid.NewGuid().ToString("N"));
        try
        {
            var session = await service.StartAsync(upstream.PlaylistUrl, "Bad Body Episode", root, 15);

            Assert.NotNull(session);
            await session!.DownloadTask.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(session.Progress.IsComplete);
            Assert.Null(session.Progress.Error);
            Assert.Equal(2, upstream.SegmentRequestCount(2));
            // 错误页不能被当成分片拼进成品。
            Assert.Equal(
                FakeTsSegment(1).Concat(FakeTsSegment(2)).Concat(FakeTsSegment(3)).ToArray(),
                await File.ReadAllBytesAsync(session.MergedFilePath));

            await service.CloseAsync(session, deleteFiles: true, continueDownload: false);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task SegmentThatStaysNonMediaFailsDownloadAndResumeCompletesCleanMergedFile()
    {
        // 三次重试拿到的都是错误页：下载必须失败并保持“未完成/可续传”，而不是把错误页合并后标记为已完成。
        await using var upstream = new FakeHlsServer(
            secondSegmentBadResponses: 3,
            badSegmentBody: "<html><body><h1>403 Forbidden</h1></body></html>");
        using var service = new VodCacheService();
        var root = Path.Combine(Path.GetTempPath(), "orion-vod-cache-badbody-resume-" + Guid.NewGuid().ToString("N"));
        try
        {
            var session = await service.StartAsync(upstream.PlaylistUrl, "Bad Body Resume Episode", root, 15);

            Assert.NotNull(session);
            await session!.DownloadTask.WaitAsync(TimeSpan.FromSeconds(15));
            await service.CloseAsync(session, deleteFiles: false, continueDownload: false);

            Assert.False(session.Progress.IsComplete);
            Assert.Contains("SEGMENT_NOT_MPEGTS", session.Progress.Error);
            Assert.Equal(3, upstream.SegmentRequestCount(2));
            Assert.Equal(FakeTsSegment(1), await File.ReadAllBytesAsync(session.MergedFilePath));
            var partialEntry = Assert.Single(service.GetEntries(root));
            Assert.False(partialEntry.IsComplete);
            Assert.True(partialEntry.CanResumeDownload);

            var resumed = await service.StartAsync(upstream.PlaylistUrl, "Bad Body Resume Episode", root, 15);
            Assert.NotNull(resumed);
            Assert.Equal(session.Directory, resumed!.Directory);
            await resumed.DownloadTask.WaitAsync(TimeSpan.FromSeconds(10));
            await service.CloseAsync(resumed, deleteFiles: false, continueDownload: false);

            Assert.True(resumed.Progress.IsComplete);
            Assert.Equal(
                FakeTsSegment(1).Concat(FakeTsSegment(2)).Concat(FakeTsSegment(3)).ToArray(),
                await File.ReadAllBytesAsync(resumed.MergedFilePath));
            Assert.True(Assert.Single(service.GetEntries(root)).IsComplete);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void MpegTsProbeAcceptsTransportStreamsAndRejectsNonMediaBodies()
    {
        Assert.True(VodCacheService.LooksLikeMpegTs(FakeTsSegment(1)));
        // 头部带伪装数据（假图片头）的 TS 仍可拼接，不能误拒。
        var disguised = Enumerable.Repeat((byte)0x89, 300).Concat(FakeTsSegment(1)).ToArray();
        Assert.True(VodCacheService.LooksLikeMpegTs(disguised));
        // M2TS：每包 192 字节（4 字节时间码 + 188）。
        var m2ts = new byte[192 * 6];
        for (var offset = 4; offset < m2ts.Length; offset += 192) m2ts[offset] = 0x47;
        Assert.True(VodCacheService.LooksLikeMpegTs(m2ts));

        Assert.False(VodCacheService.LooksLikeMpegTs(Encoding.UTF8.GetBytes(
            "<html><head><title>GGGG 403</title></head><body>Forbidden. GET /seg2.ts</body></html>")));
        Assert.False(VodCacheService.LooksLikeMpegTs(Encoding.UTF8.GetBytes("{\"code\":403,\"msg\":\"denied\"}")));
        Assert.False(VodCacheService.LooksLikeMpegTs(Enumerable.Repeat((byte)0x47, 3).ToArray()));
        Assert.False(VodCacheService.LooksLikeMpegTs(new byte[4096]));
    }

    [Fact]
    public async Task ResolvesRelativeSegmentsAgainstRedirectedPlaylistUrl()
    {
        // 模拟普通远程源：/source/xxx/play.m3u8 302 到 CDN 媒体清单，分片是相对路径。
        await using var upstream = new FakeHlsServer(redirectPlaylist: true);
        using var service = new VodCacheService();
        var root = Path.Combine(Path.GetTempPath(), "orion-vod-cache-redirect-" + Guid.NewGuid().ToString("N"));
        try
        {
            var session = await service.StartAsync(
                upstream.RedirectEntryUrl,
                "Redirect Episode",
                root,
                15);

            Assert.NotNull(session);
            await session!.DownloadTask.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(session.Progress.IsComplete);
            Assert.Equal(3, session.Progress.CompletedSegments);
            Assert.Equal(1, upstream.SegmentRequestCount(1));
            Assert.Equal(1, upstream.SegmentRequestCount(2));
            Assert.Equal(1, upstream.SegmentRequestCount(3));
            // 若仍按入口代理 URL 解析相对分片，请求会打到 /source/.../segN.ts 并 404。
            Assert.Equal(0, upstream.RequestCount("/source/nivod/seg1.ts"));

            await service.CloseAsync(session, deleteFiles: true, continueDownload: false);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task DecryptsAes128CompletedCacheIntoSingleMergedFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "orion-vod-cache-decrypt-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(root, "episode");
        Directory.CreateDirectory(dir);
        try
        {
            var key = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
            var iv = new byte[16]; // 0x00... 与清单声明一致
            File.WriteAllBytes(Path.Combine(dir, "asset_000.key"), key);

            var plains = new[]
            {
                Enumerable.Repeat((byte)0x11, 48).ToArray(),
                Enumerable.Repeat((byte)0x22, 32).ToArray(),
                Enumerable.Repeat((byte)0x33, 64).ToArray(),
            };
            for (var index = 0; index < plains.Length; index++)
            {
                var cipher = EncryptAes128Cbc(plains[index], key, iv);
                await File.WriteAllBytesAsync(Path.Combine(dir, $"segment_{index:D5}.ts"), cipher);
            }

            await File.WriteAllTextAsync(
                Path.Combine(dir, "offline.m3u8"),
                "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:10\n#EXT-X-MEDIA-SEQUENCE:0\n" +
                "#EXT-X-KEY:METHOD=AES-128,URI=\"asset_000.key\",IV=0x00000000000000000000000000000000\n" +
                "#EXTINF:6.0,\nsegment_00000.ts\n" +
                "#EXTINF:6.0,\nsegment_00001.ts\n" +
                "#EXTINF:6.0,\nsegment_00002.ts\n" +
                "#EXT-X-ENDLIST\n");
            await File.WriteAllTextAsync(
                Path.Combine(dir, "cache.json"),
                """
                {
                  "Title": "Decrypt Episode",
                  "SourceUrl": "http://example/play.m3u8",
                  "CreatedAt": "2026-07-20T00:00:00+00:00",
                  "IsComplete": true,
                  "CompletedSegments": 3,
                  "TotalSegments": 3,
                  "MergedSegments": 0,
                  "MergedFileName": null,
                  "MergeSupported": false
                }
                """);

            using var service = new VodCacheService();
            Assert.True(service.CanDecryptMerge(dir));
            var entriesBefore = service.GetEntries(root);
            Assert.Contains(entriesBefore, entry => entry.CanDecryptMerge && entry.Title == "Decrypt Episode");

            var mergedPath = await service.DecryptAndMergeAsync(dir);
            Assert.True(File.Exists(mergedPath));
            var merged = await File.ReadAllBytesAsync(mergedPath);
            var expected = plains.SelectMany(part => part).ToArray();
            Assert.Equal(expected, merged);
            Assert.Empty(Directory.EnumerateFiles(dir, "segment_*"));
            Assert.False(service.CanDecryptMerge(dir));

            var entriesAfter = service.GetEntries(root);
            var entry = Assert.Single(entriesAfter, item => item.Title == "Decrypt Episode");
            Assert.False(entry.CanDecryptMerge);
            Assert.False(string.IsNullOrWhiteSpace(entry.MergedFilePath));
            Assert.True(entry.MergeSupported);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task DecryptMergeStitchesAesSegmentsWhoseTimestampsRestartIntoOneFullLengthFile()
    {
        // 用户反馈（2026-11-22）：下载后解密合并的文件只显示 17 秒总时长，但能一直播下去。
        // 原因：分片时间戳各自重新起算，原样拼接后播放器只能读到最后一段连续区间的时长。
        var root = Path.Combine(Path.GetTempPath(), "orion-vod-decrypt-timeline-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(root, "restart-entry");
        Directory.CreateDirectory(dir);
        try
        {
            var firstKey = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
            var secondKey = Enumerable.Range(101, 16).Select(i => (byte)i).ToArray();
            var explicitIv = Enumerable.Range(0, 16).Select(i => (byte)(0xA0 + i)).ToArray();
            await File.WriteAllBytesAsync(Path.Combine(dir, "asset_000.key"), firstKey);
            await File.WriteAllBytesAsync(Path.Combine(dir, "asset_001.key"), secondKey);

            // 5 个分片各 6 秒，每片的时间戳都从头起算；前 3 片用媒体序号做 IV，换密钥后 2 片用清单声明的 IV。
            const int mediaSequence = 7;
            var plains = Enumerable.Range(1, 5).Select(RestartingTsSegment).ToArray();
            for (var index = 0; index < plains.Length; index++)
            {
                var sequenceIv = new byte[16];
                sequenceIv[15] = (byte)(mediaSequence + index);
                var cipher = index < 3
                    ? EncryptAes128Cbc(plains[index], firstKey, sequenceIv)
                    : EncryptAes128Cbc(plains[index], secondKey, explicitIv);
                Assert.NotEqual(plains[index], cipher.AsSpan(0, plains[index].Length).ToArray());
                await File.WriteAllBytesAsync(Path.Combine(dir, $"segment_{index:D5}.ts"), cipher);
            }

            await File.WriteAllTextAsync(
                Path.Combine(dir, "offline.m3u8"),
                "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:6\n" +
                $"#EXT-X-MEDIA-SEQUENCE:{mediaSequence}\n" +
                "#EXT-X-KEY:METHOD=AES-128,URI=\"asset_000.key\"\n" +
                "#EXTINF:6.0,\nsegment_00000.ts\n" +
                "#EXTINF:6.0,\nsegment_00001.ts\n" +
                "#EXTINF:6.0,\nsegment_00002.ts\n" +
                "#EXT-X-DISCONTINUITY\n" +
                $"#EXT-X-KEY:METHOD=AES-128,URI=\"asset_001.key\",IV=0x{Convert.ToHexString(explicitIv)}\n" +
                "#EXTINF:6.0,\nsegment_00003.ts\n" +
                "#EXTINF:6.0,\nsegment_00004.ts\n" +
                "#EXT-X-ENDLIST\n");
            await File.WriteAllTextAsync(
                Path.Combine(dir, "cache.json"),
                JsonSerializer.Serialize(new
                {
                    Title = "Restart Decrypt Episode",
                    SourceUrl = "http://example/restart.m3u8",
                    CreatedAt = DateTimeOffset.UtcNow,
                    IsComplete = true,
                    CompletedSegments = plains.Length,
                    TotalSegments = plains.Length,
                    MergedSegments = 0,
                    MergedFileName = (string?)null,
                    MergeSupported = false,
                }));

            // 修复前的结果就是明文分片原样拼接：播放器只能读到 6 秒。
            Assert.Equal(6.0, TsFixture.ProbedDurationSeconds(TsFixture.Concat(plains)), 3);

            using var service = new VodCacheService();
            var mergedPath = await service.DecryptAndMergeAsync(dir);
            var merged = await File.ReadAllBytesAsync(mergedPath);

            // 与“同样内容、时间戳连续”的分片逐字节相同：每一片都完整解密、没有漏片/错序，只有时间戳被接续。
            Assert.Equal(ContinuousTsSegments(5), merged);
            Assert.Equal(30.0, TsFixture.ProbedDurationSeconds(merged), 3);
            TsFixture.AssertMonotonic(merged);

            Assert.Empty(Directory.EnumerateFiles(dir, "segment_*"));
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(dir, "cache.json")));
            Assert.True(document.RootElement.GetProperty("IsComplete").GetBoolean());
            Assert.Equal(5, document.RootElement.GetProperty("MergedSegments").GetInt32());
            Assert.Equal(
                plains.Select(plain => plain.LongLength),
                document.RootElement.GetProperty("MergedSegmentLengths").EnumerateArray().Select(item => item.GetInt64()));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task DecryptMergeRefusesNonTransportStreamPlaintextAndKeepsTheCachePlayable()
    {
        // 解出来不是 MPEG-TS（fMP4 分片、密钥不对的乱码）时不能拼成 merged.ts，更不能随后把原始分片删掉。
        var root = Path.Combine(Path.GetTempPath(), "orion-vod-decrypt-nonts-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(root, "fmp4-entry");
        Directory.CreateDirectory(dir);
        try
        {
            var key = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
            await File.WriteAllBytesAsync(Path.Combine(dir, "asset_000.key"), key);

            var fragment = Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)).ToArray();
            "\0\0\0\u0018styp"u8.CopyTo(fragment);
            var plains = new[] { RestartingTsSegment(1), fragment, RestartingTsSegment(3) };
            var ciphers = new byte[plains.Length][];
            for (var index = 0; index < plains.Length; index++)
            {
                var iv = new byte[16];
                iv[15] = (byte)index;
                ciphers[index] = EncryptAes128Cbc(plains[index], key, iv);
                await File.WriteAllBytesAsync(Path.Combine(dir, $"segment_{index:D5}.ts"), ciphers[index]);
            }

            await File.WriteAllTextAsync(
                Path.Combine(dir, "offline.m3u8"),
                "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:6\n#EXT-X-MEDIA-SEQUENCE:0\n" +
                "#EXT-X-KEY:METHOD=AES-128,URI=\"asset_000.key\"\n" +
                "#EXTINF:6.0,\nsegment_00000.ts\n" +
                "#EXTINF:6.0,\nsegment_00001.ts\n" +
                "#EXTINF:6.0,\nsegment_00002.ts\n" +
                "#EXT-X-ENDLIST\n");
            var metadata = JsonSerializer.Serialize(new
            {
                Title = "Fmp4 Episode",
                SourceUrl = "http://example/fmp4.m3u8",
                CreatedAt = DateTimeOffset.UtcNow,
                IsComplete = true,
                CompletedSegments = plains.Length,
                TotalSegments = plains.Length,
                MergedSegments = 0,
                MergedFileName = (string?)null,
                MergeSupported = false,
            });
            await File.WriteAllTextAsync(Path.Combine(dir, "cache.json"), metadata);

            using var service = new VodCacheService();
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => service.DecryptAndMergeAsync(dir));
            Assert.Contains("DECRYPTED_SEGMENT_NOT_MPEGTS segment_00001.ts", error.Message);

            // 原缓存原封不动：分片、密钥、清单、元数据都在，没有留下成品或半成品。
            for (var index = 0; index < ciphers.Length; index++)
            {
                Assert.Equal(ciphers[index], await File.ReadAllBytesAsync(Path.Combine(dir, $"segment_{index:D5}.ts")));
            }

            Assert.Equal(key, await File.ReadAllBytesAsync(Path.Combine(dir, "asset_000.key")));
            Assert.Equal(metadata, await File.ReadAllTextAsync(Path.Combine(dir, "cache.json")));
            Assert.Empty(Directory.EnumerateFiles(dir, "merged.ts*"));
            Assert.True(service.CanDecryptMerge(dir));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task DownloadMergeStitchesSegmentsWhoseTimestampsRestart()
    {
        // 未加密的源走“边下边合并”，同样不能把时间戳各自起算的分片原样拼接。
        await using var upstream = new FakeHlsServer(segmentBody: RestartingTsSegment);
        using var service = new VodCacheService();
        var root = Path.Combine(Path.GetTempPath(), "orion-vod-cache-timeline-" + Guid.NewGuid().ToString("N"));
        try
        {
            var session = await service.StartAsync(upstream.PlaylistUrl, "Restart Episode", root, 15);
            Assert.NotNull(session);
            await session!.DownloadTask.WaitAsync(TimeSpan.FromSeconds(15));
            await service.CloseAsync(session, deleteFiles: false, continueDownload: false);

            Assert.True(session.Progress.IsComplete);
            var merged = await File.ReadAllBytesAsync(session.MergedFilePath);
            Assert.Equal(ContinuousTsSegments(3), merged);
            Assert.Equal(18.0, TsFixture.ProbedDurationSeconds(merged), 3);
            Assert.Empty(Directory.EnumerateFiles(session.Directory, "segment_*.*"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ResumedDownloadKeepsMergedTimelineContinuousAcrossTheResumePoint()
    {
        // 第 2 片三次都拿到错误页：首次下载只合并了第 1 片；续传在新的服务实例里进行，时间轴状态只能从 merged.ts 恢复。
        await using var upstream = new FakeHlsServer(
            secondSegmentBadResponses: 3,
            badSegmentBody: "<html><body><h1>403 Forbidden</h1></body></html>",
            segmentBody: RestartingTsSegment);
        var root = Path.Combine(Path.GetTempPath(), "orion-vod-cache-timeline-resume-" + Guid.NewGuid().ToString("N"));
        try
        {
            string directory;
            using (var service = new VodCacheService())
            {
                var session = await service.StartAsync(upstream.PlaylistUrl, "Restart Resume Episode", root, 15);
                Assert.NotNull(session);
                await session!.DownloadTask.WaitAsync(TimeSpan.FromSeconds(15));
                await service.CloseAsync(session, deleteFiles: false, continueDownload: false);

                Assert.False(session.Progress.IsComplete);
                Assert.Equal(ContinuousTsSegments(1), await File.ReadAllBytesAsync(session.MergedFilePath));
                directory = session.Directory;
            }

            using var resumedService = new VodCacheService();
            var resumed = await resumedService.StartAsync(upstream.PlaylistUrl, "Restart Resume Episode", root, 15);
            Assert.NotNull(resumed);
            Assert.Equal(directory, resumed!.Directory);
            await resumed.DownloadTask.WaitAsync(TimeSpan.FromSeconds(15));
            await resumedService.CloseAsync(resumed, deleteFiles: false, continueDownload: false);

            Assert.True(resumed.Progress.IsComplete);
            var merged = await File.ReadAllBytesAsync(resumed.MergedFilePath);
            Assert.Equal(ContinuousTsSegments(3), merged);
            Assert.Equal(18.0, TsFixture.ProbedDurationSeconds(merged), 3);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    /// <summary>真实结构的 TS 分片（6 秒），每片的时间戳都从同一个起点重新起算。</summary>
    private static byte[] RestartingTsSegment(int number) =>
        TsFixture.Segment(TsFixture.FirstTimestamp, seed: number);

    /// <summary>与 <see cref="RestartingTsSegment"/> 内容相同、但时间戳前后相接的分片拼接结果。</summary>
    private static byte[] ContinuousTsSegments(int count) =>
        TsFixture.Concat(Enumerable.Range(1, count).Select(number =>
            TsFixture.Segment(TsFixture.FirstTimestamp + ((number - 1) * 6 * TsFixture.Second), seed: number)));

    [Fact]
    public async Task BatchDecryptContinuesAfterFailureAndLeavesOnlyFailedEntryForRetry()
    {
        var root = Path.Combine(Path.GetTempPath(), "orion-vod-cache-batch-decrypt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var first = await CreateEncryptedCacheAsync(root, "episode-1", "Batch Show 第1集");
            var broken = await CreateEncryptedCacheAsync(root, "episode-2", "Batch Show 第2集", writeKey: false);
            var third = await CreateEncryptedCacheAsync(root, "episode-3", "Batch Show 第3集");
            using var service = new VodCacheService();
            var entries = service.GetEntries(root).ToDictionary(entry => entry.Title, StringComparer.Ordinal);
            var ordered = new[]
            {
                entries["Batch Show 第1集"],
                entries["Batch Show 第2集"],
                entries["Batch Show 第3集"],
            };
            Assert.All(ordered, entry => Assert.True(entry.CanDecryptMerge));

            var updates = new ConcurrentQueue<VodCacheBatchDecryptProgress>();
            var result = await service.DecryptAndMergeManyAsync(
                ordered,
                new InlineProgress<VodCacheBatchDecryptProgress>(updates.Enqueue));

            Assert.Equal(2, result.SucceededEntries.Count);
            var failure = Assert.Single(result.Failures);
            Assert.Equal("Batch Show 第2集", failure.Entry.Title);
            Assert.Equal(first.Plain, await File.ReadAllBytesAsync(Path.Combine(first.Directory, VodCacheSession.MergedFileName)));
            Assert.False(File.Exists(Path.Combine(broken.Directory, VodCacheSession.MergedFileName)));
            Assert.Equal(third.Plain, await File.ReadAllBytesAsync(Path.Combine(third.Directory, VodCacheSession.MergedFileName)));
            Assert.Contains(updates, update => update.EntryIndex == 2 && update.CurrentEntryProgress == 1);

            var retryable = service.GetEntries(root).Where(entry => entry.CanDecryptMerge).ToList();
            Assert.Equal("Batch Show 第2集", Assert.Single(retryable).Title);
            var retry = await service.DecryptAndMergeManyAsync(retryable);
            Assert.Empty(retry.SucceededEntries);
            Assert.Single(retry.Failures);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ConcurrentDecryptCallsForSameDirectoryReuseCompletedMerge()
    {
        var root = Path.Combine(Path.GetTempPath(), "orion-vod-cache-concurrent-decrypt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var blockingProgress = new BlockingFirstProgress();
        Task<string>? firstTask = null;
        try
        {
            var cache = await CreateEncryptedCacheAsync(root, "episode", "Concurrent Episode", segmentSize: 256 * 1024);
            using var service = new VodCacheService();
            firstTask = service.DecryptAndMergeAsync(cache.Directory, blockingProgress);
            await blockingProgress.Started.WaitAsync(TimeSpan.FromSeconds(10));

            var secondTask = service.DecryptAndMergeAsync(cache.Directory);
            blockingProgress.Release();
            var paths = await Task.WhenAll(firstTask, secondTask).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(paths[0], paths[1]);
            Assert.Equal(cache.Plain, await File.ReadAllBytesAsync(paths[0]));
            Assert.Empty(Directory.EnumerateFiles(cache.Directory, "*.decrypting"));
        }
        finally
        {
            blockingProgress.Release();
            if (firstTask is not null)
            {
                try { await firstTask; } catch { }
            }
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static byte[] EncryptAes128Cbc(byte[] plain, byte[] key, byte[] iv)
    {
        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Mode = System.Security.Cryptography.CipherMode.CBC;
        aes.Padding = System.Security.Cryptography.PaddingMode.PKCS7;
        aes.KeySize = 128;
        aes.BlockSize = 128;
        aes.Key = key;
        aes.IV = iv;
        using var encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(plain, 0, plain.Length);
    }

    private static async Task<(string Directory, byte[] Plain)> CreateEncryptedCacheAsync(
        string root,
        string directoryName,
        string title,
        bool writeKey = true,
        int segmentSize = 64)
    {
        var directory = Path.Combine(root, directoryName);
        Directory.CreateDirectory(directory);
        var key = Enumerable.Range(1, 16).Select(index => (byte)index).ToArray();
        var iv = new byte[16];
        if (writeKey)
        {
            await File.WriteAllBytesAsync(Path.Combine(directory, "asset_000.key"), key);
        }

        // 解密合并只接受 MPEG-TS 明文，所以夹具也按 188 字节包对齐同步字节。
        var plains = new[]
        {
            FakeTsSegment(0x31, segmentSize),
            FakeTsSegment(0x42, segmentSize + 16),
        };
        for (var index = 0; index < plains.Length; index++)
        {
            await File.WriteAllBytesAsync(
                Path.Combine(directory, $"segment_{index:D5}.ts"),
                EncryptAes128Cbc(plains[index], key, iv));
        }

        await File.WriteAllTextAsync(
            Path.Combine(directory, "offline.m3u8"),
            "#EXTM3U\n#EXT-X-MEDIA-SEQUENCE:0\n" +
            "#EXT-X-KEY:METHOD=AES-128,URI=\"asset_000.key\",IV=0x00000000000000000000000000000000\n" +
            "#EXTINF:6.0,\nsegment_00000.ts\n" +
            "#EXTINF:6.0,\nsegment_00001.ts\n#EXT-X-ENDLIST\n");
        await File.WriteAllTextAsync(
            Path.Combine(directory, "cache.json"),
            JsonSerializer.Serialize(new
            {
                Title = title,
                SourceUrl = $"http://example/{directoryName}.m3u8",
                CreatedAt = DateTimeOffset.UtcNow,
                IsComplete = true,
                CompletedSegments = plains.Length,
                TotalSegments = plains.Length,
                MergedSegments = 0,
                MergedFileName = (string?)null,
                MergeSupported = false,
            }));
        return (directory, plains.SelectMany(plain => plain).ToArray());
    }

    [Fact]
    public async Task ReenteringActiveDownloadReusesTheExistingSession()
    {
        await using var upstream = new FakeHlsServer(delayThirdSegment: true);
        using var service = new VodCacheService();
        var root = Path.Combine(Path.GetTempPath(), "orion-vod-cache-reattach-" + Guid.NewGuid().ToString("N"));
        try
        {
            var session = await service.StartAsync(upstream.PlaylistUrl, "Reattach Episode", root, 15);
            Assert.NotNull(session);
            _ = await session!.Ready.WaitAsync(TimeSpan.FromSeconds(10));

            await service.CloseAsync(session, deleteFiles: false, continueDownload: true);
            var reentered = await service.StartAsync(upstream.PlaylistUrl, "Reattach Episode", root, 15);

            Assert.Same(session, reentered);
            Assert.Single(Directory.EnumerateDirectories(root));

            await reentered!.DownloadTask.WaitAsync(TimeSpan.FromSeconds(10));
            await service.CloseAsync(reentered, deleteFiles: true, continueDownload: false);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task CompletedMergedCacheRemovesRedundantSegmentFilesWhenReleased()
    {
        await using var upstream = new FakeHlsServer();
        using var service = new VodCacheService();
        var root = Path.Combine(Path.GetTempPath(), "orion-vod-cache-cleanup-" + Guid.NewGuid().ToString("N"));
        try
        {
            var session = await service.StartAsync(upstream.PlaylistUrl, "Cleanup Episode", root, 15);
            Assert.NotNull(session);
            await session!.DownloadTask.WaitAsync(TimeSpan.FromSeconds(10));

            await service.CloseAsync(session, deleteFiles: false, continueDownload: false);

            Assert.True(File.Exists(session.MergedFilePath));
            Assert.Empty(Directory.EnumerateFiles(session.Directory, "segment_*.*"));

            // 模拟旧版元数据没有分片长度，但目录里仍保留原始分片。
            var metadataPath = Path.Combine(session.Directory, "cache.json");
            var metadata = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                await File.ReadAllTextAsync(metadataPath));
            Assert.NotNull(metadata);
            metadata!.Remove("MergedSegmentLengths");
            await File.WriteAllTextAsync(
                metadataPath,
                JsonSerializer.Serialize(metadata),
                new UTF8Encoding(false));
            for (var index = 0; index < 3; index++)
            {
                await File.WriteAllBytesAsync(
                    Path.Combine(session.Directory, $"segment_{index:D5}.ts"),
                    Enumerable.Repeat((byte)(index + 1), 1024).ToArray());
            }

            var entry = Assert.Single(service.GetEntries(root));
            Assert.Equal(session.MergedFilePath, entry.PlaylistPath);
            Assert.Empty(Directory.EnumerateFiles(session.Directory, "segment_*.*"));
            using var migratedMetadata = JsonDocument.Parse(await File.ReadAllTextAsync(metadataPath));
            Assert.Equal(3, migratedMetadata.RootElement.GetProperty("MergedSegmentLengths").GetArrayLength());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task PosterPlaybackFindsCompletedCacheWithoutDownloadingAgain()
    {
        await using var upstream = new FakeHlsServer();
        using var service = new VodCacheService();
        var root = Path.Combine(Path.GetTempPath(), "orion-vod-cache-poster-" + Guid.NewGuid().ToString("N"));
        try
        {
            const string title = "Poster Episode";
            const string poster = "https://images.example/poster.jpg";
            var session = await service.StartAsync(upstream.PlaylistUrl, title, root, 15, poster: poster);
            Assert.NotNull(session);
            await session!.DownloadTask.WaitAsync(TimeSpan.FromSeconds(10));
            await service.CloseAsync(session, deleteFiles: false, continueDownload: false);

            Assert.Equal(poster, Assert.Single(service.GetEntries(root)).Poster);

            var directoriesBefore = Directory.EnumerateDirectories(root).ToArray();
            var requestsBefore = Enumerable.Range(1, 3)
                .Select(upstream.SegmentRequestCount)
                .ToArray();

            var exactSourcePath = service.FindCompletedPlaybackPath(root, upstream.PlaylistUrl, title);
            var refreshedSourcePath = service.FindCompletedPlaybackPath(
                root,
                upstream.PlaylistUrl + "?token=refreshed",
                title);

            Assert.Equal(session.MergedFilePath, exactSourcePath);
            Assert.Equal(session.MergedFilePath, refreshedSourcePath);
            Assert.Null(service.FindCompletedPlaybackPath(root, upstream.PlaylistUrl, "Another Episode"));
            Assert.Equal(directoriesBefore, Directory.EnumerateDirectories(root).ToArray());
            for (var index = 0; index < requestsBefore.Length; index++)
            {
                Assert.Equal(requestsBefore[index], upstream.SegmentRequestCount(index + 1));
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void CompletedCacheWithStaleMetadataIsRecoveredForLocalPlayback()
    {
        var root = Path.Combine(Path.GetTempPath(), "orion-vod-cache-recover-" + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, "Recovered Series", "第1集-test");
        Directory.CreateDirectory(directory);
        try
        {
            const string title = "Recovered Series 第1集";
            const string sourceUrl = "https://video.example/episode.m3u8";
            var mergedPath = Path.Combine(directory, VodCacheSession.MergedFileName);
            File.WriteAllBytes(mergedPath, new byte[1024]);
            File.WriteAllText(
                Path.Combine(directory, "offline.m3u8"),
                "#EXTM3U\n#EXTINF:8,\nsegment_00000.ts\n#EXT-X-ENDLIST\n");
            File.WriteAllText(
                Path.Combine(directory, "cache.json"),
                JsonSerializer.Serialize(new CacheMetadata(
                    title,
                    sourceUrl,
                    DateTimeOffset.UtcNow,
                    IsComplete: false,
                    CompletedSegments: 1,
                    TotalSegments: 1,
                    MergedSegments: 1,
                    MergedFileName: VodCacheSession.MergedFileName)));

            using var service = new VodCacheService();
            Assert.Equal(mergedPath, service.FindCompletedPlaybackPath(root, sourceUrl, title));
            Assert.True(Assert.Single(service.GetEntries(root)).IsComplete);

            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "cache.json")));
            Assert.True(document.RootElement.GetProperty("IsComplete").GetBoolean());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void CompletedCacheIgnoresLegacyPlaybackMp4AndUsesMergedTs()
    {
        var root = Path.Combine(Path.GetTempPath(), "orion-vod-cache-legacy-playback-" + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, "Legacy Series", "第1集-test");
        Directory.CreateDirectory(directory);
        try
        {
            const string title = "Legacy Series 第1集";
            const string sourceUrl = "https://video.example/legacy.m3u8";
            var mergedPath = Path.Combine(directory, VodCacheSession.MergedFileName);
            var legacyPlaybackPath = Path.Combine(directory, "playback.mp4");
            File.WriteAllBytes(mergedPath, new byte[1024]);
            File.WriteAllBytes(legacyPlaybackPath, new byte[2048]);
            File.WriteAllText(
                Path.Combine(directory, "cache.json"),
                JsonSerializer.Serialize(new CacheMetadata(
                    title,
                    sourceUrl,
                    DateTimeOffset.UtcNow,
                    IsComplete: true,
                    CompletedSegments: 1,
                    TotalSegments: 1,
                    MergedSegments: 1,
                    MergedFileName: VodCacheSession.MergedFileName)));

            using var service = new VodCacheService();
            Assert.Equal(mergedPath, service.FindCompletedPlaybackPath(root, sourceUrl, title));
            Assert.Equal(mergedPath, Assert.Single(service.GetEntries(root)).PlaylistPath);
            Assert.True(File.Exists(legacyPlaybackPath));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task TransientMetadataFileLockDoesNotFailCompletedDownload()
    {
        await using var upstream = new FakeHlsServer(delayThirdSegment: true);
        using var service = new VodCacheService();
        var root = Path.Combine(Path.GetTempPath(), "orion-vod-cache-lock-" + Guid.NewGuid().ToString("N"));
        try
        {
            const string title = "Locked Metadata Episode";
            var session = await service.StartAsync(upstream.PlaylistUrl, title, root, 15);
            Assert.NotNull(session);
            _ = await session!.Ready.WaitAsync(TimeSpan.FromSeconds(10));

            var metadataPath = Path.Combine(session.Directory, "cache.json");
            using (new FileStream(metadataPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                await Task.Delay(1100);
            }

            await session.DownloadTask.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(session.Progress.IsComplete);
            await service.CloseAsync(session, deleteFiles: false, continueDownload: false);
            Assert.Equal(session.MergedFilePath, service.FindCompletedPlaybackPath(root, upstream.PlaylistUrl, title));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task DifferentEpisodesAreStoredUnderTheSameSeriesDirectory()
    {
        await using var upstream = new FakeHlsServer();
        using var service = new VodCacheService();
        var root = Path.Combine(Path.GetTempPath(), "orion-vod-cache-series-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = await service.StartAsync(upstream.PlaylistUrl, "Folder Series 第1集", root, 15);
            Assert.NotNull(first);
            await first!.DownloadTask.WaitAsync(TimeSpan.FromSeconds(10));
            await service.CloseAsync(first, deleteFiles: false, continueDownload: false);

            var second = await service.StartAsync(upstream.PlaylistUrl, "Folder Series 第2集", root, 15);
            Assert.NotNull(second);
            await second!.DownloadTask.WaitAsync(TimeSpan.FromSeconds(10));
            await service.CloseAsync(second, deleteFiles: false, continueDownload: false);

            var seriesDirectory = Path.Combine(root, "Folder Series");
            Assert.Equal(seriesDirectory, Path.GetDirectoryName(first.Directory));
            Assert.Equal(seriesDirectory, Path.GetDirectoryName(second.Directory));
            Assert.StartsWith("第1集-", Path.GetFileName(first.Directory));
            Assert.StartsWith("第2集-", Path.GetFileName(second.Directory));
            Assert.Equal(2, service.GetEntries(root).Count);
            Assert.Equal(
                first.MergedFilePath,
                service.FindCompletedPlaybackPath(root, upstream.PlaylistUrl, "Folder Series 第1集"));
            Assert.Equal(
                second.MergedFilePath,
                service.FindCompletedPlaybackPath(root, upstream.PlaylistUrl, "Folder Series 第2集"));

            Assert.Equal(2, await service.ClearAsync(root));
            Assert.False(Directory.Exists(seriesDirectory));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task DeleteEntryPrunesSeriesOnlyAfterLastEpisodeAndKeepsCacheRoot()
    {
        using var service = new VodCacheService();
        var root = Path.Combine(Path.GetTempPath(), "orion-vod-cache-delete-series-" + Guid.NewGuid().ToString("N"));
        try
        {
            var seriesDirectory = Path.Combine(root, "Delete Series");
            var first = CreateCacheEntry(seriesDirectory, "第1集-test");
            var second = CreateCacheEntry(seriesDirectory, "第2集-test");

            await service.DeleteEntryAsync(first, root);

            Assert.False(Directory.Exists(first));
            Assert.True(Directory.Exists(seriesDirectory));
            Assert.True(Directory.Exists(second));

            await service.DeleteEntryAsync(second, root);

            Assert.False(Directory.Exists(second));
            Assert.False(Directory.Exists(seriesDirectory));
            Assert.True(Directory.Exists(root));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task DeleteEntryKeepsSeriesDirectoryWhenUnmanagedFilesRemain()
    {
        using var service = new VodCacheService();
        var root = Path.Combine(Path.GetTempPath(), "orion-vod-cache-delete-content-" + Guid.NewGuid().ToString("N"));
        try
        {
            var seriesDirectory = Path.Combine(root, "Keep Series");
            var entry = CreateCacheEntry(seriesDirectory, "第1集-test");
            var unrelatedFile = Path.Combine(seriesDirectory, "keep.txt");
            await File.WriteAllTextAsync(unrelatedFile, "keep");

            await service.DeleteEntryAsync(entry, root);

            Assert.False(Directory.Exists(entry));
            Assert.True(Directory.Exists(seriesDirectory));
            Assert.True(File.Exists(unrelatedFile));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task GroupedEpisodeCacheResumesFromItsNestedDirectory()
    {
        await using var upstream = new FakeHlsServer(delayThirdSegment: true);
        using var service = new VodCacheService();
        var root = Path.Combine(Path.GetTempPath(), "orion-vod-cache-series-resume-" + Guid.NewGuid().ToString("N"));
        try
        {
            const string title = "Resume Series 第3集";
            var session = await service.StartAsync(upstream.PlaylistUrl, title, root, 15);
            Assert.NotNull(session);
            _ = await session!.Ready.WaitAsync(TimeSpan.FromSeconds(10));
            await service.CloseAsync(session, deleteFiles: false, continueDownload: false);

            var seriesDirectory = Path.Combine(root, "Resume Series");
            Assert.Equal(seriesDirectory, Path.GetDirectoryName(session.Directory));
            Assert.Single(service.GetEntries(root));

            using var resumedService = new VodCacheService();
            var resumed = await resumedService.StartAsync(upstream.PlaylistUrl, title, root, 15);
            Assert.NotNull(resumed);
            Assert.Equal(session.Directory, resumed!.Directory);
            await resumed.DownloadTask.WaitAsync(TimeSpan.FromSeconds(10));
            await resumedService.CloseAsync(resumed, deleteFiles: false, continueDownload: false);
            Assert.True(Assert.Single(resumedService.GetEntries(root)).IsComplete);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task DetachedDownloadRemovesSegmentsAfterBackgroundCompletion()
    {
        await using var upstream = new FakeHlsServer(delayThirdSegment: true);
        using var service = new VodCacheService();
        var root = Path.Combine(Path.GetTempPath(), "orion-vod-cache-detached-" + Guid.NewGuid().ToString("N"));
        try
        {
            var session = await service.StartAsync(upstream.PlaylistUrl, "Detached Episode", root, 15);
            Assert.NotNull(session);
            _ = await session!.Ready.WaitAsync(TimeSpan.FromSeconds(10));

            await service.CloseAsync(session, deleteFiles: false, continueDownload: true);
            await session.DownloadTask.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(File.Exists(session.MergedFilePath));
            Assert.Empty(Directory.EnumerateFiles(session.Directory, "segment_*.*"));
            Assert.True(Assert.Single(service.GetEntries(root)).IsComplete);
            await service.DeleteEntryAsync(session.Directory, root);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static string CreateCacheEntry(string parent, string name)
    {
        var directory = Path.Combine(parent, name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "cache.json"), "{}");
        return directory;
    }

    [Fact]
    public async Task PartialDownloadMergesAndResumeAppendsMergedFile()
    {
        await using var upstream = new FakeHlsServer(delayThirdSegment: true);
        using var service = new VodCacheService();
        var root = Path.Combine(Path.GetTempPath(), "orion-vod-cache-merge-" + Guid.NewGuid().ToString("N"));
        try
        {
            var session = await service.StartAsync(upstream.PlaylistUrl, "Merge Episode", root, 15);
            Assert.NotNull(session);
            _ = await session!.Ready.WaitAsync(TimeSpan.FromSeconds(10));

            // 等至少 1 段合并后主动停止，模拟“没看完就退出并保留”。
            for (var i = 0; i < 50 && session.Progress.CompletedSegments < 1; i++)
            {
                await Task.Delay(50);
            }

            await service.CloseAsync(session, deleteFiles: false, continueDownload: false);
            Assert.True(Directory.Exists(session.Directory));
            Assert.True(File.Exists(session.MergedFilePath));
            var partialLength = new FileInfo(session.MergedFilePath).Length;
            Assert.True(partialLength >= 1024);
            Assert.True(session.DownloadTask.IsCompleted);
            Assert.Empty(Directory.EnumerateFiles(session.Directory, "segment_*.*"));

            var entries = service.GetEntries(root);
            var partialEntry = Assert.Single(entries);
            Assert.False(string.IsNullOrWhiteSpace(partialEntry.MergedFilePath));
            Assert.True(partialEntry.CanResumeDownload);

            using (var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(session.Directory, "cache.json"))))
            {
                var mergedLengths = document.RootElement.GetProperty("MergedSegmentLengths");
                Assert.Equal(partialEntry.MergedSegments, mergedLengths.GetArrayLength());
            }

            var requestsBeforeResume = Enumerable.Range(1, partialEntry.MergedSegments)
                .Select(upstream.SegmentRequestCount)
                .ToArray();

            upstream.FailNextPlaylistRequest();
            using var resumedService = new VodCacheService();
            var resumed = await resumedService.StartAsync(upstream.PlaylistUrl, "Merge Episode", root, 15);
            Assert.NotNull(resumed);
            Assert.Equal(session.Directory, resumed!.Directory);
            Assert.Single(Directory.EnumerateDirectories(root));
            var resumedPlaylistUrl = await resumed!.Ready.WaitAsync(TimeSpan.FromSeconds(10));
            using (var http = new HttpClient())
            {
                var resumedPlaylist = await http.GetStringAsync(resumedPlaylistUrl);
                var firstSegmentUrl = resumedPlaylist.Split('\n')
                    .First(line => line.StartsWith("http://", StringComparison.Ordinal) && line.Contains("segment_00000.ts"));
                var firstSegment = await http.GetByteArrayAsync(firstSegmentUrl);
                Assert.Equal(FakeTsSegment(1), firstSegment);

                using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, firstSegmentUrl);
                rangeRequest.Headers.Range = new RangeHeaderValue(10, 19);
                using var rangeResponse = await http.SendAsync(rangeRequest);
                Assert.Equal(HttpStatusCode.PartialContent, rangeResponse.StatusCode);
                Assert.Equal("bytes 10-19/1024", rangeResponse.Content.Headers.ContentRange?.ToString());
                Assert.Equal(Enumerable.Repeat((byte)1, 10), await rangeResponse.Content.ReadAsByteArrayAsync());
            }

            await resumed!.DownloadTask.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(resumed.Progress.IsComplete);
            await resumedService.CloseAsync(resumed, deleteFiles: false, continueDownload: false);

            for (var index = 0; index < requestsBeforeResume.Length; index++)
            {
                Assert.Equal(requestsBeforeResume[index], upstream.SegmentRequestCount(index + 1));
            }

            var finalLength = new FileInfo(resumed.MergedFilePath).Length;
            Assert.True(finalLength >= partialLength);
            Assert.True(finalLength >= 3 * 1024);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ResumeFailureKeepsExistingCacheInsteadOfStartingOver()
    {
        await using var upstream = new FakeHlsServer(delayThirdSegment: true);
        using var service = new VodCacheService();
        var root = Path.Combine(Path.GetTempPath(), "orion-vod-cache-resume-failure-" + Guid.NewGuid().ToString("N"));
        try
        {
            var session = await service.StartAsync(upstream.PlaylistUrl, "Resume Failure Episode", root, 15);
            Assert.NotNull(session);
            _ = await session!.Ready.WaitAsync(TimeSpan.FromSeconds(10));
            await service.CloseAsync(session, deleteFiles: false, continueDownload: false);

            upstream.FailNextPlaylistRequest(3);
            using var resumedService = new VodCacheService();
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                resumedService.StartAsync(upstream.PlaylistUrl, "Resume Failure Episode", root, 15));

            Assert.Contains("VOD_CACHE_RESUME_FAILED", error.Message);
            Assert.Equal(session.Directory, Assert.Single(Directory.EnumerateDirectories(root)));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    /// <summary>仿 MPEG-TS 分片：每 188 字节一个同步字节 0x47，其余填分片编号，便于核对合并顺序。</summary>
    private static byte[] FakeTsSegment(int number, int length = 1024)
    {
        var body = Enumerable.Repeat((byte)number, length).ToArray();
        for (var offset = 0; offset < length; offset += 188)
        {
            body[offset] = 0x47;
        }

        return body;
    }

    private sealed class BlockingFirstProgress : IProgress<double>
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _reported;

        internal Task Started => _started.Task;

        public void Report(double value)
        {
            if (Interlocked.Exchange(ref _reported, 1) != 0)
            {
                return;
            }

            _started.TrySetResult();
            _release.Task.GetAwaiter().GetResult();
        }

        internal void Release() => _release.TrySetResult();
    }

    private sealed class FakeHlsServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Task _loop;
        private readonly bool _delayThirdSegment;
        private readonly bool _redirectPlaylist;
        private readonly bool _delayAllSegments;
        private readonly bool _stallSecondSegmentBodyOnce;
        private readonly int _secondSegmentBadResponses;
        private readonly byte[] _badSegmentBody;
        private readonly Func<int, byte[]>? _segmentBody;
        private readonly ConcurrentDictionary<string, int> _requestCounts = new(StringComparer.Ordinal);
        private int _playlistFailuresRemaining;
        private int _activeSegmentRequests;
        private int _maxConcurrentSegmentRequests;

        internal FakeHlsServer(
            bool delayThirdSegment = false,
            bool redirectPlaylist = false,
            bool delayAllSegments = false,
            bool stallSecondSegmentBodyOnce = false,
            int secondSegmentBadResponses = 0,
            string badSegmentBody = "",
            Func<int, byte[]>? segmentBody = null)
        {
            _segmentBody = segmentBody;
            _secondSegmentBadResponses = secondSegmentBadResponses;
            _badSegmentBody = Encoding.UTF8.GetBytes(badSegmentBody);
            _delayThirdSegment = delayThirdSegment;
            _redirectPlaylist = redirectPlaylist;
            _delayAllSegments = delayAllSegments;
            _stallSecondSegmentBodyOnce = stallSecondSegmentBodyOnce;
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            PlaylistUrl = $"http://127.0.0.1:{port}/playlist.m3u8";
            RedirectEntryUrl = $"http://127.0.0.1:{port}/source/nivod/play.m3u8?p=%2Fniplay%2F1-1-1%2F";
            _loop = Task.Run(AcceptLoopAsync);
        }

        internal string PlaylistUrl { get; }
        internal string RedirectEntryUrl { get; }
        internal int MaxConcurrentSegmentRequests => Volatile.Read(ref _maxConcurrentSegmentRequests);

        internal int SegmentRequestCount(int index)
        {
            var suffix = $"/seg{index}.ts";
            return _requestCounts
                .Where(entry => entry.Key.Equals(suffix, StringComparison.OrdinalIgnoreCase)
                    || entry.Key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                .Sum(entry => entry.Value);
        }

        internal int RequestCount(string path) =>
            _requestCounts.TryGetValue(path, out var count) ? count : 0;

        internal void FailNextPlaylistRequest(int count = 1) =>
            Interlocked.Add(ref _playlistFailuresRemaining, Math.Max(0, count));

        private async Task AcceptLoopAsync()
        {
            while (!_cancellation.IsCancellationRequested)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync(_cancellation.Token);
                    _ = Task.Run(() => HandleAsync(client));
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            await using (var stream = client.GetStream())
            {
                var buffer = new byte[4096];
                var count = await stream.ReadAsync(buffer);
                var request = Encoding.ASCII.GetString(buffer, 0, count);
                var path = request.Split(' ')[1];
                var requestNumber = _requestCounts.AddOrUpdate(path, 1, static (_, current) => current + 1);
                byte[] body;
                string type;
                var status = "200 OK";
                string? location = null;
                if (path.StartsWith("/source/", StringComparison.OrdinalIgnoreCase) && _redirectPlaylist)
                {
                    body = Encoding.UTF8.GetBytes("redirect");
                    type = "text/plain";
                    status = "302 Found";
                    location = "/cdn/media.m3u8";
                }
                else if ((path == "/playlist.m3u8" || path == "/cdn/media.m3u8") && TryConsumePlaylistFailure())
                {
                    body = Encoding.UTF8.GetBytes("temporary upstream failure");
                    type = "text/plain";
                    status = "502 Bad Gateway";
                }
                else if (path == "/playlist.m3u8" || path == "/cdn/media.m3u8")
                {
                    body = Encoding.UTF8.GetBytes(
                        "#EXTM3U\n#EXT-X-TARGETDURATION:8\n" +
                        "#EXTINF:8,\nseg1.ts\n" +
                        "#EXTINF:8,\nseg2.ts\n" +
                        "#EXTINF:8,\nseg3.ts\n#EXT-X-ENDLIST\n");
                    type = "application/vnd.apple.mpegurl";
                }
                else
                {
                    var activeRequests = 0;
                    if (_delayAllSegments)
                    {
                        activeRequests = Interlocked.Increment(ref _activeSegmentRequests);
                        UpdateMaximum(ref _maxConcurrentSegmentRequests, activeRequests);
                    }

                    try
                    {
                        if (_delayAllSegments)
                        {
                            await Task.Delay(300);
                        }
                        if (_delayThirdSegment && path.Contains("seg3", StringComparison.Ordinal))
                        {
                            await Task.Delay(800);
                        }
                        var segmentNumber = path.Contains('1') ? 1 : path.Contains('2') ? 2 : 3;
                        body = _segmentBody?.Invoke(segmentNumber) ?? FakeTsSegment(segmentNumber);
                        type = "video/mp2t";
                        if (requestNumber <= _secondSegmentBadResponses && path.Contains("seg2", StringComparison.Ordinal))
                        {
                            // 状态码仍是 200，但正文不是视频。
                            body = _badSegmentBody;
                            type = "text/html";
                        }
                    }
                    finally
                    {
                        if (activeRequests > 0)
                        {
                            Interlocked.Decrement(ref _activeSegmentRequests);
                        }
                    }
                }

                var locationHeader = location is null ? "" : $"Location: {location}\r\n";
                var header = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {status}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\n{locationHeader}Connection: close\r\n\r\n");
                await stream.WriteAsync(header);
                if (_stallSecondSegmentBodyOnce && requestNumber == 1 && path.Contains("seg2", StringComparison.Ordinal))
                {
                    // 路由器重拨、CDN 卡死时的样子：连接还在，但后半段数据永远不来。
                    await stream.WriteAsync(body.AsMemory(0, body.Length / 2));
                    await stream.FlushAsync();
                    try
                    {
                        await Task.Delay(Timeout.Infinite, _cancellation.Token);
                    }
                    catch (OperationCanceledException)
                    {
                    }

                    return;
                }

                await stream.WriteAsync(body);
            }
        }

        private bool TryConsumePlaylistFailure()
        {
            while (true)
            {
                var remaining = Volatile.Read(ref _playlistFailuresRemaining);
                if (remaining <= 0)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref _playlistFailuresRemaining, remaining - 1, remaining) == remaining)
                {
                    return true;
                }
            }
        }

        private static void UpdateMaximum(ref int target, int candidate)
        {
            while (true)
            {
                var current = Volatile.Read(ref target);
                if (candidate <= current
                    || Interlocked.CompareExchange(ref target, candidate, current) == current)
                {
                    return;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cancellation.Cancel();
            _listener.Stop();
            try { await _loop; } catch { }
            _cancellation.Dispose();
        }
    }
}
