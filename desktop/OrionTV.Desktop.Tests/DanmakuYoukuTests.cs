using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.Tests;

public class DanmakuYoukuTests
{
    private const string TokenCookie =
        "mtop_partitioned_detect=1;Path=/; _m_h5_tk=0123456789abcdef0123456789abcdef_1795629354744;Path=/;Domain=youku.com; _m_h5_tk_enc=encvalue;Path=/";

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    private static string Md5(string text) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static string Show(string showId, string name, string feature, int isYouku = 1, int episodeTotal = 40) =>
        $"{{\"commonData\":{{\"showId\":\"{showId}\",\"titleDTO\":{{\"displayName\":{JsonSerializer.Serialize(name)}}},\"feature\":{JsonSerializer.Serialize(feature)},\"isYouku\":{isYouku},\"hasYouku\":{isYouku},\"episodeTotal\":{episodeTotal}}}}}";

    private static string SearchJson(params string[] shows) => $"{{\"pageComponentList\":[{string.Join(",", shows)}]}}";

    private static string Video(int n, string? title = null, string duration = "2725.04") =>
        $"{{\"id\":\"VID{n}==\",\"seq\":\"{n}\",\"stage\":\"{n}\",\"title\":{JsonSerializer.Serialize(title ?? $"藏海传 {n:00}")},\"duration\":\"{duration}\"}}";

    private static string Videos(int total, params string[] videos) => $"{{\"total\":{total},\"videos\":[{string.Join(",", videos)}]}}";

    private static string SegmentJson(string items) =>
        JsonSerializer.Serialize(new
        {
            api = "mopen.youku.danmu.list",
            data = new { result = $"{{\"code\":1,\"data\":{{\"result\":[{items}]}}}}" },
            ret = new[] { "SUCCESS::调用成功" },
        });

    private class FakeHttp : IDanmakuHttp
    {
        public List<(string Method, string Url, string? Body, IReadOnlyDictionary<string, string>? Headers)> Calls { get; } = [];

        public Func<string, string> Search { get; set; } = _ => SearchJson();

        public Func<int, string> Episodes { get; set; } = _ => Videos(0);

        public Func<DanmakuRawResponse> Token { get; set; } = () => new DanmakuRawResponse("{}", TokenCookie);

        public Func<int, DanmakuRawResponse> Segment { get; set; } = _ => new DanmakuRawResponse(SegmentJson(""), "");

        protected int SegmentCalls;

        public Task<JsonDocument> GetJsonAsync(string url, IReadOnlyDictionary<string, string>? headers, TimeSpan timeout, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (Calls)
            {
                Calls.Add(("GET", url, null, headers));
            }

            if (url.Contains("search.youku.com", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonDocument.Parse(Search(url)));
            }

            if (url.Contains("shows/videos.json", StringComparison.Ordinal))
            {
                var page = int.Parse(System.Text.RegularExpressions.Regex.Match(url, @"[?&]page=(\d+)").Groups[1].Value);
                return Task.FromResult(JsonDocument.Parse(Episodes(page)));
            }

            throw new HttpRequestException("unexpected " + url);
        }

        public Task<JsonDocument> PostJsonAsync(string url, string jsonBody, IReadOnlyDictionary<string, string>? headers, TimeSpan timeout, CancellationToken ct) =>
            throw new HttpRequestException("unexpected " + url);

        public Task<byte[]> GetBytesAsync(string url, IReadOnlyDictionary<string, string>? headers, TimeSpan timeout, CancellationToken ct) =>
            throw new HttpRequestException("unexpected " + url);

        public int CountOf(string part)
        {
            lock (Calls)
            {
                return Calls.Count(call => call.Url.Contains(part, StringComparison.Ordinal));
            }
        }
    }

    private sealed class RawFakeHttp : FakeHttp, IDanmakuRawHttp
    {
        public Task<DanmakuRawResponse> SendRawAsync(string method, string url, string? body, IReadOnlyDictionary<string, string>? headers, TimeSpan timeout, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (Calls)
            {
                Calls.Add((method, url, body, headers));
            }

            if (url.Contains("aplatform.weakget", StringComparison.Ordinal))
            {
                return Task.FromResult(Token());
            }

            if (url.Contains("mopen.youku.danmu.list", StringComparison.Ordinal))
            {
                return Task.FromResult(Segment(SegmentCalls++));
            }

            throw new HttpRequestException("unexpected " + url);
        }
    }

    private static readonly DanmakuQuery Query = new("藏海传", 1, 2, 40, "2025");

    private static T Happy<T>() where T : FakeHttp, new() => new()
    {
        Search = _ => SearchJson(
            Show("show-hl", "藏海传 高光版", "2026 · 电视剧 · 中国 · 20集全", episodeTotal: 20),
            Show("show-main", "藏海传", "2025 · 电视剧 · 中国 · 40集全(完结)")),
        Episodes = _ => Videos(3, Video(1), Video(2), Video(3)),
    };

    [Fact]
    public void ParseSearch_KeepsOnlyYoukuMainShows()
    {
        var result = YoukuDanmakuProvider.ParseSearch(Json(SearchJson(
            Show("a", "<em>藏海传</em>【独播】", "2025 · 电视剧 · 中国 · 40集全(完结)"),
            Show("b", "《藏海传》剧情解读", "2026 · 电视剧 · 共4集"),
            Show("c", "藏海传 抢先看", "2025 · 电视剧"),
            Show("d", "别的平台的片", "2024 · 电视剧", isYouku: 0),
            Show("e", "长安的荔枝", "2025 · 电影 · 中国", episodeTotal: 0),
            Show("a", "藏海传", "2025 · 电视剧"),
            "{\"commonData\":{\"titleDTO\":{\"displayName\":\"没有 showId\"}}}")));

        Assert.Equal(
            [
                new YoukuDanmakuCandidate("a", "藏海传", "2025", 40, false),
                new YoukuDanmakuCandidate("e", "长安的荔枝", "2025", 1, true),
            ],
            result);
        Assert.Empty(YoukuDanmakuProvider.ParseSearch(Json("null")));
        Assert.Empty(YoukuDanmakuProvider.ParseSearch(Json("{\"pageComponentList\":[{}]}")));
    }

    [Fact]
    public void ParseEpisodes_ReadsNumberAndDuration_DropsExtras()
    {
        var page = YoukuDanmakuProvider.ParseEpisodes(Json(
            $"{{\"total\":\"40\",\"videos\":[{Video(1)},{Video(2, duration: "abc")},{Video(9, "藏海传 预告片")},{{\"title\":\"没有 id\"}}]}}"));

        Assert.Equal(40, page.Total);
        Assert.Equal(
            [
                new YoukuDanmakuEpisode("VID1==", 1, "藏海传 01", 2725.04),
                new YoukuDanmakuEpisode("VID2==", 2, "藏海传 02", 0),
            ],
            page.Episodes);
        var empty = YoukuDanmakuProvider.ParseEpisodes(Json("{}"));
        Assert.Equal(0, empty.Total);
        Assert.Empty(empty.Episodes);
    }

    [Fact]
    public void ParseToken_NeedsBothCookies()
    {
        Assert.Equal(
            new YoukuDanmakuToken("0123456789abcdef0123456789abcdef_1795629354744", "encvalue"),
            YoukuDanmakuProvider.ParseToken(TokenCookie));
        Assert.Null(YoukuDanmakuProvider.ParseToken("_m_h5_tk=abc_1;Path=/"));
        Assert.Null(YoukuDanmakuProvider.ParseToken(""));
        Assert.Null(YoukuDanmakuProvider.ParseToken(null));
    }

    [Fact]
    public void ParseSegment_ConvertsSortsAndReadsStyle()
    {
        var comments = YoukuDanmakuProvider.ParseSegment(Json(SegmentJson(
            """
            {"content":"后到","playat":61500,"propertis":"{\"color\":16711680,\"pos\":1}"},
            {"content":"先到","playat":60250,"propertis":"{\"color\":\"255\",\"pos\":2}"},
            {"content":"默认","playat":60836,"propertis":"不是 JSON"},
            {"content":"滚动","playat":62000,"propertis":"{\"color\":16777215,\"pos\":3}"},
            {"content":"  ","playat":100},
            {"content":"没时间"}
            """)));

        Assert.Equal(
            [
                new DanmakuComment(60.25, "先到", 0x0000ff, DanmakuMode.Bottom),
                new DanmakuComment(60.836, "默认", 0xffffff, DanmakuMode.Scroll),
                new DanmakuComment(61.5, "后到", 0xff0000, DanmakuMode.Top),
                new DanmakuComment(62, "滚动", 0xffffff, DanmakuMode.Scroll),
            ],
            comments);
        Assert.Empty(YoukuDanmakuProvider.ParseSegment(Json("{\"data\":{}}")));
        Assert.Empty(YoukuDanmakuProvider.ParseSegment(Json("{\"data\":{\"result\":\"{bad\"}}")));
        Assert.Empty(YoukuDanmakuProvider.ParseSegment(Json("{\"data\":{\"result\":\"{\\\"code\\\":\\\"-1\\\"}\"}}")));
    }

    [Fact]
    public void IsTokenError_DetectsTokenFailures()
    {
        Assert.True(YoukuDanmakuProvider.IsTokenError(Json("{\"ret\":[\"FAIL_SYS_TOKEN_EMPTY::令牌为空\"]}")));
        Assert.False(YoukuDanmakuProvider.IsTokenError(Json("{\"ret\":[\"SUCCESS::调用成功\"]}")));
        Assert.False(YoukuDanmakuProvider.IsTokenError(Json("{}")));
    }

    [Fact]
    public void BuildSegmentRequest_SignsLikeMobile()
    {
        const string tk = "0123456789abcdef0123456789abcdef_1795629354744";
        var request = YoukuDanmakuProvider.BuildSegmentRequest("VID1==", 3, "guid-1", tk, 1700000000000);

        Assert.StartsWith("data=", request.Body);
        var data = Uri.UnescapeDataString(request.Body[5..]);
        const string inner = "{\"ctime\":1700000000000,\"ctype\":10004,\"cver\":\"v1.0\",\"guid\":\"guid-1\",\"mat\":3,\"mcount\":1,\"pid\":0,\"sver\":\"3.1.0\",\"type\":1,\"vid\":\"VID1==\"}";
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(inner));
        Assert.Equal(
            inner[..^1] + $",\"msg\":\"{encoded}\",\"sign\":\"{Md5(encoded + "MkmC9SoIw6xCkSKHhJ7b5D2r51kBiREr")}\"}}",
            data);
        var sign = Md5($"0123456789abcdef0123456789abcdef&1700000000000&24679788&{data}");
        Assert.Equal(
            $"https://acs.youku.com/h5/mopen.youku.danmu.list/1.0/?jsv=2.5.6&appKey=24679788&t=1700000000000&sign={sign}" +
            "&api=mopen.youku.danmu.list&v=1.0&type=originaljson&dataType=jsonp&timeout=20000&jsonpIncPrefix=utility",
            request.Url);
    }

    [Fact]
    public async Task Resolve_MatchesEpisodeAndLoadsSegments()
    {
        var http = Happy<RawFakeHttp>();
        http.Segment = _ => new DanmakuRawResponse(SegmentJson("{\"content\":\"来了\",\"playat\":61000}"), "");
        var track = await new YoukuDanmakuProvider(http, () => 1700000000000).ResolveAsync(Query);

        Assert.NotNull(track);
        Assert.Equal("youku", track!.Provider);
        Assert.Equal("藏海传 02", track.Title);
        Assert.Equal(60, track.SegmentSeconds);
        Assert.Equal(1, http.CountOf("show_id=show-main"));

        Assert.Equal([new DanmakuComment(61, "来了", 0xffffff, DanmakuMode.Scroll)], await track.LoadSegmentAsync(1));
        var post = http.Calls.Single(call => call.Method == "POST");
        Assert.Equal("_m_h5_tk=0123456789abcdef0123456789abcdef_1795629354744;_m_h5_tk_enc=encvalue;", post.Headers!["Cookie"]);
        Assert.Equal("application/x-www-form-urlencoded", post.Headers["Content-Type"]);
        var msg = Json(Uri.UnescapeDataString(post.Body![5..]));
        Assert.Equal("VID2==", msg.GetProperty("vid").GetString());
        Assert.Equal(1, msg.GetProperty("mat").GetInt32());

        // 2725 秒 → 46 片(0..45);超出范围不发请求
        var before = http.Calls.Count;
        Assert.Empty(await track.LoadSegmentAsync(46));
        Assert.Empty(await track.LoadSegmentAsync(-1));
        Assert.Equal(before, http.Calls.Count);
        await track.LoadSegmentAsync(2);
        Assert.Equal(1, http.CountOf("weakget"));
    }

    [Fact]
    public async Task Resolve_CachesSeries()
    {
        var http = Happy<RawFakeHttp>();
        var provider = new YoukuDanmakuProvider(http);
        await provider.ResolveAsync(Query);
        await provider.ResolveAsync(Query with { EpisodeIndex = 2, EpisodeNumber = 3 });
        Assert.Equal(1, http.CountOf("search.youku.com"));
        Assert.Equal(1, http.CountOf("videos.json"));
    }

    [Fact]
    public async Task Resolve_LongSeriesLoadsOnlyThePageOfTheEpisode()
    {
        var http = Happy<RawFakeHttp>();
        http.Search = _ => SearchJson(Show("long", "藏海传", "2025 · 电视剧", episodeTotal: 150));
        http.Episodes = page => page == 2 ? Videos(150, Video(101), Video(102)) : Videos(150, Video(1), Video(2));
        var track = await new YoukuDanmakuProvider(http).ResolveAsync(new DanmakuQuery("藏海传", 101, 102, 150));
        Assert.Equal("藏海传 102", track!.Title);
        Assert.Equal(1, http.CountOf("page=2&count=100"));
    }

    [Fact]
    public async Task Resolve_ReturnsNullWhenNotMatched()
    {
        Assert.Null(await new YoukuDanmakuProvider(new RawFakeHttp()).ResolveAsync(Query));
        Assert.Null(await new YoukuDanmakuProvider(Happy<RawFakeHttp>()).ResolveAsync(Query with { Title = "藏海传 第二季" }));
        Assert.Null(await new YoukuDanmakuProvider(Happy<RawFakeHttp>()).ResolveAsync(Query with { EpisodeIndex = 29, EpisodeNumber = 30 }));
        Assert.Null(await new YoukuDanmakuProvider(Happy<RawFakeHttp>()).ResolveAsync(Query with { Title = "完全不同的片" }));
    }

    [Fact]
    public async Task Resolve_ThrowsOnNetworkError_AndDoesNotCacheFailure()
    {
        var http = Happy<RawFakeHttp>();
        http.Search = _ => throw new HttpRequestException("network");
        var provider = new YoukuDanmakuProvider(http);
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.ResolveAsync(Query));
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.ResolveAsync(Query));
        Assert.Equal(2, http.CountOf("search.youku.com"));
    }

    [Fact]
    public async Task Resolve_ThrowsWhenTokenUnavailable()
    {
        var noCookie = Happy<RawFakeHttp>();
        noCookie.Token = () => new DanmakuRawResponse("{}", "");
        var provider = new YoukuDanmakuProvider(noCookie);
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.ResolveAsync(Query));
        // 失败的令牌不缓存
        noCookie.Token = () => new DanmakuRawResponse("{}", TokenCookie);
        Assert.NotNull(await provider.ResolveAsync(Query));

        await Assert.ThrowsAsync<HttpRequestException>(() => new YoukuDanmakuProvider(Happy<FakeHttp>()).ResolveAsync(Query));
    }

    [Fact]
    public async Task LoadSegment_RenewsExpiredTokenOnce()
    {
        var expired = new DanmakuRawResponse(
            "{\"data\":{},\"ret\":[\"FAIL_SYS_TOKEN_EXOIRED::令牌过期\"]}",
            "_m_h5_tk=fresh_1;Path=/; _m_h5_tk_enc=freshenc;Path=/");
        var http = Happy<RawFakeHttp>();
        http.Segment = call => call == 0 ? expired : new DanmakuRawResponse(SegmentJson("{\"content\":\"好了\",\"playat\":1000}"), "");
        var track = await new YoukuDanmakuProvider(http).ResolveAsync(Query);
        Assert.Equal([new DanmakuComment(1, "好了", 0xffffff, DanmakuMode.Scroll)], await track!.LoadSegmentAsync(0));
        var posts = http.Calls.Where(call => call.Method == "POST").ToList();
        Assert.Equal(2, posts.Count);
        Assert.Equal("_m_h5_tk=fresh_1;_m_h5_tk_enc=freshenc;", posts[1].Headers!["Cookie"]);

        var dead = Happy<RawFakeHttp>();
        dead.Segment = _ => expired with { SetCookie = "" };
        var deadTrack = await new YoukuDanmakuProvider(dead).ResolveAsync(Query);
        await Assert.ThrowsAsync<HttpRequestException>(() => deadTrack!.LoadSegmentAsync(0));
    }
}
