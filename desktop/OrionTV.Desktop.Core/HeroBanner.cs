using System.Text.RegularExpressions;

namespace OrionTV.Desktop.Core;

/// <summary>
/// 首页大横幅的选片：纯本地计算，只从已经在首页显示过的条目里挑，不发任何请求。
/// 传进来的两份列表必须是已经过成人过滤 / 片源模式过滤之后的可见条目，这里不再做过滤。
/// </summary>
public static partial class HeroBanner
{
    public const int MaxItems = 5;

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>
    /// 个性化推荐在前，其后是首页已加载的数据；没有海报或没有标题的不要；按归一化标题去重；最多 <paramref name="max"/> 条。
    /// </summary>
    public static IReadOnlyList<CatalogItem> Pick(
        IReadOnlyList<CatalogItem>? recommendations,
        IReadOnlyList<CatalogItem>? homeItems,
        int max = MaxItems)
    {
        var picked = new List<CatalogItem>();
        if (max <= 0)
        {
            return picked;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in (recommendations ?? []).Concat(homeItems ?? []))
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Poster) || string.IsNullOrWhiteSpace(item.Title))
            {
                continue;
            }

            if (!seen.Add(KeyOf(item)))
            {
                continue;
            }

            picked.Add(item);
            if (picked.Count >= max)
            {
                break;
            }
        }

        return picked;
    }

    /// <summary>去重 / 定位用的键：归一化标题；归一化后为空时退回原标题。</summary>
    public static string KeyOf(CatalogItem item)
    {
        var key = CatalogTitleMatcher.Normalize(item.Title);
        return key.Length > 0 ? key : item.Title.Trim();
    }

    /// <summary>没有简介时显示的一行元信息：年份 · 评分 · 推荐理由 / 观看进度。</summary>
    public static string MetaLine(CatalogItem item)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(item.Year))
        {
            parts.Add(item.Year.Trim());
        }

        if (!string.IsNullOrWhiteSpace(item.Rate))
        {
            parts.Add($"★ {item.Rate.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(item.Reason))
        {
            parts.Add(item.Reason.Trim());
        }
        else if (item.Progress is > 0 and < 1)
        {
            parts.Add($"已观看 {(int)Math.Round(item.Progress.Value * 100)}%");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>从已经取到的候选播放源里找一段简介（首页条目自己不带简介）；没有返回 null。</summary>
    public static string? DescriptionOf(IEnumerable<SearchResult>? candidates)
    {
        if (candidates is null)
        {
            return null;
        }

        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate?.Description))
            {
                return Whitespace().Replace(candidate.Description.Trim(), " ");
            }
        }

        return null;
    }

    /// <summary>轮播下标：按步长前后移动并首尾相接；没有条目时返回 0。</summary>
    public static int Step(int current, int count, int delta)
    {
        if (count <= 0)
        {
            return 0;
        }

        var next = (current + delta) % count;
        return next < 0 ? next + count : next;
    }

    /// <summary>
    /// 列表换了以后该显示第几张：原来那张还在就停在它上面（切分类不回到第一张），不在了就夹到合法范围内。
    /// </summary>
    public static int IndexAfterUpdate(IReadOnlyList<CatalogItem> items, CatalogItem? current, int currentIndex)
    {
        if (items.Count == 0)
        {
            return 0;
        }

        if (current is not null)
        {
            var key = KeyOf(current);
            for (var i = 0; i < items.Count; i++)
            {
                if (KeyOf(items[i]) == key)
                {
                    return i;
                }
            }
        }

        return Math.Clamp(currentIndex, 0, items.Count - 1);
    }
}

/// <summary>
/// 详情页“相关推荐”：只用本机已有的候选池（首页加载过的条目），挑同系列或同分类的，不发任何请求。
/// 候选必须是已经过成人过滤 / 片源模式过滤之后的可见条目。
/// </summary>
public static class RelatedItems
{
    public const int DefaultLimit = 10;
    public const int SeriesScore = 10;
    public const int CategoryWordScore = 2;
    public const int SameKindScore = 1;

    public static IReadOnlyList<CatalogItem> Pick(
        string? title,
        string? category,
        IReadOnlyList<RecommendationCandidate>? candidates,
        int limit = DefaultLimit)
    {
        if (limit <= 0 || candidates is null || candidates.Count == 0)
        {
            return [];
        }

        var selfKey = CatalogTitleMatcher.Normalize(title ?? "");
        var selfStem = LocalRecommender.SeriesStem(title);
        var selfWords = LocalRecommender.SplitCategory(category);
        var selfKind = KindOf(selfWords);

        var scored = new List<(CatalogItem Item, int Score, int Order)>();
        var order = 0;
        foreach (var candidate in candidates)
        {
            var item = candidate.Item;
            var key = CatalogTitleMatcher.Normalize(item.Title);
            // 排除自己
            if (key.Length == 0 || key == selfKey)
            {
                continue;
            }

            var score = 0;
            if (selfStem.Length >= 2 && LocalRecommender.SeriesStem(item.Title) == selfStem)
            {
                score += SeriesScore;
            }

            var words = LocalRecommender.SplitCategory(candidate.Category);
            foreach (var word in words)
            {
                if (word.Length >= 2 && selfWords.Any(self => self.Length >= 2 && (self.Contains(word) || word.Contains(self))))
                {
                    score += CategoryWordScore;
                }
            }

            if (selfKind.Length > 0 && KindOf(words) == selfKind)
            {
                score += SameKindScore;
            }

            if (score > 0)
            {
                scored.Add((item, score, order++));
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        return scored
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.Order)
            .Where(entry => seen.Add(CatalogTitleMatcher.Normalize(entry.Item.Title)))
            .Take(limit)
            .Select(entry => entry.Item)
            .ToList();
    }

    /// <summary>粗分类：综艺 / 动漫 / 纪录 / 剧 / 电影；认不出来返回空串。</summary>
    public static string KindOf(IEnumerable<string> words)
    {
        var list = words as IReadOnlyCollection<string> ?? words.ToList();
        if (list.Any(word => word.Contains("综艺")))
        {
            return "variety";
        }

        if (list.Any(word => word.Contains("动漫") || word.Contains("动画") || word.Contains('番')))
        {
            return "anime";
        }

        if (list.Any(word => word.Contains("纪录") || word.Contains("记录")))
        {
            return "doc";
        }

        if (list.Any(word => word.Contains('剧')))
        {
            return "tv";
        }

        if (list.Any(word => word.Contains("电影") || word.EndsWith('片')))
        {
            return "movie";
        }

        return "";
    }
}
