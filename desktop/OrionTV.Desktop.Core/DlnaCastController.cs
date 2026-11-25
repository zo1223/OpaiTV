namespace OrionTV.Desktop.Core;

/// <summary>
/// 播放页投屏面板背后的逻辑：搜索设备（边搜边出）、当前会话、给界面看的文字。不依赖 WPF，可单测。
/// <see cref="Changed"/> 可能在后台线程触发，界面要自己切回界面线程。
/// </summary>
public sealed class DlnaCastController
{
    private readonly DlnaDiscovery _discovery;
    private readonly List<DlnaDevice> _devices = [];
    private CancellationTokenSource? _searchCts;
    private volatile int _searchSeq;

    public DlnaCastController(DlnaDiscovery discovery, DlnaCastSession session)
    {
        _discovery = discovery;
        Session = session;
        Session.Changed += (_, _) => Raise();
    }

    public DlnaCastSession Session { get; }

    /// <summary>一次搜索的时长。</summary>
    public TimeSpan SearchTimeout { get; init; } = TimeSpan.FromSeconds(4);

    public bool IsSearching { get; private set; }

    /// <summary>至少完成过一次搜索（用来区分“还没搜”和“搜了没找到”）。</summary>
    public bool HasSearched { get; private set; }

    /// <summary>搜索没法进行的原因（没有网卡等），或当前内容不能投屏的原因。</summary>
    public string? SearchError { get; private set; }

    public event EventHandler? Changed;

    public IReadOnlyList<DlnaDevice> Devices
    {
        get
        {
            lock (_devices)
            {
                return _devices.ToList();
            }
        }
    }

    /// <summary>搜索一轮。再次调用会取消上一轮；设备一找到就进列表并触发 <see cref="Changed"/>。不抛异常。</summary>
    public async Task SearchAsync()
    {
        CancelSearch();
        var seq = ++_searchSeq;
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        lock (_devices)
        {
            _devices.Clear();
        }

        IsSearching = true;
        SearchError = null;
        Raise();

        DlnaDiscoveryResult result;
        try
        {
            result = await _discovery.DiscoverAsync(
                SearchTimeout,
                device =>
                {
                    if (seq == _searchSeq && Add(device))
                    {
                        Raise();
                    }
                },
                cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            result = new DlnaDiscoveryResult([], $"搜索出错：{ex.Message}");
        }
        finally
        {
            cts.Dispose();
        }

        if (seq != _searchSeq)
        {
            // 已被新的搜索或取消顶替。
            return;
        }

        _searchCts = null;
        foreach (var device in result.Devices)
        {
            Add(device);
        }

        IsSearching = false;
        HasSearched = true;
        SearchError = result.Error;
        Raise();
    }

    /// <summary>取消进行中的搜索（关面板、开始投屏、离开播放页时）。已找到的设备保留。</summary>
    public void CancelSearch()
    {
        _searchSeq++;
        var cts = _searchCts;
        _searchCts = null;
        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 这一轮刚好结束。
        }

        if (IsSearching)
        {
            IsSearching = false;
            Raise();
        }
    }

    /// <summary>当前内容不能投屏（本地文件、本机地址）：不搜索，只显示原因。</summary>
    public void ShowUnavailable(string reason)
    {
        CancelSearch();
        lock (_devices)
        {
            _devices.Clear();
        }

        HasSearched = true;
        SearchError = reason;
        Raise();
    }

    /// <summary>把地址推给设备并开始播放。会先取消进行中的搜索。</summary>
    public Task<bool> CastAsync(DlnaDevice device, string? url, string title, TimeSpan startPosition)
    {
        CancelSearch();
        return Session.CastAsync(device, url ?? "", title, startPosition);
    }

    /// <summary>界面定时器每秒调一次：同步设备的进度与状态。</summary>
    public Task TickAsync() => Session.PollAsync();

    public Task<bool> SeekByAsync(TimeSpan delta) => Session.SeekAsync(Session.Position + delta);

    /// <summary>按进度条比例（0~1）跳转；设备还没报片长时不跳。</summary>
    public Task<bool> SeekToFractionAsync(double fraction) =>
        Session.Duration > TimeSpan.Zero && !double.IsNaN(fraction)
            ? Session.SeekAsync(TimeSpan.FromSeconds(Session.Duration.TotalSeconds * Math.Clamp(fraction, 0, 1)))
            : Task.FromResult(false);

    /// <summary>进度条位置（0~1）；片长未知时为 0。</summary>
    public double ProgressFraction =>
        Session.Duration > TimeSpan.Zero
            ? Math.Clamp(Session.Position.TotalSeconds / Session.Duration.TotalSeconds, 0, 1)
            : 0;

    /// <summary>设备列表上方的提示文字。</summary>
    public string SearchStatusText
    {
        get
        {
            var count = Devices.Count;
            if (IsSearching)
            {
                return count == 0 ? "正在搜索同一局域网内的投屏设备…" : $"已找到 {count} 台设备，还在搜索…";
            }

            if (SearchError is not null)
            {
                return SearchError;
            }

            if (!HasSearched)
            {
                return "";
            }

            return count == 0
                ? "没有找到投屏设备。请确认电视或盒子和这台电脑在同一个局域网，并已打开 DLNA / 投屏功能。"
                : $"找到 {count} 台设备，点设备名开始投屏";
        }
    }

    /// <summary>投屏状态的文字：连接中 / 播放中 / 已暂停 / 无响应 / 已停止 / 失败原因。</summary>
    public string CastStatusText => Session.State switch
    {
        DlnaCastState.Connecting => "连接中…",
        DlnaCastState.Playing => Session.Unresponsive ? "设备无响应，正在重试…" : "播放中",
        DlnaCastState.Paused => Session.Unresponsive ? "设备无响应，正在重试…" : "已暂停",
        DlnaCastState.Stopped => Session.EndedByDevice ? "电视端已停止播放" : "已停止投屏",
        DlnaCastState.Error => string.IsNullOrWhiteSpace(Session.Error) ? "投屏失败" : $"投屏失败：{Session.Error}",
        _ => "",
    };

    /// <summary>“12:34 / 45:00”；片长未知时只有当前进度。</summary>
    public string ProgressText =>
        Session.Duration > TimeSpan.Zero
            ? $"{FormatClock(Session.Position)} / {FormatClock(Session.Duration)}"
            : FormatClock(Session.Position);

    public static string FormatClock(TimeSpan time)
    {
        var total = (long)Math.Max(0, Math.Floor(time.TotalSeconds));
        return total >= 3600
            ? $"{total / 3600}:{total / 60 % 60:00}:{total % 60:00}"
            : $"{total / 60:00}:{total % 60:00}";
    }

    private bool Add(DlnaDevice device)
    {
        lock (_devices)
        {
            if (_devices.Any(existing => string.Equals(existing.Udn, device.Udn, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            _devices.Add(device);
            return true;
        }
    }

    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);
}
