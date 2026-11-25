using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.Tests;

public class LocalRecommenderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 0, 0, 0, TimeSpan.Zero);
    private static long DaysAgo(int days) => Now.AddDays(-days).ToUnixTimeMilliseconds();

    private static RecommendationCandidate Candidate(string title, string? category = null, string id = "") =>
        new(new CatalogItem(id.Length > 0 ? id : title, title, "", "s1", "源一", "2024"), category);

    [Theory]
    [InlineData("庆余年 第二季", "庆余年")]
    [InlineData("庆余年第2季", "庆余年")]
    [InlineData("Stranger Things Season 4", "strangerthings")]
    [InlineData("流浪地球2", "流浪地球")]
    [InlineData("三体", "三体")]
    public void SeriesStem_StripsSeasonSuffixes(string title, string expected)
    {
        Assert.Equal(expected, LocalRecommender.SeriesStem(title));
    }

    [Fact]
    public void SplitCategory_SplitsOnCommonSeparators()
    {
        Assert.Equal(new[] { "科幻", "动作", "冒险", "剧情" }, LocalRecommender.SplitCategory("科幻,动作/冒险、剧情 科幻").ToArray());
        Assert.Empty(LocalRecommender.SplitCategory(null));
    }

    [Fact]
    public void Recommend_SameSeries_ExplainsWithWatchedTitle()
    {
        var watched = new[] { new RecommendationWatched("庆余年 第一季", "国产剧", DaysAgo(3)) };

        var result = LocalRecommender.Recommend(true, watched, [], [Candidate("庆余年 第二季", "国产剧"), Candidate("无关", "体育")], Now);

        var top = Assert.Single(result);
        Assert.Equal("庆余年 第二季", top.Item.Title);
        Assert.Equal("因为你看过《庆余年 第一季》", top.Reason);
        Assert.Equal(LocalRecommender.SeriesScore + 2, top.Score);
    }

    [Fact]
    public void Recommend_CategoryMatch_ExplainsWithCategoryAndRanksByWeight()
    {
        var watched = new[]
        {
            new RecommendationWatched("甲", "科幻", DaysAgo(2)),   // 近 30 天：权重 2
            new RecommendationWatched("乙", "科幻", DaysAgo(90)),  // 权重 1
            new RecommendationWatched("丙", "爱情", DaysAgo(90)),  // 权重 1
        };

        var result = LocalRecommender.Recommend(true, watched, [], [Candidate("爱情片", "爱情"), Candidate("科幻片", "科幻,冒险")], Now);

        Assert.Equal(new[] { "科幻片", "爱情片" }, result.Select(item => item.Item.Title).ToArray());
        Assert.Equal("因为你常看「科幻」", result[0].Reason);
        Assert.Equal(3, result[0].Score);
        Assert.Equal("因为你常看「爱情」", result[1].Reason);
    }

    [Fact]
    public void Recommend_ExcludesAlreadyWatchedAndZeroScore()
    {
        var watched = new[] { new RecommendationWatched("三体", "科幻", DaysAgo(1)) };

        var result = LocalRecommender.Recommend(true, watched, [], [Candidate("三 体", "科幻"), Candidate("别的", "综艺"), Candidate("没分类")], Now);

        Assert.Empty(result);
    }

    [Fact]
    public void Recommend_UnwatchedFavorite_AndFavoriteSeriesReason()
    {
        var watched = new[] { new RecommendationWatched("看过的收藏", null, DaysAgo(1)) };
        var favorites = new[]
        {
            new RecommendationFavorite("漫长的季节", "悬疑", "s1", "7", "p.jpg", "源一", "2023"),
            new RecommendationFavorite("看过的收藏", "悬疑", "s1", "8"),
        };

        var result = LocalRecommender.Recommend(true, watched, favorites, [Candidate("漫长的季节 第二季"), Candidate("悬疑新片", "悬疑")], Now);

        Assert.Equal(new[] { "漫长的季节 第二季", "漫长的季节", "悬疑新片" }, result.Select(item => item.Item.Title).ToArray());
        Assert.Equal("因为你收藏了《漫长的季节》", result[0].Reason);
        Assert.Equal("你收藏了还没看", result[1].Reason);
        Assert.Equal(LocalRecommender.UnwatchedFavoriteScore, result[1].Score);
        Assert.Equal(("s1", "7"), (result[1].Item.Source, result[1].Item.Id));
        // 两条收藏都是悬疑：权重 2 + 2
        Assert.Equal("因为你常看「悬疑」", result[2].Reason);
        Assert.Equal(4, result[2].Score);
    }

    [Fact]
    public void Recommend_DisabledOrNoSignals_ReturnsNothing()
    {
        var watched = new[] { new RecommendationWatched("甲", "科幻", DaysAgo(1)) };
        var candidates = new[] { Candidate("科幻片", "科幻") };

        Assert.Empty(LocalRecommender.Recommend(false, watched, [], candidates, Now));
        Assert.Empty(LocalRecommender.Recommend(true, [], [], candidates, Now));
        Assert.Empty(LocalRecommender.Recommend(true, watched, [], [], Now));
        Assert.False(LocalRecommender.HasSignals([], []));
        Assert.True(new DesktopSettings().PersonalizedRecommendations);
    }

    [Fact]
    public void Recommend_DedupesByTitleAndHonoursLimit()
    {
        var watched = new[] { new RecommendationWatched("甲", "科幻", DaysAgo(1)) };
        var candidates = new[] { Candidate("科幻一", "科幻", "1"), Candidate("科幻 一", "科幻", "2"), Candidate("科幻二", "科幻"), Candidate("科幻三", "科幻") };

        var result = LocalRecommender.Recommend(true, watched, [], candidates, Now, limit: 2);

        Assert.Equal(new[] { "科幻一", "科幻二" }, result.Select(item => item.Item.Title).ToArray());
        Assert.Equal("1", result[0].Item.Id);
    }

    [Fact]
    public void Pool_KeepsLatestPerTitleAndIsBounded()
    {
        var pool = new RecommendationPool();
        pool.Add([Candidate("甲").Item, Candidate("乙").Item], "热门");
        pool.Add([Candidate("甲").Item], "科幻");

        var snapshot = pool.Snapshot();
        Assert.Equal(2, snapshot.Count);
        Assert.Equal("科幻", snapshot[0].Category);

        pool.Add(Enumerable.Range(0, RecommendationPool.Capacity + 5).Select(i => Candidate($"片{i}").Item), null);
        Assert.Equal(RecommendationPool.Capacity, pool.Snapshot().Count);
        pool.Clear();
        Assert.Empty(pool.Snapshot());
    }
}
