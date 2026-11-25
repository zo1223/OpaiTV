using System.Text.RegularExpressions;

namespace OrionTV.Desktop.Core;

/// <summary>推荐计算用的观看记录。</summary>
public sealed record RecommendationWatched(string Title, string? Category, long SaveTime);

/// <summary>推荐计算用的收藏。</summary>
public sealed record RecommendationFavorite(
    string Title, string? Category, string Source = "", string Id = "", string Poster = "", string SourceName = "", string? Year = null);

/// <summary>候选条目：首页已经加载过、留在本机内存里的条目。</summary>
public sealed record RecommendationCandidate(CatalogItem Item, string? Category);

/// <summary>一条推荐及其理由。</summary>
public sealed record Recommendation(CatalogItem Item, string Reason, int Score);

/// <summary>
/// 个性化推荐：纯本地计算，只读本机的观看历史、收藏和首页已加载的条目，不发任何网络请求、不上传观看记录
/// （与手机端 services/localRecommender.ts 同口径）。
/// </summary>
public static partial class LocalRecommender
{
    public const string Title = "为你推荐";
    public const string PrivacyNote = "仅在本机根据观看历史与收藏计算，不联网、不上传观看记录";
    public const int DefaultLimit = 20;
    public const int SeriesScore = 10;
    public const int UnwatchedFavoriteScore = 5;
    private static readonly TimeSpan RecentWindow = TimeSpan.FromDays(30);

    [GeneratedRegex(@"[,，/、|\s]+")]
    private static partial Regex CategorySplitter();

    [GeneratedRegex(@"(第[一二三四五六七八九十\d]+[季部期]|season\s*\d+|\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex SeriesSuffix();

    public static IReadOnlyList<string> SplitCategory(string? category) =>
        string.IsNullOrWhiteSpace(category)
            ? []
            : CategorySplitter().Split(category.Trim().ToLowerInvariant())
                .Where(word => word.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();

    /// <summary>系列词干：去空白转小写，去掉末尾的“第N季/部/期”、“season N”和数字。</summary>
    public static string SeriesStem(string? title)
    {
        var stem = (title ?? "").Trim().ToLowerInvariant();
        while (true)
        {
            var trimmed = SeriesSuffix().Replace(stem.TrimEnd(), "").TrimEnd();
            if (trimmed == stem)
            {
                break;
            }

            stem = trimmed;
        }

        return CatalogTitleMatcher.Normalize(stem);
    }

    /// <summary>有没有可用来推荐的本地数据（没有历史也没有收藏时不显示推荐栏）。</summary>
    public static bool HasSignals(
        IReadOnlyCollection<RecommendationWatched> watched,
        IReadOnlyCollection<RecommendationFavorite> favorites) =>
        watched.Count > 0 || favorites.Count > 0;

    public static IReadOnlyList<Recommendation> Recommend(
        bool enabled,
        IReadOnlyList<RecommendationWatched> watched,
        IReadOnlyList<RecommendationFavorite> favorites,
        IReadOnlyList<RecommendationCandidate> candidates,
        DateTimeOffset now,
        int limit = DefaultLimit)
    {
        if (!enabled || limit <= 0 || !HasSignals(watched, favorites))
        {
            return [];
        }

        // 1. 偏好画像：记录权重 1（30 天内再 +1），收藏权重 2
        var profile = new Dictionary<string, int>(StringComparer.Ordinal);
        var recentAfter = now.Subtract(RecentWindow).ToUnixTimeMilliseconds();
        foreach (var record in watched)
        {
            var weight = record.SaveTime >= recentAfter ? 2 : 1;
            foreach (var word in SplitCategory(record.Category))
            {
                profile[word] = profile.GetValueOrDefault(word) + weight;
            }
        }

        foreach (var favorite in favorites)
        {
            foreach (var word in SplitCategory(favorite.Category))
            {
                profile[word] = profile.GetValueOrDefault(word) + 2;
            }
        }

        // 2. 系列词干 → 原标题（看过的优先于收藏的）
        var watchedTitles = new HashSet<string>(
            watched.Select(record => CatalogTitleMatcher.Normalize(record.Title)).Where(title => title.Length > 0),
            StringComparer.Ordinal);
        var watchedStems = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var record in watched)
        {
            var stem = SeriesStem(record.Title);
            if (stem.Length >= 2)
            {
                watchedStems.TryAdd(stem, record.Title);
            }
        }

        var favoriteStems = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var favorite in favorites)
        {
            var stem = SeriesStem(favorite.Title);
            if (stem.Length >= 2)
            {
                favoriteStems.TryAdd(stem, favorite.Title);
            }
        }

        var scored = new List<Recommendation>();

        // 5. 收藏了但没有观看记录
        foreach (var favorite in favorites)
        {
            var normalized = CatalogTitleMatcher.Normalize(favorite.Title);
            if (normalized.Length == 0 || watchedTitles.Contains(normalized) || string.IsNullOrEmpty(favorite.Id))
            {
                continue;
            }

            scored.Add(new Recommendation(
                new CatalogItem(favorite.Id, favorite.Title, favorite.Poster, favorite.Source, favorite.SourceName, favorite.Year),
                "你收藏了还没看",
                UnwatchedFavoriteScore));
        }

        // 3 + 4. 候选池
        foreach (var candidate in candidates)
        {
            var normalized = CatalogTitleMatcher.Normalize(candidate.Item.Title);
            if (normalized.Length == 0 || watchedTitles.Contains(normalized))
            {
                continue;
            }

            var score = 0;
            var reason = "";
            var stem = SeriesStem(candidate.Item.Title);
            if (stem.Length >= 2)
            {
                if (watchedStems.TryGetValue(stem, out var watchedTitle))
                {
                    score += SeriesScore;
                    reason = $"因为你看过《{watchedTitle}》";
                }
                else if (favoriteStems.TryGetValue(stem, out var favoriteTitle)
                         && CatalogTitleMatcher.Normalize(favoriteTitle) != normalized)
                {
                    score += SeriesScore;
                    reason = $"因为你收藏了《{favoriteTitle}》";
                }
            }

            var bestWord = "";
            var bestWeight = 0;
            foreach (var word in SplitCategory(candidate.Category))
            {
                if (profile.TryGetValue(word, out var weight) && weight > bestWeight)
                {
                    bestWeight = weight;
                    bestWord = word;
                }
            }

            if (bestWeight > 0)
            {
                score += bestWeight;
                if (reason.Length == 0 || bestWeight > SeriesScore)
                {
                    reason = $"因为你常看「{bestWord}」";
                }
            }

            if (score > 0)
            {
                scored.Add(new Recommendation(candidate.Item, reason, score));
            }
        }

        // 6. 得分降序（稳定排序），按归一化标题去重
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return scored
            .OrderByDescending(item => item.Score)
            .Where(item => seen.Add(CatalogTitleMatcher.Normalize(item.Item.Title)))
            .Take(limit)
            .ToList();
    }
}

/// <summary>
/// 推荐候选池：首页加载过的条目留在内存里（不落盘、不联网），供“为你推荐”挑选。
/// </summary>
public sealed class RecommendationPool
{
    public const int Capacity = 600;
    private readonly object _sync = new();
    private readonly Dictionary<string, RecommendationCandidate> _items = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();

    public void Add(IEnumerable<CatalogItem> items, string? category)
    {
        lock (_sync)
        {
            foreach (var item in items)
            {
                var key = CatalogTitleMatcher.Normalize(item.Title);
                if (key.Length == 0)
                {
                    continue;
                }

                if (!_items.ContainsKey(key))
                {
                    _order.Enqueue(key);
                }

                _items[key] = new RecommendationCandidate(item, category);
                while (_order.Count > Capacity)
                {
                    _items.Remove(_order.Dequeue());
                }
            }
        }
    }

    public IReadOnlyList<RecommendationCandidate> Snapshot()
    {
        lock (_sync)
        {
            return _order.Where(_items.ContainsKey).Select(key => _items[key]).ToList();
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _items.Clear();
            _order.Clear();
        }
    }
}
