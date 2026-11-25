using System.Text.RegularExpressions;

namespace OrionTV.Desktop.Core;

// 弹幕公共类型与设置规则。
// 手机版(services/danmaku/*.ts)按同一套规则实现,改动匹配/排布规则时两边要一起改。

public enum DanmakuMode
{
    Scroll,
    Top,
    Bottom,
}

/// <summary>一条弹幕。</summary>
/// <param name="Time">出现时间(秒,相对视频开头)</param>
/// <param name="Text">文字</param>
/// <param name="Color">0xRRGGBB</param>
/// <param name="Mode">滚动 / 顶部 / 底部</param>
public sealed record DanmakuComment(double Time, string Text, int Color, DanmakuMode Mode);

/// <summary>给一集视频找弹幕时的查询条件。</summary>
/// <param name="Title">影片标题,如"庆余年 第二季"</param>
/// <param name="EpisodeIndex">当前集在选集列表中的下标(从 0 开始)</param>
/// <param name="EpisodeNumber">当前集的集号(从 1 开始);未知时用 EpisodeIndex + 1</param>
/// <param name="EpisodeCount">该片总集数;电影为 1</param>
/// <param name="Year">年份(可空)</param>
public sealed record DanmakuQuery(string Title, int EpisodeIndex, int EpisodeNumber, int EpisodeCount, string? Year = null);

/// <summary>一集视频的弹幕来源。弹幕按时间分片,播放到哪里取到哪里。</summary>
public sealed class DanmakuTrack
{
    private readonly Func<int, CancellationToken, Task<IReadOnlyList<DanmakuComment>>> _loadSegment;

    public DanmakuTrack(
        string provider,
        string title,
        double segmentSeconds,
        Func<int, CancellationToken, Task<IReadOnlyList<DanmakuComment>>> loadSegment)
    {
        Provider = provider;
        Title = title;
        SegmentSeconds = segmentSeconds;
        _loadSegment = loadSegment;
    }

    public string Provider { get; }

    /// <summary>匹配到的片名(仅用于日志)。</summary>
    public string Title { get; }

    /// <summary>每个分片覆盖的秒数;0 表示整集只有一个分片。</summary>
    public double SegmentSeconds { get; }

    /// <summary>取一个分片的弹幕;网络失败时抛异常(由 <see cref="DanmakuTimeline"/> 吞掉)。</summary>
    public Task<IReadOnlyList<DanmakuComment>> LoadSegmentAsync(int index, CancellationToken ct = default) =>
        _loadSegment(index, ct);
}

/// <summary>弹幕设置的取值范围与默认值。</summary>
public static class DanmakuSettingsRules
{
    public const double DefaultOpacity = 0.8;
    public const string DefaultSpeed = "normal";
    public const string DefaultFontSize = "normal";

    /// <summary>顶部/底部弹幕停留的秒数。</summary>
    public const double FixedSeconds = 4;

    public static readonly IReadOnlyList<double> OpacityOptions = [0.4, 0.6, 0.8, 1];
    public static readonly IReadOnlyList<string> SpeedOptions = ["slow", "normal", "fast"];
    public static readonly IReadOnlyList<string> FontSizeOptions = ["small", "normal", "large"];

    private static readonly Regex HasScheme = new(@"^[a-z][a-z0-9+.\-]*://", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex HttpUrl = new(@"^https?://[^/\s]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ApiSuffix = new(@"/api/v2\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// 规范化自定义弹幕服务器地址:去掉首尾空白、末尾的 "/" 和 "/api/v2";
    /// 没写协议时补 http://;不是 http(s) 地址时返回空串(等同于未配置)。
    /// </summary>
    public static string NormalizeServerUrl(string? value)
    {
        var url = (value ?? "").Trim();
        if (url.Length == 0)
        {
            return "";
        }

        if (!HasScheme.IsMatch(url))
        {
            url = "http://" + url;
        }

        if (!HttpUrl.IsMatch(url))
        {
            return "";
        }

        url = url.TrimEnd('/');
        url = ApiSuffix.Replace(url, "");
        return url.TrimEnd('/');
    }

    /// <summary>不透明度限制在 0.2 ~ 1;不是有限数时用默认值。</summary>
    public static double NormalizeOpacity(double value) =>
        double.IsFinite(value) ? Math.Min(1, Math.Max(0.2, value)) : DefaultOpacity;

    public static string NormalizeSpeed(string? value) =>
        value is "slow" or "normal" or "fast" ? value : DefaultSpeed;

    public static string NormalizeFontSize(string? value) =>
        value is "small" or "normal" or "large" ? value : DefaultFontSize;

    /// <summary>滚动弹幕横穿画面所用的秒数。</summary>
    public static double ScrollSeconds(string? speed) => NormalizeSpeed(speed) switch
    {
        "slow" => 11,
        "fast" => 5.5,
        _ => 8,
    };

    /// <summary>字号相对基准字号的倍数。</summary>
    public static double FontScale(string? fontSize) => NormalizeFontSize(fontSize) switch
    {
        "small" => 0.8,
        "large" => 1.3,
        _ => 1,
    };

    public static string SpeedLabel(string? speed) => NormalizeSpeed(speed) switch
    {
        "slow" => "慢",
        "fast" => "快",
        _ => "标准",
    };

    public static string FontSizeLabel(string? fontSize) => NormalizeFontSize(fontSize) switch
    {
        "small" => "小",
        "large" => "大",
        _ => "标准",
    };
}
