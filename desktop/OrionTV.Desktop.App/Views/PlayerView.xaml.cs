using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using OrionTV.Desktop.Core;
using OrionTV.Desktop.App.LocalPlayback;

namespace OrionTV.Desktop.App.Views;

public partial class PlayerView : UserControl, INavigationAware, IDisposable
{
    private enum LoadingNoticeMode
    {
        None,
        Elapsed,
        Countdown,
    }

    private const string PlayGlyph = "";
    private const string PauseGlyph = "";
    private const string VolumeGlyph = "";
    private const string MuteGlyph = "";
    private const string FullscreenGlyph = "";
    private const string ExitFullscreenGlyph = "";

    private readonly IAppHost _host;

    private PlaybackController? _controller;
    private MpvLocalEngine? _localEngine;
    private bool _usingLocalEngine;
    private OsdController? _osd;
    private DispatcherTimer? _singleClickTimer;
    private DispatcherTimer? _startupTimer;
    private DispatcherTimer? _loadingNoticeTimer;
    private readonly PlaybackStartupWatchdog _startupWatchdog = new();

    private string _title = "";
    private string _id = "";
    private string _sourceKey = "";
    private string _sourceName = "";
    private string? _playbackSourceKey;
    private string _poster = "";
    private List<string> _episodes = new();
    private int _episodeIndex;
    private List<SearchResult> _altSources = new();
    private readonly HashSet<string> _failedSources = new(StringComparer.Ordinal);

    private long _lengthMs;
    private long _currentTimeMs;
    private long _pendingSeekMs;
    private long _lastSavedSecond;
    private bool _draggingSlider;
    private bool _updatingSlider;
    private bool _disposed;
    private bool _initializingController;

    private LoadingNoticeMode _loadingNoticeMode;
    private DateTimeOffset _loadingNoticeStartedAt;
    private DateTimeOffset? _loadingNoticeDeadline;
    private int _loadingNoticePlaySeq;
    private string _loadingNoticeDetail = "";

    private List<AdRange> _adRanges = new();
    private double _lastSkippedAdEndMs = -1;
    private DispatcherTimer? _adBadgeTimer;
    private int _playSeq;

    public PlayerView(IAppHost host)
    {
        InitializeComponent();
        _host = host;
        Loaded += OnLoaded;
        _loadingNoticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _loadingNoticeTimer.Tick += LoadingNoticeTimer_Tick;
    }

    public void OnNavigatedTo(object? parameter)
    {
        if (parameter is not PlayParameter request)
        {
            return;
        }

        _title = request.Title;
        _id = request.Id;
        _sourceKey = request.Source;
        _sourceName = request.SourceName;
        _playbackSourceKey = request.PlaybackSourceKey;
        _poster = request.Poster;
        _episodes = request.Episodes.ToList();
        _episodeIndex = Math.Clamp(request.EpisodeIndex, 0, Math.Max(0, _episodes.Count - 1));
        _altSources = request.AltSources.ToList();
        _failedSources.Clear();
        _pendingSeekMs = (long)request.PositionMs;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_controller is not null || _initializingController)
        {
            return;
        }

        _initializingController = true;
        var coldStart = !_host.Services.IsLibVlcInitialized;
        if (coldStart)
        {
            ShowElapsedLoadingNotice(
                "第 1/2 步 · 正在启动播放器",
                "首次启动需要加载播放组件，程序仍在工作，请稍候…",
                playSeq: 0);
            await Dispatcher.Yield(DispatcherPriority.Render);
        }

        LibVLCSharp.Shared.LibVLC libVlc;
        try
        {
            libVlc = await _host.Services.GetLibVlcAsync();
        }
        catch
        {
            _initializingController = false;
            if (!_disposed)
            {
                HideLoadingNotice();
                ErrorText.Text = "播放器组件启动失败，请重试";
                ErrorOverlay.Visibility = Visibility.Visible;
            }

            return;
        }

        _initializingController = false;
        if (_disposed)
        {
            return;
        }

        _controller = new PlaybackController(libVlc, Dispatcher);
        VideoSurface.MediaPlayer = _controller.Player;
        _controller.StateChanged += Controller_StateChanged;
        _controller.TimeChanged += Controller_TimeChanged;
        _controller.LengthChanged += Controller_LengthChanged;
        _controller.EndReached += Controller_EndReached;

        var volume = Math.Clamp(_host.Services.Settings.Volume, 0, 100);
        _controller.Volume = volume;
        _updatingSlider = true;
        VolumeSlider.Value = volume;
        _updatingSlider = false;
        UpdateMuteIcon();

        _singleClickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _singleClickTimer.Tick += (_, _) =>
        {
            _singleClickTimer!.Stop();
            ToggleActivePlayPause();
        };

        _startupTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _startupTimer.Tick += StartupTimer_Tick;

        _osd = new OsdController(OverlayRoot, TopBar, BottomBar);
        _osd.Show();
        Focus();

        BuildSourceChips();
        BuildEpisodeButtons();

        if (_episodes.Count > 0)
        {
            if (coldStart)
            {
                ShowElapsedLoadingNotice(
                    "第 2/2 步 · 正在准备视频",
                    "播放器已启动，正在检查片源与缓存…",
                    playSeq: 0);
                await Dispatcher.Yield(DispatcherPriority.Render);
            }

            PlayEpisode(_episodeIndex, _pendingSeekMs);
        }
        else
        {
            HideLoadingNotice();
            TitleText.Text = _title;
        }
    }

    private void PlayEpisode(int index, long seekMs = 0) => _ = PlayEpisodeAsync(index, seekMs);

    private async Task PlayEpisodeAsync(int index, long seekMs = 0)
    {
        if (_controller is null || index < 0 || index >= _episodes.Count)
        {
            return;
        }

        _episodeIndex = index;
        var playSeq = ++_playSeq;
        ShowElapsedLoadingNotice(
            "第 2/2 步 · 正在准备视频",
            "正在连接播放源，程序没有卡死…",
            playSeq);
        _lengthMs = 0;
        _currentTimeMs = 0;
        _lastSavedSecond = 0;
        _adRanges = new List<AdRange>();
        _lastSkippedAdEndMs = -1;
        TitleText.Text = $"{_title}  第 {index + 1} 集";
        HighlightEpisode();

        var url = _episodes[index];
        if (string.IsNullOrWhiteSpace(url))
        {
            HideLoadingNotice();
            ErrorText.Text = "该集暂无可用地址";
            ErrorOverlay.Visibility = Visibility.Visible;
            return;
        }

        var isRemote = IsRemoteHttpUrl(url);
        if (_host.Services.Settings.AutoSkipAds && isRemote)
        {
            _ = LoadAdRangesAsync(_playSeq, url);
        }

        if (_disposed || playSeq != _playSeq)
        {
            return;
        }

        ArmStartupWatchdog();
        ShowCountdownLoadingNotice(
            "第 2/2 步 · 正在等待画面",
            isRemote
                ? "正在连接播放源并等待首帧；超时后会自动尝试其他播放源"
                : "正在打开本地文件（便携 mpv 内嵌）…",
            _startupWatchdog.Timeout,
            playSeq);

        // 本地：便携 mpv 内嵌（FFmpeg demux + d3d11va）；无 mpv 时回退 LibVLC。
        // 在线：始终 LibVLC。不再默认外启 PotPlayer。
        if (!isRemote && TryEnsureLocalEngine())
        {
            try
            {
                UseLocalSurface(true);
                await _localEngine!.SwitchToAsync(url, seekMs).ConfigureAwait(true);
                return;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Local mpv failed, fallback VLC: {ex.Message}");
                UseLocalSurface(false);
            }
        }
        else
        {
            UseLocalSurface(false);
        }

        if (_disposed || playSeq != _playSeq || _controller is null)
        {
            return;
        }

        var networkCachingMs = isRemote ? 1500 : 300;
        _controller.SwitchTo(url, seekMs, networkCachingMs);
        await Task.CompletedTask;
    }

    private bool TryEnsureLocalEngine()
    {
        if (!MpvLocalEngine.IsAvailable())
        {
            return false;
        }

        if (_localEngine is not null)
        {
            return true;
        }

        LocalVideoHost.Visibility = Visibility.Visible;
        LocalVideoHost.UpdateLayout();
        var hwnd = LocalVideoHost.HostHandle;
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        _localEngine = new MpvLocalEngine(hwnd, Dispatcher);
        _localEngine.StateChanged += Controller_StateChanged;
        _localEngine.TimeChanged += Controller_TimeChanged;
        _localEngine.LengthChanged += Controller_LengthChanged;
        _localEngine.EndReached += Controller_EndReached;
        _localEngine.Volume = (int)Math.Round(VolumeSlider.Value);
        return true;
    }

    private void UseLocalSurface(bool local)
    {
        _usingLocalEngine = local;
        if (local)
        {
            LocalVideoHost.Visibility = Visibility.Visible;
            try { _controller?.Stop(); } catch { }
            VideoSurface.Visibility = Visibility.Collapsed;
        }
        else
        {
            VideoSurface.Visibility = Visibility.Visible;
            LocalVideoHost.Visibility = Visibility.Collapsed;
            try { _localEngine?.Stop(); } catch { }
        }
    }

    private static bool IsRemoteHttpUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https";

    private void ToggleActivePlayPause()
    {
        if (_usingLocalEngine && _localEngine is not null)
        {
            _localEngine.TogglePlayPause();
            return;
        }

        _controller?.TogglePlayPause();
    }

    private void SetActiveVolume(int volume)
    {
        if (_usingLocalEngine && _localEngine is not null)
        {
            _localEngine.Volume = volume;
            return;
        }

        if (_controller is not null)
        {
            _controller.Volume = volume;
        }
    }

    private void SetActiveMute(bool mute)
    {
        if (_usingLocalEngine && _localEngine is not null)
        {
            _localEngine.Mute = mute;
            return;
        }

        if (_controller is not null)
        {
            _controller.Mute = mute;
        }
    }

    private bool IsActiveMuted() =>
        _usingLocalEngine && _localEngine is not null
            ? _localEngine.Mute
            : _controller?.Mute ?? false;

    private void ShowElapsedLoadingNotice(string title, string detail, int playSeq)
    {
        SetLoadingNotice(title, detail, LoadingNoticeMode.Elapsed, null, playSeq);
    }

    private void ShowCountdownLoadingNotice(string title, string detail, TimeSpan timeout, int playSeq)
    {
        SetLoadingNotice(title, detail, LoadingNoticeMode.Countdown, timeout, playSeq);
    }

    private void SetLoadingNotice(
        string title,
        string detail,
        LoadingNoticeMode mode,
        TimeSpan? timeout,
        int playSeq)
    {
        _loadingNoticeMode = mode;
        _loadingNoticeStartedAt = DateTimeOffset.UtcNow;
        _loadingNoticeDeadline = mode == LoadingNoticeMode.Countdown && timeout is { } value
            ? _loadingNoticeStartedAt + value
            : null;
        _loadingNoticePlaySeq = playSeq;
        _loadingNoticeDetail = detail;
        LoadingStageText.Text = title;
        ErrorOverlay.Visibility = Visibility.Collapsed;
        LoadingOverlay.Visibility = Visibility.Visible;
        UpdateLoadingNotice();
        _loadingNoticeTimer?.Start();
    }

    private void LoadingNoticeTimer_Tick(object? sender, EventArgs e)
    {
        if (_loadingNoticeMode == LoadingNoticeMode.None)
        {
            return;
        }

        if (_loadingNoticePlaySeq != 0 && _loadingNoticePlaySeq != _playSeq)
        {
            return;
        }

        UpdateLoadingNotice();
    }

    private void UpdateLoadingNotice()
    {
        var elapsed = Math.Max(0, (int)(DateTimeOffset.UtcNow - _loadingNoticeStartedAt).TotalSeconds);
        if (_loadingNoticeMode == LoadingNoticeMode.Countdown && _loadingNoticeDeadline is { } deadline)
        {
            var remaining = Math.Max(0, (int)Math.Ceiling((deadline - DateTimeOffset.UtcNow).TotalSeconds));
            var total = Math.Max(0, (int)Math.Ceiling((deadline - _loadingNoticeStartedAt).TotalSeconds));
            LoadingText.Text = remaining > 0
                ? $"{_loadingNoticeDetail}\n最长等待 {total} 秒，剩余约 {remaining} 秒；程序仍在工作，请勿重复点击。"
                : $"{_loadingNoticeDetail}\n等待时间比预期稍长，程序仍在工作，请继续等待。";
            return;
        }

        LoadingText.Text = $"{_loadingNoticeDetail}\n已等待 {elapsed} 秒；程序仍在工作，请稍候。";
    }

    private void HideLoadingNotice()
    {
        _loadingNoticeMode = LoadingNoticeMode.None;
        _loadingNoticeDeadline = null;
        _loadingNoticeTimer?.Stop();
        LoadingOverlay.Visibility = Visibility.Collapsed;
        LoadingStageText.Text = "";
        LoadingText.Text = "";
    }

    private async Task LoadAdRangesAsync(int forSeq, string url)
    {
        try
        {
            var ranges = await _host.Services.Hls.GetAdRangesAsync(url);
            // 按播放代次守卫:换源/换集/重试后旧 URL 的广告区间不得套在新时间轴上。
            if (!_disposed && _playSeq == forSeq)
            {
                _adRanges = ranges.ToList();
                // 区间加载完成后立即检查一次：覆盖"续播位置正好落在广告内"以及加载晚于首次进度回调的情况。
                MaybeSkipAd();
            }
        }
        catch
        {
            // 广告区间检测失败时不跳过。
        }
    }

    private void Controller_StateChanged(object? sender, PlaybackState state)
    {
        if (state == PlaybackState.Error)
        {
            DisarmStartupWatchdog();
            if (TryPlayFallback())
            {
                return;
            }

            HideLoadingNotice();
        }
        else if (PlaybackStartupPolicy.ResolvesStartup(ToStartupSignal(state)))
        {
            DisarmStartupWatchdog();
            HideLoadingNotice();
        }
        else if (state is PlaybackState.Opening or PlaybackState.Buffering)
        {
            if (_loadingNoticeMode == LoadingNoticeMode.None)
            {
                ShowElapsedLoadingNotice(
                    state == PlaybackState.Buffering ? "正在缓冲视频" : "正在打开视频",
                    "正在等待视频数据到达…",
                    _playSeq);
            }
            else
            {
                LoadingOverlay.Visibility = Visibility.Visible;
            }
        }

        PlayPauseButton.Content = state == PlaybackState.Playing ? PauseGlyph : PlayGlyph;
        ErrorOverlay.Visibility = state == PlaybackState.Error ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Controller_LengthChanged(object? sender, long length)
    {
        _lengthMs = length;
        DurationText.Text = FormatTime(_lengthMs);
    }

    private void Controller_TimeChanged(object? sender, long time)
    {
        if (time > 0 && PlaybackStartupPolicy.ResolvesStartup(PlaybackStartupSignal.TimeAdvanced))
        {
            DisarmStartupWatchdog();
            HideLoadingNotice();
        }

        _currentTimeMs = time;

        if (!_draggingSlider)
        {
            _updatingSlider = true;
            SeekSlider.Value = _lengthMs > 0 ? (double)_currentTimeMs / _lengthMs * 1000 : 0;
            _updatingSlider = false;
            PositionText.Text = FormatTime(_currentTimeMs);
        }

        MaybeSkipAd();
        SaveRecordThrottled();
    }

    private static PlaybackStartupSignal ToStartupSignal(PlaybackState state) => state switch
    {
        PlaybackState.Opening => PlaybackStartupSignal.Opening,
        PlaybackState.Buffering => PlaybackStartupSignal.Buffering,
        PlaybackState.Playing => PlaybackStartupSignal.Playing,
        PlaybackState.Paused => PlaybackStartupSignal.Paused,
        PlaybackState.Ended => PlaybackStartupSignal.Ended,
        PlaybackState.Error => PlaybackStartupSignal.Error,
        _ => PlaybackStartupSignal.Opening,
    };

    private void ArmStartupWatchdog()
    {
        _startupWatchdog.Arm(DateTimeOffset.UtcNow);
        _startupTimer?.Stop();
        _startupTimer?.Start();
    }

    private void DisarmStartupWatchdog()
    {
        _startupWatchdog.Disarm();
        _startupTimer?.Stop();
    }

    private void StartupTimer_Tick(object? sender, EventArgs e)
    {
        if (!_startupWatchdog.IsTimedOut(DateTimeOffset.UtcNow))
        {
            return;
        }

        DisarmStartupWatchdog();
        if (TryPlayFallback())
        {
            return;
        }

        _controller?.Stop();
        HideLoadingNotice();
        ErrorOverlay.Visibility = Visibility.Visible;
        PlayPauseButton.Content = PlayGlyph;
    }

    private void MaybeSkipAd()
    {
        if (_controller is null || _adRanges.Count == 0 || !_host.Services.Settings.AutoSkipAds)
        {
            return;
        }

        var range = _adRanges.FirstOrDefault(r => _currentTimeMs >= r.StartMs && _currentTimeMs < r.EndMs);
        if (range is not null)
        {
            // 只有 seek 真正生效才消费跳过标记:时长未知时 LibVLC 会丢弃 seek,
            // 此时保留标记让后续进度回调重试,否则片头广告会完整播完。
            if (_lastSkippedAdEndMs != range.EndMs && _controller.TrySeek((long)range.EndMs))
            {
                _lastSkippedAdEndMs = range.EndMs;
                ShowAdSkipBadge();
            }
        }
        else if (_lastSkippedAdEndMs >= 0)
        {
            _lastSkippedAdEndMs = -1;
        }
    }

    private void ShowAdSkipBadge()
    {
        if (_adBadgeTimer is null)
        {
            _adBadgeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            _adBadgeTimer.Tick += (_, _) =>
            {
                _adBadgeTimer!.Stop();
                AdSkipBadge.Visibility = Visibility.Collapsed;
            };
        }

        AdSkipBadge.Visibility = Visibility.Visible;
        _adBadgeTimer.Stop();
        _adBadgeTimer.Start();
    }

    private void Controller_EndReached(object? sender, EventArgs e)
    {
        if (_episodeIndex + 1 < _episodes.Count)
        {
            PlayEpisode(_episodeIndex + 1);
        }
    }

    private void BuildSourceChips()
    {
        if (_altSources.Count <= 1)
        {
            SourceBar.Visibility = Visibility.Collapsed;
            return;
        }

        SourceBar.Children.Clear();
        foreach (var source in _altSources)
        {
            var label = source.Episodes.Count > 1 ? $"{source.SourceName} · {source.Episodes.Count}集" : source.SourceName;
            var chip = new RadioButton
            {
                Style = (Style)FindResource("ChipRadioStyle"),
                GroupName = "playerSource",
                Content = label,
                Tag = source,
                IsChecked = SearchResultSources.SelectionKeyOf(source)
                    == SearchResultSources.SelectionKeyOf(_sourceKey, _id, _playbackSourceKey),
            };
            chip.Checked += SourceChip_Checked;
            SourceBar.Children.Add(chip);
        }
    }

    private void SourceChip_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: SearchResult source }
            || SearchResultSources.SelectionKeyOf(source)
            == SearchResultSources.SelectionKeyOf(_sourceKey, _id, _playbackSourceKey))
        {
            return;
        }

        _sourceKey = source.Source;
        _sourceName = source.SourceName;
        _playbackSourceKey = source.PlaybackSourceKey;
        _id = source.Id;
        _poster = source.Poster;
        _episodes = source.Episodes.ToList();
        var index = Math.Clamp(_episodeIndex, 0, Math.Max(0, _episodes.Count - 1));
        BuildEpisodeButtons();
        PlayEpisode(index);
    }

    private bool TryPlayFallback()
    {
        if (_altSources.Count <= 1 || string.IsNullOrEmpty(_sourceKey))
        {
            return false;
        }

        _failedSources.Add(SearchResultSources.SelectionKeyOf(_sourceKey, _id, _playbackSourceKey));
        var fallback = PlaybackFallbackSelector.SelectNext(
            _altSources,
            SearchResultSources.SelectionKeyOf(_sourceKey, _id, _playbackSourceKey),
            _failedSources,
            _episodeIndex);
        if (fallback is null)
        {
            return false;
        }

        _sourceKey = fallback.Source;
        _sourceName = fallback.SourceName;
        _playbackSourceKey = fallback.PlaybackSourceKey;
        _id = fallback.Id;
        _poster = fallback.Poster;
        _episodes = fallback.Episodes.ToList();
        BuildSourceChips();
        BuildEpisodeButtons();
        PlayEpisode(Math.Clamp(_episodeIndex, 0, Math.Max(0, _episodes.Count - 1)));
        return true;
    }

    private void BuildEpisodeButtons()
    {
        EpisodeBar.Children.Clear();
        for (var index = 0; index < _episodes.Count; index++)
        {
            var button = new Button
            {
                Content = $"{index + 1}",
                Style = (Style)FindResource("SecondaryButtonStyle"),
                Height = 36,
                MinWidth = 48,
                Padding = new Thickness(8, 0, 8, 0),
                Margin = new Thickness(0, 0, 8, 8),
                FontSize = 13,
                Tag = index,
            };
            button.Click += EpisodeButton_Click;
            EpisodeBar.Children.Add(button);
        }

        HighlightEpisode();
    }

    private void HighlightEpisode()
    {
        foreach (var child in EpisodeBar.Children)
        {
            if (child is Button button && button.Tag is int index)
            {
                button.Style = (Style)FindResource(index == _episodeIndex ? "PrimaryButtonStyle" : "SecondaryButtonStyle");
                button.Height = 36;
                button.MinWidth = 48;
                button.Padding = new Thickness(8, 0, 8, 0);
                button.Margin = new Thickness(0, 0, 8, 8);
                button.FontSize = 13;
            }
        }
    }

    private void EpisodeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int index })
        {
            PlayEpisode(index);
        }
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e) => ToggleActivePlayPause();

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_usingLocalEngine)
        {
            _localEngine?.Stop();
        }
        else
        {
            _controller?.Stop();
        }

        _playSeq++;
        HideLoadingNotice();
        PlayPauseButton.Content = PlayGlyph;
    }

    private void PrevButton_Click(object sender, RoutedEventArgs e)
    {
        if (_episodeIndex > 0)
        {
            PlayEpisode(_episodeIndex - 1);
        }
    }

    private void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_episodeIndex + 1 < _episodes.Count)
        {
            PlayEpisode(_episodeIndex + 1);
        }
    }

    private void EpisodesButton_Click(object sender, RoutedEventArgs e)
    {
        var show = SidePanel.Visibility != Visibility.Visible;
        SidePanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        _osd?.Pin(show);
    }

    private void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        ErrorOverlay.Visibility = Visibility.Collapsed;
        if (_controller is not null)
        {
            PlayEpisode(_episodeIndex);
        }
        else
        {
            OnLoaded(this, new RoutedEventArgs());
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) => _host.GoBack();

    private void SeekSlider_DragStarted(object sender, DragStartedEventArgs e) => _draggingSlider = true;

    private void SeekSlider_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _draggingSlider = false;
        SeekTo(SeekSlider.Value);
    }

    private void SeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingSlider)
        {
            return;
        }

        if (_draggingSlider)
        {
            if (_lengthMs > 0)
            {
                PositionText.Text = FormatTime((long)(e.NewValue / 1000 * _lengthMs));
            }

            return;
        }

        SeekTo(e.NewValue);
    }

    private void SeekTo(double sliderValue)
    {
        if (_lengthMs <= 0)
        {
            return;
        }

        var target = (long)(sliderValue / 1000 * _lengthMs);
        if (_usingLocalEngine && _localEngine is not null)
        {
            _localEngine.TrySeek(target);
            return;
        }

        if (_controller is not null)
        {
            _controller.Time = target;
        }
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_controller is null || _updatingSlider)
        {
            return;
        }

        SetActiveVolume((int)e.NewValue);
        if (e.NewValue > 0 && IsActiveMuted())
        {
            SetActiveMute(false);
        }

        UpdateMuteIcon();
    }

    private void MuteButton_Click(object sender, RoutedEventArgs e) => ToggleMute();

    private void ToggleMute()
    {
        SetActiveMute(!IsActiveMuted());
        UpdateMuteIcon();
    }

    private void UpdateMuteIcon() =>
        MuteButton.Content = IsActiveMuted() || VolumeSlider.Value <= 0 ? MuteGlyph : VolumeGlyph;

    private void AdjustVolume(int delta)
    {
        VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + delta, 0, 100);
    }

    private void FullscreenButton_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void ToggleFullscreen()
    {
        _host.SetFullscreen(!_host.IsFullscreen);
        FullscreenButton.Content = _host.IsFullscreen ? ExitFullscreenGlyph : FullscreenGlyph;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Space:
                ToggleActivePlayPause();
                break;
            case Key.Left:
                SeekBy(-5000);
                break;
            case Key.Right:
                SeekBy(5000);
                break;
            case Key.Up:
                AdjustVolume(5);
                break;
            case Key.Down:
                AdjustVolume(-5);
                break;
            case Key.F:
                ToggleFullscreen();
                break;
            case Key.M:
                ToggleMute();
                break;
            case Key.Escape:
                if (_host.IsFullscreen)
                {
                    ToggleFullscreen();
                }
                else
                {
                    _host.GoBack();
                }

                break;
            default:
                return;
        }

        _osd?.Show();
        e.Handled = true;
    }

    private void SeekBy(long deltaMs)
    {
        if (_lengthMs <= 0)
        {
            return;
        }

        var target = Math.Clamp(_currentTimeMs + deltaMs, 0, _lengthMs);
        if (_usingLocalEngine && _localEngine is not null)
        {
            _localEngine.TrySeek(target);
            return;
        }

        if (_controller is not null)
        {
            _controller.Time = target;
        }
    }

    private void Overlay_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // 仅当点击落在视频区（根容器本身，而非控制栏/遮罩等子元素）时才响应单击/双击。
        if (!ReferenceEquals(e.OriginalSource, OverlayRoot))
        {
            return;
        }

        Focus();
        _osd?.Show();

        if (e.ClickCount == 2)
        {
            _singleClickTimer?.Stop();
            ToggleFullscreen();
        }
        else
        {
            _singleClickTimer?.Stop();
            _singleClickTimer?.Start();
        }
    }

    private void Overlay_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        AdjustVolume(e.Delta > 0 ? 5 : -5);
        _osd?.Show();
    }

    private void Bar_MouseEnter(object sender, MouseEventArgs e) => _osd?.Pin(true);

    private void Bar_MouseLeave(object sender, MouseEventArgs e) =>
        _osd?.Pin(SidePanel.Visibility == Visibility.Visible);

    private void SaveRecordThrottled()
    {
        var second = _currentTimeMs / 1000;
        if (second == _lastSavedSecond || second % 5 != 0 || string.IsNullOrEmpty(_sourceKey) || string.IsNullOrEmpty(_id))
        {
            return;
        }

        _lastSavedSecond = second;
        SaveRecord();
    }

    private void SaveRecord()
    {
        if (string.IsNullOrEmpty(_sourceKey) || string.IsNullOrEmpty(_id) || _currentTimeMs <= 0)
        {
            return;
        }

        try
        {
            var isLocal = string.Equals(_sourceKey, "local", StringComparison.OrdinalIgnoreCase);
            var poster = _poster;
            if (isLocal && string.IsNullOrWhiteSpace(poster))
            {
                poster = _host.Services.LocalMedia.Get(_id)?.Poster ?? "";
            }

            _host.Services.PlayRecords.Save(_sourceKey, _id, new PlayRecordItem(
                Title: _title,
                SourceName: _sourceName,
                Cover: poster,
                Index: _episodeIndex,
                TotalEpisodes: _episodes.Count,
                PlayTime: _currentTimeMs / 1000.0,
                TotalTime: _lengthMs / 1000.0,
                Year: "",
                LocalMediaId: isLocal ? _id : null,
                LocalEpisodes: isLocal ? _episodes.ToArray() : null));
        }
        catch
        {
            // 保存记录失败忽略。
        }
    }

    private void SaveVolumeSetting()
    {
        var volume = (int)Math.Round(VolumeSlider.Value);
        var settings = _host.Services.Settings;
        if (settings.Volume == volume)
        {
            return;
        }

        try
        {
            _host.Services.Settings = settings with { Volume = volume };
            SettingsStore.Save(_host.Services.Settings);
        }
        catch
        {
            // 保存音量失败忽略。
        }
    }

    private static string FormatTime(long milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"mm\:ss");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _playSeq++;
        _adBadgeTimer?.Stop();
        _singleClickTimer?.Stop();
        _startupTimer?.Stop();
        _loadingNoticeTimer?.Stop();
        SaveVolumeSetting();
        SaveRecord();

        if (_localEngine is not null)
        {
            _localEngine.StateChanged -= Controller_StateChanged;
            _localEngine.TimeChanged -= Controller_TimeChanged;
            _localEngine.LengthChanged -= Controller_LengthChanged;
            _localEngine.EndReached -= Controller_EndReached;
            _localEngine.Dispose();
            _localEngine = null;
        }

        if (_host.IsFullscreen)
        {
            _host.SetFullscreen(false);
        }

        if (_controller is not null)
        {
            _controller.StateChanged -= Controller_StateChanged;
            _controller.TimeChanged -= Controller_TimeChanged;
            _controller.LengthChanged -= Controller_LengthChanged;
            _controller.EndReached -= Controller_EndReached;
            VideoSurface.MediaPlayer = null;
            _host.Services.DisposePlaybackInBackground(_controller);
            _controller = null;
        }
    }
}
