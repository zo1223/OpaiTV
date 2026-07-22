using OrionTV.Desktop.Core;
using System.Net;

namespace OrionTV.Desktop.Tests;

public class AppleCmsServiceTests
{
    [Fact]
    public void BuildUrl_AppendsParamsInsideProxyUrlTarget()
    {
        var url = AppleCmsUrlBuilder.Build(
            "https://proxy.example/?url=https://source.example/vod",
            new Dictionary<string, string>
            {
                ["ac"] = "detail",
                ["pg"] = "1"
            });

        Assert.Equal("https://proxy.example/?url=https://source.example/vod?ac=detail&pg=1", url);
    }

    [Fact]
    public void ParseEpisodes_ExtractsUniqueEpisodeUrls()
    {
        var episodes = AppleCmsEpisodeParser.Parse("第1集$https://video.example/1.m3u8#第2集$https://video.example/2.m3u8$$$备用$https://video.example/1.m3u8");

        Assert.Equal(
            ["https://video.example/1.m3u8", "https://video.example/2.m3u8"],
            episodes);
    }

    [Fact]
    public void ParseEpisodes_SkipsValuesThatCannotBePlayedAsAbsoluteUris()
    {
        var episodes = AppleCmsEpisodeParser.Parse("第1集$not-a-url#第2集$https://video.example/2.m3u8#纯文本");

        Assert.Equal(["https://video.example/2.m3u8"], episodes);
    }

    [Fact]
    public void ParseSources_KeepsAppleCmsPlaybackLinesSeparate()
    {
        var sources = AppleCmsEpisodeParser.ParseSources(
            "qiyi$$$qq",
            "第1集$https://video.example/qiyi-1.m3u8#第2集$https://video.example/qiyi-2.m3u8$$$" +
            "第1集$https://video.example/qq-1.m3u8#第2集$https://video.example/qq-2.m3u8");

        Assert.Equal(2, sources.Count);
        Assert.Equal("qiyi", sources[0].Name);
        Assert.Equal(["https://video.example/qiyi-1.m3u8", "https://video.example/qiyi-2.m3u8"], sources[0].Episodes);
        Assert.Equal("qq", sources[1].Name);
        Assert.Equal(["https://video.example/qq-1.m3u8", "https://video.example/qq-2.m3u8"], sources[1].Episodes);
    }

    [Fact]
    public void ExpandSources_CreatesOneSelectableResultPerPlaybackLine()
    {
        var result = new SearchResult(
            "7", "Movie", "", ["https://video.example/qiyi.m3u8"], "primary", "主线路", "",
            PlaybackSources:
            [
                new PlaybackSource("0:qiyi", "qiyi", ["https://video.example/qiyi.m3u8"]),
                new PlaybackSource("1:qq", "qq", ["https://video.example/qq.m3u8"]),
            ]);

        var expanded = SearchResultSources.Expand(result);

        Assert.Equal(2, expanded.Count);
        Assert.Equal(["主线路 · qiyi", "主线路 · qq"], expanded.Select(item => item.SourceName));
        Assert.NotEqual(SearchResultSources.KeyOf(expanded[0]), SearchResultSources.KeyOf(expanded[1]));
    }

    [Fact]
    public void ExpandSources_KeepsTheRouteNameWhenOnlyOnePlaybackLineExists()
    {
        var item = new AppleCmsVodItem
        {
            VodId = 766500,
            VodName = "告知信",
            VodPlayFrom = "WS☛最全",
            VodPlayUrl = "第1集$https://video.example/notice.m3u8",
        };
        var result = AppleCmsMapper.ToSearchResult(
            new VideoSource("primary", "主线路", "https://source.example/vod"),
            item);

        var expanded = Assert.Single(SearchResultSources.Expand(result));

        Assert.Equal("主线路 · WS☛最全", expanded.SourceName);
        Assert.Equal("0:WS☛最全", expanded.PlaybackSourceKey);
        Assert.Equal(["https://video.example/notice.m3u8"], expanded.Episodes);
    }

    [Fact]
    public void MapSearchResult_UsesAppleCmsFields()
    {
        var source = new VideoSource("demo", "Demo", "https://source.example/vod");
        var item = new AppleCmsVodItem
        {
            VodId = 7,
            VodName = "Movie",
            VodPic = "https://image.example/movie.jpg",
            VodYear = "2026",
            VodPlayUrl = "HD$https://video.example/movie.m3u8"
        };

        var result = AppleCmsMapper.ToSearchResult(source, item);

        Assert.Equal("7", result.Id);
        Assert.Equal("Movie", result.Title);
        Assert.Equal("https://image.example/movie.jpg", result.Poster);
        Assert.Equal(["https://video.example/movie.m3u8"], result.Episodes);
        Assert.Equal("demo", result.Source);
        Assert.Equal("Demo", result.SourceName);
        Assert.Equal("2026", result.Year);
    }

    [Fact]
    public async Task GetPreferredDetailSourceAsync_UsesSelectedSourceIdWithoutSearchingEverySource()
    {
        var requests = new List<string>();
        using var http = new HttpClient(new StubHandler(request =>
        {
            requests.Add(request.RequestUri!.ToString());
            return """
            {
              "list": [
                {
                  "vod_id": 7,
                  "vod_name": "Movie",
                  "vod_pic": "https://image.example/movie.jpg",
                  "vod_year": "2026",
                  "vod_play_url": "第1集$https://video.example/movie.m3u8"
                }
              ]
            }
            """;
        }));

        var service = new CatalogService(http, new DoubanClient(http));
        var sources = new[]
        {
            new VideoSource("slow", "Slow", "https://slow.example/vod"),
            new VideoSource("fast", "Fast", "https://fast.example/vod"),
        };

        var result = await service.GetPreferredDetailSourceAsync(sources, "fast", "7", "Movie");

        Assert.NotNull(result);
        Assert.Equal("fast", result.Source);
        Assert.Equal("7", result.Id);
        Assert.Equal(["https://video.example/movie.m3u8"], result.Episodes);
        Assert.Equal(["https://fast.example/vod?ac=detail&ids=7"], requests);
    }

    [Fact]
    public async Task GetPreferredDetailSourcesAsync_ReturnsEveryPlaybackLineFromPosterId()
    {
        var requests = new List<string>();
        using var http = new HttpClient(new StubHandler(request =>
        {
            requests.Add(request.RequestUri!.ToString());
            return """
            {
              "list": [
                {
                  "vod_id": 7,
                  "vod_name": "Movie",
                  "vod_play_from": "YY$$$SH$$$WS",
                  "vod_play_url": "第1集$https://video.example/yy.m3u8$$$第1集$https://video.example/sh.m3u8$$$第1集$https://video.example/ws.m3u8"
                }
              ]
            }
            """;
        }));

        var service = new CatalogService(http, new DoubanClient(http));
        var sources = new[] { new VideoSource("primary", "主线路", "https://fast.example/vod") };

        var results = await service.GetPreferredDetailSourcesAsync(sources, "primary", "7", "Movie");

        Assert.Equal(3, results.Count);
        Assert.Equal(
            ["主线路 · YY", "主线路 · SH", "主线路 · WS"],
            results.Select(result => result.SourceName));
        Assert.Equal(
            ["https://video.example/yy.m3u8", "https://video.example/sh.m3u8", "https://video.example/ws.m3u8"],
            results.Select(result => Assert.Single(result.Episodes)));
        Assert.Equal(["https://fast.example/vod?ac=detail&ids=7"], requests);
    }

    [Fact]
    public async Task GetDetailSourcesAsync_KeepsSameNamedRoutesFromDifferentMatchingIds()
    {
        using var http = new HttpClient(new StubHandler(_ =>
            """
            {
              "list": [
                {
                  "vod_id": 7,
                  "vod_name": "Movie",
                  "vod_play_from": "YY",
                  "vod_play_url": "第1集$https://video.example/version-a.m3u8"
                },
                {
                  "vod_id": 8,
                  "vod_name": "Movie",
                  "vod_play_from": "YY",
                  "vod_play_url": "第1集$https://video.example/version-b.m3u8"
                },
                {
                  "vod_id": 9,
                  "vod_name": "Movie"
                }
              ]
            }
            """));

        var service = new CatalogService(http, new DoubanClient(http));
        var sources = new[] { new VideoSource("primary", "主线路", "https://fast.example/vod") };

        var results = await service.GetDetailSourcesAsync(sources, "Movie");

        // 无剧集的 id=9 也保留（TVBox search 常无 play_url，详情页再补拉）
        Assert.Equal(["7", "8", "9"], results.Select(result => result.Id));
        Assert.NotEqual(
            SearchResultSources.SelectionKeyOf(results[0]),
            SearchResultSources.SelectionKeyOf(results[1]));
        Assert.Equal(
            ["https://video.example/version-a.m3u8", "https://video.example/version-b.m3u8"],
            results.Take(2).Select(result => Assert.Single(result.Episodes)));
        Assert.Empty(results[2].Episodes);
    }

    [Fact]
    public void PlaybackFallbackSelector_SkipsCurrentFailedAndSourcesWithoutRequestedEpisode()
    {
        var results = new[]
        {
            new SearchResult("1", "Movie", "", ["https://video.example/current-1.m3u8", "https://video.example/current-2.m3u8"], "current", "Current", ""),
            new SearchResult("2", "Movie", "", ["https://video.example/failed-1.m3u8", "https://video.example/failed-2.m3u8"], "failed", "Failed", ""),
            new SearchResult("3", "Movie", "", ["https://video.example/short-1.m3u8"], "short", "Short", ""),
            new SearchResult("4", "Movie", "", ["https://video.example/good-1.m3u8", "https://video.example/good-2.m3u8"], "good", "Good", ""),
        };

        var selected = PlaybackFallbackSelector.SelectNext(results, "current", new HashSet<string> { "failed" }, 1);

        Assert.NotNull(selected);
        Assert.Equal("good", selected.Source);
    }

    [Fact]
    public void CatalogPlayability_FromDetailSources_MatchesDetailPageSourceCount()
    {
        var results = new[]
        {
            // 同 source 不同 id → 详情页两枚 chip
            new SearchResult("1", "Movie", "", ["https://video.example/a.m3u8"], "a", "A", ""),
            new SearchResult("4", "Movie", "", ["https://video.example/a-backup.m3u8"], "a", "A", ""),
            // 空剧集仍计入（TVBox search 无 play_url）
            new SearchResult("2", "Movie", "", [], "b", "B", ""),
            // 标题归一后匹配
            new SearchResult("5", " Movie ", "", ["https://video.example/d.m3u8"], "d", "D", ""),
            // 标题不匹配
            new SearchResult("3", "Movie Extra", "", ["https://video.example/c.m3u8"], "c", "C", ""),
            // 空白剧集仍保留条目
            new SearchResult("6", "Movie", "", ["   "], "e", "E", ""),
        };

        var playability = CatalogPlayability.FromDetailSources("Movie", results);

        Assert.Equal(CatalogPlayabilityStatus.Playable, playability.Status);
        // source+id 去重：1,4,2,5,6 → 5（不含 Movie Extra）
        Assert.Equal(5, playability.PlayableSourceCount);
    }

    [Fact]
    public void CatalogPlayability_FromDetailSources_CountsPlaybackLinesSeparately()
    {
        var multiLine = new SearchResult(
            "10",
            "Movie",
            "",
            ["https://video.example/qiyi.m3u8"],
            "site",
            "Site",
            "",
            PlaybackSources:
            [
                new PlaybackSource("0:qiyi", "qiyi", ["https://video.example/qiyi.m3u8"]),
                new PlaybackSource("1:qq", "qq", ["https://video.example/qq.m3u8"]),
            ]);

        var playability = CatalogPlayability.FromDetailSources("Movie", [multiLine]);

        Assert.Equal(CatalogPlayabilityStatus.Playable, playability.Status);
        Assert.Equal(2, playability.PlayableSourceCount);
    }

    [Fact]
    public void CatalogPlayability_FromDetailSources_MarksUnavailableWhenNoMatchingTitle()
    {
        var results = new[]
        {
            new SearchResult("1", "Movie Extra", "", ["https://video.example/a.m3u8"], "a", "A", ""),
        };

        var playability = CatalogPlayability.FromDetailSources("Movie", results);

        Assert.Equal(CatalogPlayabilityStatus.Unavailable, playability.Status);
        Assert.Equal(0, playability.PlayableSourceCount);
    }

    [Fact]
    public void CatalogTitleMatcher_IgnoresWhitespaceAndCase()
    {
        Assert.True(CatalogTitleMatcher.Matches("Movie", " Movie "));
        Assert.True(CatalogTitleMatcher.Matches("AbC", "abc"));
        Assert.False(CatalogTitleMatcher.Matches("Movie", "Movie Extra"));
    }

    [Theory]
    [InlineData(CatalogPlayabilityStatus.Playable, 2, "2\u6e90")]
    [InlineData(CatalogPlayabilityStatus.Playable, 1, "1\u6e90")]
    [InlineData(CatalogPlayabilityStatus.Unavailable, 0, "\u6682\u65e0\u8d44\u6e90")]
    [InlineData(CatalogPlayabilityStatus.Checking, null, null)]
    [InlineData(CatalogPlayabilityStatus.Unknown, null, null)]
    public void CatalogPlayabilityBadge_GetLabel_ReturnsCompactVisibleStates(
        CatalogPlayabilityStatus status,
        int? playableSourceCount,
        string? expected)
    {
        Assert.Equal(expected, CatalogPlayabilityBadge.GetLabel(status, playableSourceCount));
    }

    [Fact]
    public void CatalogPlayabilityCache_ReusesCompletedResultForTheSameDoubanItem()
    {
        var cache = new CatalogPlayabilityCache();
        var firstViewItem = new CatalogItem("douban-1295644", "这个杀手不太冷", "one.jpg", "douban", "豆瓣", "1994");
        var nextViewItem = new CatalogItem("douban-1295644", "这个杀手不太冷", "two.jpg", "douban", "豆瓣", "1994");
        var result = new CatalogPlayability(CatalogPlayabilityStatus.Playable, 3);

        Assert.True(cache.Store(firstViewItem, result, cache.Generation));
        Assert.True(cache.TryGet(nextViewItem, out var cached));
        Assert.Equal(result, cached);
    }

    [Fact]
    public void CatalogPlayabilityCache_InvalidationRejectsAnInFlightResultFromOldSources()
    {
        var cache = new CatalogPlayabilityCache();
        var item = new CatalogItem("douban-1295644", "这个杀手不太冷", "poster.jpg", "douban", "豆瓣", "1994");
        var result = new CatalogPlayability(CatalogPlayabilityStatus.Playable, 3);
        var oldGeneration = cache.Generation;

        Assert.True(cache.Store(item, result, oldGeneration));
        cache.Invalidate();

        Assert.False(cache.TryGet(item, out _));
        Assert.False(cache.Store(item, result, oldGeneration));
        Assert.True(cache.Store(item, result, cache.Generation));
    }

    [Fact]
    public void PlaybackMediaOptions_ForHttpStream_AddsAndroidLikeHeadersAndReconnectOptions()
    {
        var options = PlaybackMediaOptions.ForUri(new Uri("https://cdn.example:8443/path/movie.m3u8?token=abc"));

        Assert.Contains(options, option => option.StartsWith(":http-user-agent=Mozilla/5.0 (Linux; Android", StringComparison.Ordinal));
        Assert.Contains(":http-referrer=https://cdn.example:8443/", options);
        Assert.Contains(":http-reconnect", options);
        Assert.Contains(":network-caching=1500", options);
    }

    [Fact]
    public void PlaybackMediaOptions_ForNonHttpStream_DoesNotAddHttpReferrer()
    {
        var options = PlaybackMediaOptions.ForUri(new Uri("rtmp://stream.example/live"));

        Assert.DoesNotContain(options, option => option.StartsWith(":http-referrer=", StringComparison.Ordinal));
    }

    [Fact]
    public void PlaybackStartupWatchdog_TimesOutOnlyAfterConfiguredStartupWindow()
    {
        var watchdog = new PlaybackStartupWatchdog(TimeSpan.FromSeconds(25));
        var start = DateTimeOffset.Parse("2026-06-17T10:00:00Z");

        watchdog.Arm(start);

        Assert.False(watchdog.IsTimedOut(start.AddSeconds(24)));
        Assert.True(watchdog.IsTimedOut(start.AddSeconds(25)));
    }

    [Fact]
    public void PlaybackStartupWatchdog_DisarmClearsPendingTimeout()
    {
        var watchdog = new PlaybackStartupWatchdog(TimeSpan.FromSeconds(25));
        var start = DateTimeOffset.Parse("2026-06-17T10:00:00Z");

        watchdog.Arm(start);
        watchdog.Disarm();

        Assert.False(watchdog.IsTimedOut(start.AddMinutes(1)));
    }

    [Theory]
    [InlineData(PlaybackStartupSignal.Buffering, false)]
    [InlineData(PlaybackStartupSignal.LengthChanged, false)]
    [InlineData(PlaybackStartupSignal.Playing, true)]
    [InlineData(PlaybackStartupSignal.TimeAdvanced, true)]
    public void PlaybackStartupPolicy_OnlyResolvesAfterRealPlaybackProgress(
        PlaybackStartupSignal signal,
        bool expected)
    {
        Assert.Equal(expected, PlaybackStartupPolicy.ResolvesStartup(signal));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, string> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(handle(request))
            };
            return Task.FromResult(response);
        }
    }
}
