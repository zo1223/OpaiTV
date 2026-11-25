using System.Net;
using System.Text;
using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.Tests;

/// <summary>多级广告识别：CUE → 片头片尾黑场静音 → 异源段，逐级回落。</summary>
public class HlsAdDetectionTests
{
    private const string MediaUrl = "https://media.example/v/index.m3u8";

    // ---------- 合成数据 ----------

    private static string Playlist(int count, double seconds = 5, Func<int, string>? uri = null, string extraHead = "")
    {
        var builder = new StringBuilder("#EXTM3U\n#EXT-X-TARGETDURATION:5\n").Append(extraHead);
        for (var i = 0; i < count; i++)
        {
            builder.Append("#EXTINF:").Append(seconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)).Append(",\n");
            builder.Append(uri?.Invoke(i) ?? $"seg{i}.ts").Append('\n');
        }

        return builder.Append("#EXT-X-ENDLIST\n").ToString();
    }

    private static string Seg(int index) => $"https://media.example/v/seg{index}.ts";

    private static byte[] Packet(int pid, bool payloadStart, ReadOnlySpan<byte> payload)
    {
        var packet = new byte[188];
        packet[0] = 0x47;
        packet[1] = (byte)((payloadStart ? 0x40 : 0x00) | ((pid >> 8) & 0x1F));
        packet[2] = (byte)(pid & 0xFF);
        if (payload.Length == 184)
        {
            packet[3] = 0x10;
            payload.CopyTo(packet.AsSpan(4));
            return packet;
        }

        // 用适配域填充，让负载正好落在包尾。
        packet[3] = 0x30;
        var adaptationLength = 183 - payload.Length;
        packet[4] = (byte)adaptationLength;
        if (adaptationLength > 0)
        {
            packet[5] = 0x00;
            packet.AsSpan(6, adaptationLength - 1).Fill(0xFF);
        }

        payload.CopyTo(packet.AsSpan(188 - payload.Length));
        return packet;
    }

    private static byte[] Psi(params byte[] section)
    {
        var payload = new byte[184];
        payload.AsSpan().Fill(0xFF);
        payload[0] = 0x00; // pointer_field
        section.CopyTo(payload.AsSpan(1));
        return payload;
    }

    private static void WriteStream(MemoryStream output, int pid, int payloadBytes)
    {
        var first = true;
        while (payloadBytes > 0)
        {
            var size = Math.Min(184, payloadBytes);
            output.Write(Packet(pid, first, new byte[size]));
            payloadBytes -= size;
            first = false;
        }
    }

    /// <summary>PAT + PMT（H.264 视频 PID 0x100，AAC 音频 PID 0x101）+ 指定负载字节数的两路流。</summary>
    private static byte[] Ts(int videoBytes, int audioBytes)
    {
        using var output = new MemoryStream();
        output.Write(Packet(0, true, Psi(
            0x00, 0xB0, 0x0D, 0x00, 0x01, 0xC1, 0x00, 0x00,
            0x00, 0x01, 0xF0, 0x00,
            0, 0, 0, 0)));
        output.Write(Packet(0x1000, true, Psi(
            0x02, 0xB0, 0x17, 0x00, 0x01, 0xC1, 0x00, 0x00,
            0xE1, 0x00, 0xF0, 0x00,
            0x1B, 0xE1, 0x00, 0xF0, 0x00,
            0x0F, 0xE1, 0x01, 0xF0, 0x00,
            0, 0, 0, 0)));
        WriteStream(output, 0x100, videoBytes);
        WriteStream(output, 0x101, audioBytes);
        return output.ToArray();
    }

    private static byte[] BlackSilent() => Ts(videoBytes: 10_000, audioBytes: 2_000);

    private static byte[] BlackButLoud() => Ts(videoBytes: 10_000, audioBytes: 40_000);

    private sealed class FakeProbe : ISegmentProbe
    {
        public Dictionary<string, byte[]> Small { get; } = new();
        public long NormalSize { get; set; } = 500_000;
        public bool NoSizes { get; set; }
        public bool Throw { get; set; }
        public bool Hang { get; set; }
        public int SizeCalls;
        public int FetchCalls;

        public async Task<long?> GetSizeAsync(string url, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref SizeCalls);
            if (Hang)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            if (Throw)
            {
                throw new HttpRequestException("boom");
            }

            if (NoSizes)
            {
                return null;
            }

            return Small.TryGetValue(url, out var bytes) ? bytes.Length : NormalSize;
        }

        public Task<byte[]?> FetchAsync(string url, int maxBytes, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref FetchCalls);
            return Task.FromResult(Small.TryGetValue(url, out var bytes) ? bytes : null);
        }
    }

    private sealed class PlaylistHandler(Dictionary<string, string> playlists) : HttpMessageHandler
    {
        public List<string> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            lock (Requests)
            {
                Requests.Add($"{request.Method} {url}");
            }

            return Task.FromResult(playlists.TryGetValue(url, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static HlsAdSkip Create(string playlist, ISegmentProbe probe, string url = MediaUrl) =>
        new(new HttpClient(new PlaylistHandler(new Dictionary<string, string> { [url] = playlist })), probe);

    private static string ForeignHostPlaylist() =>
        Playlist(80, uri: i => i is 40 or 41 ? $"https://ad-cdn.example/ad{i}.ts" : $"seg{i}.ts");

    // ---------- 第一级 ----------

    [Fact]
    public async Task Cue_markers_win_and_no_segment_is_probed()
    {
        var playlist = "#EXTM3U\n#EXTINF:10,\ns0.ts\n#EXT-X-CUE-OUT:DURATION=5\n#EXTINF:5,\nad.ts\n#EXT-X-CUE-IN\n#EXTINF:10,\ns1.ts\n";
        var probe = new FakeProbe();

        var result = await Create(playlist, probe).DetectAsync(MediaUrl);

        Assert.Equal(AdDetectionTier.Cue, result.Tier);
        Assert.Equal(new AdRange(10_000, 15_000), Assert.Single(result.Ranges));
        Assert.Equal(AdTierOutcome.Hit, Assert.Single(result.Attempts).Outcome);
        Assert.Equal(0, probe.SizeCalls);
    }

    // ---------- 第二级 ----------

    [Fact]
    public async Task Without_cue_falls_to_black_silence_and_skips_the_head_block()
    {
        var probe = new FakeProbe();
        probe.Small[Seg(3)] = BlackSilent(); // 15s~20s 是黑场静音

        var result = await Create(Playlist(80), probe).DetectAsync(MediaUrl);

        Assert.Equal(AdDetectionTier.BlackSilence, result.Tier);
        Assert.Equal(new AdRange(0, 20_000), Assert.Single(result.Ranges));
        Assert.Equal(
            new[] { (AdDetectionTier.Cue, AdTierOutcome.Miss), (AdDetectionTier.BlackSilence, AdTierOutcome.Hit) },
            result.Attempts.Select(attempt => (attempt.Tier, attempt.Outcome)).ToArray());
        Assert.Equal(1, probe.FetchCalls); // 只下载码率骤降的那一个分片
    }

    [Fact]
    public async Task Black_silence_near_the_end_skips_the_tail_block()
    {
        var probe = new FakeProbe();
        probe.Small[Seg(75)] = BlackSilent(); // 375s~380s，总长 400s

        var result = await Create(Playlist(80), probe).DetectAsync(MediaUrl);

        Assert.Equal(AdDetectionTier.BlackSilence, result.Tier);
        Assert.Equal(new AdRange(375_000, 400_000), Assert.Single(result.Ranges));
    }

    [Fact]
    public async Task Head_and_tail_blocks_are_both_reported()
    {
        var probe = new FakeProbe();
        probe.Small[Seg(2)] = BlackSilent();
        probe.Small[Seg(3)] = BlackSilent();
        probe.Small[Seg(77)] = BlackSilent();

        var result = await Create(Playlist(80), probe).DetectAsync(MediaUrl);

        Assert.Equal(new[] { new AdRange(0, 20_000), new AdRange(385_000, 400_000) }, result.Ranges);
    }

    [Fact]
    public async Task Black_but_not_silent_is_not_a_mark_and_everything_falls_through_to_normal_playback()
    {
        var probe = new FakeProbe();
        probe.Small[Seg(3)] = BlackButLoud();

        var result = await Create(Playlist(80), probe).DetectAsync(MediaUrl);

        Assert.Equal(AdDetectionTier.None, result.Tier);
        Assert.Empty(result.Ranges);
        Assert.Equal(
            new[]
            {
                (AdDetectionTier.Cue, AdTierOutcome.Miss),
                (AdDetectionTier.BlackSilence, AdTierOutcome.Miss),
                (AdDetectionTier.ForeignHost, AdTierOutcome.Miss),
            },
            result.Attempts.Select(attempt => (attempt.Tier, attempt.Outcome)).ToArray());
    }

    [Fact]
    public async Task Mark_later_than_45_seconds_is_ignored()
    {
        var probe = new FakeProbe();
        probe.Small[Seg(10)] = BlackSilent(); // 50s~55s，超出片头 45 秒上限

        var result = await Create(Playlist(80), probe).DetectAsync(MediaUrl);

        Assert.Empty(result.Ranges);
        Assert.Equal(AdTierOutcome.Miss, result.Attempts[1].Outcome);
    }

    [Fact]
    public async Task Black_silent_run_longer_than_15_seconds_is_not_a_boundary()
    {
        var probe = new FakeProbe();
        foreach (var index in new[] { 1, 2, 3, 4 }) // 连续 20 秒
        {
            probe.Small[Seg(index)] = BlackSilent();
        }

        var result = await Create(Playlist(80), probe).DetectAsync(MediaUrl);

        Assert.Empty(result.Ranges);
    }

    [Fact]
    public async Task Short_playlists_are_not_probed()
    {
        var probe = new FakeProbe();

        var result = await Create(Playlist(20), probe).DetectAsync(MediaUrl); // 100 秒

        Assert.Empty(result.Ranges);
        Assert.Equal(0, probe.SizeCalls);
        Assert.Equal(AdTierOutcome.Miss, result.Attempts[1].Outcome);
    }

    [Fact]
    public async Task Only_head_and_tail_windows_are_probed()
    {
        var probe = new FakeProbe();

        await Create(Playlist(80), probe).DetectAsync(MediaUrl);

        Assert.Equal(24, probe.SizeCalls); // 开头 60 秒 12 个 + 结尾 60 秒 12 个
        Assert.Equal(0, probe.FetchCalls);
    }

    // ---------- 第二级失败 → 第三级 ----------

    [Fact]
    public async Task Missing_sizes_fall_back_to_foreign_host_heuristic()
    {
        var probe = new FakeProbe { NoSizes = true };

        var result = await Create(ForeignHostPlaylist(), probe).DetectAsync(MediaUrl);

        Assert.Equal(AdDetectionTier.ForeignHost, result.Tier);
        Assert.Equal(new AdRange(200_000, 210_000), Assert.Single(result.Ranges));
        Assert.Equal(AdTierOutcome.Error, result.Attempts[1].Outcome);
    }

    [Fact]
    public async Task Probe_exceptions_fall_back_to_foreign_host_heuristic()
    {
        var probe = new FakeProbe { Throw = true };

        var result = await Create(ForeignHostPlaylist(), probe).DetectAsync(MediaUrl);

        Assert.Equal(AdDetectionTier.ForeignHost, result.Tier);
        Assert.Equal(AdTierOutcome.Error, result.Attempts[1].Outcome);
    }

    [Fact]
    public async Task Probe_timeout_falls_back_to_foreign_host_heuristic()
    {
        var probe = new FakeProbe { Hang = true };
        var skip = new HlsAdSkip(
            new HttpClient(new PlaylistHandler(new Dictionary<string, string> { [MediaUrl] = ForeignHostPlaylist() })),
            probe)
        {
            BlackSilenceTimeout = TimeSpan.FromMilliseconds(50),
        };

        var result = await skip.DetectAsync(MediaUrl);

        Assert.Equal(AdDetectionTier.ForeignHost, result.Tier);
        Assert.Equal((AdTierOutcome.Error, "探测超时"), (result.Attempts[1].Outcome, result.Attempts[1].Detail));
    }

    [Fact]
    public async Task Encrypted_playlists_skip_the_probe_and_fall_back()
    {
        var probe = new FakeProbe();
        var playlist = Playlist(
            80,
            uri: i => i is 40 or 41 ? $"https://ad-cdn.example/ad{i}.ts" : $"seg{i}.ts",
            extraHead: "#EXT-X-KEY:METHOD=AES-128,URI=\"key.bin\"\n");

        var result = await Create(playlist, probe).DetectAsync(MediaUrl);

        Assert.Equal(AdDetectionTier.ForeignHost, result.Tier);
        Assert.Equal(AdTierOutcome.Error, result.Attempts[1].Outcome);
        Assert.Equal(0, probe.SizeCalls);
    }

    [Fact]
    public async Task Unreadable_candidates_count_as_a_failed_tier()
    {
        var probe = new FakeProbe();
        probe.Small[Seg(3)] = Encoding.ASCII.GetBytes(new string('x', 4000)); // 不是 TS

        var result = await Create(ForeignHostPlaylist(), probe).DetectAsync(MediaUrl);

        Assert.Equal(AdDetectionTier.ForeignHost, result.Tier);
        Assert.Equal(AdTierOutcome.Error, result.Attempts[1].Outcome);
    }

    // ---------- 全部失败 ----------

    [Fact]
    public async Task Playlist_fetch_failure_returns_empty_without_throwing()
    {
        var skip = new HlsAdSkip(new HttpClient(new PlaylistHandler(new Dictionary<string, string>())), new FakeProbe());

        var result = await skip.DetectAsync(MediaUrl);

        Assert.Equal(AdDetectionTier.None, result.Tier);
        Assert.Empty(result.Ranges);
        Assert.Empty(await skip.GetAdRangesAsync(MediaUrl));
    }

    [Fact]
    public async Task Non_hls_urls_are_ignored()
    {
        var handler = new PlaylistHandler(new Dictionary<string, string>());
        var skip = new HlsAdSkip(new HttpClient(handler), new FakeProbe());

        Assert.Empty((await skip.DetectAsync("https://media.example/v/movie.mp4")).Ranges);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Caller_cancellation_returns_empty_without_throwing()
    {
        var probe = new FakeProbe { Hang = true };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var result = await Create(ForeignHostPlaylist(), probe).DetectAsync(MediaUrl, cts.Token);

        Assert.Empty(result.Ranges);
    }

    [Fact]
    public async Task GetAdRangesAsync_returns_the_ranges_of_the_winning_tier()
    {
        var probe = new FakeProbe();
        probe.Small[Seg(3)] = BlackSilent();

        var ranges = await Create(Playlist(80), probe).GetAdRangesAsync(MediaUrl);

        Assert.Equal(new AdRange(0, 20_000), Assert.Single(ranges));
    }

    [Fact]
    public async Task Master_playlist_is_resolved_before_probing()
    {
        const string master = "https://media.example/master.m3u8";
        var probe = new FakeProbe();
        probe.Small[Seg(3)] = BlackSilent();
        var handler = new PlaylistHandler(new Dictionary<string, string>
        {
            [master] = "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=800000\nv/index.m3u8\n",
            [MediaUrl] = Playlist(80),
        });

        var result = await new HlsAdSkip(new HttpClient(handler), probe).DetectAsync(master);

        Assert.Equal(AdDetectionTier.BlackSilence, result.Tier);
        Assert.Equal(new AdRange(0, 20_000), Assert.Single(result.Ranges));
    }

    // ---------- 默认探测器与 TS 解析 ----------

    [Fact]
    public void Ts_probe_counts_video_and_audio_payload_bytes()
    {
        var stats = MpegTsProbe.Analyze(Ts(videoBytes: 1_000, audioBytes: 300));

        Assert.NotNull(stats);
        Assert.Equal((1_000L, 300L, true, true), (stats!.VideoBytes, stats.AudioBytes, stats.HasVideo, stats.HasAudio));
    }

    [Fact]
    public void Ts_probe_rejects_non_ts_data()
    {
        Assert.Null(MpegTsProbe.Analyze(Encoding.ASCII.GetBytes(new string('x', 1000))));
        Assert.Null(MpegTsProbe.Analyze(ReadOnlySpan<byte>.Empty));
    }

    private sealed class SegmentHandler(byte[] body, bool withLength = true) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Content = request.Method == HttpMethod.Head ? new ByteArrayContent([]) : new ByteArrayContent(body);
            response.Content.Headers.ContentLength = withLength ? body.Length : null;
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task Http_probe_reads_size_from_head_and_caps_downloads()
    {
        var body = new byte[4096];
        var probe = new HttpSegmentProbe(new HttpClient(new SegmentHandler(body)));

        Assert.Equal(4096, await probe.GetSizeAsync(Seg(0), CancellationToken.None));
        Assert.Equal(4096, (await probe.FetchAsync(Seg(0), 10_000, CancellationToken.None))!.Length);
        Assert.Null(await probe.FetchAsync(Seg(0), 1_000, CancellationToken.None));
    }

    [Fact]
    public void BuildRanges_merges_adjacent_marks()
    {
        var marked = new[]
        {
            new BlackSilenceAdDetector.Segment(2, 10_000, 5_000, Seg(2)),
            new BlackSilenceAdDetector.Segment(3, 15_000, 5_000, Seg(3)),
        };

        var ranges = BlackSilenceAdDetector.BuildRanges(marked, 400_000);

        Assert.Equal(new AdRange(0, 20_000), Assert.Single(ranges));
    }
}
