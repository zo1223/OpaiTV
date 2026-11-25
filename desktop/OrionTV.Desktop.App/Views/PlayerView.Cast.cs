using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.App.Views;

/// <summary>
/// 播放页的 DLNA 投屏：按钮、侧栏、投屏中的状态条。协议与状态逻辑都在 Core
/// （<see cref="DlnaCastController"/> / <see cref="DlnaCastSession"/>），这里只做界面与本机播放器的暂停 / 恢复。
/// </summary>
public partial class PlayerView
{
    // 电视在局域网里：直连，不走系统代理，也不走播放代理。
    private static readonly HttpClient CastHttp = new(new SocketsHttpHandler
    {
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(3),
    })
    {
        Timeout = TimeSpan.FromSeconds(5),
    };

    private DlnaCastController? _cast;
    private DispatcherTimer? _castTimer;
    private bool _castSliderDragging;
    private bool _castRendering;

    /// <summary>这次投屏结束后的“恢复本机播放”是否已经处理过（只处理一次）。</summary>
    private bool _castResumeHandled = true;

    private bool IsCasting => _cast?.Session.IsActive == true;

    private DlnaCastController EnsureCast()
    {
        if (_cast is null)
        {
            _cast = new DlnaCastController(
                new DlnaDiscovery(CastHttp),
                new DlnaCastSession(new DlnaAvTransport(CastHttp)));
            // 搜索到设备的回调在后台线程：切回界面线程再画。
            _cast.Changed += (_, _) => Dispatcher.BeginInvoke(new Action(RenderCast));
        }

        return _cast;
    }

    private string? CurrentCastUrl() =>
        _episodeIndex >= 0 && _episodeIndex < _episodes.Count ? _episodes[_episodeIndex] : null;

    private void CastButton_Click(object sender, RoutedEventArgs e)
    {
        if (CastPanel.Visibility == Visibility.Visible)
        {
            CloseCastPanel();
            return;
        }

        OpenCastPanel();
    }

    private void CastBadge_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 不让点击冒泡到画面（那里的单击是播放 / 暂停）。
        e.Handled = true;
        if (CastPanel.Visibility != Visibility.Visible)
        {
            OpenCastPanel();
        }
    }

    private void OpenCastPanel()
    {
        SidePanel.Visibility = Visibility.Collapsed;
        CloseSourceSwitchPanel();
        CastPanel.Visibility = Visibility.Visible;
        _osd?.Pin(true);
        var cast = EnsureCast();
        RenderCast();
        if (!cast.Session.IsActive)
        {
            StartCastSearch();
        }
    }

    private void CloseCastPanel()
    {
        if (CastPanel.Visibility != Visibility.Visible)
        {
            return;
        }

        CastPanel.Visibility = Visibility.Collapsed;
        _cast?.CancelSearch();
        _osd?.Pin(false);
    }

    private void StartCastSearch()
    {
        var cast = EnsureCast();
        var invalid = _usingLocalEngine
            ? "本地文件无法投屏，只能投在线片源"
            : DlnaCastSession.ValidateCastUrl(CurrentCastUrl());
        if (invalid is not null)
        {
            // 本地文件、本机地址：电视取不到，不搜索也不发任何请求。
            cast.ShowUnavailable(invalid);
            return;
        }

        _ = cast.SearchAsync();
    }

    private void CastRescanButton_Click(object sender, RoutedEventArgs e) => StartCastSearch();

    private async void CastDeviceButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DlnaDevice device } || _cast is not { } cast || cast.Session.IsActive)
        {
            return;
        }

        var url = CurrentCastUrl();
        var playSeq = _playSeq;
        var start = TimeSpan.FromMilliseconds(Math.Max(0, CurrentResumeContext().PositionMs));
        _castResumeHandled = false;
        var ok = await cast.CastAsync(device, url, TitleText.Text, start);
        if (_disposed)
        {
            return;
        }

        if (ok && playSeq != _playSeq)
        {
            // 连接期间本机已经换集 / 换源：电视上的是旧地址，结束这次投屏。
            EndCastForNavigation();
            return;
        }

        if (ok)
        {
            PauseLocalForCast();
            _castTimer ??= CreateCastTimer();
            _castTimer.Start();
        }
        else
        {
            _castResumeHandled = true;
        }

        RenderCast();
    }

    private DispatcherTimer CreateCastTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += CastTimer_Tick;
        return timer;
    }

    private async void CastTimer_Tick(object? sender, EventArgs e)
    {
        var cast = _cast;
        if (cast is null || _disposed)
        {
            _castTimer?.Stop();
            return;
        }

        if (cast.Session.IsActive)
        {
            // 投屏开始时本机还在加载的话，等它真的播起来再按住。
            PauseLocalForCast();
            await cast.TickAsync();
        }

        if (!cast.Session.IsActive && !_disposed)
        {
            // 电视端自己停了（播完或被遥控器停止）：回到本机播放。
            _castTimer?.Stop();
            FinishCast(resumeLocal: true);
        }
    }

    private void PauseLocalForCast()
    {
        // LibVLC 的 Pause 是“切换”：只在确实在播时调用。
        if (!_usingLocalEngine && _controller is { IsPlaying: true })
        {
            _controller.Pause();
        }
    }

    private void CastPlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsCasting)
        {
            _ = _cast!.Session.TogglePlayAsync();
        }
    }

    private void CastBackButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsCasting)
        {
            _ = _cast!.SeekByAsync(TimeSpan.FromSeconds(-30));
        }
    }

    private void CastForwardButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsCasting)
        {
            _ = _cast!.SeekByAsync(TimeSpan.FromSeconds(30));
        }
    }

    private void CastSeekSlider_DragStarted(object sender, DragStartedEventArgs e) => _castSliderDragging = true;

    private void CastSeekSlider_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _castSliderDragging = false;
        if (IsCasting)
        {
            _ = _cast!.SeekToFractionAsync(CastSeekSlider.Value / 1000);
        }
    }

    private void CastSeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // 拖动中等松手再跳；代码更新进度时不触发跳转；点击轨道（IsMoveToPointEnabled）直接跳。
        if (_castRendering || _castSliderDragging || !IsCasting)
        {
            return;
        }

        _ = _cast!.SeekToFractionAsync(e.NewValue / 1000);
    }

    private void CastStopButton_Click(object sender, RoutedEventArgs e) => _ = StopCastAsync(resumeLocal: true);

    /// <summary>用户点了停止投屏：本机立刻从电视端最后的进度接着播，给电视的 Stop 在后台发，不等它。</summary>
    private async Task StopCastAsync(bool resumeLocal)
    {
        var cast = _cast;
        if (cast is null)
        {
            return;
        }

        _castTimer?.Stop();
        cast.CancelSearch();
        // StopAsync 在发请求之前就把会话状态改成已停止（同步完成的那一段），所以这里可以马上恢复本机播放。
        var stopping = cast.Session.StopAsync();
        FinishCast(resumeLocal);
        await stopping;
    }

    private void FinishCast(bool resumeLocal)
    {
        var cast = _cast;
        if (cast is null)
        {
            return;
        }

        if (!_castResumeHandled)
        {
            _castResumeHandled = true;
            if (resumeLocal)
            {
                ResumeLocalAfterCast(cast.Session.Position);
            }
        }

        RenderCast();
    }

    private void ResumeLocalAfterCast(TimeSpan position)
    {
        if (_disposed || _usingLocalEngine || _controller is null)
        {
            return;
        }

        var positionMs = (long)position.TotalMilliseconds;
        if (positionMs > 0 && _controller.TrySeek(positionMs))
        {
            SeekDanmaku(positionMs);
        }

        if (!_controller.IsPlaying)
        {
            _controller.Play();
        }
    }

    /// <summary>
    /// 离开播放页、打开另一部片、换集 / 换源、关窗口：结束投屏并尽力让电视停下（不等结果），不恢复本机播放。
    /// 电视端的最后进度记成本机进度，随后的保存观看记录用的就是它。定时器在这里停掉。
    /// </summary>
    private void EndCastForNavigation()
    {
        var cast = _cast;
        if (cast is null)
        {
            return;
        }

        _castTimer?.Stop();
        cast.CancelSearch();
        if (cast.Session.IsActive)
        {
            var positionMs = (long)cast.Session.Position.TotalMilliseconds;
            if (positionMs > 0)
            {
                _currentTimeMs = positionMs;
            }

            _castResumeHandled = true;
            _ = cast.Session.StopAsync();
        }

        if (_disposed)
        {
            return;
        }

        CastPanel.Visibility = Visibility.Collapsed;
        RenderCast();
    }

    private void RenderCast()
    {
        if (_disposed || _cast is not { } cast)
        {
            return;
        }

        var session = cast.Session;
        var active = session.IsActive;
        CastBadge.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        CastSessionSection.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        CastDevicesSection.Visibility = active ? Visibility.Collapsed : Visibility.Visible;

        if (active)
        {
            var deviceName = session.Device?.Name ?? "";
            CastBadgeText.Text = $"正在投屏到 {deviceName} · {cast.CastStatusText} · {cast.ProgressText}";
            CastDeviceNameText.Text = deviceName;
            CastStateText.Text = cast.CastStatusText;
            CastErrorText.Text = session.Error ?? "";
            CastErrorText.Visibility = string.IsNullOrEmpty(session.Error) ? Visibility.Collapsed : Visibility.Visible;
            CastTimeText.Text = cast.ProgressText;
            CastPlayPauseButton.Content = session.State == DlnaCastState.Paused ? "继续播放" : "暂停";
            CastSeekSlider.IsEnabled = session.Duration > TimeSpan.Zero;
            if (!_castSliderDragging)
            {
                _castRendering = true;
                CastSeekSlider.Value = cast.ProgressFraction * 1000;
                _castRendering = false;
            }

            // 底部控制条在投屏期间显示并控制电视端的进度（本机播放器是暂停的）。
            PlayPauseButton.Content = session.State == DlnaCastState.Paused ? PlayGlyph : PauseGlyph;
            PositionText.Text = FormatTime((long)session.Position.TotalMilliseconds);
            if (session.Duration > TimeSpan.Zero)
            {
                DurationText.Text = FormatTime((long)session.Duration.TotalMilliseconds);
                if (!_draggingSlider)
                {
                    _updatingSlider = true;
                    SeekSlider.Value = cast.ProgressFraction * 1000;
                    _updatingSlider = false;
                }
            }

            return;
        }

        // 未投屏：上一次投屏的结局（失败原因 / 已停止） + 搜索状态 + 设备列表。
        var notice = session.State is DlnaCastState.Error or DlnaCastState.Stopped ? cast.CastStatusText : "";
        if (session.State == DlnaCastState.Stopped)
        {
            notice += "，已回到本机播放";
        }

        CastNoticeText.Text = notice;
        CastNoticeText.Visibility = notice.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        CastSearchText.Text = cast.SearchStatusText;
        CastSearchProgress.Visibility = cast.IsSearching ? Visibility.Visible : Visibility.Collapsed;
        CastRescanButton.IsEnabled = !cast.IsSearching;
        CastDeviceList.Children.Clear();
        foreach (var device in cast.Devices)
        {
            var host = Uri.TryCreate(device.Location, UriKind.Absolute, out var location) ? location.Host : "";
            var detail = string.Join(" · ", new[] { device.ModelName, host }.Where(part => !string.IsNullOrWhiteSpace(part)));
            var button = new Button
            {
                Content = detail.Length > 0 ? $"{device.Name}\n{detail}" : device.Name,
                Tag = device,
                Style = (Style)FindResource("SecondaryButtonStyle"),
                MinHeight = 52,
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(0, 0, 0, 8),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                ToolTip = "投屏到这台设备",
            };
            button.Click += CastDeviceButton_Click;
            CastDeviceList.Children.Add(button);
        }
    }
}
