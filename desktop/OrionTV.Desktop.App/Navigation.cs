using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.App;

/// <summary>应用内页面枚举（对齐安卓侧边栏 + 详情/播放）。</summary>
public enum AppPage
{
    Home,
    Search,
    Favorites,
    Live,
    LocalVideo,
    Settings,
    Detail,
    Player,
}

/// <summary>导航宿主：页面通过它访问共享服务并发起导航。</summary>
public interface IAppHost
{
    AppServices Services { get; }
    void Navigate(AppPage page, object? parameter = null);
    void GoBack();

    /// <summary>当前是否处于沉浸全屏（隐藏侧边栏、无边框最大化）。</summary>
    bool IsFullscreen { get; }

    /// <summary>进入/退出沉浸全屏。</summary>
    void SetFullscreen(bool enabled);
}

/// <summary>页面可选实现：导航到该页时接收参数。</summary>
public interface INavigationAware
{
    void OnNavigatedTo(object? parameter);
}

public interface INavigationVisibilityAware
{
    void OnShown();
    void OnHidden();
}

public sealed record PlaybackResumeContext(
    int EpisodeIndex,
    double PositionMs,
    string? EpisodeLabel = null,
    int? EpisodeNumber = null,
    string? HistorySource = null,
    string? HistoryId = null)
{
    public double StartPositionMs => PlaybackResumePolicy.StartPositionMilliseconds(PositionMs);

    public int FindEpisodeIndex(IReadOnlyList<string>? episodeLabels, int episodeCount) =>
        PlaybackResumePolicy.FindEpisodeIndex(
            episodeLabels,
            episodeCount,
            EpisodeIndex,
            EpisodeLabel,
            EpisodeNumber);
}

/// <summary>详情页导航参数（对齐安卓 detail 的 q/source/id，并携带同一轮源验证快照）。</summary>
public sealed record DetailParameter(
    string Title,
    string? Source = null,
    string? Id = null,
    IReadOnlyList<SearchResult>? CandidateSources = null,
    IReadOnlyList<SearchResult>? PlayableSources = null,
    string? PlaybackSourceKey = null,
    PlaybackResumeContext? Resume = null);

/// <summary>播放页导航参数。</summary>
public sealed record PlayParameter(
    string Title,
    string Source,
    string SourceName,
    string Id,
    IReadOnlyList<string> Episodes,
    int EpisodeIndex,
    double PositionMs,
    IReadOnlyList<SearchResult> AltSources,
    string Poster = "",
    string? PlaybackSourceKey = null,
    IReadOnlyList<string>? EpisodeLabels = null,
    PlaybackResumeContext? Resume = null);
