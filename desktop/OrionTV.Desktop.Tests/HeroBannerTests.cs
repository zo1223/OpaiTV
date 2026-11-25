using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.Tests;

public class HeroBannerTests
{
    private static CatalogItem Item(
        string title,
        string poster = "https://img.example/p.jpg",
        string source = "s1",
        string? year = null,
        string? rate = null,
        string? reason = null,
        double? progress = null) =>
        new(title, title, poster, source, "源一", year, rate, progress, Reason: reason);

    private static RecommendationCandidate Candidate(string title, string? category) =>
        new(Item(title), category);

    [Fact]
    public void Pick_PutsRecommendationsFirstThenHomeItems_AndCapsAtFive()
    {
        var picked = HeroBanner.Pick(
            [Item("推荐一"), Item("推荐二")],
            [Item("首页一"), Item("首页二"), Item("首页三"), Item("首页四")]);

        Assert.Equal(["推荐一", "推荐二", "首页一", "首页二", "首页三"], picked.Select(item => item.Title));
        Assert.Equal(HeroBanner.MaxItems, picked.Count);
    }

    [Fact]
    public void Pick_SkipsItemsWithoutPosterOrTitle()
    {
        var picked = HeroBanner.Pick(
            [Item("没海报", poster: ""), Item("空白海报", poster: "  ")],
            [Item("  "), Item("有海报")]);

        Assert.Equal(["有海报"], picked.Select(item => item.Title));
    }

    [Fact]
    public void Pick_DeduplicatesByNormalizedTitle_KeepingTheRecommendation()
    {
        var picked = HeroBanner.Pick(
            [Item("流浪地球", reason: "因为你看过《流浪地球2》")],
            [Item(" 流浪地球 "), Item("三体")]);

        Assert.Equal(2, picked.Count);
        Assert.Equal("因为你看过《流浪地球2》", picked[0].Reason);
        Assert.Equal("三体", picked[1].Title);
    }

    [Fact]
    public void Pick_ReturnsEmptyForEmptyInputOrNonPositiveMax()
    {
        Assert.Empty(HeroBanner.Pick(null, null));
        Assert.Empty(HeroBanner.Pick([], []));
        Assert.Empty(HeroBanner.Pick([Item("推荐一")], [Item("首页一")], 0));
        Assert.Single(HeroBanner.Pick([Item("推荐一")], [Item("首页一")], 1));
    }

    [Fact]
    public void MetaLine_JoinsYearRateAndReason()
    {
        Assert.Equal("2024 · ★ 8.6 · 因为你常看「科幻」", HeroBanner.MetaLine(Item("A", year: "2024", rate: "8.6", reason: "因为你常看「科幻」")));
        Assert.Equal("2023 · 已观看 42%", HeroBanner.MetaLine(Item("A", year: "2023", progress: 0.42)));
        Assert.Equal("", HeroBanner.MetaLine(Item("A")));
        // 看完的（进度为 1）不显示进度
        Assert.Equal("", HeroBanner.MetaLine(Item("A", progress: 1)));
    }

    [Fact]
    public void DescriptionOf_TakesFirstNonEmptyDescription_AndCollapsesWhitespace()
    {
        SearchResult Source(string? description) =>
            new("1", "片名", "", [], "s1", "源一", "2024", Description: description);

        Assert.Equal("第一行 第二行", HeroBanner.DescriptionOf([Source(null), Source("  "), Source(" 第一行\n  第二行 "), Source("后面的")]));
        Assert.Null(HeroBanner.DescriptionOf([Source(null), Source("")]));
        Assert.Null(HeroBanner.DescriptionOf(null));
    }

    [Theory]
    [InlineData(0, 5, 1, 1)]
    [InlineData(4, 5, 1, 0)]
    [InlineData(0, 5, -1, 4)]
    [InlineData(2, 1, 1, 0)]
    [InlineData(3, 0, 1, 0)]
    public void Step_WrapsAround(int current, int count, int delta, int expected) =>
        Assert.Equal(expected, HeroBanner.Step(current, count, delta));

    [Fact]
    public void IndexAfterUpdate_StaysOnTheSameItem_OrClampsWhenItIsGone()
    {
        var items = new[] { Item("甲"), Item("乙"), Item("丙") };

        // 原来显示的那条还在：停在它的新位置上（切分类不回到第一张）
        Assert.Equal(2, HeroBanner.IndexAfterUpdate(items, Item("丙"), 0));
        // 不在了：下标夹到合法范围
        Assert.Equal(2, HeroBanner.IndexAfterUpdate(items, Item("丁"), 7));
        Assert.Equal(1, HeroBanner.IndexAfterUpdate(items, null, 1));
        Assert.Equal(0, HeroBanner.IndexAfterUpdate([], Item("甲"), 3));
    }

    [Fact]
    public void Related_SameSeriesRanksFirst_AndExcludesItself()
    {
        var related = RelatedItems.Pick(
            "庆余年",
            "国产剧",
            [
                Candidate("狂飙", "电视剧,国产剧"),
                Candidate("庆余年", "电视剧,国产剧"),
                Candidate("庆余年 第二季", "热门剧集,热门"),
            ]);

        Assert.Equal(["庆余年 第二季", "狂飙"], related.Select(item => item.Title));
    }

    [Fact]
    public void Related_MatchesCategoryWordsByContainment_AndDropsUnrelatedKinds()
    {
        var related = RelatedItems.Pick(
            "疾速追杀",
            "动作片",
            [
                Candidate("奔跑吧", "综艺,综艺"),
                Candidate("甄嬛传", "电视剧,国产剧"),
                Candidate("碟中谍", "电影,动作"),
                Candidate("泰坦尼克号", "电影,爱情"),
            ]);

        // “动作”命中分类词排前；同为电影的排后；综艺和电视剧不出现
        Assert.Equal(["碟中谍", "泰坦尼克号"], related.Select(item => item.Title));
    }

    [Fact]
    public void Related_RespectsLimit_AndDeduplicatesByTitle()
    {
        var candidates = Enumerable.Range(1, 15)
            .Select(i => Candidate($"剧{i:00}集锦", "电视剧,国产剧"))
            .Append(Candidate("剧01集锦", "电视剧,国产剧"))
            .ToList();

        var related = RelatedItems.Pick("狂飙", "国产剧", candidates);

        Assert.Equal(RelatedItems.DefaultLimit, related.Count);
        Assert.Equal(related.Count, related.Select(item => item.Title).Distinct().Count());
        Assert.Equal(3, RelatedItems.Pick("狂飙", "国产剧", candidates, 3).Count);
    }

    [Fact]
    public void Related_ReturnsEmptyWhenNothingIsRelated()
    {
        Assert.Empty(RelatedItems.Pick("狂飙", "国产剧", null));
        Assert.Empty(RelatedItems.Pick("狂飙", "国产剧", []));
        Assert.Empty(RelatedItems.Pick("狂飙", null, [Candidate("奔跑吧", "综艺,综艺")]));
        Assert.Empty(RelatedItems.Pick("狂飙", "国产剧", [Candidate("奔跑吧", "综艺,综艺")]));
        Assert.Empty(RelatedItems.Pick("狂飙", "国产剧", [Candidate("甄嬛传", "电视剧,国产剧")], 0));
    }

    [Theory]
    [InlineData("电视剧,美剧", "tv")]
    [InlineData("热门剧集,热门", "tv")]
    [InlineData("电影,科幻", "movie")]
    [InlineData("动作片", "movie")]
    [InlineData("日本动漫", "anime")]
    [InlineData("综艺,综艺", "variety")]
    [InlineData("纪录片", "doc")]
    [InlineData("热门", "")]
    public void Related_KindOf_MapsCategoryWordsToCoarseKinds(string category, string expected) =>
        Assert.Equal(expected, RelatedItems.KindOf(LocalRecommender.SplitCategory(category)));
}
