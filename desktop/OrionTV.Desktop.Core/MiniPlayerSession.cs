namespace OrionTV.Desktop.Core;

/// <summary>迷你播放窗口要接着播的内容：由播放页在“小窗播放”时交接过来。</summary>
public sealed record MiniPlayerMedia(string Title, string EpisodeLabel, string Url, double StartPositionMs);

/// <summary>
/// 迷你播放窗口（灵动岛）的状态机，不依赖 WPF：片名、进度、播放/暂停、收起/展开、关闭，
/// 以及回到全屏播放页（或存观看记录）时应使用的进度。界面层只负责把播放器事件报进来、把状态画出来。
/// </summary>
public sealed class MiniPlayerSession
{
    public MiniPlayerSession(MiniPlayerMedia media)
    {
        Media = media ?? throw new ArgumentNullException(nameof(media));
        PositionMs = Math.Max(0, Sanitize(media.StartPositionMs));
    }

    public MiniPlayerMedia Media { get; }

    /// <summary>状态有变化时触发（界面据此重画）。关闭之后不再触发。</summary>
    public event Action? Changed;

    public double PositionMs { get; private set; }

    public double DurationMs { get; private set; }

    public bool IsPlaying { get; private set; }

    public bool IsCollapsed { get; private set; }

    public bool IsClosed { get; private set; }

    /// <summary>播放器是否已经报过进度；没报过时进度仍是交接时的起点。</summary>
    public bool HasProgressed { get; private set; }

    /// <summary>标题行：片名 + 集数标签（没有集数时只有片名）。</summary>
    public string DisplayTitle
    {
        get
        {
            var title = (Media.Title ?? "").Trim();
            var label = (Media.EpisodeLabel ?? "").Trim();
            if (label.Length == 0)
            {
                return title;
            }

            return title.Length == 0 ? label : $"{title}  {label}";
        }
    }

    /// <summary>进度比例，夹在 0~1；时长未知时为 0。</summary>
    public double ProgressFraction =>
        DurationMs > 0 ? Math.Clamp(PositionMs / DurationMs, 0, 1) : 0;

    public string PositionText => FormatTime(PositionMs);

    public string DurationText => DurationMs > 0 ? FormatTime(DurationMs) : "--:--";

    public string ProgressText => $"{PositionText} / {DurationText}";

    /// <summary>
    /// 回到全屏播放页时的续播位置（也是存观看记录用的位置）：播放器报过进度就用它，
    /// 否则用交接时的起点，避免小窗还没起播就把进度交回 0。
    /// </summary>
    public double ResumePositionMs =>
        HasProgressed ? PositionMs : Math.Max(0, Sanitize(Media.StartPositionMs));

    /// <summary>播放器上报进度。负数、NaN 按 0 处理；已知时长时进度不超过时长。</summary>
    public void ReportProgress(double positionMs, double durationMs)
    {
        if (IsClosed)
        {
            return;
        }

        var duration = Math.Max(0, Sanitize(durationMs));
        var position = Math.Max(0, Sanitize(positionMs));
        if (duration > 0)
        {
            position = Math.Min(position, duration);
        }

        if (HasProgressed && position == PositionMs && duration == DurationMs)
        {
            return;
        }

        HasProgressed = true;
        PositionMs = position;
        DurationMs = duration;
        Changed?.Invoke();
    }

    /// <summary>播放器上报播放 / 暂停状态。</summary>
    public void ReportPlaying(bool playing)
    {
        if (IsClosed || IsPlaying == playing)
        {
            return;
        }

        IsPlaying = playing;
        Changed?.Invoke();
    }

    /// <summary>点了播放/暂停：返回希望播放器进入的状态（true = 播放）。已关闭时不变并返回 false。</summary>
    public bool TogglePlayPause()
    {
        if (IsClosed)
        {
            return false;
        }

        IsPlaying = !IsPlaying;
        Changed?.Invoke();
        return IsPlaying;
    }

    /// <summary>收起 / 展开；返回切换后的收起状态。</summary>
    public bool ToggleCollapsed()
    {
        if (IsClosed)
        {
            return IsCollapsed;
        }

        IsCollapsed = !IsCollapsed;
        Changed?.Invoke();
        return IsCollapsed;
    }

    /// <summary>点进度条跳转：把 0~1 的比例换成目标位置；时长未知时返回 null（无法跳转）。</summary>
    public double? SeekTargetMs(double fraction)
    {
        if (IsClosed || DurationMs <= 0 || double.IsNaN(fraction))
        {
            return null;
        }

        return Math.Clamp(fraction, 0, 1) * DurationMs;
    }

    /// <summary>关闭：之后的上报与操作全部忽略，进度停在关闭时的位置。重复关闭无效果。</summary>
    public void Close()
    {
        if (IsClosed)
        {
            return;
        }

        IsPlaying = false;
        IsClosed = true;
        Changed?.Invoke();
        Changed = null;
    }

    /// <summary>mm:ss，满一小时用 h:mm:ss；负数、NaN 按 0。</summary>
    public static string FormatTime(double milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(0, Sanitize(milliseconds)));
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"mm\:ss");
    }

    /// <summary>只有 http / https 的在线地址能交给迷你窗口（本地文件走内嵌 mpv，不接）。</summary>
    public static bool CanHandOff(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static double Sanitize(double value) =>
        double.IsNaN(value) || double.IsInfinity(value) ? 0 : value;
}
