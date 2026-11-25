using System.Windows;
using System.Windows.Input;
using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.App;

/// <summary>
/// 迷你播放窗口（灵动岛）：无边框、置顶、默认停在屏幕右下角的小窗口，自带一个 LibVLC 播放器。
/// 按“进度交接”工作：播放页点“小窗播放”时把当前集地址与进度交过来（<see cref="PlayParameter"/>），
/// 主窗口照常离开播放页，这里从同一进度接着播；点画面或“回到播放器”再把进度交回全屏播放页。
/// 媒体的创建沿用播放页的 <see cref="PlaybackController"/>（同一套 LibVLC 选项与代理设置），状态逻辑在
/// <see cref="MiniPlayerSession"/>。只接在线点播，不接直播与本地视频；一集播完后停住，不自动连播。
/// </summary>
public partial class MiniPlayerWindow : Window
{
    private const double ExpandedHeight = 289; // 225 画面 + 62 信息栏 + 2 边框
    private const double CollapsedHeight = 64;
    private const double ScreenMargin = 24;
    private const string PlayGlyph = "\uE768";
    private const string PauseGlyph = "\uE769";
    private const string CollapseGlyph = "\uE70D";
    private const string ExpandGlyph = "\uE70E";

    private readonly AppServices _services;
    private readonly PlayParameter _request;
    private readonly MiniPlayerSession _session;
    private PlaybackController? _controller;
    private long _lastSavedSecond;
    private bool _closed;

    public MiniPlayerWindow(AppServices services, PlayParameter request)
    {
        InitializeComponent();
        _services = services;
        _request = request;
        var index = Math.Clamp(request.EpisodeIndex, 0, Math.Max(0, request.Episodes.Count - 1));
        _session = new MiniPlayerSession(new MiniPlayerMedia(
            request.Title,
            EpisodeLabelFormatter.DisplayAt(request.EpisodeLabels, index),
            request.Episodes.Count > 0 ? request.Episodes[index] : "",
            request.PositionMs));
        _session.Changed += RefreshUi;
        Height = ExpandedHeight;
        PlaceAtBottomRight();
        RefreshUi();
        Loaded += OnLoaded;
    }

    /// <summary>用户要回到全屏播放页：参数是带着小窗当前进度的播放请求。由主窗口先关掉小窗再导航。</summary>
    public event Action<PlayParameter>? RestoreRequested;

    /// <summary>小窗正在播的内容所属的源 key（成人内容开关关闭时主窗口据此关掉小窗）。</summary>
    internal string SourceKey => _request.Source;

    /// <summary>小窗正在播的内容的分类信息（同上，按分类判断）。</summary>
    internal string? Category => _request.Category;

    private void PlaceAtBottomRight()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - ScreenMargin;
        Top = area.Bottom - Height - ScreenMargin;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!MiniPlayerSession.CanHandOff(_session.Media.Url))
        {
            StatusText.Text = "该集暂无可用地址";
            return;
        }

        LibVLCSharp.Shared.LibVLC libVlc;
        try
        {
            libVlc = await _services.GetLibVlcAsync();
        }
        catch
        {
            StatusText.Text = "播放器初始化失败";
            return;
        }

        if (_closed)
        {
            return;
        }

        _controller = new PlaybackController(libVlc, Dispatcher);
        VideoSurface.MediaPlayer = _controller.Player;
        _controller.StateChanged += Controller_StateChanged;
        _controller.TimeChanged += Controller_TimeChanged;
        _controller.LengthChanged += Controller_LengthChanged;
        _controller.Volume = Math.Clamp(_services.Settings.Volume, 0, 100);
        _controller.Mute = false;
        // 与播放页的在线起播一致：同一个 SwitchTo（内部用 PlaybackMediaOptions.ForUri），网络缓存 6000ms
        _controller.SwitchTo(_session.Media.Url, (long)Math.Max(0, _session.Media.StartPositionMs), 6000);
    }

    private void Controller_StateChanged(object? sender, PlaybackState state)
    {
        if (_closed)
        {
            return;
        }

        switch (state)
        {
            case PlaybackState.Playing:
                _session.ReportPlaying(true);
                StatusText.Visibility = Visibility.Collapsed;
                break;
            case PlaybackState.Paused:
                _session.ReportPlaying(false);
                break;
            case PlaybackState.Ended:
                _session.ReportPlaying(false);
                ShowStatus("本集已播完，点击回到播放器");
                SaveRecord();
                break;
            case PlaybackState.Error:
                _session.ReportPlaying(false);
                ShowStatus("播放失败，点击回到播放器重试");
                break;
            default:
                // Opening / Buffering：LibVLC 正常播放中也会不断报 Buffering，不算暂停
                break;
        }
    }

    private void ShowStatus(string text)
    {
        StatusText.Text = text;
        StatusText.Visibility = Visibility.Visible;
    }

    private void Controller_TimeChanged(object? sender, long time)
    {
        if (_closed || _controller is null)
        {
            return;
        }

        _session.ReportProgress(time, _controller.Length);
        var second = time / 1000;
        if (second > 0 && Math.Abs(second - _lastSavedSecond) >= 5)
        {
            _lastSavedSecond = second;
            SaveRecord();
        }
    }

    private void Controller_LengthChanged(object? sender, long length)
    {
        if (_closed || !_session.HasProgressed)
        {
            return;
        }

        _session.ReportProgress(_session.PositionMs, length);
    }

    private void RefreshUi()
    {
        TitleText.Text = _session.DisplayTitle;
        TitleText.ToolTip = _session.DisplayTitle;
        ProgressIndicator.Value = _session.ProgressFraction;
        ProgressText.Text = _session.ProgressText;
        PlayPauseButton.Content = _session.IsPlaying ? PauseGlyph : PlayGlyph;
        CollapseButton.Content = _session.IsCollapsed ? ExpandGlyph : CollapseGlyph;
        CollapseButton.ToolTip = _session.IsCollapsed ? "展开" : "收起";
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e) => _controller?.TogglePlayPause();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void RestoreButton_Click(object sender, RoutedEventArgs e) => RequestRestore();

    private void Video_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => RequestRestore();

    private void RequestRestore()
    {
        if (_closed)
        {
            return;
        }

        RestoreRequested?.Invoke(_request with { PositionMs = _session.ResumePositionMs, Resume = null });
    }

    private void CollapseButton_Click(object sender, RoutedEventArgs e)
    {
        var collapsed = _session.ToggleCollapsed();
        var bottom = Top + Height;
        if (collapsed)
        {
            // LibVLCSharp 的画面与浮层是独立的原生窗口，只有从可视树上拿掉才会一起隐藏（与播放页的做法相同）；声音不停。
            if (ReferenceEquals(VideoSurface.Parent, VideoArea))
            {
                VideoArea.Children.Remove(VideoSurface);
            }

            VideoArea.Visibility = Visibility.Collapsed;
            Height = CollapsedHeight;
        }
        else
        {
            Height = ExpandedHeight;
            VideoArea.Visibility = Visibility.Visible;
            if (VideoSurface.Parent is null)
            {
                VideoArea.Children.Add(VideoSurface);
            }

            if (_controller is not null)
            {
                // 重新挂回后重新绑定播放器，但不重启媒体
                VideoSurface.MediaPlayer = _controller.Player;
            }
        }

        // 收起 / 展开时保持窗口底边不动
        Top = bottom - Height;
    }

    private void ProgressIndicator_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true; // 点进度条是跳转，不拖动窗口
        var width = ProgressIndicator.ActualWidth;
        if (width <= 0 || _controller is null)
        {
            return;
        }

        var fraction = e.GetPosition(ProgressIndicator).X / width;
        if (_session.SeekTargetMs(fraction) is { } target && _controller.TrySeek((long)target))
        {
            _session.ReportProgress(target, _session.DurationMs);
        }
    }

    private void InfoBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 鼠标左键已松开时 DragMove 会抛异常，忽略。
        }
    }

    /// <summary>
    /// 存一次观看记录，字段与播放页 SaveRecord 的在线分支一致。播放器还没报过进度时不存，
    /// 避免小窗没起播就把已有记录的进度覆盖掉。
    /// </summary>
    private void SaveRecord()
    {
        var positionMs = _session.ResumePositionMs;
        if (!_session.HasProgressed
            || positionMs <= 0
            || string.IsNullOrEmpty(_request.Source)
            || string.IsNullOrEmpty(_request.Id))
        {
            return;
        }

        try
        {
            var index = Math.Clamp(_request.EpisodeIndex, 0, Math.Max(0, _request.Episodes.Count - 1));
            var episodeLabel = EpisodeLabelFormatter.DisplayAt(_request.EpisodeLabels, index);
            var episodeNumber = EpisodeLabelFormatter.TryGetNumber(episodeLabel, out var number)
                ? number
                : (int?)null;
            _services.PlayRecords.Save(_request.Source, _request.Id, new PlayRecordItem(
                Title: _request.Title,
                SourceName: _request.SourceName,
                Cover: _request.Poster,
                Index: index,
                TotalEpisodes: _request.Episodes.Count,
                PlayTime: positionMs / 1000.0,
                TotalTime: _session.DurationMs / 1000.0,
                Year: "",
                PlaybackSourceKey: _request.PlaybackSourceKey,
                EpisodeLabel: episodeLabel,
                EpisodeNumber: episodeNumber,
                Category: _request.Category));
        }
        catch
        {
            // 保存记录失败忽略。
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (!_closed)
        {
            _closed = true;
            SaveRecord();
            _session.Close();
            if (_controller is not null)
            {
                _controller.StateChanged -= Controller_StateChanged;
                _controller.TimeChanged -= Controller_TimeChanged;
                _controller.LengthChanged -= Controller_LengthChanged;
                VideoSurface.MediaPlayer = null;
                _services.DisposePlaybackInBackground(_controller);
                _controller = null;
            }
        }

        base.OnClosed(e);
    }
}
