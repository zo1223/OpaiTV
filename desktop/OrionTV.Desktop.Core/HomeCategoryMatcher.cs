namespace OrionTV.Desktop.Core;

public sealed record HomeCategoryMatch(VideoSourceCategory Category, string Candidate, bool UsedTypeFallback);

/// <summary>
/// 移植自安卓 services/videoSourceSubscription.ts 的首页分类匹配逻辑：
/// 把豆瓣式的 tag（热门/电视剧/电影...）映射到各 AppleCMS 源站自有的分类，并对返回条目做区域/类型过滤。
/// </summary>
public static class HomeCategoryMatcher
{
    private static readonly Dictionary<string, string[]> CategoryAliases = new()
    {
        ["热门"] = ["连续剧", "电视剧", "剧集"],
        ["最新"] = ["电影", "电影片"],
        ["可播放"] = ["可播放", "电影", "电影片"],
        ["国产剧"] = ["国产剧", "大陆剧", "内地剧"],
        ["美剧"] = ["欧美剧", "美国剧"],
        ["英剧"] = ["欧美剧", "英国剧"],
        ["韩剧"] = ["韩国剧", "韩剧"],
        ["日剧"] = ["日本剧", "日剧"],
        ["港剧"] = ["香港剧", "港剧"],
        ["日本动画"] = ["日本动漫", "日韩动漫", "日本动画"],
        ["动画"] = ["动漫", "动漫片", "动画片"],
        ["综艺"] = ["综艺", "综艺片"],
        ["纪录片"] = ["纪录片", "纪录", "纪录剧集"],
        ["动作"] = ["动作片", "动作"],
        ["喜剧"] = ["喜剧片", "喜剧"],
        ["爱情"] = ["爱情片", "爱情"],
        ["科幻"] = ["科幻片", "科幻"],
        ["悬疑"] = ["悬疑片", "悬疑"],
        ["恐怖"] = ["恐怖片", "恐怖"],
        ["文艺"] = ["文艺片", "文艺", "剧情片", "剧情", "电影", "电影片"],
        ["华语"] = ["华语", "华语电影", "国产电影", "大陆电影", "中国电影", "电影", "电影片"],
        ["欧美"] = ["欧美", "欧美电影", "美国电影", "英国电影", "电影", "电影片"],
        ["韩国"] = ["韩国电影", "韩国", "电影", "电影片"],
        ["日本"] = ["日本电影", "日本", "电影", "电影片"],
        ["经典"] = ["经典", "经典电影"],
        ["豆瓣高分"] = ["豆瓣高分", "高分电影"],
        ["冷门佳片"] = ["冷门佳片"],
        ["top250"] = ["top250", "豆瓣Top250", "豆瓣 Top250"],
    };

    private static readonly Dictionary<string, string> TagAliases = new()
    {
        ["hot"] = "热门",
        ["latest"] = "最新",
    };

    private static readonly Dictionary<string, string[]> AreaFilters = new()
    {
        ["国产剧"] = ["中国", "大陆", "内地"],
        ["美剧"] = ["美国"],
        ["英剧"] = ["英国"],
        ["韩剧"] = ["韩国", "韩"],
        ["日剧"] = ["日本", "日"],
        ["港剧"] = ["香港", "港"],
        ["日本动画"] = ["日本", "日"],
        ["华语"] = ["中国", "大陆", "内地", "香港", "台湾", "澳门", "华语"],
        ["欧美"] = ["欧美", "美国", "英国", "法国", "德国", "意大利", "西班牙", "加拿大", "澳大利亚", "欧洲"],
        ["韩国"] = ["韩国", "韩"],
        ["日本"] = ["日本", "日"],
    };

    private static readonly Dictionary<string, string[]> ClassFilters = new()
    {
        ["国产剧"] = ["国产", "大陆", "内地"],
        ["美剧"] = ["美剧", "美国"],
        ["英剧"] = ["英剧", "英国"],
        ["韩剧"] = ["韩剧", "韩国"],
        ["日剧"] = ["日剧", "日本"],
        ["港剧"] = ["港剧", "香港"],
        ["日本动画"] = ["日本动漫", "日韩动漫", "日本动画"],
        ["动画"] = ["动漫", "动画"],
        ["综艺"] = ["综艺"],
        ["纪录片"] = ["纪录片", "纪录"],
        ["动作"] = ["动作"],
        ["喜剧"] = ["喜剧"],
        ["爱情"] = ["爱情"],
        ["科幻"] = ["科幻"],
        ["悬疑"] = ["悬疑"],
        ["恐怖"] = ["恐怖"],
        ["文艺"] = ["文艺", "剧情"],
    };

    private static readonly HashSet<string> BroadTags = ["热门", "最新"];

    private static readonly string[] TvClearKeywords =
        ["电视剧", "连续剧", "剧集", "国产剧", "大陆剧", "内地剧", "欧美剧", "美国剧", "英国剧", "韩剧", "韩国剧", "日剧", "日本剧", "港剧", "香港剧", "美剧", "英剧", "纪录片"];

    private static readonly string[] MovieClearKeywords =
        ["电影", "电影片", "动作片", "喜剧片", "爱情片", "科幻片", "悬疑片", "恐怖片", "剧情片", "纪录片"];

    public static string NormalizeTag(string tag) => TagAliases.TryGetValue(tag, out var alias) ? alias : tag;

    public static bool IsBroadTag(string tag) => BroadTags.Contains(tag);

    private static string[] GetTypeFallback(string type) =>
        type == "movie" ? ["电影", "电影片"] : ["连续剧", "电视剧", "剧集"];

    private static bool ShouldUseTypeFallback(string tag) =>
        BroadTags.Contains(tag) || AreaFilters.ContainsKey(tag) || ClassFilters.ContainsKey(tag);

    private static (string[] Exact, string[] Fallback) GetCategoryCandidates(string type, string tag)
    {
        var normalizedTag = NormalizeTag(tag);
        var tagCandidates = CategoryAliases.TryGetValue(normalizedTag, out var aliases)
            ? aliases
            : CategoryAliases.TryGetValue(normalizedTag.ToLowerInvariant(), out var lowerAliases)
                ? lowerAliases
                : [normalizedTag];

        if (normalizedTag == "热门" && type == "movie")
        {
            return (["电影", "电影片"], []);
        }

        return (tagCandidates, ShouldUseTypeFallback(normalizedTag) ? GetTypeFallback(type) : []);
    }

    private static string NormalizeCategoryName(string value)
    {
        var trimmed = new string(value.Trim().ToLowerInvariant().Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (trimmed.Length > 0 && trimmed[^1] is '片' or '劇' or '剧')
        {
            trimmed = trimmed[..^1];
        }

        return trimmed;
    }

    private static bool HasAnyKeyword(string value, string[] keywords) => keywords.Any(value.Contains);

    private static bool IsClearlyTvCategory(string value) => HasAnyKeyword(value, TvClearKeywords);

    private static bool IsClearlyMovieCategory(string value) => HasAnyKeyword(value, MovieClearKeywords);

    private static bool IsCompatibleHomeCategory(string categoryName, string type) =>
        type == "movie"
            ? !IsClearlyTvCategory(categoryName)
            : !IsClearlyMovieCategory(categoryName) || IsClearlyTvCategory(categoryName);

    private static (VideoSourceCategory Category, string Candidate)? FindMatchingCategory(
        IReadOnlyList<VideoSourceCategory> categories,
        string[] candidates,
        string type)
    {
        foreach (var candidate in candidates)
        {
            var exactName = candidate.Trim().ToLowerInvariant();
            var matched = categories.FirstOrDefault(category =>
                category.Name.Trim().ToLowerInvariant() == exactName && IsCompatibleHomeCategory(category.Name, type));
            if (matched is not null)
            {
                return (matched, candidate);
            }
        }

        foreach (var candidate in candidates)
        {
            var normalizedName = NormalizeCategoryName(candidate);
            var matched = categories.FirstOrDefault(category =>
                NormalizeCategoryName(category.Name) == normalizedName && IsCompatibleHomeCategory(category.Name, type));
            if (matched is not null)
            {
                return (matched, candidate);
            }
        }

        return null;
    }

    public static HomeCategoryMatch? FindHomeCategory(IReadOnlyList<VideoSourceCategory> categories, string type, string tag)
    {
        var candidates = GetCategoryCandidates(type, tag);

        var exact = FindMatchingCategory(categories, candidates.Exact, type);
        if (exact is not null)
        {
            return new HomeCategoryMatch(exact.Value.Category, exact.Value.Candidate, false);
        }

        var fallback = FindMatchingCategory(categories, candidates.Fallback, type);
        if (fallback is not null)
        {
            return new HomeCategoryMatch(fallback.Value.Category, fallback.Value.Candidate, true);
        }

        return null;
    }

    private static bool IsGenericTypeCandidate(string candidate, string type)
    {
        var normalizedCandidate = NormalizeCategoryName(candidate);
        return GetTypeFallback(type).Any(fallback => NormalizeCategoryName(fallback) == normalizedCandidate);
    }

    private static string JoinMetadata(params string?[] values) =>
        string.Join(" ", values.Where(value => !string.IsNullOrWhiteSpace(value)));

    private static bool IsCompatibleHomeType(AppleCmsVodItem item, string type)
    {
        var metadata = JoinMetadata(item.TypeName, item.VodClass);
        if (string.IsNullOrEmpty(metadata))
        {
            return true;
        }

        return type == "movie"
            ? !IsClearlyTvCategory(metadata)
            : !IsClearlyMovieCategory(metadata) || IsClearlyTvCategory(metadata);
    }

    private static string GetClassMetadata(AppleCmsVodItem item) => JoinMetadata(item.VodClass, item.TypeName);

    private static bool MatchesClass(AppleCmsVodItem item, string tag, bool requireMetadata)
    {
        if (!ClassFilters.TryGetValue(tag, out var keywords))
        {
            return true;
        }

        var metadata = GetClassMetadata(item);
        if (string.IsNullOrEmpty(metadata))
        {
            return !requireMetadata;
        }

        return HasAnyKeyword(metadata, keywords);
    }

    private static bool MatchesArea(AppleCmsVodItem item, string tag, bool requireMetadata)
    {
        if (!AreaFilters.TryGetValue(tag, out var keywords))
        {
            return true;
        }

        if (!requireMetadata)
        {
            return true;
        }

        if (ClassFilters.TryGetValue(tag, out var classKeywords) && HasAnyKeyword(GetClassMetadata(item), classKeywords))
        {
            return true;
        }

        var area = item.VodArea?.Trim() ?? "";
        return !string.IsNullOrEmpty(area) && HasAnyKeyword(area, keywords);
    }

    public static bool ShouldIncludeHomeItem(AppleCmsVodItem item, string type, string tag, bool usedTypeFallback, string matchedCandidate)
    {
        var requireMetadata = usedTypeFallback || IsGenericTypeCandidate(matchedCandidate, type);
        return IsCompatibleHomeType(item, type) &&
               MatchesArea(item, tag, requireMetadata) &&
               MatchesClass(item, tag, requireMetadata);
    }
}
