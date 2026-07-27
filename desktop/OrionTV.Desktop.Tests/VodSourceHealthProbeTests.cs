using OrionTV.Desktop.App;
using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.Tests;

public class VodSourceHealthProbeTests
{
    [Fact]
    public void Sampler_selects_latest_numbered_middle_and_first_available_episodes()
    {
        var source = Result(
            ["u1", "u2", "special", "u4", "u5", "trailer"],
            ["第1集", "第2集", "特别篇", "第4集", "第5集", "预告"]);

        var samples = VodSourceProbeSampler.Select(source);

        Assert.Equal([4, 3, 0], samples.Select(sample => sample.Index));
        Assert.Equal(["第5集", "第4集", "第1集"], samples.Select(sample => sample.Label));
    }

    [Fact]
    public void Sampler_checks_resume_episode_before_general_source_samples()
    {
        var source = Result(
            ["u1", "u2", "u3", "u4", "u5"],
            ["第1集", "第2集", "第3集", "第4集", "第5集"]);

        var samples = VodSourceProbeSampler.Select(source, preferredEpisodeIndex: 1);

        Assert.Equal([1, 4, 2], samples.Select(sample => sample.Index));
        Assert.Equal("u2", samples[0].Url);
    }

    [Fact]
    public async Task Probe_marks_source_playable_only_after_latest_sample_decodes()
    {
        var decoder = new StubDecodeProbe(_ => new(MediaDecodeProbeStatus.Decoded, "ok"));
        var networkCalls = new List<(string Url, bool MeasureSpeed)>();
        var probe = new VodSourceHealthProbe((url, measureSpeed, _) =>
        {
            networkCalls.Add((url, measureSpeed));
            return Task.FromResult(Validated());
        }, decoder);

        var result = await probe.ProbeAsync(
            Result(["u1", "u2", "u3", "u4", "u5"], ["第1集", "第2集", "第3集", "第4集", "第5集"]),
            CancellationToken.None);

        Assert.Equal(VodSourceProbeStatus.Playable, result.Status);
        Assert.Equal(3, result.PassedSamples);
        Assert.Equal(3, result.TotalSamples);
        Assert.Equal(["u5"], decoder.Urls);
        Assert.Equal([("u5", true), ("u3", false), ("u1", false)], networkCalls);
        Assert.Contains("可播 · 3/3", result.DisplayLabel);
    }

    [Fact]
    public async Task Probe_marks_source_partial_when_one_sample_has_broken_media()
    {
        var decoder = new StubDecodeProbe(url => url == "u3"
            ? new(MediaDecodeProbeStatus.Failed, "failed")
            : new(MediaDecodeProbeStatus.Decoded, "ok"));
        var probe = new VodSourceHealthProbe(
            (url, _, _) => Task.FromResult(url == "u3"
                ? ChannelHealthResult.Invalid("媒体分片无法访问")
                : Validated()),
            decoder);

        var result = await probe.ProbeAsync(
            Result(["u1", "u2", "u3", "u4", "u5"], ["第1集", "第2集", "第3集", "第4集", "第5集"]),
            CancellationToken.None);

        Assert.Equal(VodSourceProbeStatus.Partial, result.Status);
        Assert.Equal(2, result.PassedSamples);
        Assert.Equal(3, result.TotalSamples);
    }

    [Fact]
    public async Task Probe_uses_player_as_authority_when_http_probe_gets_403()
    {
        var decoder = new StubDecodeProbe(_ => new(MediaDecodeProbeStatus.Decoded, "ok"));
        var http403 = new ChannelHealthResult(
            ChannelHealth.Bad,
            0,
            "HTTP 403",
            TtfbMs: 30,
            MediaStatus: MediaProbeStatus.Unreachable);
        var probe = new VodSourceHealthProbe((_, _, _) => Task.FromResult(http403), decoder);

        var result = await probe.ProbeAsync(
            Result(["u1", "u2", "u3", "u4", "u5"], ["第1集", "第2集", "第3集", "第4集", "第5集"]),
            CancellationToken.None);

        Assert.Equal(VodSourceProbeStatus.Playable, result.Status);
        Assert.Equal(3, result.PassedSamples);
        Assert.Equal(["u5", "u3", "u1"], decoder.Urls);
        Assert.Equal("可播 · 3/3 · 未取得测速数据", result.DisplayLabel);
        Assert.DoesNotContain("403", result.DisplayLabel);
    }

    [Fact]
    public async Task Playable_source_resolver_keeps_only_fully_playable_routes_and_caches_result()
    {
        var candidates = new[]
        {
            Result(["good"], ["第1集"]) with { Source = "good", SourceName = "Good" },
            Result(["partial"], ["第1集"]) with { Source = "partial", SourceName = "Partial" },
            Result(["bad"], ["第1集"]) with { Source = "bad", SourceName = "Bad" },
        };
        var probeCalls = 0;
        using var resolver = new VodPlayableSourceResolver(
            (_, _) => Task.FromResult<IReadOnlyList<SearchResult>>(candidates),
            (item, _) => Task.FromResult<SearchResult?>(item),
            (items, onResult, _) =>
            {
                probeCalls++;
                foreach (var item in items)
                {
                    var status = item.Source switch
                    {
                        "good" => VodSourceProbeStatus.Playable,
                        "partial" => VodSourceProbeStatus.Partial,
                        _ => VodSourceProbeStatus.Unplayable,
                    };
                    onResult(
                        SearchResultSources.SelectionKeyOf(item),
                        new VodSourceHealthResult(
                            status,
                            status == VodSourceProbeStatus.Playable ? 3 : 0,
                            3,
                            Validated(),
                            status.ToString()));
                }

                return Task.CompletedTask;
            });

        var first = await resolver.ResolveAsync("Series");
        var second = await resolver.ResolveAsync("Series");
        var card = first.ApplyTo(new CatalogItem("1", "Series", "", "good", "Good"));

        Assert.Equal(3, first.Candidates.Count);
        Assert.Equal("good", Assert.Single(first.PlayableSources).Source);
        Assert.Equal(CatalogPlayabilityStatus.Playable, first.Status);
        Assert.Same(first, second);
        Assert.Equal(1, probeCalls);
        Assert.Equal(1, card.PlayableSourceCount);
        Assert.Equal(first.PlayableSources, card.PlayableSources);
    }

    [Fact]
    public async Task Probe_reports_html_route_without_starting_decoder()
    {
        var decoder = new StubDecodeProbe(_ => new(MediaDecodeProbeStatus.Decoded, "unexpected"));
        var probe = new VodSourceHealthProbe(
            (_, _, _) => Task.FromResult(ChannelHealthResult.Unsupported("网页线路")),
            decoder);

        var result = await probe.ProbeAsync(Result(["u1", "u2"], ["第1集", "第2集"]), CancellationToken.None);

        Assert.Equal(VodSourceProbeStatus.Unsupported, result.Status);
        Assert.Equal("网页线路", result.DisplayLabel);
        Assert.Empty(decoder.Urls);
    }

    [Fact]
    public async Task Probe_reports_unplayable_when_every_sample_fails_to_decode()
    {
        var decoder = new StubDecodeProbe(_ => new(MediaDecodeProbeStatus.Failed, "failed"));
        var probe = new VodSourceHealthProbe((_, _, _) => Task.FromResult(Validated()), decoder);

        var result = await probe.ProbeAsync(
            Result(["u1", "u2", "u3", "u4", "u5"], ["第1集", "第2集", "第3集", "第4集", "第5集"]),
            CancellationToken.None);

        Assert.Equal(VodSourceProbeStatus.Unplayable, result.Status);
        Assert.Equal("无法解码视频画面", result.DisplayLabel);
        Assert.Equal(["u5"], decoder.Urls);
    }

    [Fact]
    public void LibVlc_decode_probe_uses_the_same_online_request_options_as_playback()
    {
        var options = LibVlcMediaDecodeProbe.BuildMediaOptions(
            new Uri("https://video.example:8443/episode.m3u8"));

        Assert.Contains($":http-user-agent={PlaybackMediaOptions.AndroidLikeUserAgent}", options);
        Assert.Contains(":http-referrer=https://video.example:8443/", options);
        Assert.Contains(":http-reconnect", options);
        Assert.Contains(":network-caching=6000", options);
        Assert.DoesNotContain(":no-audio", options);
        Assert.Equal(PlaybackStartupWatchdog.DefaultTimeout, LibVlcMediaDecodeProbe.DecodeTimeout);
    }

    private static SearchResult Result(IReadOnlyList<string> episodes, IReadOnlyList<string> labels) => new(
        "1",
        "Series",
        "",
        episodes,
        "source",
        "Source",
        "",
        EpisodeLabels: labels);

    private static ChannelHealthResult Validated() => new(
        ChannelHealth.Good,
        90,
        "4.0 Mbps",
        ThroughputMbps: 4,
        TtfbMs: 100,
        MediaStatus: MediaProbeStatus.MediaValidated);

    private sealed class StubDecodeProbe(Func<string, MediaDecodeProbeResult> resultFactory) : IMediaDecodeProbe
    {
        public List<string> Urls { get; } = [];

        public Task<MediaDecodeProbeResult> ProbeAsync(string url, CancellationToken cancellationToken)
        {
            Urls.Add(url);
            return Task.FromResult(resultFactory(url));
        }
    }

}
