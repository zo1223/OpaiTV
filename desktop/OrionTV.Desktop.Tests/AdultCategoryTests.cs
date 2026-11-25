using System.Net;
using System.Text.Json;
using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.Tests;

/// <summary>
/// 成人内容(18+)按分类过滤：源本身没有 adult 标记，但分类表里带成人分类
/// （伦理片 / 港台三级 / 里番动漫 / 福利 / 写真热舞 / 擦边短剧 / 两性课堂…）。
/// </summary>
public class AdultCategoryTests
{
    private static readonly VideoSource Source = new("site", "综合源", "https://site.example/api.php/provide/vod");

    // ---- 词表 ----

    [Theory]
    // CineFlow 11 个内置源分类表里实际出现的成人分类
    [InlineData("伦理片")]
    [InlineData("伦理")]
    [InlineData("港台三级")]
    [InlineData("韩国伦理")]
    [InlineData("西方伦理")]
    [InlineData("日本伦理")]
    [InlineData("里番动漫")]
    [InlineData("福利")]
    [InlineData("写真热舞")]
    [InlineData("擦边短剧")]
    [InlineData("擦边剧")]
    [InlineData("两性课堂")]
    // 常见变体
    [InlineData("理论片")]
    [InlineData("三级片")]
    [InlineData("福利片")]
    [InlineData("福利视频")]
    [InlineData("情色片")]
    [InlineData("成人动漫")]
    [InlineData("肉番")]
    [InlineData("18禁")]
    // 繁体、全角、空白与标点
    [InlineData("倫理片")]
    [InlineData("港台三級")]
    [InlineData("裏番動漫")]
    [InlineData("寫真熱舞")]
    [InlineData("擦邊短劇")]
    [InlineData("兩性課堂")]
    [InlineData(" 伦 理 片 ")]
    [InlineData("伦理·片")]
    [InlineData("１８＋")]
    // 英文大小写
    [InlineData("AV")]
    [InlineData("国产AV")]
    [InlineData("Adult")]
    [InlineData("HENTAI")]
    [InlineData("18+")]
    [InlineData("R18")]
    [InlineData("r-18")]
    // 多标签字段里只要有一个成人标签
    [InlineData("剧情,伦理")]
    [InlineData("剧情 / 爱情 / 情色")]
    public void IsAdult_HitsAdultCategories(string name) => Assert.True(AdultCategories.IsAdult(name));

    [Theory]
    [InlineData("剧情片")]
    [InlineData("爱情片")]
    [InlineData("动作片")]
    [InlineData("喜剧片")]
    [InlineData("恐怖片")]
    [InlineData("科幻片")]
    [InlineData("战争片")]
    [InlineData("纪录片")]
    [InlineData("国产剧")]
    [InlineData("韩国剧")]
    [InlineData("港台剧")]
    [InlineData("日本动漫")]
    [InlineData("国产动漫")]
    [InlineData("动漫电影")]
    [InlineData("综艺")]
    [InlineData("短剧")]
    [InlineData("体育赛事")]
    [InlineData("电影解说")]
    [InlineData("剧情,爱情,家庭")]
    // 含关键词但属于正常分类的固定说法
    [InlineData("家庭伦理")]
    [InlineData("家庭伦理剧")]
    [InlineData("伦理学")]
    [InlineData("福利院")]
    [InlineData("成人教育")]
    // 英文关键词必须是独立的词
    [InlineData("Avatar")]
    [InlineData("Marvel")]
    [InlineData("AVC 4K")]
    [InlineData("Essex")]
    [InlineData("2018")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void IsAdult_LeavesNormalCategoriesAlone(string? name) => Assert.False(AdultCategories.IsAdult(name));

    [Fact]
    public void IsAdult_AcceptsSeveralFields_AndTheSwitchDecidesWhetherToHide()
    {
        Assert.True(AdultCategories.IsAdult("剧情片", "伦理"));
        Assert.True(AdultCategories.IsAdult(null, "福利"));
        Assert.False(AdultCategories.IsAdult(null, "", "剧情片"));
        Assert.False(AdultCategories.IsAdult());

        Assert.True(AdultContentFilter.IsCategoryHidden(showAdultContent: false, "伦理片"));
        Assert.False(AdultContentFilter.IsCategoryHidden(showAdultContent: true, "伦理片"));
        Assert.False(AdultContentFilter.IsCategoryHidden(showAdultContent: false, "剧情片"));
        Assert.False(AdultContentFilter.IsCategoryHidden(showAdultContent: false, null, null));

        var result = new SearchResult("1", "片", "", [], "site", "综合源", "", TypeName: "剧情片", Class: "剧情,伦理");
        Assert.Equal("剧情片,剧情,伦理", AdultContentFilter.CategoryOf(result));
        Assert.Null(AdultContentFilter.CategoryOf(result with { TypeName = " ", Class = null }));
        Assert.Null(AdultContentFilter.CategoryOf(null));
    }

    // ---- 取数入口 ----

    private static string Vod(int id, string name, string typeName, string? vodClass = null) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["vod_id"] = id,
            ["vod_name"] = name,
            ["vod_pic"] = "",
            ["type_name"] = typeName,
            ["vod_class"] = vodClass,
            ["vod_play_from"] = "m3u8",
            ["vod_play_url"] = $"第1集$https://video.example/{id}.m3u8",
        });

    private static string ListOf(params string[] items) =>
        $$"""{ "code": 1, "page": 1, "pagecount": 1, "total": {{items.Length}}, "list": [ {{string.Join(',', items)}} ] }""";

    // 一个没有 adult 标记的综合源：正常片、成人分类片、片名带“伦理”的正常片、题材标签带成人标签的片
    private static readonly string MixedList = ListOf(
        Vod(1, "普通电影", "剧情片"),
        Vod(2, "成人分类片", "伦理片"),
        Vod(3, "伦理学的诞生", "纪录片"),
        Vod(4, "福利院的孩子", "爱情片", "剧情,爱情"),
        Vod(5, "标签成人片", "剧情片", "剧情,情色"),
        Vod(6, "里番", "里番动漫"),
        Vod(7, "港台片", "港台三级"));

    private static CatalogService Catalog(Func<bool> showAdultContent, Func<HttpRequestMessage, string?> handle) =>
        new(new HttpClient(new StubHandler(handle)), null)
        {
            IsAdultSourceHidden = _ => false,
            ShowAdultContent = showAdultContent,
        };

    [Fact]
    public async Task Search_HidesAdultCategoryItemsOnlyWhileSwitchIsOff_AndTheSwitchAppliesImmediately()
    {
        var showAdultContent = false;
        var service = Catalog(() => showAdultContent, _ => MixedList);
        IReadOnlyList<VideoSource> sources = [Source];

        // 关闭：成人分类 / 成人题材标签的条目不出现；片名带“伦理”“福利”的正常分类条目保留
        var off = await service.SearchAllAsync(sources, "片");
        Assert.Equal(["普通电影", "伦理学的诞生", "福利院的孩子"], off.Select(item => item.Title));

        // 打开：同一个 service 实例，不重建、不重启，全部正常显示
        showAdultContent = true;
        var on = await service.SearchAllAsync(sources, "片");
        Assert.Equal(7, on.Count);
        Assert.Contains("成人分类片", on.Select(item => item.Title));

        // 再关：立即生效
        showAdultContent = false;
        Assert.Equal(3, (await service.SearchAllAsync(sources, "片")).Count);
    }

    [Fact]
    public async Task Search_WithoutASwitchProvider_DoesNotFilterByCategory()
    {
        using var http = new HttpClient(new StubHandler(_ => MixedList));
        var service = new CatalogService(http);

        Assert.Equal(7, (await service.SearchAllAsync([Source], "片")).Count);
    }

    [Fact]
    public async Task Search_DropsAdultCategoryItemsWhenSwitchIsTurnedOffWhileRequestIsInFlight()
    {
        var showAdultContent = true;
        var service = Catalog(() => showAdultContent, _ =>
        {
            showAdultContent = false;
            return MixedList;
        });

        var results = await service.SearchAllAsync([Source], "片");
        Assert.Equal(["普通电影", "伦理学的诞生", "福利院的孩子"], results.Select(item => item.Title));
    }

    [Fact]
    public async Task HomeFallback_HidesAdultCategoryItemsOnlyWhileSwitchIsOff()
    {
        var showAdultContent = false;
        var service = Catalog(() => showAdultContent, request =>
        {
            var url = request.RequestUri!.ToString();
            // 豆瓣不可用 -> 首页回退到源站
            return request.RequestUri.Host.Contains("douban") ? null : url.Contains("ac=list") ? """{ "class": [] }""" : MixedList;
        });

        var off = await service.GetHomeDataAsync([Source], "movie", "热门", 20, 0);
        Assert.NotEmpty(off);
        Assert.DoesNotContain(off, item => item.Title is "成人分类片" or "标签成人片" or "里番" or "港台片");

        showAdultContent = true;
        var on = await service.GetHomeDataAsync([Source], "movie", "热门", 20, 0);
        Assert.Contains(on, item => item.Title == "成人分类片");
        Assert.True(on.Count > off.Count);
    }

    [Fact]
    public async Task Detail_NeverReturnsAdultCategoryItemsWhileSwitchIsOff()
    {
        var showAdultContent = false;
        var service = Catalog(() => showAdultContent, request =>
            request.RequestUri!.Query.Contains("ids=2") ? ListOf(Vod(2, "成人分类片", "伦理片"))
            : request.RequestUri.Query.Contains("ids=1") ? ListOf(Vod(1, "普通电影", "剧情片"))
            : MixedList);
        IReadOnlyList<VideoSource> sources = [Source];

        // 收藏 / 历史里的旧条目按 source+id 点进来也一样不返回
        Assert.Empty(await service.GetPreferredDetailSourcesAsync(sources, "site", "2", "成人分类片"));
        Assert.Single(await service.GetPreferredDetailSourcesAsync(sources, "site", "1", "普通电影"));
        Assert.Empty(await service.GetDetailSourcesAsync(sources, "成人分类片"));
        Assert.Single(await service.GetDetailSourcesAsync(sources, "普通电影"));
        // 片名带“伦理”的正常分类条目照常
        Assert.Single(await service.GetDetailSourcesAsync(sources, "伦理学的诞生"));

        // 补剧集：成人分类的条目不补（保持没有剧集，无法起播）
        var bare = new SearchResult("2", "成人分类片", "", [], "site", "综合源", "");
        Assert.Same(bare, await service.HydrateEpisodesAsync(sources, bare));

        Assert.True(service.IsResultHidden(bare with { TypeName = "伦理片" }));
        Assert.False(service.IsResultHidden(bare with { TypeName = "剧情片" }));

        showAdultContent = true;
        Assert.Single(await service.GetPreferredDetailSourcesAsync(sources, "site", "2", "成人分类片"));
        Assert.Single(await service.GetDetailSourcesAsync(sources, "成人分类片"));
        Assert.NotEmpty((await service.HydrateEpisodesAsync(sources, bare))!.Episodes);
        Assert.False(service.IsResultHidden(bare with { TypeName = "伦理片" }));
    }

    [Fact]
    public async Task IntentSearch_SkipsAdultCategoriesOfTheSourceWhileSwitchIsOff()
    {
        var requested = new List<string>();
        var showAdultContent = false;
        var service = Catalog(() => showAdultContent, request =>
        {
            var url = request.RequestUri!.ToString();
            requested.Add(url);
            if (url.Contains("ac=list", StringComparison.Ordinal))
            {
                // “伦理剧情”会被“剧情”题材匹配到，但它是成人分类
                return """{"code":1,"class":[{"type_id":6,"type_name":"伦理剧情"},{"type_id":7,"type_name":"剧情片"}],"list":[]}""";
            }

            return url.Contains("t=6", StringComparison.Ordinal)
                ? ListOf(Vod(60, "成人分类片", "伦理剧情"))
                : ListOf(Vod(70, "剧情佳作", "剧情片"), Vod(71, "混进来的", "伦理片"));
        });
        var intent = SearchIntents.Resolve("回南天")!;

        var off = await service.SearchByIntentAsync([Source], intent);
        Assert.Equal(["剧情佳作"], off.Select(item => item.Title));
        Assert.DoesNotContain(requested, url => url.Contains("t=6", StringComparison.Ordinal));

        showAdultContent = true;
        var on = await service.SearchByIntentAsync([Source], intent);
        // 打开后“伦理剧情”正常参与题材匹配（它排在“剧情片”前面，被“剧情”选中）
        Assert.Equal(["成人分类片"], on.Select(item => item.Title));
        Assert.Contains(requested, url => url.Contains("t=6", StringComparison.Ordinal));
    }

    // ---- 收藏 / 接着看 ----

    [Fact]
    public void FavoritesAndHistory_HideAdultCategoryEntriesWithoutDeletingThem()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"opaitv-adult-category-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var showAdultContent = false;
            bool IsCategoryHidden(string? category) => AdultContentFilter.IsCategoryHidden(showAdultContent, category);

            var favorites = new FavoriteStore(Path.Combine(directory, "favorites.json"));
            favorites.Save("site", "1", new FavoriteItem("", "普通电影", "综合源", 1, "普通电影", "2026", Category: "剧情片"));
            favorites.Save("site", "2", new FavoriteItem("", "成人分类片", "综合源", 1, "成人分类片", "2026", Category: "伦理片"));
            favorites.Save("site", "3", new FavoriteItem("", "伦理学的诞生", "综合源", 1, "伦理学的诞生", "2026", Category: "纪录片"));
            favorites.Save("site", "5", new FavoriteItem("", "标签成人片", "综合源", 1, "标签成人片", "2026", Category: "剧情片,剧情,情色"));
            // 旧版本保存的收藏没有分类信息：无法判断，保持显示
            favorites.Save("site", "9", new FavoriteItem("", "旧收藏", "综合源", 1, "旧收藏", "2026"));

            Assert.Equal(
                ["site+1", "site+3", "site+9"],
                favorites.GetVisible(_ => false, IsCategoryHidden).Keys.OrderBy(key => key, StringComparer.Ordinal));
            Assert.Equal(5, favorites.GetAll().Count);

            // 旧收藏再次打开时补上分类，之后同样按分类过滤；已有分类的不覆盖，不存在的收藏不新建
            favorites.BackfillCategory("site", "9", "福利");
            favorites.BackfillCategory("site", "1", "伦理片");
            favorites.BackfillCategory("site", "404", "伦理片");
            favorites.BackfillCategory("site", "3", null);
            Assert.Equal(
                ["site+1", "site+3"],
                favorites.GetVisible(_ => false, IsCategoryHidden).Keys.OrderBy(key => key, StringComparer.Ordinal));
            Assert.Equal(5, favorites.GetAll().Count);
            // 分类信息落盘后能读回来
            Assert.Equal("伦理片", new FavoriteStore(Path.Combine(directory, "favorites.json")).GetAll()["site+2"].Category);

            var recordsPath = Path.Combine(directory, "play_records.json");
            File.WriteAllText(recordsPath, JsonSerializer.Serialize(new Dictionary<string, PlayRecordItem>
            {
                ["site+1"] = new("普通电影", "综合源", "", 0, 1, 10, 100, "", 100, Category: "剧情片"),
                ["site+2"] = new("成人分类片", "综合源", "", 0, 1, 20, 100, "", 200, Category: "港台三级"),
                ["site+3"] = new("伦理学的诞生", "综合源", "", 0, 1, 30, 100, "", 300, Category: "纪录片"),
                ["site+9"] = new("旧记录", "综合源", "", 0, 1, 40, 100, "", 400),
            }));
            var records = new PlayRecordStore(recordsPath);

            Assert.Equal(
                ["旧记录", "伦理学的诞生", "普通电影"],
                records.GetLatestByMedia(_ => false, IsCategoryHidden).Select(entry => entry.Record.Title));
            Assert.Equal(4, records.GetAll().Count);

            // 打开开关：立即全部恢复，数据没有被删
            showAdultContent = true;
            Assert.Equal(5, favorites.GetVisible(_ => false, IsCategoryHidden).Count);
            Assert.Equal(4, records.GetLatestByMedia(_ => false, IsCategoryHidden).Count);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LegacyFavoritesAndRecords_WithoutCategory_StillDeserialize()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var favorite = JsonSerializer.Deserialize<FavoriteItem>(
            """{ "Cover": "", "Title": "旧收藏", "SourceName": "综合源", "TotalEpisodes": 1, "SearchTitle": "旧收藏", "Year": "", "SaveTime": 5 }""",
            options)!;
        Assert.Null(favorite.Category);
        Assert.Equal(5, favorite.SaveTime);

        var record = JsonSerializer.Deserialize<PlayRecordItem>(
            """{ "Title": "旧记录", "SourceName": "综合源", "Cover": "", "Index": 0, "TotalEpisodes": 1, "PlayTime": 1, "TotalTime": 2, "Year": "" }""",
            options)!;
        Assert.Null(record.Category);
    }

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
