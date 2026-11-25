using System.IO.Compression;
using System.Text;
using System.Text.Json;
using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.Tests;

/// <summary>爱奇艺弹幕来源。与手机版 services/danmaku/__tests__/iqiyi.test.ts 对应。</summary>
public class DanmakuIqiyiTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    private sealed class FakeHttp : IDanmakuHttp
    {
        public List<string> Calls { get; } = [];

        /// <summary>地址 → JSON 字符串 / byte[];抛异常表示网络失败。</summary>
        public Func<string, object> Handler { get; set; } = url => throw new HttpRequestException("unexpected " + url);

        public Task<JsonDocument> GetJsonAsync(string url, IReadOnlyDictionary<string, string>? headers, TimeSpan timeout, CancellationToken ct) =>
            Task.FromResult(JsonDocument.Parse((string)Record(url, ct)));

        public Task<JsonDocument> PostJsonAsync(string url, string jsonBody, IReadOnlyDictionary<string, string>? headers, TimeSpan timeout, CancellationToken ct) =>
            Task.FromResult(JsonDocument.Parse((string)Record(url, ct)));

        public Task<byte[]> GetBytesAsync(string url, IReadOnlyDictionary<string, string>? headers, TimeSpan timeout, CancellationToken ct) =>
            Task.FromResult((byte[])Record(url, ct));

        private object Record(string url, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (Calls)
            {
                Calls.Add(url);
            }

            return Handler(url);
        }

        public int CountOf(string urlPart)
        {
            lock (Calls)
            {
                return Calls.Count(url => url.Contains(urlPart, StringComparison.Ordinal));
            }
        }
    }

    private static string Album(
        string albumId = "5328486914190101",
        string title = "狂飙",
        string channel = "电视剧,2",
        string siteId = "iqiyi",
        int videoDocType = 1,
        string releaseDate = "20230114",
        int total = 39,
        string extra = "") =>
        "{\"albumDocInfo\":{\"albumId\":" + albumId + ",\"albumTitle\":\"" + title + "\",\"channel\":\"" + channel
        + "\",\"siteId\":\"" + siteId + "\",\"videoDocType\":" + videoDocType + ",\"releaseDate\":\"" + releaseDate
        + "\",\"itemTotalNumber\":" + total + extra + "}}";

    private static readonly string Search =
        "{\"data\":{\"code\":0,\"docinfos\":[" + string.Join(",",
            Album(albumId: "111", title: "电视剧《狂飙》原创解读", channel: "教育,12"),
            Album(albumId: "222", siteId: "sohu"),
            Album(albumId: "333", title: "狂飙 片段", videoDocType: 2),
            Album(),
            Album(),
            Album(albumId: "7292991076670500", title: "流浪地球2", channel: "电影,1", releaseDate: "20230122", total: 1,
                extra: ",\"videoinfos\":[{\"tvId\":7292991076670501}]")) + "]}}";

    private static string Episode(int order, string tvId, int contentType = 1, string? name = null) =>
        "{\"tvId\":" + tvId + ",\"order\":" + order + ",\"name\":\"" + (name ?? $"狂飙第{order}集") + "\",\"contentType\":" + contentType + "}";

    private static readonly string Episodes =
        "{\"code\":\"A00000\",\"data\":{\"total\":4,\"epsodelist\":[" + string.Join(",",
            Episode(1, "3493131456125200"),
            Episode(2, "6173969719145800"),
            Episode(3, "900", contentType: 3, name: "狂飙第3集预告"),
            Episode(3, "4296901635527000"),
            Episode(3, "4296901635527000")) + "]}}";

    private const string Xml = """
        <?xml version="1.0" encoding="UTF-8"?><danmu><data><entry><int>1</int><list>
        <bulletInfo><contentId>1</contentId><content>京海不大，创造神话</content><showTime>1</showTime><color>ffffff</color><position>0</position></bulletInfo>
        <bulletInfo><contentId>2</contentId><content>电影&#127916;的质感 &amp; 好看</content><showTime>1</showTime><color>ff0000</color></bulletInfo>
        <bulletInfo><contentId>3</contentId><content>   </content><showTime>2</showTime><color>ffffff</color></bulletInfo>
        <bulletInfo><contentId>4</contentId><content>没有时间</content><showTime>abc</showTime><color>ffffff</color></bulletInfo>
        <bulletInfo><contentId>5</contentId><content>坏颜色</content><showTime>299</showTime><color>zzz</color></bulletInfo>
        </list></entry></data></danmu>
        """;

    private static byte[] Zlib(string text)
    {
        using var output = new MemoryStream();
        using (var stream = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            stream.Write(bytes, 0, bytes.Length);
        }

        return output.ToArray();
    }

    private static object StandardRoutes(string url)
    {
        if (url.Contains("search.video.iqiyi.com", StringComparison.Ordinal)) return Search;
        if (url.Contains("avlistinfo", StringComparison.Ordinal)) return Episodes;
        if (url.Contains("baseinfo", StringComparison.Ordinal)) return "{\"code\":\"A00000\",\"data\":{\"durationSec\":2733}}";
        if (url.Contains("cmts.iqiyi.com", StringComparison.Ordinal)) return Zlib(Xml);
        throw new HttpRequestException("unexpected " + url);
    }

    private static readonly DanmakuQuery Query = new("狂飙", 1, 2, 39, "2023");

    [Fact]
    public void ParseSearch_KeepsOnlyIqiyiFeatureAlbums()
    {
        var candidates = IqiyiDanmakuProvider.ParseSearch(Json(Search));
        Assert.Equal(2, candidates.Count);
        Assert.Equal(new IqiyiDanmakuCandidate("5328486914190101", "", "狂飙", "2023", 39, false), candidates[0]);
        Assert.Equal(new IqiyiDanmakuCandidate("7292991076670500", "7292991076670501", "流浪地球2", "2023", 1, true), candidates[1]);
        Assert.Empty(IqiyiDanmakuProvider.ParseSearch(Json("{\"data\":\"search error\"}")));
        Assert.Empty(IqiyiDanmakuProvider.ParseSearch(Json("null")));
    }

    [Fact]
    public void ParseEpisodePage_KeepsFeatureEpisodesOnce()
    {
        var page = IqiyiDanmakuProvider.ParseEpisodePage(Json(Episodes));
        Assert.Equal(4, page.Total);
        Assert.Equal(
            [(1, "3493131456125200"), (2, "6173969719145800"), (3, "4296901635527000")],
            page.Episodes.Select(e => (e.Number ?? 0, e.TvId)).ToArray());
        Assert.Empty(IqiyiDanmakuProvider.ParseEpisodePage(Json("{}")).Episodes);
    }

    [Fact]
    public void ParseDuration_ZeroWhenDanmakuClosedOrMissing()
    {
        Assert.Equal(2733, IqiyiDanmakuProvider.ParseDuration(Json("{\"data\":{\"durationSec\":2733}}")));
        Assert.Equal(0, IqiyiDanmakuProvider.ParseDuration(Json("{\"data\":{\"durationSec\":2733,\"displayBarrage\":false}}")));
        Assert.Equal(0, IqiyiDanmakuProvider.ParseDuration(Json("{\"data\":{}}")));
        Assert.Equal(0, IqiyiDanmakuProvider.ParseDuration(Json("null")));
    }

    [Fact]
    public void BuildSegmentUrl_UsesLastFourDigitsAsFolder()
    {
        Assert.Equal(
            "https://cmts.iqiyi.com/bullet/52/00/3493131456125200_300_1.z",
            IqiyiDanmakuProvider.BuildSegmentUrl("3493131456125200", 0));
        Assert.Equal("https://cmts.iqiyi.com/bullet/01/23/123_300_10.z", IqiyiDanmakuProvider.BuildSegmentUrl("123", 9));
    }

    [Fact]
    public void ParseXml_DecodesEntitiesDropsBadRowsAndSpreadsSameSecond()
    {
        var comments = IqiyiDanmakuProvider.ParseXml(Xml);
        Assert.Equal(
            [
                new DanmakuComment(1, "京海不大，创造神话", 0xffffff, DanmakuMode.Scroll),
                new DanmakuComment(1.5, "电影🎬的质感 & 好看", 0xff0000, DanmakuMode.Scroll),
                new DanmakuComment(299, "坏颜色", 0xffffff, DanmakuMode.Scroll),
            ],
            comments);
        Assert.Empty(IqiyiDanmakuProvider.ParseXml(""));
    }

    [Fact]
    public void DecodeSegmentBytes_InflatesZlibOrPassesText()
    {
        Assert.Equal(Xml, IqiyiDanmakuProvider.DecodeSegmentBytes(Zlib(Xml)));
        Assert.Equal(Xml, IqiyiDanmakuProvider.DecodeSegmentBytes(Encoding.UTF8.GetBytes(Xml)));
        Assert.Equal("", IqiyiDanmakuProvider.DecodeSegmentBytes([1, 2, 3]));
        Assert.Equal("", IqiyiDanmakuProvider.DecodeSegmentBytes([]));
    }

    [Fact]
    public async Task Resolve_Series_SearchEpisodesDurationThenLazySegments()
    {
        var http = new FakeHttp { Handler = StandardRoutes };
        var provider = new IqiyiDanmakuProvider(http);
        Assert.Equal("iqiyi", provider.Name);
        var track = await provider.ResolveAsync(Query);
        Assert.NotNull(track);
        Assert.Equal("iqiyi", track!.Provider);
        Assert.Equal("狂飙第2集", track.Title);
        Assert.Equal(300, track.SegmentSeconds);
        Assert.Contains("key=%E7%8B%82%E9%A3%99", http.Calls[0]);
        Assert.Contains("aid=5328486914190101&page=1&size=200", http.Calls[1]);
        Assert.Equal("https://pcw-api.iqiyi.com/video/video/baseinfo/6173969719145800", http.Calls[2]);
        Assert.Equal(3, http.Calls.Count);

        var comments = await track.LoadSegmentAsync(9);
        Assert.Equal("https://cmts.iqiyi.com/bullet/58/00/6173969719145800_300_10.z", http.Calls[3]);
        Assert.Equal(3, comments.Count);
        // 2733 秒只有 10 个分片,超出的不发请求
        Assert.Empty(await track.LoadSegmentAsync(10));
        Assert.Equal(4, http.Calls.Count);
    }

    [Fact]
    public async Task Resolve_SameSeriesDoesNotSearchAgain()
    {
        var http = new FakeHttp { Handler = StandardRoutes };
        var provider = new IqiyiDanmakuProvider(http);
        await provider.ResolveAsync(Query);
        var track = await provider.ResolveAsync(Query with { EpisodeIndex = 0, EpisodeNumber = 1 });
        Assert.Equal("狂飙第1集", track!.Title);
        Assert.Equal(1, http.CountOf("search.video"));
        Assert.Equal(1, http.CountOf("avlistinfo"));
    }

    [Fact]
    public async Task Resolve_MovieUsesTvIdFromSearch()
    {
        var http = new FakeHttp { Handler = StandardRoutes };
        var track = await new IqiyiDanmakuProvider(http).ResolveAsync(new DanmakuQuery("流浪地球2", 0, 1, 1));
        Assert.Equal("流浪地球2", track!.Title);
        Assert.Equal(0, http.CountOf("avlistinfo"));
        Assert.Equal("https://pcw-api.iqiyi.com/video/video/baseinfo/7292991076670501", http.Calls[1]);
    }

    [Fact]
    public async Task Resolve_ReturnsNullWhenNoSeriesNoEpisodeOrNoDanmaku()
    {
        var provider = new IqiyiDanmakuProvider(new FakeHttp { Handler = StandardRoutes });
        Assert.Null(await provider.ResolveAsync(Query with { Title = "藏海传" }));
        Assert.Null(await provider.ResolveAsync(Query with { EpisodeIndex = 29, EpisodeNumber = 30 }));

        var closed = new FakeHttp
        {
            Handler = url => url.Contains("baseinfo", StringComparison.Ordinal)
                ? "{\"data\":{\"durationSec\":2733,\"displayBarrage\":false}}"
                : StandardRoutes(url),
        };
        Assert.Null(await new IqiyiDanmakuProvider(closed).ResolveAsync(Query));
    }

    [Fact]
    public async Task Resolve_ThrowsOnNetworkErrorAndDoesNotCacheFailure()
    {
        var down = true;
        var http = new FakeHttp { Handler = url => down ? throw new HttpRequestException("network error") : StandardRoutes(url) };
        var provider = new IqiyiDanmakuProvider(http);
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.ResolveAsync(Query));
        down = false;
        Assert.NotNull(await provider.ResolveAsync(Query));

        var segmentDown = new FakeHttp
        {
            Handler = url => url.Contains("cmts", StringComparison.Ordinal) ? throw new HttpRequestException("HTTP 500") : StandardRoutes(url),
        };
        var track = await new IqiyiDanmakuProvider(segmentDown).ResolveAsync(Query);
        await Assert.ThrowsAsync<HttpRequestException>(() => track!.LoadSegmentAsync(0));
    }

    [Fact]
    public async Task Resolve_LongSeriesLoadsOnlyThePageOfTheEpisode()
    {
        static string PageOf(int page) =>
            "{\"data\":{\"total\":450,\"epsodelist\":[" + string.Join(",",
                Enumerable.Range(0, 200).Select(i => Episode(((page - 1) * 200) + i + 1, (1000 + ((page - 1) * 200) + i).ToString()))) + "]}}";

        var http = new FakeHttp();
        http.Handler = url =>
        {
            if (url.Contains("search.video", StringComparison.Ordinal))
            {
                return "{\"data\":{\"docinfos\":[" + Album(title: "长剧", total: 450) + "]}}";
            }

            var marker = url.IndexOf("&page=", StringComparison.Ordinal);
            if (url.Contains("avlistinfo", StringComparison.Ordinal) && marker > 0)
            {
                return PageOf(int.Parse(url[(marker + 6)..].Split('&')[0]));
            }

            return StandardRoutes(url);
        };
        var track = await new IqiyiDanmakuProvider(http).ResolveAsync(new DanmakuQuery("长剧", 249, 250, 450));
        Assert.Equal("狂飙第250集", track!.Title);
        Assert.Equal(2, http.CountOf("avlistinfo"));
        Assert.Equal(1, http.CountOf("page=2"));
    }
}
