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

/// <summary>详情页导航参数（对齐安卓 detail 的 q/source/id）。</summary>
public sealed record DetailParameter(string Title, string? Source = null, string? Id = null);

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
    string? PlaybackSourceKey = null);
