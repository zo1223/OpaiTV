using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.Tests;

public class HomeCategoryMatcherTests
{
    private static AppleCmsVodItem Item(string? typeName = null, string? vodClass = null, string? area = null) =>
        new() { VodName = "x", TypeName = typeName, VodClass = vodClass, VodArea = area };

    [Theory]
    [InlineData("hot", "热门")]
    [InlineData("latest", "最新")]
    [InlineData("综艺", "综艺")]
    public void NormalizeTag_maps_aliases(string input, string expected)
    {
        Assert.Equal(expected, HomeCategoryMatcher.NormalizeTag(input));
    }

    [Theory]
    [InlineData("热门", true)]
    [InlineData("最新", true)]
    [InlineData("综艺", false)]
    public void IsBroadTag_only_hot_and_latest(string tag, bool expected)
    {
        Assert.Equal(expected, HomeCategoryMatcher.IsBroadTag(tag));
    }

    [Fact]
    public void FindHomeCategory_movie_hot_matches_movie_category()
    {
        // 热门 + movie 走特例：候选为 ["电影","电影片"]
        var categories = new List<VideoSourceCategory>
        {
            new("1", "电影"),
            new("2", "电视剧"),
        };

        var match = HomeCategoryMatcher.FindHomeCategory(categories, "movie", "热门");

        Assert.NotNull(match);
        Assert.Equal("1", match!.Category.Id);
        Assert.False(match.UsedTypeFallback);
    }

    [Fact]
    public void FindHomeCategory_tv_guoChanJu_matches_alias()
    {
        var categories = new List<VideoSourceCategory>
        {
            new("10", "国产剧"),
            new("11", "电影"),
        };

        var match = HomeCategoryMatcher.FindHomeCategory(categories, "tv", "国产剧");

        Assert.NotNull(match);
        Assert.Equal("10", match!.Category.Id);
    }

    [Fact]
    public void FindHomeCategory_returns_null_when_no_compatible_category()
    {
        var categories = new List<VideoSourceCategory> { new("1", "图片") };
        var match = HomeCategoryMatcher.FindHomeCategory(categories, "tv", "韩剧");
        Assert.Null(match);
    }

    [Fact]
    public void ShouldIncludeHomeItem_excludes_movie_item_for_tv_type()
    {
        // tv 类型下，明显的电影条目应被排除
        var include = HomeCategoryMatcher.ShouldIncludeHomeItem(Item(typeName: "动作片"), "tv", "热门", usedTypeFallback: true, matchedCandidate: "电视剧");
        Assert.False(include);
    }

    [Fact]
    public void ShouldIncludeHomeItem_keeps_matching_tv_item()
    {
        var include = HomeCategoryMatcher.ShouldIncludeHomeItem(Item(typeName: "连续剧"), "tv", "热门", usedTypeFallback: false, matchedCandidate: "连续剧");
        Assert.True(include);
    }
}
