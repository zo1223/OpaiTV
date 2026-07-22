using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.Tests;

public class ChannelHealthScoringTests
{
    [Fact]
    public void Unreachable_is_bad_with_zero_score()
    {
        var result = ChannelHealthScoring.Evaluate(reachable: false, ttfbMs: 1200, throughputBps: 0, requiredBps: null);

        Assert.Equal(ChannelHealth.Bad, result.Health);
        Assert.Equal(0, result.Score);
        Assert.Equal("无法连接", result.Detail);
    }

    [Fact]
    public void Download_well_above_bitrate_is_good()
    {
        // 下载 12Mbps，码率 2Mbps → ratio 6 → 流畅
        var result = ChannelHealthScoring.Evaluate(true, ttfbMs: 200, throughputBps: 12_000_000, requiredBps: 2_000_000);

        Assert.Equal(ChannelHealth.Good, result.Health);
        Assert.True(result.Score >= 70);
    }

    [Fact]
    public void Download_barely_meeting_bitrate_is_fair()
    {
        // 下载 2.2Mbps，码率 2Mbps → ratio 1.1 → 一般
        var result = ChannelHealthScoring.Evaluate(true, ttfbMs: 300, throughputBps: 2_200_000, requiredBps: 2_000_000);

        Assert.Equal(ChannelHealth.Fair, result.Health);
        Assert.InRange(result.Score, 40, 69);
    }

    [Fact]
    public void Download_below_bitrate_is_bad()
    {
        // 下载 1Mbps，码率 4Mbps → ratio 0.25 → 不畅
        var result = ChannelHealthScoring.Evaluate(true, ttfbMs: 300, throughputBps: 1_000_000, requiredBps: 4_000_000);

        Assert.Equal(ChannelHealth.Bad, result.Health);
        Assert.Contains("跟不上", result.Detail);
    }

    [Fact]
    public void Slow_first_byte_downgrades_one_notch()
    {
        // ratio 6 本应流畅，但首字节 5s（>3s）降到一般
        var fast = ChannelHealthScoring.Evaluate(true, ttfbMs: 200, throughputBps: 12_000_000, requiredBps: 2_000_000);
        var slow = ChannelHealthScoring.Evaluate(true, ttfbMs: 5000, throughputBps: 12_000_000, requiredBps: 2_000_000);

        Assert.Equal(ChannelHealth.Good, fast.Health);
        Assert.Equal(ChannelHealth.Fair, slow.Health);
    }

    [Fact]
    public void No_declared_bitrate_uses_absolute_throughput()
    {
        var good = ChannelHealthScoring.Evaluate(true, ttfbMs: 200, throughputBps: 5_000_000, requiredBps: null);
        var bad = ChannelHealthScoring.Evaluate(true, ttfbMs: 200, throughputBps: 500_000, requiredBps: null);

        Assert.Equal(ChannelHealth.Good, good.Health);
        Assert.Equal(ChannelHealth.Bad, bad.Health);
    }

    [Fact]
    public void Connected_without_measurable_throughput_is_fair()
    {
        var result = ChannelHealthScoring.Evaluate(true, ttfbMs: 400, throughputBps: 0, requiredBps: 2_000_000);

        Assert.Equal(ChannelHealth.Fair, result.Health);
        Assert.Equal("已连接·未测速", result.Detail);
    }

    [Theory]
    [InlineData(ChannelHealth.Good, "流畅")]
    [InlineData(ChannelHealth.Fair, "一般")]
    [InlineData(ChannelHealth.Bad, "不畅")]
    [InlineData(ChannelHealth.Unknown, "")]
    public void LabelOf_maps_each_level(ChannelHealth health, string expected)
    {
        Assert.Equal(expected, ChannelHealthScoring.LabelOf(health));
    }

    [Fact]
    public void MetricsLabelOf_prefers_latency_and_throughput_values()
    {
        var result = new ChannelHealthResult(ChannelHealth.Bad, 20, "1.0 Mbps", ThroughputMbps: 4.456, TtfbMs: 120);

        Assert.Equal("120ms · 4.5Mbps", ChannelHealthDisplay.MetricsLabelOf(result));
    }

    [Fact]
    public void MetricsLabelOf_handles_partial_or_unknown_probe_results()
    {
        Assert.Equal("850ms", ChannelHealthDisplay.MetricsLabelOf(new ChannelHealthResult(ChannelHealth.Fair, 40, "", TtfbMs: 850)));
        Assert.Equal("未测速", ChannelHealthDisplay.MetricsLabelOf(new ChannelHealthResult(ChannelHealth.Unknown, 0, "")));
    }

    [Fact]
    public void SummaryOf_reports_average_probe_metrics()
    {
        var results = new[]
        {
            new ChannelHealthResult(ChannelHealth.Good, 90, "", ThroughputMbps: 5.0, TtfbMs: 100),
            new ChannelHealthResult(ChannelHealth.Bad, 20, "", ThroughputMbps: 1.0, TtfbMs: 300),
            new ChannelHealthResult(ChannelHealth.Unknown, 0, ""),
        };

        Assert.Equal("已测 2 · 平均延迟 200ms · 平均速率 3.0Mbps", ChannelHealthDisplay.SummaryOf(results));
    }

    [Fact]
    public async Task ProbeAsync_sends_playback_compatible_headers_to_http_requests()
    {
        var handler = new RecordingHandler();
        var probe = new ChannelHealthProbe(new HttpClient(handler));

        await probe.ProbeAsync("https://live.example/cctv1.m3u8", CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal(PlaybackMediaOptions.AndroidLikeUserAgent, request.UserAgent);
            Assert.Equal(new Uri("https://live.example/"), request.Referrer);
        });
    }

    [Fact]
    public async Task ProbeAsync_treats_flv_response_as_raw_stream_even_when_original_url_ends_with_m3u8()
    {
        var handler = new FlvResponseHandler();
        var probe = new ChannelHealthProbe(new HttpClient(handler));

        var result = await probe.ProbeAsync("http://live.example/live.m3u8?u=token", CancellationToken.None);

        Assert.Single(handler.Requests);
        Assert.True(result.TtfbMs >= 0);
        Assert.True(result.ThroughputMbps > 0);
    }

    private sealed record CapturedRequest(Uri Uri, string UserAgent, Uri? Referrer);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new CapturedRequest(
                request.RequestUri!,
                request.Headers.UserAgent.ToString(),
                request.Headers.Referrer));

            var response = request.RequestUri?.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) == true
                ? new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("#EXTM3U\n#EXT-X-TARGETDURATION:10\n#EXTINF:10,\nseg.ts"),
                }
                : new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(new byte[256_000]),
                };

            return Task.FromResult(response);
        }
    }

    private sealed class FlvResponseHandler : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            if (Requests.Count > 1)
            {
                throw new InvalidOperationException("FLV response must be measured directly.");
            }

            var bytes = new byte[256_000];
            bytes[0] = (byte)'F';
            bytes[1] = (byte)'L';
            bytes[2] = (byte)'V';

            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes),
            };
            response.Content.Headers.ContentType = new("video/x-flv");
            return Task.FromResult(response);
        }
    }
}
