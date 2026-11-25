using System.Net;
using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.Tests;

public sealed class SearchIntentTests
{
    [Theory]
    [InlineData("回南天适合看什么", "阴天絮语")]
    [InlineData("梅雨季窝在家", "阴天絮语")]
    [InlineData("高铁上打发时间", "出差远行")]
    [InlineData("候机的时候看点啥", "出差远行")]
    [InlineData("夜宵配什么片", "小酌夜宵")]
    [InlineData("朋友聚会一起看", "朋友聚会")]
    [InlineData("做家务当背景音", "家务背景音")]
    public void Resolve_maps_weather_mood_and_scene_phrases_to_an_intent(string query, string expectedLabel)
    {
        var intent = SearchIntents.Resolve(query);

        Assert.NotNull(intent);
        Assert.Equal(expectedLabel, intent!.Label);
        Assert.NotEmpty(intent.Genres);
        Assert.NotEmpty(intent.Matched);
    }

    [Theory]
    [InlineData("流浪地球")]
    [InlineData("庆余年 第二季")]
    [InlineData("")]
    [InlineData(null)]
    public void Resolve_returns_null_for_plain_titles(string? query)
    {
        Assert.Null(SearchIntents.Resolve(query));
    }

    [Fact]
    public void Resolve_prefers_the_more_specific_phrase_when_several_intents_match()
    {
        // “热闹”(朋友聚会, 2 字) 与 “做家务”+“当背景音”(家务背景音, 8 字) 同时出现：取更具体的一组。
        var intent = SearchIntents.Resolve("做家务当背景音，热闹一点");

        Assert.Equal("家务背景音", intent!.Label);
    }

    [Fact]
    public void Negative_hints_remove_the_genre_and_record_the_exclusion()
    {
        var intent = SearchIntents.Resolve("回南天，不要爱情");

        Assert.NotNull(intent);
        Assert.DoesNotContain("爱情", intent!.Genres);
        Assert.Contains("爱情", intent.ExcludeGenres);
        Assert.Contains("剧情", intent.Genres);
    }

    [Fact]
    public void MatchCategories_picks_source_categories_by_genre_order_and_skips_excluded_ones()
    {
        var intent = SearchIntents.Resolve("做家务当背景音")!; // 纪录 / 喜剧 / 音乐 / 综艺，排除 恐怖、惊悚
        VideoSourceCategory[] categories =
        [
            new("1", "动作片"),
            new("2", "喜剧片"),
            new("3", "恐怖喜剧"),
            new("4", "记录片"),
            new("5", "综艺"),
        ];

        var picked = SearchIntents.MatchCategories(categories, intent, limit: 2);

        Assert.Equal(["4", "2"], picked.Select(category => category.Id).ToArray());
    }

    [Fact]
    public void Describe_names_the_intent_and_its_genres()
    {
        var intent = SearchIntents.Resolve("回南天")!;

        Assert.Equal("已按“阴天絮语”扩展检索：剧情 / 悬疑 / 爱情", SearchIntents.Describe(intent));
    }

    [Fact]
    public void MergeKeywordAndIntent_keeps_keyword_hits_first_and_drops_duplicate_titles()
    {
        CatalogItem[] keyword = [Item("1", "回南天"), Item("2", "梅雨 之夜")];
        CatalogItem[] intent = [Item("9", "梅雨之夜"), Item("3", "看不见的客人"), Item("4", "看不见的客人")];

        var merged = CatalogService.MergeKeywordAndIntent(keyword, intent);

        Assert.Equal(["1", "2", "3"], merged.Select(item => item.Id).ToArray());
        Assert.Same(keyword, CatalogService.MergeKeywordAndIntent(keyword, []));
    }

    [Fact]
    public async Task SearchByIntentAsync_lists_matched_categories_and_survives_a_failing_source()
    {
        var requested = new List<string>();
        var handler = new StubHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            requested.Add(url);
            if (url.Contains("broken.example.com", StringComparison.Ordinal))
            {
                return null;
            }

            if (url.Contains("ac=list", StringComparison.Ordinal))
            {
                return """{"code":1,"class":[{"type_id":6,"type_name":"动作片"},{"type_id":7,"type_name":"剧情片"},{"type_id":8,"type_name":"悬疑片"}],"list":[]}""";
            }

            var title = url.Contains("t=7", StringComparison.Ordinal) ? "剧情佳作" : "悬疑佳作";
            var json = "{\"code\":1,\"page\":1,\"pagecount\":1,\"total\":1,\"list\":[{\"vod_id\":" + (url.Contains("t=7", StringComparison.Ordinal) ? "70" : "80")
                + ",\"vod_name\":\"" + title + "\",\"vod_pic\":\"\",\"vod_year\":\"2024\",\"type_name\":\"x\",\"vod_play_from\":\"m3u8\",\"vod_play_url\":\"第1集$http://media.example.com/1.m3u8\"}]}";
            return json;
        });
        var catalog = new CatalogService(new HttpClient(handler));
        VideoSource[] sources =
        [
            new("ok", "正常源", "http://ok.example.com/api.php/provide/vod"),
            new("broken", "坏源", "http://broken.example.com/api.php/provide/vod"),
        ];

        var items = await catalog.SearchByIntentAsync(sources, SearchIntents.Resolve("回南天")!);

        Assert.Equal(["剧情佳作", "悬疑佳作"], items.Select(item => item.Title).ToArray());
        Assert.All(items, item => Assert.Equal("ok", item.Source));
        // 不该去拉没被意图选中的“动作片”(t=6)。
        Assert.DoesNotContain(requested, url => url.Contains("t=6", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SearchByIntentAsync_never_queries_hidden_adult_sources()
    {
        var requested = new List<string>();
        var handler = new StubHandler(request =>
        {
            requested.Add(request.RequestUri!.Host);
            return """{"code":1,"class":[],"list":[]}""";
        });
        var catalog = new CatalogService(new HttpClient(handler))
        {
            IsAdultSourceHidden = key => key == "adult",
        };
        VideoSource[] sources =
        [
            new("adult", "成人源", "http://adult.example.com/api.php/provide/vod", Adult: true),
            new("ok", "正常源", "http://ok.example.com/api.php/provide/vod"),
        ];

        await catalog.SearchByIntentAsync(sources, SearchIntents.Resolve("回南天")!);

        Assert.DoesNotContain("adult.example.com", requested);
        Assert.Contains("ok.example.com", requested);
    }

    private static CatalogItem Item(string id, string title) => new(id, title, "", "s", "源");

    private sealed class StubHandler(Func<HttpRequestMessage, string?> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = handle(request);
            var response = body is null
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
            return Task.FromResult(response);
        }
    }
}
