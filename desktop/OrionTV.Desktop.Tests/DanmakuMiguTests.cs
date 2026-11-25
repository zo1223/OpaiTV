using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.Tests;

public class DanmakuMiguTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    private sealed class FakeHttp : IDanmakuHttp
    {
        public List<(string Method, string Url, string? Body, IReadOnlyDictionary<string, string>? Headers)> Calls { get; } = [];

        /// <summary>(方法, 地址, 请求体) → JSON 字符串 / byte[]；抛异常表示网络失败。</summary>
        public Func<string, string, string?, object> Handler { get; set; } = (_, url, _) => throw new HttpRequestException("unexpected " + url);

        public Task<JsonDocument> GetJsonAsync(string url, IReadOnlyDictionary<string, string>? headers, TimeSpan timeout, CancellationToken ct) =>
            Run(() => JsonDocument.Parse((string)Record("GET", url, null, headers, ct)));

        public Task<JsonDocument> PostJsonAsync(string url, string jsonBody, IReadOnlyDictionary<string, string>? headers, TimeSpan timeout, CancellationToken ct) =>
            Run(() => JsonDocument.Parse((string)Record("POST", url, jsonBody, headers, ct)));

        public Task<byte[]> GetBytesAsync(string url, IReadOnlyDictionary<string, string>? headers, TimeSpan timeout, CancellationToken ct) =>
            Run(() => (byte[])Record("BYTES", url, null, headers, ct));

        private static Task<T> Run<T>(Func<T> action)
        {
            try
            {
                return Task.FromResult(action());
            }
            catch (Exception exception)
            {
                return Task.FromException<T>(exception);
            }
        }

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

    // 与 MiguDanmakuProvider 里固定密钥推导出的结果一致(base64 解码后逐个半字节查表)
    private static readonly byte[] Nibbles = [3, 5, 7, 0, 15, 10, 13, 1, 11, 14, 4, 6, 9, 12, 8, 2];
    private static readonly byte[] Key = Convert.FromBase64String("vwwLu7e6ug4HAQMAug8CsA8HD7oHDwuxAg4HAQG6DLA=")
        .Select(value => (byte)((Nibbles[value >> 4] << 4) | Nibbles[value & 15]))
        .ToArray();

    private static string Encrypt(string text)
    {
        using var aes = Aes.Create();
        aes.Key = Key;
        return Convert.ToBase64String(aes.EncryptEcb(Encoding.UTF8.GetBytes(text), PaddingMode.PKCS7));
    }

    private const string SearchResponse = """
        {"body":{"contentInfoList":[
          {"shortMediaAsset":{"name":"三体 花絮速看","isLong":0,"contDisplayName":"娱乐","pID":"1"}},
          {"shortMediaAsset":{"name":"三体","isLong":1,"contDisplayName":"电视剧","year":"2023 ","pID":"760834922",
            "extraData":{"episodes":["760835542","760835537","760835548"]}}},
          {"shortMediaAsset":{"name":"三体之灵魂危机","isLong":1,"contDisplayName":"电影","year":"2015 ","pID":"611147049"}},
          {}
        ]}}
        """;

    private const string DetailResponse = """
        {"code":200,"body":{"data":{"name":"三体","epsID":"760834922","totalPage":1,"pageSize":150,
          "playing":{"pID":"760835542","duration":"42:44"},
          "datas":[
            {"name":"《三体》第01集：自杀者名单令汪淼震惊","index":"1","pID":"760835542","duration":"42:44"},
            {"name":"《三体》第02集：汪淼物理认知被颠覆","index":"2","pID":"760835537","duration":"00:50"},
            {"name":"《三体》第03集 预告","index":"3","pID":"760835548","duration":"01:00"}
          ]}}}
        """;

    private const string SegmentResponse = """
        {"code":200,"body":{"result":[
          {"msg":"打卡","playtime":64,"textcolor":"#5FEA68"},
          {"msg":"第一","playtime":60,"textcolor":"FBFFFF"},
          {"msg":"又一条","playtime":64,"textcolor":""},
          {"msg":"  ","playtime":65},
          {"msg":"没时间"},
          {"msg":"负数","playtime":-1}
        ]}}
        """;

    private static FakeHttp MiguHttp(string? search = null, string? detail = null)
    {
        var http = new FakeHttp();
        http.Handler = (_, url, _) =>
        {
            if (url.Contains("open-search", StringComparison.Ordinal))
            {
                return search ?? SearchResponse;
            }

            if (url.Contains("content-info", StringComparison.Ordinal))
            {
                return detail ?? DetailResponse;
            }

            if (url.Contains("barrage", StringComparison.Ordinal))
            {
                return Encoding.UTF8.GetBytes(Encrypt(SegmentResponse));
            }

            throw new HttpRequestException("unexpected " + url);
        };
        return http;
    }

    private static DanmakuQuery Query(string title = "三体", int index = 0, int number = 1, int count = 30) =>
        new(title, index, number, count);

    // ---------- 解析 ----------

    [Fact]
    public void ParseSearch_KeepsLongVideosOnly()
    {
        var result = MiguDanmakuProvider.ParseSearch(Json(SearchResponse));
        Assert.Equal(2, result.Count);
        Assert.Equal(new MiguDanmakuCandidate("760835542", "三体", "2023", 3, false), result[0]);
        Assert.Equal(new MiguDanmakuCandidate("611147049", "三体之灵魂危机", "2015", 1, true), result[1]);
        Assert.Empty(MiguDanmakuProvider.ParseSearch(Json("{}")));
        Assert.Empty(MiguDanmakuProvider.ParseSearch(Json("""{"body":{"contentInfoList":null}}""")));
    }

    [Fact]
    public void ParseDuration_ReadsClockText()
    {
        Assert.Equal(2564, MiguDanmakuProvider.ParseDuration("42:44"));
        Assert.Equal(3604, MiguDanmakuProvider.ParseDuration("01:00:04"));
        Assert.Equal(90, MiguDanmakuProvider.ParseDuration("90"));
        Assert.Equal(0, MiguDanmakuProvider.ParseDuration(""));
        Assert.Equal(0, MiguDanmakuProvider.ParseDuration("abc"));
        Assert.Equal(0, MiguDanmakuProvider.ParseDuration(null));
    }

    [Fact]
    public void ParseEpisodePage_ReadsNumbersAndDurations_DropsTrailers()
    {
        var page = MiguDanmakuProvider.ParseEpisodePage(Json(DetailResponse));
        Assert.Equal("760834922", page.SeriesId);
        Assert.Equal(1, page.TotalPages);
        Assert.Equal(150, page.PageSize);
        Assert.Equal(2, page.Episodes.Count);
        Assert.Equal(new MiguDanmakuEpisode("760835542", 1, "《三体》第01集：自杀者名单令汪淼震惊", 2564), page.Episodes[0]);
        Assert.Equal(new MiguDanmakuEpisode("760835537", 2, "《三体》第02集：汪淼物理认知被颠覆", 50), page.Episodes[1]);
    }

    [Fact]
    public void ParseEpisodePage_SingleVideoUsesPlaying_EmptyHasNoEpisodes()
    {
        var page = MiguDanmakuProvider.ParseEpisodePage(Json(
            """{"body":{"data":{"name":"三体之灵魂危机","epsID":"955171179","playing":{"pID":"611147049","duration":"01:00:04"}}}}"""));
        Assert.Equal([new MiguDanmakuEpisode("611147049", null, "三体之灵魂危机", 3604)], page.Episodes);
        Assert.Empty(MiguDanmakuProvider.ParseEpisodePage(Json("""{"code":410}""")).Episodes);
    }

    [Fact]
    public void ParseSegment_SortsSpreadsAndSkipsBadItems()
    {
        var comments = MiguDanmakuProvider.ParseSegment(Json(SegmentResponse));
        Assert.Equal(
            [
                new DanmakuComment(60, "第一", 0xfbffff, DanmakuMode.Scroll),
                new DanmakuComment(64, "打卡", 0x5fea68, DanmakuMode.Scroll),
                new DanmakuComment(64.5, "又一条", 0xffffff, DanmakuMode.Scroll),
            ],
            comments);
        Assert.Empty(MiguDanmakuProvider.ParseSegment(Json("""{"body":{}}""")));
        Assert.Empty(MiguDanmakuProvider.ParseSegment(null));
    }

    // ---------- 解密 ----------

    [Fact]
    public void DecodeSegmentBody_DecryptsCipherText_AcceptsPlainJson_ThrowsOnGarbage()
    {
        using (var decoded = MiguDanmakuProvider.DecodeSegmentBody(Encrypt(SegmentResponse) + "\n"))
        {
            Assert.Equal(6, decoded!.RootElement.GetProperty("body").GetProperty("result").GetArrayLength());
        }

        using (var plain = MiguDanmakuProvider.DecodeSegmentBody(""" {"body":{"result":[]}} """))
        {
            Assert.Equal(0, plain!.RootElement.GetProperty("body").GetProperty("result").GetArrayLength());
        }

        Assert.Null(MiguDanmakuProvider.DecodeSegmentBody(""));
        Assert.ThrowsAny<Exception>(() => MiguDanmakuProvider.DecodeSegmentBody("<html>error</html>"));
        Assert.Throws<FormatException>(() => MiguDanmakuProvider.DecryptSegment(new byte[15]));
        // 不是用这把密钥加密的内容:填充对不上
        Assert.Throws<FormatException>(() => MiguDanmakuProvider.DecryptSegment(Enumerable.Repeat((byte)7, 16).ToArray()));
    }

    // ---------- 取数 ----------

    [Fact]
    public async Task Resolve_MatchesSeries_LoadsEncryptedSegmentsBy30Seconds()
    {
        var http = MiguHttp();
        var track = await new MiguDanmakuProvider(http).ResolveAsync(Query());
        Assert.NotNull(track);
        Assert.Equal("migu", track.Provider);
        Assert.Contains("第01集", track.Title);
        Assert.Equal(30, track.SegmentSeconds);
        Assert.Equal("三体", Json(http.Calls[0].Body!).GetProperty("k").GetString());
        Assert.Equal("https://v3-sc.miguvideo.com/program/v4/cont/content-info/760835542/1", http.Calls[1].Url);

        var comments = await track.LoadSegmentAsync(2);
        Assert.Equal(["第一", "打卡", "又一条"], comments.Select(c => c.Text));
        var segment = http.Calls[^1];
        Assert.Equal(
            "https://webapi.miguvideo.com/gateway/live_barrage/videox/barrage/v2/list/760834922/760835542/60/90/020",
            segment.Url);
        Assert.Equal("miguvideo_default_h5", segment.Headers!["appCode"]);

        // 最后一个分片截到片长;片长之外不发请求
        await track.LoadSegmentAsync(85);
        Assert.EndsWith("/2550/2564/020", http.Calls[^1].Url);
        var before = http.Calls.Count;
        Assert.Empty(await track.LoadSegmentAsync(86));
        Assert.Equal(before, http.Calls.Count);
    }

    [Fact]
    public async Task Resolve_SameSeriesNextEpisode_DoesNotSearchAgain()
    {
        var http = MiguHttp();
        var provider = new MiguDanmakuProvider(http);
        await provider.ResolveAsync(Query());
        var second = await provider.ResolveAsync(Query(index: 1, number: 2));
        Assert.Contains("第02集", second!.Title);
        Assert.Equal(1, http.CountOf("open-search"));
        Assert.Equal(1, http.CountOf("content-info"));
    }

    [Fact]
    public async Task Resolve_ReturnsNull_WhenNoSeriesNoEpisodeOrNoDuration()
    {
        Assert.Null(await new MiguDanmakuProvider(MiguHttp()).ResolveAsync(Query("狂飙")));
        Assert.Null(await new MiguDanmakuProvider(MiguHttp(search: """{"body":{}}""")).ResolveAsync(Query()));
        Assert.Null(await new MiguDanmakuProvider(MiguHttp()).ResolveAsync(Query(index: 8, number: 9)));
        const string noDuration = """{"body":{"data":{"epsID":"1","datas":[{"name":"第1集","index":"1","pID":"9"}]}}}""";
        Assert.Null(await new MiguDanmakuProvider(MiguHttp(detail: noDuration)).ResolveAsync(Query()));
    }

    [Fact]
    public async Task Resolve_DifferentSeason_IsNotTheSameSeries()
    {
        Assert.Null(await new MiguDanmakuProvider(MiguHttp()).ResolveAsync(Query("三体 第二季")));
    }

    [Fact]
    public async Task Resolve_NetworkErrorThrows_AndFailureIsNotCached()
    {
        var dead = true;
        var http = new FakeHttp();
        http.Handler = (_, url, _) =>
        {
            if (dead)
            {
                throw new HttpRequestException("network error");
            }

            if (url.Contains("open-search", StringComparison.Ordinal))
            {
                return SearchResponse;
            }

            if (url.Contains("content-info", StringComparison.Ordinal))
            {
                return DetailResponse;
            }

            throw new HttpRequestException("segment down");
        };
        var provider = new MiguDanmakuProvider(http);
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.ResolveAsync(Query()));
        dead = false;
        var track = await provider.ResolveAsync(Query());
        Assert.NotNull(track);
        await Assert.ThrowsAsync<HttpRequestException>(() => track.LoadSegmentAsync(0));
    }
}
