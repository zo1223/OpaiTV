namespace OrionTV.Desktop.Core;

/// <summary>豆瓣条目（首页分类网格数据）。</summary>
public sealed record DoubanItem(
    string Id,
    string Title,
    string Poster,
    string? Rate = null,
    string? Year = null);

public enum CatalogPlayabilityStatus
{
    Unknown,
    Checking,
    Playable,
    Unavailable,
}

/// <summary>详情/海报共用的标题匹配（去空白、大小写归一）。</summary>
public static class CatalogTitleMatcher
{
    public static bool Matches(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return false;
        }

        if (string.Equals(a.Trim(), b.Trim(), StringComparison.Ordinal))
        {
            return true;
        }

        return string.Equals(Normalize(a), Normalize(b), StringComparison.Ordinal);
    }

    public static string Normalize(string? value) =>
        string.IsNullOrEmpty(value)
            ? ""
            : new string(value.Trim().ToLowerInvariant().Where(c => !char.IsWhiteSpace(c)).ToArray());
}

public sealed record CatalogPlayability(CatalogPlayabilityStatus Status, int PlayableSourceCount)
{
    /// <summary>
    /// 与详情页播放源列表同口径计数：
    /// - 标题用 <see cref="CatalogTitleMatcher"/>（与 GetDetailSourcesAsync 一致）
    /// - 去重用 SelectionKey（source+id+线路），与详情 chip 一致
    /// - 保留无剧集条目（TVBox search 常无 play_url，详情页再补拉）
    /// 匹配到任意源即 Playable，数字 = 详情页可展示的源数。
    /// </summary>
    public static CatalogPlayability FromDetailSources(string title, IEnumerable<SearchResult> results)
    {
        var sourceCount = results
            .SelectMany(SearchResultSources.Expand)
            .Where(item => CatalogTitleMatcher.Matches(item.Title, title))
            .Select(SearchResultSources.SelectionKeyOf)
            .Distinct(StringComparer.Ordinal)
            .Count();

        return new CatalogPlayability(
            sourceCount > 0 ? CatalogPlayabilityStatus.Playable : CatalogPlayabilityStatus.Unavailable,
            sourceCount);
    }
}

public static class CatalogPlayabilityBadge
{
    public static string? GetLabel(CatalogPlayabilityStatus status, int? playableSourceCount) =>
        status switch
        {
            CatalogPlayabilityStatus.Playable when playableSourceCount.GetValueOrDefault() > 0 =>
                $"{playableSourceCount.GetValueOrDefault()}\u6e90",
            CatalogPlayabilityStatus.Playable => "\u53ef\u64ad",
            CatalogPlayabilityStatus.Unavailable => "\u6682\u65e0\u8d44\u6e90",
            _ => null,
        };
}

/// <summary>
/// 当前应用会话内的豆瓣可播性结果缓存。
/// 首页视图会在切换顶层页面时销毁，结果不能只放在卡片对象里。
/// </summary>
public sealed class CatalogPlayabilityCache
{
    private readonly object _sync = new();
    private readonly Dictionary<string, CatalogPlayability> _entries = new(StringComparer.Ordinal);
    private int _generation;

    public int Generation
    {
        get
        {
            lock (_sync)
            {
                return _generation;
            }
        }
    }

    public bool TryGet(CatalogItem item, out CatalogPlayability playability)
    {
        lock (_sync)
        {
            if (_entries.TryGetValue(GetKey(item), out var cached))
            {
                playability = cached;
                return true;
            }
        }

        playability = default!;
        return false;
    }

    public bool Store(CatalogItem item, CatalogPlayability playability, int generation)
    {
        if (playability.Status is not (CatalogPlayabilityStatus.Playable or CatalogPlayabilityStatus.Unavailable))
        {
            return false;
        }

        lock (_sync)
        {
            if (generation != _generation)
            {
                return false;
            }

            _entries[GetKey(item)] = playability;
            return true;
        }
    }

    public void Invalidate()
    {
        lock (_sync)
        {
            _entries.Clear();
            unchecked
            {
                _generation++;
            }
        }
    }

    private static string GetKey(CatalogItem item)
    {
        var source = item.Source.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(item.Id))
        {
            return $"{source}:id:{item.Id.Trim()}";
        }

        return $"{source}:title:{CatalogTitleMatcher.Normalize(item.Title)}:year:{CatalogTitleMatcher.Normalize(item.Year)}";
    }
}

/// <summary>源站分类（AppleCMS ac=list 的 class 项）。</summary>
public sealed record VideoSourceCategory(string Id, string Name);

/// <summary>源站分类视频分页结果（AppleCMS ac=detail&t=&pg=）。</summary>
public sealed record CategoryVideosResponse(
    IReadOnlyList<SearchResult> Results,
    int Total,
    int Page,
    int PageCount);

/// <summary>首页分类定义（对齐安卓 homeStore 的 Category）。</summary>
public sealed record HomeCategory(
    string Title,
    string? Type = null,        // "movie" | "tv" | "record"
    string? Tag = null,
    IReadOnlyList<string>? Tags = null);

/// <summary>本地收藏项（对齐安卓 Favorite）。</summary>
public sealed record FavoriteItem(
    string Cover,
    string Title,
    string SourceName,
    int TotalEpisodes,
    string SearchTitle,
    string Year,
    long SaveTime = 0);

/// <summary>本地观看记录（对齐安卓 PlayRecord）。</summary>
public sealed record PlayRecordItem(
    string Title,
    string SourceName,
    string Cover,
    int Index,
    int TotalEpisodes,
    double PlayTime,
    double TotalTime,
    string Year,
    long SaveTime = 0);

/// <summary>直播频道（对齐安卓 m3u Channel）。</summary>
public sealed record LiveChannel(
    string Id,
    string Name,
    string Url,
    string Group,
    string? Logo = null);

/// <summary>播放失败时选择下一条可播放源，跳过当前源、已失败源和缺少当前集的源。</summary>
public static class PlaybackFallbackSelector
{
    public static SearchResult? SelectNext(
        IEnumerable<SearchResult> sources,
        string currentSource,
        IReadOnlySet<string> failedSources,
        int episodeIndex)
    {
        return sources.FirstOrDefault(source =>
        {
            var sourceKey = SearchResultSources.SelectionKeyOf(source);
            return sourceKey != currentSource
                   && source.Source != currentSource
                   && !failedSources.Contains(sourceKey)
                   && !failedSources.Contains(source.Source)
                   && episodeIndex >= 0
                   && source.Episodes.Count > episodeIndex
                   && !string.IsNullOrWhiteSpace(source.Episodes[episodeIndex]);
        });
    }
}

/// <summary>LibVLC 播放网络流时使用的兼容性选项，尽量贴近安卓端播放器请求行为。</summary>
public static class PlaybackMediaOptions
{
    public const string AndroidLikeUserAgent =
        "Mozilla/5.0 (Linux; Android 13; Android TV) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36";

    public static IReadOnlyList<string> ForUri(Uri uri, int networkCachingMs = 1500)
    {
        if (uri.IsFile)
        {
            return [":file-caching=1000"];
        }

        var options = new List<string>
        {
            $":http-user-agent={AndroidLikeUserAgent}",
            ":http-reconnect",
            $":network-caching={Math.Clamp(networkCachingMs, 500, 30000)}",
        };

        if (TryBuildHttpReferrer(uri) is { } referrer)
        {
            options.Insert(1, $":http-referrer={referrer}");
        }

        return options;
    }

    public static string? TryBuildHttpReferrer(Uri uri)
    {
        if (uri.Scheme is not ("http" or "https"))
        {
            return null;
        }

        return $"{uri.GetLeftPart(UriPartial.Authority)}/";
    }
}

/// <summary>播放器启动期卡在打开/缓冲状态的超时判断。</summary>
public sealed class PlaybackStartupWatchdog
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(25);
    private DateTimeOffset? _startedAt;

    public PlaybackStartupWatchdog()
        : this(DefaultTimeout)
    {
    }

    public PlaybackStartupWatchdog(TimeSpan timeout)
    {
        Timeout = timeout;
    }

    public TimeSpan Timeout { get; }

    public void Arm(DateTimeOffset now) => _startedAt = now;

    public void Disarm() => _startedAt = null;

    public bool IsTimedOut(DateTimeOffset now) =>
        _startedAt is { } startedAt && now - startedAt >= Timeout;
}

public enum PlaybackStartupSignal
{
    Opening,
    Buffering,
    LengthChanged,
    Playing,
    Paused,
    Ended,
    TimeAdvanced,
    Error,
}

public static class PlaybackStartupPolicy
{
    public static bool ResolvesStartup(PlaybackStartupSignal signal) =>
        signal is PlaybackStartupSignal.Playing
            or PlaybackStartupSignal.Paused
            or PlaybackStartupSignal.Ended
            or PlaybackStartupSignal.TimeAdvanced
            or PlaybackStartupSignal.Error;
}
