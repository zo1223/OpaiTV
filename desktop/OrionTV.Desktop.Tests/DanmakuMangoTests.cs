using System.Text.Json;
using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.Tests;

public class DanmakuMangoTests
{
    private const string Search = """
        {"data":{"contents":[
          {"type":"mediaRebirthV2","data":[
            {"title":"<B>大宋少年志2</B>","clipId":448573,"source":"","desc":["类型: 电视剧 / 2023 / 内地"]},
            {"title":"大宋少年志","clipId":"326733","source":"imgo","desc":["类型: 电视剧 / 2019 / 内地"]},
            {"title":"大宋少年志2","clipId":"999","source":"qq","desc":["类型: 电视剧 / 2023"]},
            {"title":"没有编号"},
            {"title":"某电影","clipId":"777","desc":["类型: 电影 / 2020 / 内地"]}
          ]},
          {"type":"shortVideoMerge","data":[{"title":"大宋少年志2","clipId":"1"}]}
        ]}}
        """;

    private const string Control = """{"data":{"cdn_list":"bullet-ali.hitv.com,bullet-ws.hitv.com","cdn_version":"bullet/tx/2026/11/25/1/v2"}}""";

    private const string Segment = """
        {"data":{"items":[
          {"content":"后来的","time":61500},
          {"content":" 第一条 ","time":60100,"v2_position":1},
          {"content":"彩色","time":60200,"v2_position":2,"v2_color":{"color_left":{"r":255,"g":0,"b":0},"color_right":{"r":-1,"g":-1,"b":-1}}},
          {"content":"","time":60300},
          {"content":"没时间"}
        ]}}
        """;

    private static string Episode(int n, string vid, string isnew = "0", string? t1 = null, string clip = "448573") =>
        $$"""{"src_clip_id":"{{clip}}","video_id":"{{vid}}","t1":"{{t1 ?? "标题" + n}}","t2":"第{{n}}集","t3":"大宋少年志2 第{{n}}集","time":"46:36","isnew":"{{isnew}}","isIntact":"1","ts":"2023-08-{{n:00}} 10:00:00.0"}""";

    private const string Tabs = """{"data":{"tab_m":[{"m":"202308"},{"m":"202307"}],"list":[""";

    private static readonly string PageNew = Tabs + string.Join(
        ",",
        Episode(3, "v3"),
        Episode(4, "v4"),
        Episode(5, "v5", isnew: "2"),
        Episode(6, "v6", t1: "幕后花絮"),
        Episode(7, "v7", clip: "other")) + "]}}";

    private static readonly string PageOld = Tabs + string.Join(",", Episode(1, "v1"), Episode(2, "v2")) + "]}}";

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    private sealed class FakeHttp(Func<string, string> handler) : IDanmakuHttp
    {
        public List<string> Urls { get; } = [];

        public Task<JsonDocument> GetJsonAsync(string url, IReadOnlyDictionary<string, string>? headers, TimeSpan timeout, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (Urls)
            {
                Urls.Add(url);
            }

            return Task.FromResult(JsonDocument.Parse(handler(url)));
        }

        public Task<JsonDocument> PostJsonAsync(string url, string jsonBody, IReadOnlyDictionary<string, string>? headers, TimeSpan timeout, CancellationToken ct) =>
            throw new HttpRequestException("unexpected post");

        public Task<byte[]> GetBytesAsync(string url, IReadOnlyDictionary<string, string>? headers, TimeSpan timeout, CancellationToken ct) =>
            throw new HttpRequestException("unexpected bytes");
    }

    private static string Realistic(string url)
    {
        if (url.Contains("/search/rebirth/v2", StringComparison.Ordinal))
        {
            return Search;
        }

        if (url.Contains("/variety/showlist", StringComparison.Ordinal))
        {
            return url.Contains("month=202307", StringComparison.Ordinal) ? PageOld : PageNew;
        }

        if (url.Contains("/getctlbarrage", StringComparison.Ordinal))
        {
            return Control;
        }

        if (url.Contains("hitv.com/", StringComparison.Ordinal))
        {
            return Segment;
        }

        throw new HttpRequestException("unexpected " + url);
    }

    private static DanmakuQuery Query(string title = "大宋少年志2", int index = 1, int number = 2) => new(title, index, number, 42);

    [Fact]
    public void ParseSearch_KeepsOnlyMangoOwnedItems()
    {
        var result = MangoDanmakuProvider.ParseSearch(Json(Search));
        Assert.Equal(
            [
                new MangoDanmakuCandidate("448573", "大宋少年志2", "2023", 0, false),
                new MangoDanmakuCandidate("326733", "大宋少年志", "2019", 0, false),
                new MangoDanmakuCandidate("777", "某电影", "2020", 1, true),
            ],
            result);
        Assert.Empty(MangoDanmakuProvider.ParseSearch(Json("{}")));
        Assert.Empty(MangoDanmakuProvider.ParseSearch(Json("""{"data":{"contents":"x"}}""")));
    }

    [Fact]
    public void ParseEpisodePage_DropsTrailersExtrasAndForeignItems()
    {
        var page = MangoDanmakuProvider.ParseEpisodePage(Json(PageNew), "448573");
        Assert.Equal(["202307"], page.Months);
        Assert.Equal(["v3", "v4"], page.Episodes.Select(e => e.Vid));
        Assert.Equal([3, 4], page.Episodes.Select(e => e.Number));
        Assert.All(page.Episodes, e => Assert.Equal(2796, e.Seconds));
        var empty = MangoDanmakuProvider.ParseEpisodePage(Json("null"), "1");
        Assert.Empty(empty.Months);
        Assert.Empty(empty.Episodes);
    }

    [Fact]
    public void OrderEpisodes_SortsByNumberOrTimestampAndDeduplicates()
    {
        var a = MangoDanmakuProvider.ParseEpisodePage(Json(PageNew), "448573").Episodes;
        var b = MangoDanmakuProvider.ParseEpisodePage(Json(PageOld), "448573").Episodes;
        Assert.Equal(["v1", "v2", "v3", "v4"], MangoDanmakuProvider.OrderEpisodes(a.Concat(b).Concat(a)).Select(e => e.Vid));
        MangoDanmakuEpisode[] mixed =
        [
            new("b", null, "", 0, "2024-02", true),
            new("a", 9, "", 0, "2024-01", true),
        ];
        Assert.Equal(["a", "b"], MangoDanmakuProvider.OrderEpisodes(mixed).Select(e => e.Vid));
    }

    [Fact]
    public void ParseDuration_HandlesClockText()
    {
        Assert.Equal(2796, MangoDanmakuProvider.ParseDuration("46:36"));
        Assert.Equal(3723, MangoDanmakuProvider.ParseDuration("1:02:03"));
        Assert.Equal(0, MangoDanmakuProvider.ParseDuration("abc"));
        Assert.Equal(0, MangoDanmakuProvider.ParseDuration(null));
    }

    [Fact]
    public void ParseSegment_ConvertsTimeSortsAndReadsStyle()
    {
        var result = MangoDanmakuProvider.ParseSegment(Json(Segment));
        Assert.Equal(
            [
                new DanmakuComment(60.1, "第一条", 0xffffff, DanmakuMode.Top),
                new DanmakuComment(60.2, "彩色", 0xff0000, DanmakuMode.Bottom),
                new DanmakuComment(61.5, "后来的", 0xffffff, DanmakuMode.Scroll),
            ],
            result);
        Assert.Empty(MangoDanmakuProvider.ParseSegment(Json("{}")));
    }

    [Fact]
    public void ParseControl_AcceptsOnlyMangoHosts()
    {
        Assert.Equal("https://bullet-ali.hitv.com/bullet/tx/2026/11/25/1/v2", MangoDanmakuProvider.ParseControl(Json(Control)));
        Assert.Null(MangoDanmakuProvider.ParseControl(Json("""{"data":{"cdn_list":"evil.example.com","cdn_version":"a/b"}}""")));
        Assert.Null(MangoDanmakuProvider.ParseControl(Json("""{"data":{"cdn_list":"bullet-ali.hitv.com","cdn_version":"a/../b"}}""")));
        Assert.Null(MangoDanmakuProvider.ParseControl(Json("""{"data":{}}""")));
    }

    [Fact]
    public async Task Resolve_SearchesLoadsAllMonthsAndSegments()
    {
        var http = new FakeHttp(Realistic);
        var provider = new MangoDanmakuProvider(http);
        Assert.Equal("mango", provider.Name);
        var track = await provider.ResolveAsync(Query());
        Assert.NotNull(track);
        Assert.Equal("mango", track!.Provider);
        Assert.Equal("大宋少年志2 第2集", track.Title);
        Assert.Equal(60, track.SegmentSeconds);
        Assert.Contains(http.Urls, url => url.Contains("vid=v2", StringComparison.Ordinal) && url.Contains("cid=448573", StringComparison.Ordinal));

        var comments = await track.LoadSegmentAsync(1);
        Assert.Equal("https://bullet-ali.hitv.com/bullet/tx/2026/11/25/1/v2/1.json", http.Urls[^1]);
        Assert.Equal(["第一条", "彩色", "后来的"], comments.Select(c => c.Text));

        // 超出片长的分片不发请求
        var before = http.Urls.Count;
        Assert.Empty(await track.LoadSegmentAsync(47));
        Assert.Equal(before, http.Urls.Count);

        // 同一部片换集不再搜索
        await provider.ResolveAsync(Query(index: 2, number: 3));
        Assert.Equal(1, http.Urls.Count(url => url.Contains("/search/rebirth/v2", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Resolve_MatchesSeasonMarkerSpelling()
    {
        var track = await new MangoDanmakuProvider(new FakeHttp(Realistic)).ResolveAsync(Query("大宋少年志 第二季"));
        Assert.Equal("大宋少年志2 第2集", track?.Title);
    }

    [Fact]
    public async Task Resolve_FallsBackWhenControlHasNoCdn()
    {
        var http = new FakeHttp(url =>
            url.Contains("/getctlbarrage", StringComparison.Ordinal) ? """{"data":{}}"""
            : url.Contains("/rdbarrage", StringComparison.Ordinal) ? Segment
            : Realistic(url));
        var track = await new MangoDanmakuProvider(http).ResolveAsync(Query());
        Assert.Equal(3, (await track!.LoadSegmentAsync(2)).Count);
        Assert.Equal("https://galaxy.bz.mgtv.com/rdbarrage?vid=v2&cid=448573&time=120000", http.Urls[^1]);
    }

    [Fact]
    public async Task Resolve_ReturnsNullWhenTitleOrEpisodeMissing()
    {
        Assert.Null(await new MangoDanmakuProvider(new FakeHttp(Realistic)).ResolveAsync(Query("狂飙")));
        Assert.Null(await new MangoDanmakuProvider(new FakeHttp(Realistic)).ResolveAsync(Query(index: 29, number: 30)));
        Assert.Null(await new MangoDanmakuProvider(new FakeHttp(_ => """{"data":{"contents":[]}}""")).ResolveAsync(Query()));
    }

    [Fact]
    public async Task Resolve_ThrowsOnNetworkErrorAndDoesNotCacheFailure()
    {
        var down = true;
        var provider = new MangoDanmakuProvider(new FakeHttp(url => down ? throw new HttpRequestException("network error") : Realistic(url)));
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.ResolveAsync(Query()));
        down = false;
        Assert.True(SpinWait.SpinUntil(() => true, 50));
        Assert.NotNull(await provider.ResolveAsync(Query()));
    }
}
