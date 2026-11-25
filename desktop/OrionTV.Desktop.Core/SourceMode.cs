namespace OrionTV.Desktop.Core;

/// <summary>片源模式：每个片源单独设置，存在本地设置里，切换立即生效。</summary>
public enum SourceMode
{
    /// <summary>全量（默认）：不做任何额外过滤。</summary>
    Full,
    /// <summary>稳定：该源每条结果只保留一条最稳的线路。</summary>
    Stable,
    /// <summary>影视：该源只保留影视类条目。</summary>
    Media,
    /// <summary>关闭：该源不参与搜索与首页。</summary>
    Off,
}

/// <summary>片源模式的解析、持久化与过滤规则（与手机端 services/sourceMode.ts 同口径）。</summary>
public static class SourceModes
{
    public const string FullValue = "full";
    public const string StableValue = "stable";
    public const string MediaValue = "media";
    public const string OffValue = "off";

    /// <summary>设置页下拉框的顺序。</summary>
    public static IReadOnlyList<SourceMode> All { get; } =
        [SourceMode.Stable, SourceMode.Media, SourceMode.Full, SourceMode.Off];

    // 先看排除词：命中即不是影视
    private static readonly string[] NonMediaWords =
    [
        "短剧", "解说", "预告", "花絮", "体育", "赛事", "足球", "篮球", "新闻", "资讯", "音乐", "mv",
        "演唱会", "游戏", "电竞", "教程", "课程", "直播", "写真", "福利",
    ];

    // 再看包含词：命中即影视
    private static readonly string[] MediaWords =
    [
        "电影", "片", "剧", "动漫", "动画", "番", "综艺", "纪录", "记录",
    ];

    /// <summary>未设置或非法值一律按全量处理。</summary>
    public static SourceMode Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        StableValue => SourceMode.Stable,
        MediaValue => SourceMode.Media,
        OffValue => SourceMode.Off,
        _ => SourceMode.Full,
    };

    public static string ToValue(SourceMode mode) => mode switch
    {
        SourceMode.Stable => StableValue,
        SourceMode.Media => MediaValue,
        SourceMode.Off => OffValue,
        _ => FullValue,
    };

    public static string Label(SourceMode mode) => mode switch
    {
        SourceMode.Stable => "稳定",
        SourceMode.Media => "影视",
        SourceMode.Off => "关闭",
        _ => "全量",
    };

    public static string Describe(SourceMode mode) => mode switch
    {
        SourceMode.Stable => "只用该源最稳的一条线路",
        SourceMode.Media => "只保留影视类内容",
        SourceMode.Off => "不参与搜索与首页",
        _ => "什么都搜",
    };

    /// <summary>读取某个片源的模式；没有记录时是全量。</summary>
    public static SourceMode ModeOf(IReadOnlyDictionary<string, string>? modes, string? sourceKey) =>
        modes is not null && !string.IsNullOrEmpty(sourceKey) && modes.TryGetValue(sourceKey, out var value)
            ? Parse(value)
            : SourceMode.Full;

    /// <summary>
    /// 返回写入某个片源模式之后的新字典（不修改原字典）。全量是默认值，不占条目。
    /// </summary>
    public static IReadOnlyDictionary<string, string> With(
        IReadOnlyDictionary<string, string>? modes, string sourceKey, SourceMode mode)
    {
        var updated = modes is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(modes, StringComparer.Ordinal);
        if (mode == SourceMode.Full)
        {
            updated.Remove(sourceKey);
        }
        else
        {
            updated[sourceKey] = ToValue(mode);
        }

        return updated;
    }

    /// <summary>设置文件里的值规整：丢掉空 key、非法值和全量（默认值）。</summary>
    public static IReadOnlyDictionary<string, string>? Normalize(IReadOnlyDictionary<string, string>? modes)
    {
        if (modes is null)
        {
            return null;
        }

        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in modes)
        {
            var mode = Parse(value);
            if (!string.IsNullOrWhiteSpace(key) && mode != SourceMode.Full)
            {
                normalized[key] = ToValue(mode);
            }
        }

        return normalized.Count == modes.Count && modes.All(pair =>
                normalized.TryGetValue(pair.Key, out var value) && value == pair.Value)
            ? modes
            : normalized;
    }

    /// <summary>参与搜索与首页的片源：去掉模式为“关闭”的。</summary>
    public static IReadOnlyList<VideoSource> ActiveSources(
        IReadOnlyList<VideoSource> sources, Func<string, SourceMode>? modeOf) =>
        modeOf is null || sources.All(source => modeOf(source.Key) != SourceMode.Off)
            ? sources
            : sources.Where(source => modeOf(source.Key) != SourceMode.Off).ToList();

    /// <summary>
    /// 是否影视类分类。空分类保留；先看排除词（短剧、解说、体育、新闻…），再看包含词（电影、剧、动漫、综艺、纪录…）；
    /// 都不命中的不算影视。大小写不敏感。
    /// </summary>
    public static bool IsMediaCategory(params string?[] categoryNames)
    {
        var text = string.Join(",", categoryNames.Where(name => !string.IsNullOrWhiteSpace(name)))
            .Trim()
            .ToLowerInvariant();
        if (text.Length == 0)
        {
            return true;
        }

        if (NonMediaWords.Any(word => text.Contains(word, StringComparison.Ordinal)))
        {
            return false;
        }

        return MediaWords.Any(word => text.Contains(word, StringComparison.Ordinal));
    }

    private static bool IsHls(IReadOnlyList<string> episodes) =>
        episodes.Count > 0 && episodes.Any(url => url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 选出最稳的一条线路。没有运行时测速数据，用启发式：剧集地址是 m3u8 的线路优先；并列时集数多的优先；
    /// 再并列取原顺序第一条。
    /// </summary>
    public static PlaybackSource? PickStableLine(IReadOnlyList<PlaybackSource>? lines)
    {
        if (lines is not { Count: > 0 })
        {
            return null;
        }

        PlaybackSource? best = null;
        foreach (var line in lines)
        {
            if (best is null)
            {
                best = line;
                continue;
            }

            var lineHls = IsHls(line.Episodes);
            var bestHls = IsHls(best.Episodes);
            if ((lineHls && !bestHls) || (lineHls == bestHls && line.Episodes.Count > best.Episodes.Count))
            {
                best = line;
            }
        }

        return best;
    }

    /// <summary>稳定模式：把一条结果收敛到最稳的一条线路（只有一条或没有线路信息时原样返回）。</summary>
    public static SearchResult KeepStableLine(SearchResult result)
    {
        if (result.PlaybackSources is not { Count: > 1 } lines)
        {
            return result;
        }

        var best = PickStableLine(lines)!;
        return result with
        {
            Episodes = best.Episodes,
            EpisodeLabels = best.EpisodeLabels,
            PlaybackSources = [best],
        };
    }

    /// <summary>按模式处理某个片源返回的结果。关闭 → 空；影视 → 只留影视类；稳定 → 每条只留一条线路；全量 → 原样。</summary>
    public static IReadOnlyList<SearchResult> Apply(SourceMode mode, IReadOnlyList<SearchResult> results) => mode switch
    {
        SourceMode.Off => [],
        SourceMode.Media => results.Where(result => IsMediaCategory(result.TypeName, result.Class)).ToList(),
        SourceMode.Stable => results.Select(KeepStableLine).ToList(),
        _ => results,
    };
}
