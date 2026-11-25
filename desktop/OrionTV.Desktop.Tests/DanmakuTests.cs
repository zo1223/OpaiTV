using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.Tests;

/// <summary>
/// 弹幕：标题匹配、三个来源的解析与取数流程（假 HTTP，不联网）、时钟、分片缓存、排布和时间对齐。
/// WPF 弹幕层（DanmakuOverlay）只负责把 <see cref="DanmakuSession.Frame"/> 的结果画出来，这里测的是它画的内容。
/// </summary>
public class DanmakuTests
{
    // ---------- 工具 ----------

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    private sealed class FakeHttp : IDanmakuHttp
    {
        public List<(string Method, string Url, string? Body, IReadOnlyDictionary<string, string>? Headers)> Calls { get; } = [];

        /// <summary>(方法, 地址, 请求体) → JSON 字符串 / byte[]；抛异常表示网络失败。</summary>
        public Func<string, string, string?, object> Handler { get; set; } = (_, url, _) => throw new HttpRequestException("unexpected " + url);

        public Task<JsonDocument> GetJsonAsync(string url, IReadOnlyDictionary<string, string>? headers, TimeSpan timeout, CancellationToken ct) =>
            Task.FromResult(JsonDocument.Parse((string)Record("GET", url, null, headers, ct)));

        public Task<JsonDocument> PostJsonAsync(string url, string jsonBody, IReadOnlyDictionary<string, string>? headers, TimeSpan timeout, CancellationToken ct) =>
            Task.FromResult(JsonDocument.Parse((string)Record("POST", url, jsonBody, headers, ct)));

        public Task<byte[]> GetBytesAsync(string url, IReadOnlyDictionary<string, string>? headers, TimeSpan timeout, CancellationToken ct) =>
            Task.FromResult((byte[])Record("GET", url, null, headers, ct));

        private object Record(string method, string url, string? body, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (Calls)
            {
                Calls.Add((method, url, body, headers));
            }

            return Handler(method, url, body);
        }

        public int CountOf(string urlPart)
        {
            lock (Calls)
            {
                return Calls.Count(call => call.Url.Contains(urlPart, StringComparison.Ordinal));
            }
        }
    }

    private static DanmakuComment Scroll(double time, string text = "弹幕") => new(time, text, 0xffffff, DanmakuMode.Scroll);

    /// <summary>每个字 20 宽，方便手算位置。</summary>
    private static DanmakuLayoutOptions Layout(int maxItems = DanmakuEngine.DefaultMaxItems) =>
        new(1000, 600, 20, 8, 4, (text, fontSize) => text.Length * fontSize, maxItems);

    private static DanmakuEngine EngineOf(IReadOnlyList<DanmakuComment> comments, DanmakuLayoutOptions? options = null) =>
        new(
            (from, to) => comments.Where(c => c.Time > from && c.Time <= to).OrderBy(c => c.Time).ToList(),
            options ?? Layout());

    private static DanmakuTrack TrackOf(double segmentSeconds, Func<int, IReadOnlyList<DanmakuComment>> load, List<int>? requested = null) =>
        new("test", "test", segmentSeconds, (index, _) =>
        {
            if (requested is not null)
            {
                lock (requested)
                {
                    requested.Add(index);
                }
            }

            return Task.FromResult(load(index));
        });

    private static void WaitUntil(Func<bool> condition) =>
        Assert.True(SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(5)), "condition not reached in time");

    // ---------- 设置 ----------

    [Fact]
    public void Settings_DanmakuIsOffByDefault_AndUsesBuiltInSource()
    {
        var settings = new DesktopSettings();

        Assert.False(settings.DanmakuEnabled);
        Assert.Equal("", settings.DanmakuServerUrl);
        Assert.Equal(0.8, settings.DanmakuOpacity);
        Assert.Equal("normal", settings.DanmakuSpeed);
        Assert.Equal("normal", settings.DanmakuFontSize);
    }

    [Fact]
    public void Settings_LegacyFileWithoutDanmakuFields_KeepsDefaults()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var legacy = JsonSerializer.Deserialize<DesktopSettings>("""{"SubscriptionUrl":"https://a.example/s.json","Volume":70}""", options)!;
        var migrated = DesktopSettingsMigration.Apply(legacy);

        Assert.False(migrated.DanmakuEnabled);
        Assert.Equal("", migrated.DanmakuServerUrl);
        Assert.Equal(0.8, migrated.DanmakuOpacity);
        Assert.Equal(70, migrated.Volume);
    }

    [Fact]
    public void Settings_RoundTripAndMigrationNormalizeDanmakuFields()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var saved = new DesktopSettings(
            DanmakuEnabled: true,
            DanmakuServerUrl: " 192.168.1.7:9321/api/v2/ ",
            DanmakuOpacity: 7,
            DanmakuSpeed: "warp",
            DanmakuFontSize: "large");
        var loaded = DesktopSettingsMigration.Apply(
            JsonSerializer.Deserialize<DesktopSettings>(JsonSerializer.Serialize(saved, options), options)!);

        Assert.True(loaded.DanmakuEnabled);
        Assert.Equal("http://192.168.1.7:9321", loaded.DanmakuServerUrl);
        Assert.Equal(1, loaded.DanmakuOpacity);
        Assert.Equal("normal", loaded.DanmakuSpeed);
        Assert.Equal("large", loaded.DanmakuFontSize);
    }

    [Fact]
    public void Settings_EditingOtherFieldsKeepsDanmakuSettings()
    {
        var current = new DesktopSettings(DanmakuEnabled: true, DanmakuServerUrl: "http://10.0.0.2:9321", DanmakuSpeed: "fast");

        var updated = DesktopSettingsForm.ApplyEditableFields(current, "https://a.example/s.json", "", "", autoSkipAds: false);

        Assert.True(updated.DanmakuEnabled);
        Assert.Equal("http://10.0.0.2:9321", updated.DanmakuServerUrl);
        Assert.Equal("fast", updated.DanmakuSpeed);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    [InlineData("http://192.168.1.7:9321", "http://192.168.1.7:9321")]
    [InlineData("192.168.1.7:9321", "http://192.168.1.7:9321")]
    [InlineData(" https://dm.example.com/ ", "https://dm.example.com")]
    [InlineData("https://dm.example.com/api/v2", "https://dm.example.com")]
    [InlineData("https://dm.example.com/API/V2///", "https://dm.example.com")]
    [InlineData("https://dm.example.com/mytoken/", "https://dm.example.com/mytoken")]
    [InlineData("https://dm.example.com/mytoken/api/v2", "https://dm.example.com/mytoken")]
    [InlineData("ftp://dm.example.com", "")]
    [InlineData("http://", "")]
    public void NormalizeServerUrl_AcceptsOnlyHttpAddresses(string? input, string expected)
    {
        Assert.Equal(expected, DanmakuSettingsRules.NormalizeServerUrl(input));
    }

    [Fact]
    public void SettingsRules_MapSpeedAndFontSize()
    {
        Assert.Equal(11, DanmakuSettingsRules.ScrollSeconds("slow"));
        Assert.Equal(8, DanmakuSettingsRules.ScrollSeconds("normal"));
        Assert.Equal(5.5, DanmakuSettingsRules.ScrollSeconds("fast"));
        Assert.Equal(8, DanmakuSettingsRules.ScrollSeconds("bogus"));
        Assert.Equal(0.8, DanmakuSettingsRules.FontScale("small"));
        Assert.Equal(1.3, DanmakuSettingsRules.FontScale("large"));
        Assert.Equal(0.2, DanmakuSettingsRules.NormalizeOpacity(0));
        Assert.Equal(0.8, DanmakuSettingsRules.NormalizeOpacity(double.NaN));
    }

    // ---------- 标题匹配 ----------

    [Theory]
    [InlineData("庆余年 第二季", "庆余年", 2)]
    [InlineData("庆余年第2季", "庆余年", 2)]
    [InlineData("庆余年2", "庆余年", 2)]
    [InlineData("斗罗大陆 Season 3", "斗罗大陆", 3)]
    [InlineData("权力的游戏 S08", "权力的游戏", 8)]
    [InlineData("鬼灭之刃 第十二季", "鬼灭之刃", 12)]
    public void ParseTitle_ExtractsSeason(string title, string baseText, int season)
    {
        var parsed = DanmakuTitle.ParseTitle(title);

        Assert.Equal(baseText, parsed.BaseText);
        Assert.Equal(season, parsed.Season);
    }

    [Theory]
    [InlineData("请回答1988")]
    [InlineData("庆余年")]
    [InlineData("1917")]
    [InlineData("流浪地球")]
    public void ParseTitle_DoesNotInventSeason(string title)
    {
        Assert.Null(DanmakuTitle.ParseTitle(title).Season);
    }

    [Fact]
    public void ParseTitle_StripsBracketsAndVariantSuffix()
    {
        var parsed = DanmakuTitle.ParseTitle("【4K】繁花 (2023)[沪语版]");
        Assert.Equal("繁花", parsed.Cleaned);
        Assert.Equal("繁花", parsed.Normalized);

        var dubbed = DanmakuTitle.ParseTitle("甄嬛传 国语版");
        Assert.Equal("甄嬛传", dubbed.Base);
        Assert.True(dubbed.Variant);
    }

    [Theory]
    [InlineData("十", 10)]
    [InlineData("十二", 12)]
    [InlineData("二十", 20)]
    [InlineData("二十三", 23)]
    [InlineData("两", 2)]
    [InlineData("7", 7)]
    public void ParseChineseNumber_Parses(string text, int expected)
    {
        Assert.Equal(expected, DanmakuTitle.ParseChineseNumber(text));
    }

    [Fact]
    public void ParseChineseNumber_RejectsGarbage()
    {
        Assert.Null(DanmakuTitle.ParseChineseNumber(""));
        Assert.Null(DanmakuTitle.ParseChineseNumber("甲"));
        Assert.Null(DanmakuTitle.ParseChineseNumber("一百二"));
    }

    [Fact]
    public void ScoreTitleCandidate_NeverMatchesAnotherShowOrSeason()
    {
        var query = DanmakuTitle.ParseTitle("庆余年 第二季");

        Assert.Equal(0, DanmakuTitle.ScoreTitleCandidate(query, new DanmakuTitleCandidate("庆余年"), null, 36, 1));
        Assert.Equal(0, DanmakuTitle.ScoreTitleCandidate(query, new DanmakuTitleCandidate("庆余年 第三季"), null, 36, 1));
        Assert.Equal(0, DanmakuTitle.ScoreTitleCandidate(query, new DanmakuTitleCandidate("庆余年之帝王业"), null, 36, 1));
        Assert.Equal(100, DanmakuTitle.ScoreTitleCandidate(query, new DanmakuTitleCandidate("庆余年第二季"), null, 36, 1));
        Assert.Equal(90, DanmakuTitle.ScoreTitleCandidate(query, new DanmakuTitleCandidate("庆余年2"), null, 36, 1));

        var first = DanmakuTitle.ParseTitle("庆余年");
        Assert.Equal(0, DanmakuTitle.ScoreTitleCandidate(first, new DanmakuTitleCandidate("庆余年 第二季"), null, 46, 1));
        Assert.Equal(90, DanmakuTitle.ScoreTitleCandidate(first, new DanmakuTitleCandidate("庆余年 第一季"), null, 46, 1));
    }

    [Fact]
    public void PickBestCandidate_UsesYearAndKindToBreakTies()
    {
        var query = DanmakuTitle.ParseTitle("射雕英雄传");
        var candidates = new[]
        {
            new DanmakuTitleCandidate("射雕英雄传", "2003", 42, false),
            new DanmakuTitleCandidate("射雕英雄传", "2017", 52, false),
            new DanmakuTitleCandidate("射雕英雄传", "2017", 1, true),
            new DanmakuTitleCandidate("神雕侠侣", "2017", 52, false),
        };

        Assert.Equal(1, DanmakuTitle.PickBestCandidate(query, candidates, "2017", 52, 3));
        Assert.Equal(0, DanmakuTitle.PickBestCandidate(query, candidates, "2003", 42, 3));
        Assert.Equal(2, DanmakuTitle.PickBestCandidate(query, candidates, "2017", 1, 1));
        Assert.Equal(-1, DanmakuTitle.PickBestCandidate(DanmakuTitle.ParseTitle("天龙八部"), candidates, null, 40, 1));
    }

    [Fact]
    public void PickEpisodeIndex_PrefersEpisodeNumberAndRefusesMissingEpisodes()
    {
        // 平台分集带集号：只按集号找
        Assert.Equal(2, DanmakuTitle.PickEpisodeIndex([1, 2, 3, 4], episodeIndex: 0, episodeNumber: 3, episodeCount: 4));
        Assert.Equal(-1, DanmakuTitle.PickEpisodeIndex([1, 2, 3, 4], episodeIndex: 1, episodeNumber: 9, episodeCount: 40));
        // 有不带集号的：找不到集号时按下标
        Assert.Equal(1, DanmakuTitle.PickEpisodeIndex([null, null, null], episodeIndex: 1, episodeNumber: 2, episodeCount: 3));
        Assert.Equal(-1, DanmakuTitle.PickEpisodeIndex([null, null], episodeIndex: 5, episodeNumber: 6, episodeCount: 8));
        // 电影
        Assert.Equal(0, DanmakuTitle.PickEpisodeIndex([null], episodeIndex: 0, episodeNumber: 1, episodeCount: 1));
        Assert.Equal(-1, DanmakuTitle.PickEpisodeIndex([], 0, 1, 1));
    }

    [Fact]
    public void EpisodeHelpers_ParseLabels()
    {
        Assert.Equal(12, DanmakuTitle.ParseEpisodeNumber("第12集"));
        Assert.Equal(3, DanmakuTitle.ParseEpisodeNumber("03"));
        Assert.Null(DanmakuTitle.ParseEpisodeNumber("彩蛋1"));
        Assert.Null(DanmakuTitle.ParseEpisodeNumber(null));

        Assert.Equal(12, DanmakuTitle.EpisodeNumberFromLabel("第12集", 3));
        Assert.Equal(7, DanmakuTitle.EpisodeNumberFromLabel("07", 3));
        Assert.Equal(4, DanmakuTitle.EpisodeNumberFromLabel("HD国语", 3));
        Assert.Equal(1, DanmakuTitle.EpisodeNumberFromLabel(null, 0));

        Assert.True(DanmakuTitle.IsExtraEpisodeTitle("第5集预告"));
        Assert.False(DanmakuTitle.IsExtraEpisodeTitle("第5集"));
    }

    [Fact]
    public void BuildMatchFileName_FollowsDanmuApiConvention()
    {
        Assert.Equal("庆余年 S02E05", DanmakuTitle.BuildMatchFileName("庆余年 第二季", 5, 36));
        Assert.Equal("繁花 S01E12", DanmakuTitle.BuildMatchFileName("繁花", 12, 30));
        Assert.Equal("流浪地球2", DanmakuTitle.BuildMatchFileName("流浪地球2", 1, 1));
    }

    // ---------- 腾讯视频 ----------

    private const string TencentSearchJson = """
    {"ret":0,"data":{
      "areaBoxList":[
        {"boxId":"MainNeed","itemList":[
          {"doc":{"id":"cid_s2"},"videoInfo":{"title":"<em>庆余年</em> 第二季","year":2024,"typeName":"电视剧","subjectDoc":{"videoNum":36},"playSites":[{"enName":"qq"}]}},
          {"doc":{"id":"cid_other_site"},"videoInfo":{"title":"庆余年 第二季","year":2024,"typeName":"电视剧","playSites":[{"enName":"iqiyi"}]}}
        ]},
        {"boxId":"Other","itemList":[{"doc":{"id":"cid_ignored"},"videoInfo":{"title":"庆余年 第二季","year":2024,"typeName":"电视剧"}}]}
      ],
      "normalList":{"itemList":[
        {"doc":{"id":"cid_s2"},"videoInfo":{"title":"庆余年 第二季","year":2024,"typeName":"电视剧"}},
        {"doc":{"id":"cid_s1"},"videoInfo":{"title":"庆余年","year":"2019","typeName":"电视剧","subjectDoc":{"videoNum":"46"}}},
        {"doc":{"id":"cid_short"},"videoInfo":{"title":"庆余年 第二季 解说","typeName":"电视剧"}},
        {"doc":{"id":"cid_web"},"videoInfo":{"title":"庆余年 第二季","year":2024,"typeName":"电视剧","subTitle":"全网搜"}},
        {"doc":{"id":"cid_game"},"videoInfo":{"title":"庆余年 第二季","year":2024,"typeName":"游戏"}},
        {"doc":{"id":"cid_movie"},"videoInfo":{"title":"流浪地球2","year":2023,"typeName":"电影"}}
      ]}
    }}
    """;

    [Fact]
    public void Tencent_ParseSearch_KeepsOnlyPlayableLongVideos()
    {
        var result = TencentDanmakuProvider.ParseSearch(Json(TencentSearchJson));

        Assert.Equal(["cid_s2", "cid_s1", "cid_movie"], result.Select(candidate => candidate.Cid).ToArray());
        Assert.Equal("庆余年 第二季", result[0].Title);
        Assert.Equal("2024", result[0].Year);
        Assert.Equal(36, result[0].EpisodeCount);
        Assert.False(result[0].IsMovie);
        Assert.Equal("2019", result[1].Year);
        Assert.Equal(46, result[1].EpisodeCount);
        Assert.True(result[2].IsMovie);
        Assert.Equal(1, result[2].EpisodeCount);
        Assert.Empty(TencentDanmakuProvider.ParseSearch(Json("{}")));
        Assert.Empty(TencentDanmakuProvider.ParseSearch(Json("""{"data":null}""")));
    }

    private static string TencentEpisodePageJson(int from, int to, bool withTabs, bool withExtras = false)
    {
        var items = new List<string>();
        for (var n = from; n <= to; n++)
        {
            items.Add($$$"""{"item_params":{"vid":"vid{{{n}}}","title":"{{{n}}}","play_title":"庆余年第二季 第{{{n}}}集","is_trailer":"0"}}""");
        }

        if (withExtras)
        {
            items.Add("""{"item_params":{"vid":"vid_trailer","title":"37","is_trailer":"1"}}""");
            items.Add("""{"item_params":{"vid":"vid_extra","title":"花絮","play_title":"庆余年第二季 幕后花絮"}}""");
            items.Add("""{"item_params":{"vid":"vid1","title":"1","play_title":"重复"}}""");
        }

        var tabs = withTabs
            ? JsonSerializer.Serialize("""[{"begin":1,"end":30,"page_context":"ctx_1_30"},{"begin":31,"end":36,"page_context":"ctx_31_36"}]""")
            : "\"\"";
        return $$$"""{"ret":0,"data":{"module_list_datas":[{"module_datas":[{"module_params":{"tabs":{{{tabs}}}},"item_data_lists":{"item_datas":[{{{string.Join(",", items)}}}]}}]}]}}""";
    }

    [Fact]
    public void Tencent_ParseEpisodePage_ReadsTabsAndDropsTrailersAndExtras()
    {
        var page = TencentDanmakuProvider.ParseEpisodePage(Json(TencentEpisodePageJson(1, 3, withTabs: true, withExtras: true)));

        Assert.Equal(2, page.Tabs.Count);
        Assert.Equal(31, page.Tabs[1].Begin);
        Assert.Equal(36, page.Tabs[1].End);
        Assert.Equal("ctx_31_36", page.Tabs[1].PageContext);
        Assert.Equal(["vid1", "vid2", "vid3"], page.Episodes.Select(episode => episode.Vid).ToArray());
        Assert.Equal([1, 2, 3], page.Episodes.Select(episode => episode.Number).ToArray());
        Assert.Equal("庆余年第二季 第2集", page.Episodes[1].Title);
    }

    [Fact]
    public void Tencent_ParseEpisodePage_ToleratesBrokenTabs()
    {
        var page = TencentDanmakuProvider.ParseEpisodePage(Json(
            """{"data":{"module_list_datas":[{"module_datas":[{"module_params":{"tabs":"not json"},"item_data_lists":{"item_datas":[{"item_params":{"vid":"v1","title":"正片"}}]}}]}]}}"""));

        Assert.Empty(page.Tabs);
        Assert.Single(page.Episodes);
        Assert.Null(page.Episodes[0].Number);
    }

    [Fact]
    public void Tencent_ParseSegment_ReadsTimeColorAndPosition()
    {
        var comments = TencentDanmakuProvider.ParseSegment(Json("""
        {"barrage_list":[
          {"content":" 第一条 ","time_offset":"61000","content_style":""},
          {"content":"渐变色","time_offset":"62500","content_style":"{\"gradient_colors\":[\"#FF8800\",\"00FF00\"],\"position\":1}"},
          {"content":"顶部","time_offset":63000,"content_style":"{\"color\":\"ff0000\",\"position\":2}"},
          {"content":"底部","time_offset":"64000","content_style":"{\"color\":\"ffffff\",\"position\":3}"},
          {"content":"坏样式","time_offset":"65000","content_style":"{oops"},
          {"content":"","time_offset":"66000"},
          {"content":"没有时间"},
          {"content":"负时间","time_offset":"-5"}
        ]}
        """));

        Assert.Equal(5, comments.Count);
        Assert.Equal(new DanmakuComment(61, "第一条", 0xffffff, DanmakuMode.Scroll), comments[0]);
        Assert.Equal(new DanmakuComment(62.5, "渐变色", 0xff8800, DanmakuMode.Scroll), comments[1]);
        Assert.Equal(new DanmakuComment(63, "顶部", 0xff0000, DanmakuMode.Top), comments[2]);
        Assert.Equal(new DanmakuComment(64, "底部", 0xffffff, DanmakuMode.Bottom), comments[3]);
        Assert.Equal(new DanmakuComment(65, "坏样式", 0xffffff, DanmakuMode.Scroll), comments[4]);
        Assert.Empty(TencentDanmakuProvider.ParseSegment(Json("{}")));
    }

    [Fact]
    public void Tencent_SpreadWithinSecond_SpacesOutCommentsSharingASecond()
    {
        var spread = TencentDanmakuProvider.SpreadWithinSecond(
        [
            Scroll(10, "a"), Scroll(10, "b"), Scroll(10, "c"), Scroll(10, "d"),
            Scroll(11, "e"),
            Scroll(12.5, "f"), Scroll(12.5, "g"),
        ]);

        Assert.Equal([10, 10.25, 10.5, 10.75, 11, 12.5, 12.5], spread.Select(comment => comment.Time).ToArray());
        Assert.Equal("abcdefg", string.Concat(spread.Select(comment => comment.Text)));
    }

    private static FakeHttp TencentHttp(bool paged)
    {
        var http = new FakeHttp();
        http.Handler = (method, url, body) =>
        {
            if (url.Contains("MbSearch", StringComparison.Ordinal))
            {
                return TencentSearchJson;
            }

            if (url.Contains("GetPageData", StringComparison.Ordinal))
            {
                if (!paged)
                {
                    return TencentEpisodePageJson(1, 36, withTabs: false, withExtras: true);
                }

                return body!.Contains("ctx_31_36", StringComparison.Ordinal)
                    ? TencentEpisodePageJson(31, 36, withTabs: true)
                    : TencentEpisodePageJson(1, 30, withTabs: true);
            }

            if (url.Contains("/barrage/base/", StringComparison.Ordinal))
            {
                return """{"segment_span":"30000","segment_index":{"0":{"segment_start":"0","segment_name":"t/v1/0/30000"},"30000":{"segment_start":"30000","segment_name":"t/v1/30000/60000"}}}""";
            }

            if (url.Contains("/barrage/segment/", StringComparison.Ordinal))
            {
                return url.EndsWith("t/v1/30000/60000", StringComparison.Ordinal)
                    ? """{"barrage_list":[{"content":"第二片","time_offset":"31000"}]}"""
                    : """{"barrage_list":[{"content":"第一片","time_offset":"1000"}]}""";
            }

            throw new HttpRequestException("unexpected " + url);
        };
        return http;
    }

    [Fact]
    public async Task Tencent_Resolve_FindsEpisodeAndLoadsSegmentsLazily()
    {
        var http = TencentHttp(paged: false);
        var provider = new TencentDanmakuProvider(http);

        var track = await provider.ResolveAsync(new DanmakuQuery("庆余年 第二季", 4, 5, 36));

        Assert.NotNull(track);
        Assert.Equal("tencent", track.Provider);
        Assert.Equal(30, track.SegmentSeconds);
        Assert.Equal("庆余年第二季 第5集", track.Title);
        Assert.Equal(1, http.CountOf("/barrage/base/vid5"));
        Assert.Equal(0, http.CountOf("/barrage/segment/"));
        Assert.Contains("\"query\":", http.Calls[0].Body);
        Assert.Equal("https://v.qq.com", http.Calls[0].Headers!["Origin"]);

        var second = await track.LoadSegmentAsync(1);
        Assert.Equal(new DanmakuComment(31, "第二片", 0xffffff, DanmakuMode.Scroll), Assert.Single(second));
        Assert.Equal(1, http.CountOf("/barrage/segment/vid5/t/v1/30000/60000"));
        Assert.Empty(await track.LoadSegmentAsync(7));

        // 同一部片换一集：不再搜索、不再取分集列表
        var next = await provider.ResolveAsync(new DanmakuQuery("庆余年 第二季", 5, 6, 36));
        Assert.NotNull(next);
        Assert.Equal(1, http.CountOf("MbSearch"));
        Assert.Equal(1, http.CountOf("GetPageData"));
        Assert.Equal(1, http.CountOf("/barrage/base/vid6"));
    }

    [Fact]
    public async Task Tencent_Resolve_LongSeriesLoadsOnlyThePageOfTheEpisode()
    {
        var http = TencentHttp(paged: true);
        var provider = new TencentDanmakuProvider(http);

        var track = await provider.ResolveAsync(new DanmakuQuery("庆余年 第二季", 32, 33, 36));

        Assert.NotNull(track);
        Assert.Equal(1, http.CountOf("/barrage/base/vid33"));
        Assert.Equal(2, http.CountOf("GetPageData"));

        var early = await provider.ResolveAsync(new DanmakuQuery("庆余年 第二季", 1, 2, 36));
        Assert.NotNull(early);
        Assert.Equal(1, http.CountOf("/barrage/base/vid2"));
        Assert.Equal(3, http.CountOf("GetPageData"));
    }

    [Fact]
    public async Task Tencent_Resolve_ReturnsNullWhenNothingMatches()
    {
        var http = TencentHttp(paged: false);
        var provider = new TencentDanmakuProvider(http);

        Assert.Null(await provider.ResolveAsync(new DanmakuQuery("完全不存在的片子", 0, 1, 10)));
        Assert.Equal(0, http.CountOf("GetPageData"));
        // 片子对、但没有这一集
        Assert.Null(await provider.ResolveAsync(new DanmakuQuery("庆余年 第二季", 49, 50, 60)));
        Assert.Equal(0, http.CountOf("/barrage/"));
    }

    [Fact]
    public async Task Tencent_Resolve_NetworkFailureIsNotCached()
    {
        var http = TencentHttp(paged: false);
        var working = http.Handler;
        var fail = true;
        http.Handler = (method, url, body) => fail ? throw new HttpRequestException("offline") : working(method, url, body);
        var provider = new TencentDanmakuProvider(http);
        var query = new DanmakuQuery("庆余年 第二季", 0, 1, 36);

        await Assert.ThrowsAsync<HttpRequestException>(() => provider.ResolveAsync(query));
        fail = false;

        // 失败的那次查找是在后台从缓存里摘掉的，给它一点时间
        DanmakuTrack? track = null;
        for (var attempt = 0; attempt < 200 && track is null; attempt++)
        {
            try
            {
                track = await provider.ResolveAsync(query);
            }
            catch (HttpRequestException)
            {
                await Task.Delay(10);
            }
        }

        Assert.NotNull(track);
    }

    // ---------- 哔哩哔哩 ----------

    [Fact]
    public void Bilibili_WbiSigning_MatchesReferenceVectors()
    {
        Assert.Equal(
            "ea1db124af3cd062474693fa704f4ff8",
            BilibiliDanmakuProvider.BuildWbiMixinKey(
                "https://i0.hdslb.com/bfs/wbi/7cd084941338484aae1ad9425b84077c.png",
                "https://i0.hdslb.com/bfs/wbi/4932caff0ff746eaa6f01bf08ddd3c01.png"));

        // bilibili-API-collect 文档里的示例
        Assert.Equal(
            "bar=514&foo=114&wts=1702204169&zab=1919810&w_rid=8f6f2b5b3d485fe1886cec6a0be8c5d4",
            BilibiliDanmakuProvider.SignWbiQuery(
                new Dictionary<string, string> { ["foo"] = "114", ["bar"] = "514", ["zab"] = "1919810" },
                "ea1db124af3c7062474693fa704f4ff8",
                1702204169));

        Assert.StartsWith(
            "keyword=%E5%87%A1%E4%BA%BA%20%E4%BF%AE%E4%BB%99&page=1&wts=1&w_rid=",
            BilibiliDanmakuProvider.SignWbiQuery(
                new Dictionary<string, string> { ["page"] = "1", ["keyword"] = "凡人 修仙" },
                "k",
                1));
    }

    private const string BilibiliSearchJson = """
    {"code":0,"data":{"result":[
      {"season_id":28747,"title":"<em class=\"keyword\">凡人修仙传</em>","pubtime":1595606400,"ep_size":150,"season_type_name":"国创","media_type":4},
      {"season_id":"333","title":"凡人修仙传 剧场版","pubtime":0,"ep_size":0,"season_type_name":"电影","media_type":2},
      {"season_id":0,"title":"没有编号"},
      {"season_id":5,"title":""}
    ]}}
    """;

    [Fact]
    public void Bilibili_ParseSearch_ReadsSeasons()
    {
        var result = BilibiliDanmakuProvider.ParseSearch(Json(BilibiliSearchJson));

        Assert.Equal(2, result.Count);
        Assert.Equal(new BilibiliDanmakuCandidate(28747, "凡人修仙传", "2020", 150, false), result[0]);
        Assert.Equal(new BilibiliDanmakuCandidate(333, "凡人修仙传 剧场版", null, 0, true), result[1]);
        Assert.Empty(BilibiliDanmakuProvider.ParseSearch(Json("""{"code":0,"data":{}}""")));
    }

    [Fact]
    public void Bilibili_ParseEpisodes_DropsPreviews()
    {
        var episodes = BilibiliDanmakuProvider.ParseEpisodes(Json("""
        {"code":0,"result":{"episodes":[
          {"cid":1001,"title":"1","long_title":"初入修仙界","badge":"会员"},
          {"cid":1002,"title":"2","long_title":""},
          {"cid":1003,"title":"3","long_title":"下集","badge":"预告"},
          {"cid":1004,"title":"特别篇","long_title":"幕后特辑"},
          {"cid":0,"title":"4"},
          {"cid":1005,"title":"番外","long_title":"重逢"}
        ]}}
        """));

        Assert.Equal([1001L, 1002L, 1005L], episodes.Select(episode => episode.Cid).ToArray());
        Assert.Equal([1, 2, null], episodes.Select(episode => episode.Number).ToArray());
        Assert.Equal("1 初入修仙界", episodes[0].Title);
        Assert.Equal("2", episodes[1].Title);
    }

    [Fact]
    public void Bilibili_ParseXml_ReadsModesColorsAndEntities()
    {
        var comments = BilibiliDanmakuProvider.ParseXml("""
        <?xml version="1.0" encoding="UTF-8"?><i><chatserver>chat.bilibili.com</chatserver>
        <d p="12.5,1,25,16777215,1700000000,0,abc,1,10">普通滚动</d>
        <d p="13.25,4,25,16711680,1700000000,0,abc,2,10">底部 &lt;红&gt; &amp; &#x4F60;&#22909;</d>
        <d p="14,5,25,255,1700000000,0,abc,3,10">顶部</d>
        <d p="15,7,25,255,1700000000,0,abc,4,10">高级弹幕不显示</d>
        <d p="16,6,25,255,1700000000,0,abc,5,10">逆向弹幕不显示</d>
        <d p="-1,1,25,255,1700000000,0,abc,6,10">负时间</d>
        <d p="17,1,25,255,1700000000,0,abc,7,10">   </d>
        <d p="18,2,25,x,1700000000,0,abc,8,10">颜色坏了</d>
        </i>
        """);

        Assert.Equal(4, comments.Count);
        Assert.Equal(new DanmakuComment(12.5, "普通滚动", 0xffffff, DanmakuMode.Scroll), comments[0]);
        Assert.Equal(new DanmakuComment(13.25, "底部 <红> & 你好", 0xff0000, DanmakuMode.Bottom), comments[1]);
        Assert.Equal(new DanmakuComment(14, "顶部", 0x0000ff, DanmakuMode.Top), comments[2]);
        Assert.Equal(new DanmakuComment(18, "颜色坏了", 0xffffff, DanmakuMode.Scroll), comments[3]);
        Assert.Empty(BilibiliDanmakuProvider.ParseXml(""));
    }

    [Fact]
    public void Bilibili_DecodeXmlBytes_HandlesPlainAndEveryDeflateFlavour()
    {
        const string xml = """<?xml version="1.0" encoding="UTF-8"?><i><d p="1.5,1,25,16777215,0,0,x,1">你好 &amp; hi</d></i>""";
        var plain = Encoding.UTF8.GetBytes(xml);
        // Python zlib 压出来的裸 deflate（B 站实际返回的就是这种，没有 zlib 头）
        var rawDeflate = Convert.FromBase64String(
            "s7GvyM1RKEstKs7Mz7NVMtQzUFJIzUvOT8nMS7dVCg1x07VQsrezybSzSVEoAMmb6hjqGAEJM3NzcyNDUx0DIKzQMVSye7J3wdOlexXUEnMLrBUyMm30U+xs9DPtAA==");

        Assert.Equal(xml, BilibiliDanmakuProvider.DecodeXmlBytes(plain));
        Assert.Equal(xml, BilibiliDanmakuProvider.DecodeXmlBytes([0xEF, 0xBB, 0xBF, .. plain]));
        Assert.Equal(xml, BilibiliDanmakuProvider.DecodeXmlBytes(rawDeflate));
        Assert.Equal(xml, BilibiliDanmakuProvider.DecodeXmlBytes(Compress(plain, stream => new ZLibStream(stream, CompressionLevel.Optimal))));
        Assert.Equal(xml, BilibiliDanmakuProvider.DecodeXmlBytes(Compress(plain, stream => new GZipStream(stream, CompressionLevel.Optimal))));
        Assert.Equal(xml, BilibiliDanmakuProvider.DecodeXmlBytes(Compress(plain, stream => new DeflateStream(stream, CompressionLevel.Optimal))));
        Assert.Equal("", BilibiliDanmakuProvider.DecodeXmlBytes([1, 2, 3, 4, 5]));
        Assert.Equal("", BilibiliDanmakuProvider.DecodeXmlBytes([]));

        var comment = Assert.Single(BilibiliDanmakuProvider.ParseXml(BilibiliDanmakuProvider.DecodeXmlBytes(rawDeflate)));
        Assert.Equal(new DanmakuComment(1.5, "你好 & hi", 0xffffff, DanmakuMode.Scroll), comment);
    }

    private static byte[] Compress(byte[] data, Func<Stream, Stream> open)
    {
        using var output = new MemoryStream();
        using (var stream = open(output))
        {
            stream.Write(data);
        }

        return output.ToArray();
    }

    private static FakeHttp BilibiliHttp(Func<string, bool>? searchBlocked = null)
    {
        var http = new FakeHttp();
        http.Handler = (_, url, _) =>
        {
            if (url.Contains("/finger/spi", StringComparison.Ordinal))
            {
                return """{"code":0,"data":{"b_3":"BUVID3","b_4":"BUVID4"}}""";
            }

            if (url.Contains("/web-interface/nav", StringComparison.Ordinal))
            {
                return """{"code":-101,"data":{"wbi_img":{"img_url":"https://i0.hdslb.com/bfs/wbi/7cd084941338484aae1ad9425b84077c.png","sub_url":"https://i0.hdslb.com/bfs/wbi/4932caff0ff746eaa6f01bf08ddd3c01.png"}}}""";
            }

            if (url.Contains("/wbi/search/type", StringComparison.Ordinal))
            {
                if (searchBlocked?.Invoke(url) == true)
                {
                    return """{"code":-352,"data":{"v_voucher":"voucher_x"}}""";
                }

                return url.Contains("search_type=media_bangumi", StringComparison.Ordinal)
                    ? BilibiliSearchJson
                    : """{"code":0,"data":{}}""";
            }

            if (url.Contains("/pgc/view/web/season?season_id=28747", StringComparison.Ordinal))
            {
                return """{"code":0,"result":{"episodes":[{"cid":1001,"title":"1","long_title":"初入修仙界"},{"cid":1002,"title":"2","long_title":"升仙大会"}]}}""";
            }

            if (url == "https://comment.bilibili.com/1002.xml")
            {
                return Convert.FromBase64String(
                    "s7GvyM1RKEstKs7Mz7NVMtQzUFJIzUvOT8nMS7dVCg1x07VQsrezybSzSVEoAMmb6hjqGAEJM3NzcyNDUx0DIKzQMVSye7J3wdOlexXUEnMLrBUyMm30U+xs9DPtAA==");
            }

            throw new HttpRequestException("unexpected " + url);
        };
        return http;
    }

    [Fact]
    public async Task Bilibili_Resolve_SignsSearchAndInflatesXml()
    {
        var http = BilibiliHttp();
        var provider = new BilibiliDanmakuProvider(http, () => 1_700_000_000_000);

        var track = await provider.ResolveAsync(new DanmakuQuery("凡人修仙传", 1, 2, 150));

        Assert.NotNull(track);
        Assert.Equal("bilibili", track.Provider);
        Assert.Equal(0, track.SegmentSeconds);
        var comment = Assert.Single(await track.LoadSegmentAsync(0));
        Assert.Equal("你好 & hi", comment.Text);
        Assert.Empty(await track.LoadSegmentAsync(1));

        var search = http.Calls.First(call => call.Url.Contains("/wbi/search/type", StringComparison.Ordinal));
        Assert.Contains("&wts=1700000000&w_rid=", search.Url);
        Assert.Contains("keyword=%E5%87%A1%E4%BA%BA%E4%BF%AE%E4%BB%99%E4%BC%A0", search.Url);
        Assert.Equal("buvid3=BUVID3; buvid4=BUVID4", search.Headers!["Cookie"]);
        Assert.Equal("https://www.bilibili.com/", search.Headers["Referer"]);
        Assert.Equal(2, http.CountOf("/wbi/search/type"));
        Assert.Equal(1, http.CountOf("/finger/spi"));
    }

    [Fact]
    public async Task Bilibili_Resolve_RiskControlledSearchMeansNoMatch()
    {
        var http = BilibiliHttp(searchBlocked: _ => true);
        var provider = new BilibiliDanmakuProvider(http, () => 1_700_000_000_000);

        Assert.Null(await provider.ResolveAsync(new DanmakuQuery("凡人修仙传", 1, 2, 150)));
        Assert.Equal(0, http.CountOf("/pgc/view/web/season"));
    }

    // ---------- 自定义 danmu_api 服务器 ----------

    [Fact]
    public void Server_ParseComments_ReadsDandanplayFormat()
    {
        var comments = ServerDanmakuProvider.ParseComments(Json("""
        {"count":5,"comments":[
          {"cid":1,"p":"12.34,1,16777215,[tencent]","m":"滚动"},
          {"cid":2,"p":"20,5,16711680,[bilibili]","m":"顶部"},
          {"cid":3,"p":"30.5,4,255,[iqiyi]","m":"底部"},
          {"cid":4,"p":"bad,1,255","m":"时间坏了"},
          {"cid":5,"p":"40,1","m":"没有颜色"},
          {"cid":6,"p":"50,1,255","m":""}
        ]}
        """));

        Assert.Equal(4, comments.Count);
        Assert.Equal(new DanmakuComment(12.34, "滚动", 0xffffff, DanmakuMode.Scroll), comments[0]);
        Assert.Equal(new DanmakuComment(20, "顶部", 0xff0000, DanmakuMode.Top), comments[1]);
        Assert.Equal(new DanmakuComment(30.5, "底部", 0x0000ff, DanmakuMode.Bottom), comments[2]);
        Assert.Equal(new DanmakuComment(40, "没有颜色", 0xffffff, DanmakuMode.Scroll), comments[3]);
    }

    [Fact]
    public async Task Server_Resolve_MatchesThenLoadsWholeEpisode()
    {
        var http = new FakeHttp
        {
            Handler = (method, url, body) => (method, url) switch
            {
                ("POST", "http://10.0.0.2:9321/api/v2/match") =>
                    """{"errorCode":0,"success":true,"isMatched":true,"matches":[{"episodeId":10009,"animeId":236379,"animeTitle":"庆余年 第二季(2024)【电视剧】from tencent","episodeTitle":"【qq】 第5集"}]}""",
                ("GET", "http://10.0.0.2:9321/api/v2/comment/10009?format=json") =>
                    """{"count":2,"comments":[{"cid":1,"p":"1.5,1,16777215,[qq]","m":"第一条"},{"cid":2,"p":"3,5,255,[qq]","m":"第二条"}]}""",
                _ => throw new HttpRequestException("unexpected " + url),
            },
        };
        var provider = new ServerDanmakuProvider(http, "http://10.0.0.2:9321");

        var track = await provider.ResolveAsync(new DanmakuQuery("庆余年 第二季", 4, 5, 36));

        Assert.NotNull(track);
        Assert.Equal("server", track.Provider);
        Assert.Equal(0, track.SegmentSeconds);
        Assert.Equal(2, (await track.LoadSegmentAsync(0)).Count);
        Assert.Empty(await track.LoadSegmentAsync(1));
        Assert.Equal("""{"fileName":"庆余年 S02E05"}""", http.Calls[0].Body);
    }

    [Fact]
    public async Task Server_Resolve_NoMatchOrNoCommentsMeansNull()
    {
        var noMatch = new FakeHttp { Handler = (_, _, _) => """{"errorCode":0,"success":true,"isMatched":false,"matches":[]}""" };
        Assert.Null(await new ServerDanmakuProvider(noMatch, "http://s").ResolveAsync(new DanmakuQuery("x", 0, 1, 1)));
        Assert.Single(noMatch.Calls);

        var empty = new FakeHttp
        {
            Handler = (method, _, _) => method == "POST"
                ? """{"matches":[{"episodeId":"abc 1","animeTitle":"x","episodeTitle":"1"}]}"""
                : """{"count":0,"comments":[]}""",
        };
        Assert.Null(await new ServerDanmakuProvider(empty, "http://s").ResolveAsync(new DanmakuQuery("x", 0, 1, 1)));
        Assert.Equal("http://s/api/v2/comment/abc%201?format=json", empty.Calls[1].Url);
    }

    // ---------- 取数入口 ----------

    [Fact]
    public async Task Service_DefaultSourceNeedsNoServer_AndPrefersTencent()
    {
        var http = TencentHttp(paged: false);
        var service = new DanmakuService(http);

        var track = await service.ResolveAsync(new DanmakuQuery("庆余年 第二季", 0, 1, 36), serverUrl: "");

        Assert.NotNull(track);
        Assert.Equal("tencent", track.Provider);
        Assert.All(http.Calls, call => Assert.Contains("qq.com", call.Url));
    }

    [Fact]
    public async Task Service_FallsBackToBilibiliWhenTencentHasNoMatch()
    {
        var bilibili = BilibiliHttp();
        var inner = bilibili.Handler;
        bilibili.Handler = (method, url, body) => url.Contains("qq.com", StringComparison.Ordinal)
            ? """{"ret":0,"data":{}}"""
            : inner(method, url, body);
        var service = new DanmakuService(bilibili);

        var track = await service.ResolveAsync(new DanmakuQuery("凡人修仙传", 1, 2, 150), null);

        Assert.NotNull(track);
        Assert.Equal("bilibili", track.Provider);
    }

    [Fact]
    public async Task Service_CustomServerFirst_ThenBuiltInWhenServerFails()
    {
        var http = TencentHttp(paged: false);
        var builtin = http.Handler;
        http.Handler = (method, url, body) => url.StartsWith("http://10.0.0.2:9321/", StringComparison.Ordinal)
            ? throw new HttpRequestException("connection refused")
            : builtin(method, url, body);
        var service = new DanmakuService(http);

        var track = await service.ResolveAsync(new DanmakuQuery("庆余年 第二季", 0, 1, 36), " 10.0.0.2:9321/api/v2/ ");

        Assert.NotNull(track);
        Assert.Equal("tencent", track.Provider);
        Assert.Equal("http://10.0.0.2:9321/api/v2/match", http.Calls[0].Url);
    }

    [Fact]
    public async Task Service_ResolveDetailed_TellsFoundNotFoundAndFailedApart()
    {
        var found = await new DanmakuService(TencentHttp(paged: false))
            .ResolveDetailedAsync(new DanmakuQuery("庆余年 第二季", 0, 1, 36), "");
        Assert.Equal(DanmakuResolveOutcome.Found, found.Outcome);
        Assert.Equal("tencent", found.Track?.Provider);
        Assert.Equal("弹幕已加载 · 腾讯视频", DanmakuNotice.ForResult(found));

        // 所有内置来源都正常应答，只是没有这部片
        var empty = BilibiliHttp();
        var bilibiliHandler = empty.Handler;
        empty.Handler = (method, url, body) => url.Contains("qq.com", StringComparison.Ordinal)
            ? """{"ret":0,"data":{}}"""
            : url.Contains("bilibili.com", StringComparison.Ordinal)
                ? bilibiliHandler(method, url, body)
                : "{}"; // 爱奇艺、优酷、芒果TV、咪咕视频:正常应答,没有结果
        var missing = await new DanmakuService(empty)
            .ResolveDetailedAsync(new DanmakuQuery("完全不存在的片子", 0, 1, 10), "");
        Assert.Null(missing.Track);
        Assert.Equal(DanmakuResolveOutcome.NotFound, missing.Outcome);
        Assert.Equal("未找到该集弹幕", DanmakuNotice.ForResult(missing));

        // 来源访问出错（断网）：不能说成“未找到”
        var dead = new FakeHttp { Handler = (_, _, _) => throw new HttpRequestException("offline") };
        var failed = await new DanmakuService(dead)
            .ResolveDetailedAsync(new DanmakuQuery("庆余年 第二季", 0, 1, 36), "");
        Assert.Null(failed.Track);
        Assert.Equal(DanmakuResolveOutcome.Failed, failed.Outcome);
        Assert.Equal(DanmakuNotice.Failed, DanmakuNotice.ForResult(failed));

        // 空标题不发请求，按未找到处理
        var blank = await new DanmakuService(dead).ResolveDetailedAsync(new DanmakuQuery("  ", 0, 1, 1), "");
        Assert.Equal(DanmakuResolveOutcome.NotFound, blank.Outcome);
    }

    [Fact]
    public async Task Service_TriesAllBuiltInSourcesInPriorityOrder()
    {
        var bilibili = BilibiliHttp();
        var bilibiliHandler = bilibili.Handler;
        bilibili.Handler = (method, url, body) => url.Contains("qq.com", StringComparison.Ordinal)
            ? """{"ret":0,"data":{}}"""
            : url.Contains("bilibili.com", StringComparison.Ordinal)
                ? bilibiliHandler(method, url, body)
                : "{}";

        var result = await new DanmakuService(bilibili)
            .ResolveDetailedAsync(new DanmakuQuery("完全不存在的片子", 0, 1, 10), "");

        Assert.Equal(DanmakuResolveOutcome.NotFound, result.Outcome);
        string[] markers = ["qq.com", "bilibili.com", "iqiyi.com", "youku.com", "mgtv.com", "migu"];
        var firstHits = markers
            .Select(marker => bilibili.Calls.FindIndex(call => new Uri(call.Url).Host.Contains(marker, StringComparison.Ordinal)))
            .ToList();
        Assert.All(firstHits, index => Assert.True(index >= 0));
        Assert.Equal(firstHits.OrderBy(index => index), firstHits);

        Assert.Equal("爱奇艺", DanmakuNotice.ProviderLabel("iqiyi"));
        Assert.Equal("优酷", DanmakuNotice.ProviderLabel("youku"));
        Assert.Equal("芒果TV", DanmakuNotice.ProviderLabel("mango"));
        Assert.Equal("咪咕视频", DanmakuNotice.ProviderLabel("migu"));
    }

    [Fact]
    public async Task Service_ResolveDetailed_CancelledGivesNoNotice()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var result = await new DanmakuService(TencentHttp(paged: false))
            .ResolveDetailedAsync(new DanmakuQuery("庆余年 第二季", 0, 1, 36), "", cts.Token);

        Assert.Null(result.Track);
        Assert.Equal(DanmakuResolveOutcome.Cancelled, result.Outcome);
        Assert.Null(DanmakuNotice.ForResult(result));
    }

    [Fact]
    public async Task Service_NeverThrows()
    {
        var offline = new FakeHttp { Handler = (_, _, _) => throw new HttpRequestException("offline") };
        var service = new DanmakuService(offline);

        Assert.Null(await service.ResolveAsync(new DanmakuQuery("庆余年", 0, 1, 46), "http://10.0.0.2:9321"));
        Assert.Null(await service.ResolveAsync(new DanmakuQuery("   ", 0, 1, 1), null));
        Assert.Null(await service.ResolveAsync(null!, null));

        var garbage = new FakeHttp { Handler = (_, url, _) => url.EndsWith(".xml", StringComparison.Ordinal) ? new byte[] { 1, 2 } : "[1,2,3]" };
        Assert.Null(await new DanmakuService(garbage).ResolveAsync(new DanmakuQuery("庆余年", 0, 1, 46), "http://10.0.0.2:9321"));

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var http = TencentHttp(paged: false);
        Assert.Null(await new DanmakuService(http).ResolveAsync(new DanmakuQuery("庆余年 第二季", 0, 1, 36), null, cancelled.Token));
        Assert.Empty(http.Calls);
    }

    // ---------- 时钟 ----------

    [Fact]
    public void Clock_AdvancesOnlyWhilePlaying()
    {
        var clock = new DanmakuClock();
        clock.Sync(10, 0);
        Assert.Equal(10, clock.Now(5000));

        clock.SetPlaying(true, 5000);
        clock.Sync(10.2, 5200);
        Assert.Equal(10.4, clock.Now(5400), 6);

        clock.SetPlaying(false, 5400);
        clock.Sync(10.4, 5450);
        Assert.Equal(10.4, clock.Now(60000), 6);

        clock.SetPlaying(true, 60000);
        clock.Sync(10.5, 60100);
        Assert.Equal(10.9, clock.Now(60500), 6);
    }

    [Fact]
    public void Clock_IgnoresReportJitterButFollowsRealJumps()
    {
        var clock = new DanmakuClock();
        clock.SetPlaying(true, 0);
        Assert.True(clock.Sync(100, 0));

        // 上报比推算慢 0.3 秒：不对表，弹幕不抖
        Assert.False(clock.Sync(100.2, 500));
        Assert.Equal(100.5, clock.Now(500), 6);

        // 拖动进度
        Assert.True(clock.Sync(300, 750));
        Assert.Equal(300, clock.Now(750), 6);
        Assert.Equal(300.25, clock.Now(1000), 6);

        // 往回拖
        Assert.True(clock.Sync(42, 1000));
        Assert.Equal(42, clock.Now(1000), 6);
    }

    [Fact]
    public void Clock_StopsWhenProgressReportsStopComing()
    {
        var clock = new DanmakuClock();
        clock.SetPlaying(true, 0);
        clock.Sync(50, 0);

        Assert.True(clock.IsRunningAt(1000));
        Assert.Equal(51.5, clock.Now(1500), 6);
        // 1.5 秒没有新的进度：卡住了，时间停在那里
        Assert.False(clock.IsRunningAt(1600));
        Assert.Equal(51.5, clock.Now(9000), 6);

        // 恢复上报：按播放器的真实位置继续
        Assert.True(clock.Sync(50.4, 9000));
        Assert.Equal(50.9, clock.Now(9500), 6);
    }

    [Fact]
    public void Clock_RepeatedPositionWhilePlayingMeansStalled()
    {
        var clock = new DanmakuClock();
        clock.SetPlaying(true, 0);
        clock.Sync(30, 0);

        Assert.True(clock.Sync(30, 300));
        Assert.Equal(30, clock.Now(1000), 6);
        Assert.False(clock.IsRunningAt(1000));

        Assert.True(clock.Sync(30.25, 1250));
        Assert.Equal(30.75, clock.Now(1750), 6);
    }

    [Fact]
    public void Clock_RateScalesTime()
    {
        var clock = new DanmakuClock();
        clock.SetPlaying(true, 0);
        clock.Sync(10, 0);
        Assert.True(clock.SetRate(2, 0));
        Assert.Equal(12, clock.Now(1000), 6);
        Assert.False(clock.SetRate(2, 1000));
        Assert.True(clock.SetRate(double.NaN, 1000));
        Assert.Equal(1, clock.PlaybackRate);
    }

    [Fact]
    public void Clock_SeekWhilePaused_JumpsImmediatelyAndStaysPut()
    {
        var clock = new DanmakuClock();
        clock.Sync(10, 0);

        // LibVLC 暂停时不上报进度：拖动后只有这一次通知
        clock.Seek(600, 1000);

        Assert.Equal(600, clock.Now(1000), 6);
        Assert.Equal(600, clock.Now(30000), 6);

        clock.SetPlaying(true, 30000);
        clock.Sync(600.1, 30100);
        Assert.Equal(600.5, clock.Now(30500), 6);
    }

    [Fact]
    public void Clock_SeekWhilePlaying_HoldsUntilPlayerCatchesUpAndDropsStaleReports()
    {
        var clock = new DanmakuClock();
        clock.SetPlaying(true, 0);
        clock.Sync(10, 0);

        clock.Seek(600, 1000);
        // 播放器还在重新缓冲：时间停在目标上
        Assert.Equal(600, clock.Now(1400), 6);
        // 拖动之前排队的旧进度：丢掉
        Assert.False(clock.Sync(11.2, 1100));
        Assert.Equal(600, clock.Now(1400), 6);

        // 播放器报出拖动后的位置：继续走
        Assert.True(clock.Sync(600.3, 1500));
        Assert.Equal(600.8, clock.Now(2000), 6);
    }

    [Fact]
    public void Clock_SeekGuardExpires_SoAFailedSeekCannotStickForever()
    {
        var clock = new DanmakuClock();
        clock.SetPlaying(true, 0);
        clock.Sync(10, 0);
        clock.Seek(600, 1000);

        // 一秒之后播放器仍然报旧位置：以播放器为准
        Assert.True(clock.Sync(12.1, 2100));
        Assert.Equal(12.1, clock.Now(2100), 6);
        Assert.Equal(12.6, clock.Now(2600), 6);
    }

    [Fact]
    public void Clock_Reset_GoesBackToZeroAndPaused()
    {
        var clock = new DanmakuClock();
        clock.SetPlaying(true, 0);
        clock.Sync(500, 0);
        clock.Reset();

        Assert.False(clock.IsPlaying);
        Assert.Equal(0, clock.Now(99999));
    }

    [Fact]
    public void TimeMapping_SubtractsAdBreaksBeforeThePosition()
    {
        AdRange[] ads = [new(60_000, 75_000), new(600_000, 630_000)];

        Assert.Equal(30, DanmakuTimeMapping.ContentSeconds(30_000, ads));
        // 广告里：正片时间停在广告开头
        Assert.Equal(60, DanmakuTimeMapping.ContentSeconds(60_000, ads));
        Assert.Equal(60, DanmakuTimeMapping.ContentSeconds(70_000, ads));
        Assert.Equal(60, DanmakuTimeMapping.ContentSeconds(75_000, ads));
        Assert.Equal(85, DanmakuTimeMapping.ContentSeconds(100_000, ads));
        Assert.Equal(655, DanmakuTimeMapping.ContentSeconds(700_000, ads));
        Assert.Equal(700, DanmakuTimeMapping.ContentSeconds(700_000, null));
        Assert.Equal(700, DanmakuTimeMapping.ContentSeconds(700_000, []));
        Assert.Equal(0, DanmakuTimeMapping.ContentSeconds(-5, ads));
        Assert.Equal(0, DanmakuTimeMapping.ContentSeconds(double.NaN, ads));
        Assert.Equal(10, DanmakuTimeMapping.ContentSeconds(10_000, [new AdRange(5000, 5000), new AdRange(9000, 2000)]));
    }

    // ---------- 分片缓存 ----------

    [Fact]
    public void Timeline_LoadsSegmentsLazilyWithLookahead()
    {
        var requested = new List<int>();
        using var timeline = new DanmakuTimeline(TrackOf(30, index => [Scroll(index * 30 + 1)], requested));

        timeline.Ensure(5);
        Assert.Equal([0], requested);
        timeline.Ensure(12);
        Assert.Equal([0], requested);
        // 离下一片不到 10 秒：提前取
        timeline.Ensure(21);
        Assert.Equal([0, 1], requested);
        timeline.Ensure(615);
        Assert.Equal([0, 1, 20], requested);
        // 离分片开头不到 12 秒（如刚拖到这里）：上一片末尾的弹幕还在飞，上一片也取
        timeline.Ensure(605);
        Assert.Equal([0, 1, 20, 19], requested);

        Assert.Equal([0, 1, 20, 19], timeline.DrainLoaded());
        Assert.Empty(timeline.DrainLoaded());
        Assert.Equal((30, 60), timeline.RangeOf(1));
    }

    [Fact]
    public void Timeline_Between_IsSortedAndHalfOpen()
    {
        using var timeline = new DanmakuTimeline(TrackOf(30, index => index == 0
            ? [Scroll(20, "c"), Scroll(5, "a"), Scroll(5, "b"), Scroll(29.9, "d")]
            : [Scroll(31, "e"), Scroll(30, "x")]));
        timeline.Ensure(25);

        Assert.Equal("abcdxe", string.Concat(timeline.Between(-10, 100).Select(c => c.Text)));
        Assert.Equal("cd", string.Concat(timeline.Between(5, 29.9).Select(c => c.Text)));
        Assert.Equal("ab", string.Concat(timeline.Between(4.99, 5).Select(c => c.Text)));
        Assert.Empty(timeline.Between(5, 5));
        Assert.Empty(timeline.Between(100, 200));
    }

    [Fact]
    public void Timeline_UnsegmentedTrackLoadsOnce()
    {
        var requested = new List<int>();
        using var timeline = new DanmakuTimeline(TrackOf(0, _ => [Scroll(3), Scroll(4000)], requested));

        timeline.Ensure(0);
        timeline.Ensure(3999);
        Assert.Equal([0], requested);
        Assert.Equal(2, timeline.Between(0, 5000).Count);
        Assert.Equal((0, double.PositiveInfinity), timeline.RangeOf(0));
    }

    [Fact]
    public void Timeline_FailedSegmentRetriesLaterAndGivesUpAfterThreeAttempts()
    {
        long now = 0;
        var attempts = 0;
        var track = new DanmakuTrack("test", "test", 0, (_, _) =>
        {
            attempts++;
            throw new HttpRequestException("offline");
        });
        using var timeline = new DanmakuTimeline(track, () => now);

        timeline.Ensure(0);
        timeline.Ensure(1);
        Assert.Equal(1, attempts);
        now = DanmakuTimeline.RetryMs - 1;
        timeline.Ensure(2);
        Assert.Equal(1, attempts);
        now = DanmakuTimeline.RetryMs;
        timeline.Ensure(3);
        Assert.Equal(2, attempts);
        now = DanmakuTimeline.RetryMs * 2;
        timeline.Ensure(4);
        Assert.Equal(3, attempts);
        now = DanmakuTimeline.RetryMs * 10;
        timeline.Ensure(5);
        Assert.Equal(3, attempts);
        Assert.Empty(timeline.Between(0, 100));
        Assert.Empty(timeline.DrainLoaded());
    }

    // ---------- 排布 ----------

    [Fact]
    public void Engine_ScrollCommentMovesRightToLeftAsAFunctionOfVideoTime()
    {
        var engine = EngineOf([Scroll(10, "abcde")]);

        Assert.False(engine.Advance(9.5));
        Assert.True(engine.Advance(10));
        var item = Assert.Single(engine.Items);
        Assert.Equal(100, item.Width);
        Assert.Equal(10, item.Start);
        Assert.Equal(18, item.End);
        Assert.Equal(0, item.Y);

        // 出现时文字左边贴着画面右边；一半时间走完一半路程；结束时整条移出左边
        Assert.Equal(1000, engine.XAt(item, 10), 6);
        Assert.Equal(450, engine.XAt(item, 14), 6);
        Assert.Equal(-100, engine.XAt(item, 18), 6);
        Assert.True(engine.IsVisibleAt(item, 10));
        Assert.False(engine.IsVisibleAt(item, 9.99));
        Assert.False(engine.IsVisibleAt(item, 18));

        double previous = double.MaxValue;
        for (var time = 10.0; time < 18; time += 0.25)
        {
            engine.Advance(time);
            var x = engine.XAt(Assert.Single(engine.Items), time);
            Assert.True(x < previous);
            previous = x;
        }

        Assert.True(engine.Advance(18));
        Assert.Empty(engine.Items);
    }

    [Fact]
    public void Engine_ScrollCommentsNeverOverlapInALane()
    {
        var engine = EngineOf(
        [
            Scroll(10, "aaaaa"),
            Scroll(10, "bbbbb"),
            // 前一条的尾巴还没完全进来
            Scroll(10.5, "ccccc"),
            // 尾巴进来了，速度一样：回到第一行
            Scroll(11, "ddddd"),
            // 很长的弹幕飞得快，会追上第一行、第二行的前一条：只能另起一行
            Scroll(12, new string('长', 50)),
        ]);

        engine.Advance(10);
        engine.Advance(10.5);
        engine.Advance(11);
        engine.Advance(12);

        Assert.Equal([0, 1, 2, 0, 3], engine.Items.Select(item => item.Lane).ToArray());
        Assert.Equal([0, 27, 54, 0, 81], engine.Items.Select(item => item.Y).ToArray());

        // 同一行的两条在任何时刻都不重叠
        var first = engine.Items[0];
        var fourth = engine.Items[3];
        for (var time = 11.0; time < 18; time += 0.1)
        {
            Assert.True(engine.XAt(first, time) + first.Width + 20 <= engine.XAt(fourth, time) + 1e-6);
        }
    }

    [Fact]
    public void Engine_TopAndBottomCommentsStayCentredForAFewSeconds()
    {
        var engine = EngineOf(
        [
            new DanmakuComment(5, "顶部一", 0xff0000, DanmakuMode.Top),
            new DanmakuComment(6, "顶部二二", 0xff0000, DanmakuMode.Top),
            new DanmakuComment(6, "底部", 0x00ff00, DanmakuMode.Bottom),
            new DanmakuComment(9.5, "顶部三", 0xff0000, DanmakuMode.Top),
        ]);

        engine.Advance(5);
        engine.Advance(6);
        var top1 = engine.Items[0];
        var top2 = engine.Items[1];
        var bottom = engine.Items[2];
        Assert.Equal((0, 0.0, 5.0, 9.0), (top1.Lane, top1.Y, top1.Start, top1.End));
        Assert.Equal((1, 27.0), (top2.Lane, top2.Y));
        // 底部弹幕在画面下方 10% 之上
        Assert.Equal((0, 513.0, 10.0), (bottom.Lane, bottom.Y, bottom.End));
        // 固定弹幕不随时间移动，水平居中
        Assert.Equal(470, engine.XAt(top1, 5));
        Assert.Equal(470, engine.XAt(top1, 8.9));
        Assert.Equal(480, engine.XAt(bottom, 7));

        engine.Advance(7.4);
        engine.Advance(8.8);
        Assert.Equal(3, engine.Items.Count);
        engine.Advance(9.5);
        // 第一条到 9 秒消失，第三条用回第一行
        Assert.Equal(["顶部二二", "底部", "顶部三"], engine.Items.Select(item => item.Comment.Text).ToArray());
        Assert.Equal(0, engine.Items[2].Lane);
    }

    [Fact]
    public void Engine_SeekRebuildsTheScreenForTheNewTime_IncludingMidFlightComments()
    {
        var comments = Enumerable.Range(1, 200).Select(second => Scroll(second, $"第{second}秒")).ToList();
        var engine = EngineOf(comments);

        engine.Advance(50);
        Assert.Equal(Enumerable.Range(43, 8).Select(s => (double)s).ToArray(), engine.Items.Select(item => item.Start).ToArray());
        var midFlight = engine.Items.Single(item => item.Start == 46);
        Assert.Equal(1000 - 0.5 * (1000 + midFlight.Width), engine.XAt(midFlight, 50), 6);

        // 往后拖
        Assert.True(engine.Advance(120.5));
        Assert.Equal(Enumerable.Range(113, 8).Select(s => (double)s).ToArray(), engine.Items.Select(item => item.Start).ToArray());
        Assert.All(engine.Items, item => Assert.True(engine.IsVisibleAt(item, 120.5)));

        // 往回拖
        Assert.True(engine.Advance(20));
        Assert.Equal(Enumerable.Range(13, 8).Select(s => (double)s).ToArray(), engine.Items.Select(item => item.Start).ToArray());

        // 继续播放：只多出新到的一条，已经在画面上的不重排
        var ids = engine.Items.Select(item => item.Id).ToArray();
        Assert.True(engine.Advance(21));
        Assert.Equal(ids.Skip(1).ToArray(), engine.Items.Take(7).Select(item => item.Id).ToArray());
        Assert.Equal(21, engine.Items[^1].Start);
    }

    [Fact]
    public void Engine_SmallBackwardJitterDoesNotRebuild()
    {
        var engine = EngineOf(Enumerable.Range(1, 60).Select(second => Scroll(second)).ToList());
        engine.Advance(30);
        var ids = engine.Items.Select(item => item.Id).ToArray();

        Assert.False(engine.Advance(29.6));
        Assert.False(engine.Advance(30));
        Assert.Equal(ids, engine.Items.Select(item => item.Id).ToArray());
    }

    [Fact]
    public void Engine_LateLoadedSegmentIsBackfilled()
    {
        var comments = new List<DanmakuComment>();
        var engine = EngineOf(comments);
        engine.Advance(30);
        Assert.Empty(engine.Items);

        // 这段弹幕在播过去之后才加载完
        comments.AddRange([Scroll(25), Scroll(29), Scroll(45)]);
        engine.NotifyLoaded(0, 60);
        Assert.True(engine.Advance(30.016));
        Assert.Equal([25.0, 29.0], engine.Items.Select(item => item.Start).ToArray());

        // 还没播到的分片加载完：不动现有画面
        var ids = engine.Items.Select(item => item.Id).ToArray();
        engine.NotifyLoaded(60, 90);
        engine.Advance(30.032);
        Assert.Equal(ids, engine.Items.Select(item => item.Id).ToArray());
    }

    [Fact]
    public void Engine_DropsCommentsWhenScreenIsFull()
    {
        var burst = Enumerable.Range(0, 200).Select(i => Scroll(10 + i * 0.001, "刷屏")).ToList();
        var engine = EngineOf(burst);
        engine.Advance(11);
        // 600 高的画面上半部分只有 10 行，同一瞬间每行一条
        Assert.Equal(10, engine.Items.Count);
        Assert.Equal(Enumerable.Range(0, 10).ToArray(), engine.Items.Select(item => item.Lane).ToArray());

        var capped = EngineOf(
            Enumerable.Range(0, 100).Select(i => Scroll(10 + i * 0.5, "一条")).ToList(),
            Layout(maxItems: 5));
        for (var time = 10.0; time <= 20; time += 0.25)
        {
            capped.Advance(time);
            Assert.True(capped.Items.Count <= 5);
        }
    }

    [Fact]
    public void Engine_TruncatesVeryLongText_AndEstimatesWidth()
    {
        var engine = EngineOf([Scroll(1, new string('长', 200))]);
        engine.Advance(1);
        Assert.Equal(80, Assert.Single(engine.Items).Comment.Text.Length);

        Assert.Equal(48, DanmakuEngine.EstimateTextWidth("弹幕", 20));
        Assert.Equal(Math.Ceiling(0.58 * 4 * 20) + 8, DanmakuEngine.EstimateTextWidth("abcd", 20));
    }

    [Fact]
    public void Engine_ResizeOrStyleChangeRebuildsOnNextAdvance()
    {
        var engine = EngineOf(Enumerable.Range(1, 60).Select(second => Scroll(second)).ToList());
        engine.Advance(30);
        Assert.Equal(27, engine.LineHeight);

        engine.SetOptions(Layout() with { Width = 2000, FontSize = 30, ScrollSeconds = 5.5 });
        Assert.Empty(engine.Items);
        engine.Advance(30.016);
        Assert.Equal([25.0, 26.0, 27.0, 28.0, 29.0, 30.0], engine.Items.Select(item => item.Start).ToArray());
        Assert.Equal(41, engine.LineHeight);
        Assert.Equal(60, engine.Items[0].Width);

        var empty = EngineOf([Scroll(1)], Layout() with { Width = 0 });
        Assert.False(empty.Advance(1));
        Assert.Empty(empty.Items);
    }

    // ---------- 时间对齐（渲染层每帧拿到的内容） ----------

    private static DanmakuTrack OnePerSecondTrack(List<int>? requested = null) =>
        TrackOf(
            30,
            index => Enumerable.Range(index * 30, 30).Select(second => Scroll(second + 0.5, $"第{second}秒")).ToList(),
            requested);

    private static double ExpectedX(double commentTime, double width, double videoTime) =>
        1000 - (videoTime - commentTime) / 8 * (1000 + width);

    [Fact]
    public void Session_FollowsPlaybackPausesAndResumes()
    {
        var clock = new DanmakuClock();
        using var session = new DanmakuSession(OnePerSecondTrack(), Layout(), clock);
        clock.SetPlaying(true, 0);
        clock.Sync(0, 0);

        Assert.Empty(session.Frame(0));

        // 播到 1 秒：0.5 秒的那条已经飘进来 0.5 秒
        var sprite = Assert.Single(session.Frame(1000));
        Assert.Equal("第0秒", sprite.Item.Comment.Text);
        Assert.Equal(ExpectedX(0.5, sprite.Item.Width, 1.0), sprite.X, 6);
        Assert.Equal(0, sprite.Y);

        clock.Sync(1.25, 1250);
        clock.Sync(3.0, 3000);
        var playing = session.Frame(3000).ToArray();
        Assert.Equal(["第0秒", "第1秒", "第2秒"], playing.Select(s => s.Item.Comment.Text).ToArray());

        // 暂停：之后不管过多久，每条弹幕都停在原地
        clock.SetPlaying(false, 3000);
        var paused = session.Frame(20000).ToArray();
        Assert.Equal(playing.Select(s => (s.Item.Id, s.X, s.Y)).ToArray(), paused.Select(s => (s.Item.Id, s.X, s.Y)).ToArray());

        // 继续：从停下的位置接着飘
        clock.SetPlaying(true, 20000);
        clock.Sync(3.0, 20000);
        clock.Sync(3.25, 20250);
        var resumed = session.Frame(20500).ToArray();
        Assert.Equal(paused.Select(s => s.Item.Id).ToArray(), resumed.Take(3).Select(s => s.Item.Id).ToArray());
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(paused[i].X - 0.5 / 8 * (1000 + paused[i].Item.Width), resumed[i].X, 6);
        }
    }

    [Fact]
    public void Session_SeekShowsTheCommentsOfTheNewPosition()
    {
        var requested = new List<int>();
        var clock = new DanmakuClock();
        using var session = new DanmakuSession(OnePerSecondTrack(requested), Layout(), clock);
        clock.SetPlaying(true, 0);
        clock.Sync(0, 0);
        session.Frame(0);
        clock.Sync(2, 2000);
        Assert.Equal(2, session.Frame(2000).Count);

        // 拖到 10 分钟：画面立刻换成 10 分钟处该有的弹幕，包括已经飞到一半的
        clock.Seek(600, 2100);
        var after = session.Frame(2116).ToArray();
        Assert.Equal(
            Enumerable.Range(592, 8).Select(second => $"第{second}秒").ToArray(),
            after.Select(s => s.Item.Comment.Text).ToArray());
        Assert.All(after, s => Assert.Equal(ExpectedX(s.Item.Start, s.Item.Width, 600), s.X, 6));
        Assert.Contains(20, requested);
        Assert.DoesNotContain(5, requested);

        // 播放器报出新位置后继续走
        clock.Sync(600.2, 2500);
        clock.Sync(601.2, 3500);
        var later = session.Frame(3500).ToArray();
        Assert.Equal("第600秒", later[^1].Item.Comment.Text);
        Assert.All(later, s => Assert.Equal(ExpectedX(s.Item.Start, s.Item.Width, 601.2), s.X, 6));

        // 暂停后往回拖：停在新位置的画面上
        clock.SetPlaying(false, 3500);
        clock.Seek(45, 4000);
        var back = session.Frame(4016).ToArray();
        Assert.Equal(
            Enumerable.Range(37, 8).Select(second => $"第{second}秒").ToArray(),
            back.Select(s => s.Item.Comment.Text).ToArray());
        Assert.Equal(back.Select(s => s.X).ToArray(), session.Frame(60000).Select(s => s.X).ToArray());
    }

    [Fact]
    public void Session_NewEpisodeStartsFromZeroWithNothingOnScreen()
    {
        var clock = new DanmakuClock();
        using var session = new DanmakuSession(OnePerSecondTrack(), Layout(), clock);
        clock.SetPlaying(true, 0);
        clock.Sync(100, 0);
        Assert.NotEmpty(session.Frame(16));

        clock.Reset();
        Assert.Empty(session.Frame(32));
        Assert.Empty(session.Frame(5000));
    }

    [Fact]
    public void Session_SlowSegmentAppearsOnceLoaded_AndFailedSegmentShowsNothing()
    {
        var pending = new TaskCompletionSource<IReadOnlyList<DanmakuComment>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = new DanmakuTrack("test", "test", 0, (_, _) => pending.Task);
        var clock = new DanmakuClock();
        using var session = new DanmakuSession(slow, Layout(), clock);
        clock.Sync(100, 0);

        Assert.Empty(session.Frame(0));
        Assert.Empty(session.Frame(16));
        pending.SetResult([Scroll(95, "迟到的"), Scroll(300, "以后的")]);
        WaitUntil(() => session.Frame(32).Count == 1);
        var sprite = Assert.Single(session.Frame(48));
        Assert.Equal("迟到的", sprite.Item.Comment.Text);
        Assert.Equal(ExpectedX(95, sprite.Item.Width, 100), sprite.X, 6);

        var broken = new DanmakuTrack("test", "test", 0, (_, _) => Task.FromException<IReadOnlyList<DanmakuComment>>(new HttpRequestException("offline")));
        var brokenClock = new DanmakuClock();
        using var brokenSession = new DanmakuSession(broken, Layout(), brokenClock);
        brokenClock.SetPlaying(true, 0);
        brokenClock.Sync(10, 0);
        Assert.Empty(brokenSession.Frame(0));
        Assert.Empty(brokenSession.Frame(1000));
    }

    [Fact]
    public void Session_UsesInvariantNumbersRegardlessOfCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var comments = BilibiliDanmakuProvider.ParseXml("""<d p="12.5,1,25,16777215,0,0,x,1">小数点</d>""");
            Assert.Equal(12.5, Assert.Single(comments).Time);
            Assert.Equal(12.34, Assert.Single(ServerDanmakuProvider.ParseComments(Json("""{"comments":[{"p":"12.34,1,255","m":"x"}]}"""))).Time);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
