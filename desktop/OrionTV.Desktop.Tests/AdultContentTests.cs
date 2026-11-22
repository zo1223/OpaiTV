using System.Net;
using System.Text.Json;
using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.Tests;

/// <summary>成人内容(18+)开关、一键加源、AppleCMS XML 自动识别（对齐 RN 端 v1.3.23）。</summary>
public class AdultContentTests
{
    private static readonly VideoSource Normal = new("normal", "普通源", "https://normal.example/api.php/provide/vod");
    private static readonly VideoSource Adult = new("adult", "成人源", "https://adult.example/api.php/provide/vod", Adult: true);

    private const string VodJson = """
        { "list": [ { "vod_id": 7, "vod_name": "Movie", "vod_play_url": "第1集$https://video.example/movie.m3u8" } ] }
        """;

    // ---- 设置 ----

    [Fact]
    public void ShowAdultContent_DefaultsToOff_AndSurvivesOtherSettingsEdits()
    {
        Assert.False(new DesktopSettings().ShowAdultContent);

        var legacy = JsonSerializer.Deserialize<DesktopSettings>(
            """{ "SubscriptionUrl": "https://sub.example/config.json" }""",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.False(legacy.ShowAdultContent);

        var enabled = new DesktopSettings(ShowAdultContent: true);
        var saved = DesktopSettingsForm.ApplyEditableFields(enabled, "https://sub.example/b.json", "", "", autoSkipAds: false);
        Assert.True(saved.ShowAdultContent);
        Assert.True(JsonSerializer.Deserialize<DesktopSettings>(JsonSerializer.Serialize(saved))!.ShowAdultContent);
    }

    // ---- adult 标记 ----

    [Theory]
    [InlineData("\"adult\": true", true)]
    [InlineData("\"adult\": 1", true)]
    [InlineData("\"adult\": \"1\"", true)]
    [InlineData("\"adult\": \" TRUE \"", true)]
    [InlineData("\"is_adult\": true", true)]
    [InlineData("\"adult\": null, \"is_adult\": 1", true)]
    [InlineData("\"adult\": false", false)]
    [InlineData("\"adult\": 0", false)]
    [InlineData("\"adult\": \"false\"", false)]
    [InlineData("\"adult\": \"yes\"", false)]
    [InlineData("\"name\": \"x\"", false)]
    public void SubscriptionAndExtraSources_ReadAdultFlag(string field, bool expected)
    {
        var payload = $$"""
            { "api_site": { "s": { "name": "S", "api": "https://s.example/vod", {{field}} } } }
            """;
        Assert.Equal(expected, Assert.Single(VideoSourceSubscriptionParser.Parse(payload).Sources).Adult);

        var loose = $$"""
            [ { "key": "s", "name": "S", "api": "https://s.example/vod", {{field}} } ]
            """;
        Assert.Equal(expected, Assert.Single(ExtraVideoSourcesStore.ParseFlexible(loose)).Adult);
    }

    [Fact]
    public void ExtraSources_SerializeWritesAdultOnlyWhenMarked_AndRoundTrips()
    {
        var json = ExtraVideoSourcesStore.Serialize([Normal, Adult]);
        using var document = JsonDocument.Parse(json);
        var site = document.RootElement.GetProperty("api_site");
        Assert.False(site.GetProperty("normal").TryGetProperty("adult", out _));
        Assert.True(site.GetProperty("adult").GetProperty("adult").GetBoolean());

        var parsed = ExtraVideoSourcesStore.ParseFlexible(json);
        Assert.Equal([false, true], parsed.Select(source => source.Adult));
    }

    // ---- 过滤 ----

    [Fact]
    public void Filter_HidesAdultSourcesOnlyWhileSwitchIsOff()
    {
        IReadOnlyList<VideoSource> all = [Normal, Adult];

        Assert.Equal([Normal], AdultContentFilter.VisibleSources(all, showAdultContent: false));
        Assert.Same(all, AdultContentFilter.VisibleSources(all, showAdultContent: true));

        Assert.True(AdultContentFilter.IsHidden(all, false, "adult"));
        Assert.True(AdultContentFilter.IsHidden(all, false, "ADULT"));
        Assert.False(AdultContentFilter.IsHidden(all, false, "normal"));
        Assert.False(AdultContentFilter.IsHidden(all, false, "douban"));
        Assert.False(AdultContentFilter.IsHidden(all, false, null));
        Assert.False(AdultContentFilter.IsHidden(all, true, "adult"));

        Assert.Equal("adult", AdultContentFilter.HiddenSignature(all, false));
        Assert.Equal("", AdultContentFilter.HiddenSignature(all, true));
        Assert.Equal("", AdultContentFilter.HiddenSignature([Normal], false));
    }

    [Fact]
    public async Task Catalog_SearchHomeAndDetail_NeverTouchHiddenAdultSources()
    {
        var requests = new List<string>();
        using var http = new HttpClient(new StubHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            requests.Add(url);
            // 豆瓣不可用 -> 首页回退到各源站
            return request.RequestUri.Host.Contains("douban") ? null : url.Contains("ac=list") ? """{ "class": [] }""" : VodJson;
        }));
        var showAdultContent = false;
        IReadOnlyList<VideoSource> all = [Normal, Adult];
        var service = new CatalogService(http, new DoubanClient(http))
        {
            IsAdultSourceHidden = key => AdultContentFilter.IsHidden(all, showAdultContent, key),
        };

        // 即使调用方传入的是含成人源的旧快照，也不请求、不返回
        var search = await service.SearchAllAsync(all, "Movie");
        Assert.Equal(["normal"], search.SelectMany(item => item.CandidateSources!).Select(item => item.Source).Distinct());

        var home = await service.GetHomeDataAsync(all, "movie", "热门", 20, 0);
        Assert.All(home, item => Assert.Equal("normal", item.Source));
        Assert.NotEmpty(home);

        Assert.Empty(await service.GetPreferredDetailSourcesAsync(all, "adult", "7", "Movie"));
        Assert.All(await service.GetDetailSourcesAsync(all, "Movie"), item => Assert.Equal("normal", item.Source));

        var bare = new SearchResult("7", "Movie", "", [], "adult", "成人源", "");
        Assert.Same(bare, await service.HydrateEpisodesAsync(all, bare));

        Assert.DoesNotContain(requests, url => url.Contains("adult.example"));

        // 打开开关后恢复
        showAdultContent = true;
        var visible = await service.SearchAllAsync(all, "Movie");
        Assert.Contains("adult", visible.SelectMany(item => item.CandidateSources!).Select(item => item.Source));
        Assert.Single(await service.GetPreferredDetailSourcesAsync(all, "adult", "7", "Movie"));
    }

    [Fact]
    public async Task Catalog_DropsResultsWhenSwitchIsTurnedOffWhileRequestIsInFlight()
    {
        var showAdultContent = true;
        using var http = new HttpClient(new StubHandler(_ =>
        {
            showAdultContent = false;
            return VodJson;
        }));
        IReadOnlyList<VideoSource> all = [Adult];
        var service = new CatalogService(http, new DoubanClient(http))
        {
            IsAdultSourceHidden = key => AdultContentFilter.IsHidden(all, showAdultContent, key),
        };

        Assert.Empty(await service.SearchAllAsync(all, "Movie"));

        showAdultContent = true;
        Assert.Empty(await service.GetPreferredDetailSourcesAsync(all, "adult", "7", "Movie"));
    }

    [Fact]
    public void FavoritesAndHistory_HideAdultSourceEntriesWithoutDeletingThem()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"opaitv-adult-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var favorites = new FavoriteStore(Path.Combine(directory, "favorites.json"));
            favorites.Save("normal", "1", new FavoriteItem("", "普通片", "普通源", 1, "普通片", "2026"));
            favorites.Save("adult", "2", new FavoriteItem("", "成人片", "成人源", 1, "成人片", "2026"));

            Assert.Equal(["normal+1"], favorites.GetVisible(key => key == "adult").Keys);
            Assert.Equal(2, favorites.GetVisible(_ => false).Count);
            Assert.Equal(2, favorites.GetAll().Count);

            var recordsPath = Path.Combine(directory, "play_records.json");
            File.WriteAllText(recordsPath, JsonSerializer.Serialize(new Dictionary<string, PlayRecordItem>
            {
                ["normal+1"] = new("同名片", "普通源", "", 0, 1, 10, 100, "", 100),
                ["adult+2"] = new("同名片", "成人源", "", 0, 1, 20, 100, "", 200),
                ["adult+3"] = new("成人片", "成人源", "", 0, 1, 30, 100, "", 300),
            }));
            var records = new PlayRecordStore(recordsPath);

            // 归并之前过滤：同一部片在普通源的记录仍然保留
            var visible = Assert.Single(records.GetLatestByMedia(key => key == "adult"));
            Assert.Equal("normal", visible.Source);
            Assert.Equal(2, records.GetLatestByMedia().Count);
            Assert.Equal(3, records.GetAll().Count);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ---- 一键加源 ----

    [Theory]
    [InlineData("https://a.example/api.php/provide/vod/", "https://a.example/api.php/provide/vod")]
    [InlineData("  a.example/api.php/provide/vod  ", "https://a.example/api.php/provide/vod")]
    [InlineData("192.168.1.2/vod", "http://192.168.1.2/vod")]
    [InlineData("nas:8080/vod", "http://nas:8080/vod")]
    [InlineData("HTTP://localhost/vod", "HTTP://localhost/vod")]
    public void OneClick_NormalizesPastedUrl(string input, string expected) =>
        Assert.Equal(expected, OneClickVideoSources.NormalizeUrl(input));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("随手输入的一段文字")]
    [InlineData("ftp://a.example/vod")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://")]
    [InlineData("https://nodot/vod")]
    [InlineData("https://a.example/v od")]
    public void OneClick_RejectsAnythingThatIsNotAnHttpUrl(string input)
    {
        var error = Assert.Throws<InvalidOperationException>(() => OneClickVideoSources.AddSingle([], [], input, adult: false));
        Assert.Equal(OneClickVideoSources.InvalidUrlError, error.Message);
    }

    [Fact]
    public void OneClick_AddSingle_AddsOnceAndThenOnlyUpdatesTheAdultMark()
    {
        var first = OneClickVideoSources.AddSingle([], [], "https://A.example:8443/api.php/provide/vod/", adult: true);
        var source = Assert.Single(first.ExtraSources);
        Assert.Equal((1, 0, false), (first.Added, first.Updated, first.FromSubscription));
        Assert.Equal("custom_a.example_8443", source.Key);
        Assert.Equal("a.example:8443", source.Name);
        Assert.Equal("https://A.example:8443/api.php/provide/vod", source.Api);
        Assert.True(source.Adult);

        var again = OneClickVideoSources.AddSingle(first.ExtraSources, [], "https://A.example:8443/api.php/provide/vod", adult: true);
        Assert.Equal((0, 0), (again.Added, again.Updated));
        Assert.Single(again.ExtraSources);

        var unmarked = OneClickVideoSources.AddSingle(again.ExtraSources, [], "https://A.example:8443/api.php/provide/vod", adult: false);
        Assert.Equal((0, 1), (unmarked.Added, unmarked.Updated));
        Assert.False(Assert.Single(unmarked.ExtraSources).Adult);

        // 同主机的另一个接口：key 不冲突
        var second = OneClickVideoSources.AddSingle(unmarked.ExtraSources, [], "https://a.example:8443/other/vod", adult: false);
        Assert.Equal(["custom_a.example_8443", "custom_a.example_8443_2"], second.ExtraSources.Select(item => item.Key));

        Assert.Equal(["custom_a.example_8443_2"], OneClickVideoSources.Remove(second.ExtraSources, "CUSTOM_a.example_8443").Select(item => item.Key));
    }

    [Fact]
    public void OneClick_AddSingle_MarksAnExistingSubscriptionSourceThroughAnOverride()
    {
        IReadOnlyList<VideoSource> subscription = [Normal];

        var same = OneClickVideoSources.AddSingle([], subscription, Normal.Api, adult: false);
        Assert.Equal((0, 0), (same.Added, same.Updated));
        Assert.Empty(same.ExtraSources);

        var marked = OneClickVideoSources.AddSingle([], subscription, Normal.Api + "/", adult: true);
        Assert.Equal((0, 1), (marked.Added, marked.Updated));
        var merged = VideoSourceMerger.Merge(subscription, marked.ExtraSources);
        var source = Assert.Single(merged);
        Assert.Equal("normal", source.Key);
        Assert.True(source.Adult);
        Assert.Empty(AdultContentFilter.VisibleSources(merged, showAdultContent: false));
    }

    [Fact]
    public void OneClick_AddSubscription_ImportsEverySourceAndNeverClearsAnAdultMark()
    {
        IReadOnlyList<VideoSource> subscription = [Normal];
        IReadOnlyList<VideoSource> extras = [new("x", "X", "https://x.example/vod", Adult: true)];
        VideoSource[] incoming =
        [
            new("normal", "撞 key 的另一个源", "https://other.example/vod"),
            new("x2", "X 的重复", "https://x.example/vod/"),
            new("flagged", "订阅自带标记", "https://flagged.example/vod", Adult: true),
            new("plain", "普通", "https://plain.example/vod", Protocol: VideoSourceProtocols.MacCmsXml),
        ];

        var plain = OneClickVideoSources.AddSubscription(extras, subscription, incoming, adult: false);
        Assert.True(plain.FromSubscription);
        Assert.Equal((3, 0), (plain.Added, plain.Updated));
        Assert.Equal(["x", "normal_2", "flagged", "plain"], plain.ExtraSources.Select(item => item.Key));
        Assert.Equal([true, false, true, false], plain.ExtraSources.Select(item => item.Adult));
        Assert.Equal(VideoSourceProtocols.MacCmsXml, plain.ExtraSources[3].Protocol);
        // 撞 key 的源改了名，没有覆盖主订阅里的源
        Assert.Equal(Normal.Api, VideoSourceMerger.Merge(subscription, plain.ExtraSources).First(item => item.Key == "normal").Api);

        var allAdult = OneClickVideoSources.AddSubscription(extras, subscription, incoming, adult: true);
        Assert.All(allAdult.ExtraSources, item => Assert.True(item.Adult));
    }

    // ---- AppleCMS XML 自动识别 ----

    private const string ListXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <rss version="5.1">
          <list page="2" pagecount="35" pagesize="20" recordcount="689">
            <video><last>2026-11-20 10:00:00</last><id>101</id><tid>6</tid><name><![CDATA[第一部 & 续集]]></name><type>动作片</type><note><![CDATA[HD]]></note></video>
            <video><last>2026-11-19 10:00:00</last><id>102</id><tid>13</tid><name><![CDATA[第二部]]></name><type>国产剧</type><note><![CDATA[更新至10集]]></note></video>
          </list>
          <class><ty id="1">电影</ty><ty id="2">连续剧</ty><ty id="6">动作片</ty></class>
        </rss>
        """;

    private const string VideoListXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <rss version="5.1">
          <list page="1" pagecount="1" pagesize="20" recordcount="2">
            <video>
              <id>101</id><tid>6</tid><name><![CDATA[第一部 & 续集]]></name><type>动作片</type>
              <pic>https://img.example/101.jpg?a=1&amp;b=2</pic><year>2026</year><area>大陆</area>
              <actor><![CDATA[演员甲,演员乙]]></actor><director><![CDATA[导演甲]]></director>
              <dl><dd flag="m3u8"><![CDATA[正片$https://video.example/101/index.m3u8]]></dd></dl>
              <des><![CDATA[<p>简介</p>]]></des>
            </video>
            <video>
              <id>102</id><tid>13</tid><name><![CDATA[第二部]]></name><type>国产剧</type>
              <pic>https://img.example/102.jpg</pic><year>2025</year>
              <dl>
                <dd flag="yun"><![CDATA[第1集$https://yun.example/102/1.html#第2集$https://yun.example/102/2.html]]></dd>
                <dd flag="m3u8"><![CDATA[第1集$https://video.example/102/1.m3u8#第2集$https://video.example/102/2.m3u8]]></dd>
              </dl>
            </video>
          </list>
        </rss>
        """;

    private static readonly VideoSource XmlSource = new("xml", "XML 源", "https://xml.example/api.php/provide/vod");

    [Fact]
    public async Task AppleCms_AutoDetectsXml_AndFetchesPlayUrlsWithVideolist()
    {
        var requests = new List<string>();
        using var http = new HttpClient(new StubHandler(request =>
        {
            // AbsoluteUri：保留实际发出的转义形式
            var url = request.RequestUri!.AbsoluteUri;
            requests.Add(url);
            return url.Contains("ac=videolist") ? VideoListXml : ListXml;
        }));
        var client = new AppleCmsClient(http);

        var results = await client.SearchAsync(XmlSource, "第");

        Assert.Equal(
            [
                "https://xml.example/api.php/provide/vod?ac=detail&wd=%E7%AC%AC",
                "https://xml.example/api.php/provide/vod?ac=videolist&ids=101%2C102",
            ],
            requests);
        Assert.Equal(["101", "102"], results.Select(item => item.Id));
        Assert.Equal("第一部 & 续集", results[0].Title);
        Assert.Equal("https://img.example/101.jpg?a=1&b=2", results[0].Poster);
        Assert.Equal("2026", results[0].Year);
        Assert.Equal("动作片", results[0].TypeName);
        Assert.Equal(["https://video.example/101/index.m3u8"], results[0].Episodes);
        Assert.Equal(["yun", "m3u8"], results[1].PlaybackSources!.Select(line => line.Name));
        Assert.Equal(2, results[1].PlaybackSources![1].Episodes.Count);

        var categories = await client.GetCategoriesAsync(XmlSource);
        Assert.Equal([("1", "电影"), ("2", "连续剧"), ("6", "动作片")], categories.Select(item => (item.Id, item.Name)));

        var page = await client.GetCategoryVideosAsync(XmlSource, "6", 2, 20);
        Assert.Equal((689, 2, 35), (page.Total, page.Page, page.PageCount));
        Assert.Equal(2, page.Results.Count);
    }

    [Fact]
    public async Task AppleCms_XmlDetail_UsesTheRequestedIdNotTheFirstListEntry()
    {
        // 不认识 ac=detail 的老接口忽略 ids，返回最新列表
        using var http = new HttpClient(new StubHandler(request =>
            request.RequestUri!.ToString().Contains("ac=videolist") ? VideoListXml : ListXml));
        var client = new AppleCmsClient(http);

        var result = Assert.Single(await client.GetByIdAsync(XmlSource, "102"));
        Assert.Equal("102", result.Id);
        Assert.Equal("第二部", result.Title);
        Assert.NotEmpty(result.Episodes);

        var detail = await client.GetDetailAsync(XmlSource, "101");
        Assert.Equal("101", detail.Id);
        Assert.Equal("导演甲", detail.Director);
        Assert.Equal("<p>简介</p>", detail.Description);
    }

    [Fact]
    public async Task AppleCms_XmlList_StaysVisibleWhenVideolistFails()
    {
        using var http = new HttpClient(new StubHandler(request =>
            request.RequestUri!.ToString().Contains("ac=videolist") ? null : ListXml));

        var results = await new AppleCmsClient(http).SearchAsync(XmlSource, "第");

        Assert.Equal(["101", "102"], results.Select(item => item.Id));
        Assert.All(results, item => Assert.Empty(item.Episodes));
    }

    [Theory]
    [InlineData("<!DOCTYPE html><html><body>502 Bad Gateway</body></html>")]
    [InlineData("<html><body>blocked</body></html>")]
    [InlineData("<?xml version=\"1.0\"?><feed><entry/></feed>")]
    [InlineData("<rss><list><video>")]
    [InlineData("not json at all")]
    public async Task AppleCms_ResponsesThatAreNeitherJsonNorCmsXml_FailAsBefore(string body)
    {
        using var http = new HttpClient(new StubHandler(_ => body));
        var client = new AppleCmsClient(http);

        await Assert.ThrowsAnyAsync<JsonException>(() => client.SearchAsync(XmlSource, "x"));
        await Assert.ThrowsAnyAsync<JsonException>(() => client.GetCategoriesAsync(XmlSource));
    }

    /// <summary>返回 null 表示该请求失败（HTTP 500）。</summary>
    private sealed class StubHandler(Func<HttpRequestMessage, string?> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = handle(request);
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
