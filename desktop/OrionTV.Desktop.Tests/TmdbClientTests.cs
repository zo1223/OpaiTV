using System.Net;
using System.Text.Json;
using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.Tests;

public class TmdbClientTests
{
    private const string V3Key = "0123456789abcdef0123456789abcdef";
    private const string BearerToken = "eyJhbGciOiJIUzI1NiJ9.eyJhdWQiOiJ0ZXN0In0.c2lnbmF0dXJl";

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;

        public FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return _respond(request, cancellationToken);
        }
    }

    private static FakeHandler Returning(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json) }));

    private static string Results(params object[] items) => JsonSerializer.Serialize(new { page = 1, results = items });

    private static object Movie(int id, string title, string date = "2019-02-05", string? poster = "/poster.jpg",
        string overview = "简介", double vote = 7.94, int votes = 100, string? original = null) =>
        new
        {
            id,
            media_type = "movie",
            title,
            original_title = original ?? title,
            poster_path = poster,
            overview,
            vote_average = vote,
            vote_count = votes,
            release_date = date,
        };

    private static object Tv(int id, string name, string date = "2019-11-26", string? poster = "/tv.jpg",
        string overview = "剧集简介", double vote = 8.06, int votes = 50) =>
        new
        {
            id,
            media_type = "tv",
            name,
            original_name = name,
            poster_path = poster,
            overview,
            vote_average = vote,
            vote_count = votes,
            first_air_date = date,
        };

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankKey_ReturnsNullWithoutAnyRequest(string? key)
    {
        var handler = Returning(Results(Movie(1, "流浪地球")));
        var client = new TmdbClient(new HttpClient(handler));

        Assert.Null(await client.LookupAsync(key, "流浪地球", "2019", "科幻片"));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task V3Key_GoesInQueryString_ToOfficialHost_WithAdultExcluded()
    {
        var handler = Returning(Results(Movie(1, "流浪地球")));
        var client = new TmdbClient(new HttpClient(handler));

        var metadata = await client.LookupAsync($"  {V3Key} ", "流浪地球", "2019", "科幻片");

        Assert.NotNull(metadata);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https", request.RequestUri!.Scheme);
        Assert.Equal(TmdbClient.ApiHost, request.RequestUri.Host);
        Assert.Equal("/3/search/multi", request.RequestUri.AbsolutePath);
        var query = request.RequestUri.Query;
        Assert.Contains("include_adult=false", query, StringComparison.Ordinal);
        Assert.Contains("language=zh-CN", query, StringComparison.Ordinal);
        Assert.Contains("api_key=" + V3Key, query, StringComparison.Ordinal);
        Assert.Contains("query=" + Uri.EscapeDataString("流浪地球"), query, StringComparison.Ordinal);
        Assert.Null(request.Headers.Authorization);
    }

    [Fact]
    public async Task BearerToken_GoesInHeader_NotInUrl()
    {
        var handler = Returning(Results(Movie(1, "流浪地球")));
        var client = new TmdbClient(new HttpClient(handler));

        Assert.NotNull(await client.LookupAsync(BearerToken, "流浪地球", "2019", null));

        var request = Assert.Single(handler.Requests);
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal(BearerToken, request.Headers.Authorization.Parameter);
        Assert.DoesNotContain("api_key", request.RequestUri!.Query, StringComparison.Ordinal);
        Assert.DoesNotContain(BearerToken, request.RequestUri.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("include_adult=false", request.RequestUri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lookup_MapsPosterOverviewRatingAndYear()
    {
        var handler = Returning(Results(Movie(535167, "流浪地球", "2019-02-05", "/abc.jpg", "太阳即将毁灭。", 7.94, 3000)));
        var client = new TmdbClient(new HttpClient(handler));

        var metadata = await client.LookupAsync(V3Key, "流浪地球", "2019", "科幻片");

        Assert.NotNull(metadata);
        Assert.Equal(535167, metadata!.TmdbId);
        Assert.Equal("movie", metadata.MediaType);
        Assert.Equal("流浪地球", metadata.Title);
        Assert.Equal("https://image.tmdb.org/t/p/w500/abc.jpg", metadata.PosterUrl);
        Assert.Equal("太阳即将毁灭。", metadata.Overview);
        Assert.Equal(7.9, metadata.Rating);
        Assert.Equal("2019", metadata.Year);
    }

    [Fact]
    public async Task Lookup_QueryDropsSeasonSuffix_AndMatchesTheSeries()
    {
        var handler = Returning(Results(Movie(9, "庆余年", "2020-01-01"), Tv(1, "庆余年")));
        var client = new TmdbClient(new HttpClient(handler));

        var metadata = await client.LookupAsync(V3Key, "庆余年 第二季", "2024", "国产剧");

        Assert.Equal("tv", metadata!.MediaType);
        Assert.Equal(1, metadata.TmdbId);
        var query = Assert.Single(handler.Requests).RequestUri!.Query;
        Assert.Contains("query=" + Uri.EscapeDataString("庆余年") + "&", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lookup_NoExactTitle_ReturnsNull()
    {
        var handler = Returning(Results(Movie(1, "流浪地球2"), Tv(2, "流浪地球：飞跃2020特别版")));
        var client = new TmdbClient(new HttpClient(handler));

        Assert.Null(await client.LookupAsync(V3Key, "流浪地球", "2019", "科幻片"));
    }

    [Fact]
    public async Task Lookup_SuccessIsCached_FailureIsNot()
    {
        var calls = 0;
        var handler = new FakeHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(calls == 1
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Results(Movie(1, "流浪地球"))) });
        });
        var client = new TmdbClient(new HttpClient(handler));

        Assert.Null(await client.LookupAsync(V3Key, "流浪地球", "2019", null));
        Assert.NotNull(await client.LookupAsync(V3Key, "流浪地球", "2019", null));
        Assert.NotNull(await client.LookupAsync(V3Key, "流浪地球", "2019", null));
        Assert.Equal(2, calls);
        // 缓存不能绕过“没填 Key 就不启用”
        Assert.Null(await client.LookupAsync("", "流浪地球", "2019", null));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Lookup_ErrorStatus_ReturnsNull(HttpStatusCode status)
    {
        var handler = Returning("{\"status_code\":7,\"status_message\":\"Invalid API key\"}", status);
        var client = new TmdbClient(new HttpClient(handler));

        Assert.Null(await client.LookupAsync(V3Key, "流浪地球", "2019", null));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{\"results\":\"oops\"}")]
    [InlineData("{\"results\":[1,null,{\"media_type\":\"movie\"}]}")]
    public async Task Lookup_UnexpectedBody_ReturnsNull(string body)
    {
        var client = new TmdbClient(new HttpClient(Returning(body)));

        Assert.Null(await client.LookupAsync(V3Key, "流浪地球", "2019", null));
    }

    [Fact]
    public async Task Lookup_NetworkException_ReturnsNull()
    {
        var handler = new FakeHandler((_, _) => throw new HttpRequestException("connection refused"));
        var client = new TmdbClient(new HttpClient(handler));

        Assert.Null(await client.LookupAsync(V3Key, "流浪地球", "2019", null));
    }

    [Fact]
    public async Task Lookup_Timeout_ReturnsNull()
    {
        var handler = new FakeHandler(async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var client = new TmdbClient(new HttpClient(handler), TimeSpan.FromMilliseconds(50));

        Assert.Null(await client.LookupAsync(V3Key, "流浪地球", "2019", null));
    }

    [Fact]
    public async Task Lookup_CallerCancellation_ReturnsNull()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var handler = new FakeHandler(async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var client = new TmdbClient(new HttpClient(handler));

        Assert.Null(await client.LookupAsync(V3Key, "流浪地球", "2019", null, cts.Token));
    }

    [Theory]
    [InlineData("庆余年 第二季", "庆余年")]
    [InlineData("庆余年第2季", "庆余年")]
    [InlineData("三体 第2部", "三体")]
    [InlineData("Breaking Bad Season 2", "Breaking Bad")]
    [InlineData("Breaking Bad S02", "Breaking Bad")]
    [InlineData("Breaking Bad S02E05", "Breaking Bad")]
    [InlineData("鬼灭之刃 2", "鬼灭之刃")]
    [InlineData("流浪地球（国语版）", "流浪地球")]
    [InlineData("流浪地球[4K]", "流浪地球")]
    [InlineData("流浪地球2", "流浪地球2")]
    [InlineData("银翼杀手 2049", "银翼杀手 2049")]
    [InlineData("1917", "1917")]
    public void StripSeasonSuffix_RemovesSeasonAndNotes_ButKeepsRealTitles(string title, string expected)
    {
        Assert.Equal(expected, TmdbMatcher.StripSeasonSuffix(title));
    }

    [Theory]
    [InlineData("流浪 地球！", "流浪地球")]
    [InlineData("Ｂｒｅａｋｉｎｇ　Bad", "breakingbad")]
    [InlineData("Spider-Man: No Way Home", "spidermannowayhome")]
    [InlineData(null, "")]
    public void Normalize_DropsPunctuationWidthAndCase(string? value, string expected)
    {
        Assert.Equal(expected, TmdbMatcher.Normalize(value));
    }

    [Theory]
    [InlineData("电影", "movie")]
    [InlineData("剧情片", "movie")]
    [InlineData("动漫电影", "movie")]
    [InlineData("国产剧", "tv")]
    [InlineData("日本动漫", "tv")]
    [InlineData("大陆综艺", "tv")]
    [InlineData("新番", "tv")]
    [InlineData("", null)]
    [InlineData("其它", null)]
    public void HintMediaType_FollowsSourceCategory(string category, string? expected)
    {
        Assert.Equal(expected, TmdbMatcher.HintMediaType(category));
    }

    private static TmdbCandidate Candidate(int id, string type, string title, string? date = null, string? original = null) =>
        new(id, type, title, original, "/p.jpg", "o", 7, 10, date);

    [Fact]
    public void PickBest_PrefersMatchingYear_ThenTypeHint_ThenOrder()
    {
        var results = new[]
        {
            Candidate(1, "movie", "倚天屠龙记", "1993-12-18"),
            Candidate(2, "tv", "倚天屠龙记", "2003-01-01"),
            Candidate(3, "tv", "倚天屠龙记", "2019-02-27"),
        };

        Assert.Equal(3, TmdbMatcher.PickBest(results, "倚天屠龙记", "2019", "国产剧")!.Id);
        Assert.Equal(1, TmdbMatcher.PickBest(results, "倚天屠龙记", "1994", null)!.Id);
        // 年份未知：按分类挑类型，同类型取靠前的
        Assert.Equal(2, TmdbMatcher.PickBest(results, "倚天屠龙记", "", "国产剧")!.Id);
        Assert.Equal(1, TmdbMatcher.PickBest(results, "倚天屠龙记", null, "动作片")!.Id);
        // 什么线索都没有：原顺序第一条
        Assert.Equal(1, TmdbMatcher.PickBest(results, "倚天屠龙记", null, null)!.Id);
    }

    [Fact]
    public void PickBest_MatchesOriginalTitle_AndIgnoresPeopleAndPartialTitles()
    {
        var results = new[]
        {
            Candidate(1, "person", "Breaking Bad"),
            Candidate(2, "tv", "绝命毒师：前传"),
            Candidate(3, "tv", "绝命毒师", "2008-01-20", "Breaking Bad"),
        };

        Assert.Equal(3, TmdbMatcher.PickBest(results, "Breaking Bad Season 2", "2009", null)!.Id);
        Assert.Null(TmdbMatcher.PickBest(results, "绝命", null, null));
        Assert.Null(TmdbMatcher.PickBest(results, "", null, null));
        Assert.Null(TmdbMatcher.PickBest([], "绝命毒师", null, null));
    }

    [Fact]
    public void PickBest_FullTitleWinsOverStrippedTitle()
    {
        var results = new[]
        {
            Candidate(1, "movie", "鬼灭之刃"),
            Candidate(2, "movie", "鬼灭之刃 2"),
        };

        Assert.Equal(2, TmdbMatcher.PickBest(results, "鬼灭之刃 2", null, null)!.Id);
    }

    [Fact]
    public void ParseResults_SkipsAdultAndNonMediaEntries()
    {
        var json = "{\"results\":[{\"id\":1,\"media_type\":\"person\",\"name\":\"某人\"},"
            + "{\"id\":2,\"media_type\":\"movie\",\"title\":\"甲\",\"adult\":true},"
            + "{\"id\":3,\"media_type\":\"tv\",\"name\":\"乙\",\"first_air_date\":\"2020-05-01\",\"vote_average\":0,\"vote_count\":0}]}";

        var parsed = TmdbMatcher.ParseResults(json);

        var only = Assert.Single(parsed);
        Assert.Equal(3, only.Id);
        var metadata = TmdbMatcher.ToMetadata(only);
        Assert.Null(metadata.Rating);
        Assert.Null(metadata.PosterUrl);
        Assert.Null(metadata.Overview);
        Assert.Equal("2020", metadata.Year);
    }

    [Fact]
    public void Merge_KeepsEveryOriginalValue()
    {
        var tmdb = new TmdbMetadata(1, "movie", "流浪地球", "https://image.tmdb.org/t/p/w500/a.jpg", "TMDB 简介", 7.9, "2019");

        var merged = TmdbMerge.Apply("https://img.example/poster.jpg", "源站简介", "2018", tmdb);

        Assert.Equal("https://img.example/poster.jpg", merged.Poster);
        Assert.Equal("源站简介", merged.Description);
        Assert.Equal("2018", merged.Year);
        Assert.False(merged.PosterFilled);
        Assert.False(merged.DescriptionFilled);
        Assert.False(merged.YearFilled);
        Assert.Equal("TMDB 7.9", merged.RatingText);
        Assert.Equal("9.1", TmdbMerge.Apply("p", "d", "2018", tmdb, existingRating: " 9.1 ").RatingText);
    }

    [Fact]
    public void Merge_FillsOnlyBlanks()
    {
        var tmdb = new TmdbMetadata(1, "movie", "流浪地球", "https://image.tmdb.org/t/p/w500/a.jpg", "TMDB 简介", 8.0, "2019");

        var merged = TmdbMerge.Apply("", "  ", null, tmdb);

        Assert.Equal("https://image.tmdb.org/t/p/w500/a.jpg", merged.Poster);
        Assert.Equal("TMDB 简介", merged.Description);
        Assert.Equal("2019", merged.Year);
        Assert.True(merged.PosterFilled && merged.DescriptionFilled && merged.YearFilled);
        Assert.Equal("TMDB 8.0", merged.RatingText);
    }

    [Fact]
    public void Merge_WithoutTmdbData_ChangesNothing()
    {
        var merged = TmdbMerge.Apply("", "源站简介", "", null);

        Assert.Equal("", merged.Poster);
        Assert.Equal("源站简介", merged.Description);
        Assert.Equal("", merged.Year);
        Assert.Equal("", merged.RatingText);
        Assert.False(merged.PosterFilled || merged.DescriptionFilled || merged.YearFilled);

        var empty = new TmdbMetadata(1, "tv", "乙", null, null, null, null);
        var stillEmpty = TmdbMerge.Apply("", "", "", empty);
        Assert.False(stillEmpty.PosterFilled || stillEmpty.DescriptionFilled || stillEmpty.YearFilled);
        Assert.Equal("", stillEmpty.RatingText);
    }

    [Fact]
    public void Settings_TmdbKey_DefaultsToEmpty_AndRoundTrips()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var legacy = JsonSerializer.Deserialize<DesktopSettings>("{\"SubscriptionUrl\":\"https://sub.example/list\"}", options)!;
        Assert.Equal("", DesktopSettingsMigration.Apply(legacy).TmdbApiKey);

        var saved = DesktopSettingsMigration.Apply(legacy with { TmdbApiKey = $"  {V3Key}\n" });
        Assert.Equal(V3Key, saved.TmdbApiKey);
        var reloaded = JsonSerializer.Deserialize<DesktopSettings>(JsonSerializer.Serialize(saved), options)!;
        Assert.Equal(V3Key, reloaded.TmdbApiKey);
        Assert.Equal("https://sub.example/list", reloaded.SubscriptionUrl);
    }
}
