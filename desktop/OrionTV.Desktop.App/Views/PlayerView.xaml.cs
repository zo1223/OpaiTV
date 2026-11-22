using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using OrionTV.Desktop.Core;
using OrionTV.Desktop.App.LocalPlayback;

namespace OrionTV.Desktop.App.Views;

internal enum PlayerShortcutCommand
{
    TogglePlayPause,
    SeekBackward,
    SeekForward,
    VolumeUp,
    VolumeDown,
    ToggleFullscreen,
    ToggleMute,
    Escape,
}

internal static class PlayerShortcutMap
{
    internal static bool TryResolve(Key key, ModifierKeys modifiers, out PlayerShortcutCommand command)
    {
        command = default;
        if (modifiers != ModifierKeys.None)
        {
            return false;
        }

        command = key switch
        {
            Key.Space => PlayerShortcutCommand.TogglePlayPause,
            Key.Left => PlayerShortcutCommand.SeekBackward,
            Key.Right => PlayerShortcutCommand.SeekForward,
            Key.Up => PlayerShortcutCommand.VolumeUp,
            Key.Down => PlayerShortcutCommand.VolumeDown,
            Key.F => PlayerShortcutCommand.ToggleFullscreen,
            Key.M => PlayerShortcutCommand.ToggleMute,
            Key.Escape => PlayerShortcutCommand.Escape,
            _ => default,
        };
        return key is Key.Space or Key.Left or Key.Right or Key.Up or Key.Down or Key.F or Key.M or Key.Escape;
    }
}

internal sealed record PlayerSourceSwitchCandidate(
    SearchResult Source,
    int EpisodeIndex,
    SearchResult ProbeSource);

internal static class PlayerSourceSwitchPolicy
{
    internal static IReadOnlyList<PlayerSourceSwitchCandidate> SelectCandidates(
        IEnumerable<SearchResult> sources,
        PlaybackResumeContext target)
    {
        var result = new List<PlayerSourceSwitchCandidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            var episodeIndex = target.FindEpisodeIndex(source.EpisodeLabels, source.Episodes.Count);
            if (episodeIndex < 0
                || episodeIndex >= source.Episodes.Count
                || string.IsNullOrWhiteSpace(source.Episodes[episodeIndex]))
            {
                continue;
            }

            var key = SearchResultSources.SelectionKeyOf(source);
            if (!seen.Add(key))
            {
                continue;
            }

            var label = EpisodeLabelFormatter.DisplayAt(source.EpisodeLabels, episodeIndex);
            var probeSource = source with
            {
                Episodes = [source.Episodes[episodeIndex]],
                EpisodeLabels = [label],
                PlaybackSources = null,
            };
            result.Add(new PlayerSourceSwitchCandidate(source, episodeIndex, probeSource));
        }

        return result;
    }
}

internal static class PlayerEpisodeAdvancePolicy
{
    private const long EndToleranceMs = 10_000;

    internal static bool ShouldAdvance(bool playbackConfirmed, long currentTimeMs, long lengthMs) =>
        playbackConfirmed
        && lengthMs > 0
        && currentTimeMs >= Math.Max(0, lengthMs - EndToleranceMs);
}

public partial class PlayerView : UserControl, INavigationAware, IDisposable
{
    private enum LoadingNoticeMode
    {
        None,
        Elapsed,
        Countdown,
    }

    private const int WmKeyDown = 0x0100;

    private const string PlayGlyph = "";
    private const string PauseGlyph = "";
    private const string VolumeGlyph = "";
    private const string MuteGlyph = "";
    private const string FullscreenGlyph = "";
    private const string ExitFullscreenGlyph = "";

    private readonly IAppHost _host;

    private PlaybackController? _controller;
    private MpvLocalEngine? _localEngine;
    private PlayerKeyboardHook? _keyboardHook;
    private bool _usingLocalEngine;
    private OsdController? _osd;
    private DispatcherTimer? _singleClickTimer;
    private DispatcherTimer? _startupTimer;
    private DispatcherTimer? _loadingNoticeTimer;
    private readonly PlaybackStartupWatchdog _startupWatchdog = new();

    private string _title = "";
    private string _id = "";
    private string _sourceKey = "";

    /// <summary>当前播放内容所属的源 key（成人内容开关关闭时据此关闭正在播放的成人源）。</summary>
    internal string CurrentSourceKey => _sourceKey;
    private string _sourceName = "";
    private string? _playbackSourceKey;
    private string _poster = "";
    private List<string> _episodes = new();
    private List<string> _episodeLabels = new();
    private int _episodeIndex;

    private long _lengthMs;
    private long _currentTimeMs;
    private long _pendingSeekMs;
    private long _lastSavedSecond;
    private bool _draggingSlider;
    private bool _updatingSlider;
    private bool _disposed;
    private bool _initializingController;
    private bool _restartOnNextLoad;
    private bool _nativeShortcutHookAttached;
    private int _localLayoutRefreshVersion;
    private bool _awaitingVideoOutput;
    private long _currentPlaybackStartMs;
    private PlaybackResumeContext? _resumeContext;
    private string? _historySource;
    private string? _historyId;
    private bool _returningToDetail;
    private bool _playbackConfirmed;
    private bool _resumePositionReached;

    private CancellationTokenSource? _sourceSwitchCts;
    private IReadOnlyList<PlayerSourceSwitchCandidate> _sourceSwitchCandidates = [];
    private readonly Dictionary<string, VodSourceHealthResult> _sourceSwitchHealth = new(StringComparer.Ordinal);

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
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnVisibilityChanged;
        _loadingNoticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _loadingNoticeTimer.Tick += LoadingNoticeTimer_Tick;
    }

    public void OnNavigatedTo(object? parameter)
    {
        if (parameter is not PlayParameter request)
        {
            return;
        }

        CancelSourceSwitchScan();
        SourceSwitchPanel.Visibility = Visibility.Collapsed;
        SidePanel.Visibility = Visibility.Collapsed;

        var wasInitialized = _controller is not null || _localEngine is not null;
        if (wasInitialized)
        {
            SaveRecord();
        }

        _title = request.Title;
        _id = request.Id;
        _sourceKey = request.Source;
        _sourceName = request.SourceName;
        _playbackSourceKey = request.PlaybackSourceKey;
        _poster = request.Poster;
        _episodes = request.Episodes.ToList();
        _episodeLabels = request.EpisodeLabels?.ToList() ?? [];
        _episodeIndex = Math.Clamp(request.EpisodeIndex, 0, Math.Max(0, _episodes.Count - 1));
        _pendingSeekMs = (long)request.PositionMs;
        _resumeContext = request.Resume;
        _historySource = request.Resume?.HistorySource;
        _historyId = request.Resume?.HistoryId;
        _returningToDetail = false;
        UpdateHistoryPlaybackControls();

        // 播放页常驻后，打开另一部影片会复用同一个视图；已有播放器时立即切换媒体。
        if (wasInitialized)
        {
            BuildEpisodeButtons();
            if (IsLoaded)
            {
                _restartOnNextLoad = false;
                PlayEpisode(_episodeIndex, _pendingSeekMs);
            }
            else
            {
                _restartOnNextLoad = true;
            }
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachNativeShortcutHook();
        if (_controller is not null || _initializingController)
        {
            if (_controller is not null)
            {
                // 视图重新挂回窗口时重新绑定视频表面，但不重启媒体。
                if (!_usingLocalEngine)
                {
                    VideoSurface.MediaPlayer = _controller.Player;
                }

                BuildEpisodeButtons();
                if (_restartOnNextLoad)
                {
                    _restartOnNextLoad = false;
                    PlayEpisode(_episodeIndex, _pendingSeekMs);
                }
            }

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
        _controller.VideoOutputChanged += Controller_VideoOutputChanged;

        var volume = Math.Clamp(_host.Services.Settings.Volume, 0, 100);
        _controller.Volume = volume;
        _controller.Mute = false;
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

    private void OnUnloaded(object sender, RoutedEventArgs e) => DetachNativeShortcutHook();

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (!IsVisible)
        {
            UnmountVideoSurface();
            CancelSourceSwitchScan();
            SaveRecord();
            return;
        }

        MountVideoSurface();
        if (_controller is null)
        {
            return;
        }

        AttachNativeShortcutHook();
        if (_usingLocalEngine)
        {
            RequestLocalLayoutRefresh();
        }
        else
        {
            // 某些 WPF 原生宿主在 Hidden -> Visible 时会重建视频窗口，重新绑定但不重启媒体。
            VideoSurface.MediaPlayer = _controller.Player;
        }
    }

    private void UnmountVideoSurface()
    {
        // LibVLCSharp's overlay is a separate Window that hides on Unloaded, not ancestor Visibility.
        if (ReferenceEquals(VideoSurface.Parent, PlayerRoot))
        {
            PlayerRoot.Children.Remove(VideoSurface);
        }
    }

    private void MountVideoSurface()
    {
        if (VideoSurface.Parent is null)
        {
            PlayerRoot.Children.Add(VideoSurface);
        }
    }

    private void AttachNativeShortcutHook()
    {
        if (_nativeShortcutHookAttached || _disposed)
        {
            return;
        }

        ComponentDispatcher.ThreadPreprocessMessage += OnThreadPreprocessMessage;
        if (Window.GetWindow(this) is { } owner)
        {
            _keyboardHook = PlayerKeyboardHook.TryCreate(owner, QueueShortcutFromNativeHook);
        }
        _nativeShortcutHookAttached = true;
    }

    private void DetachNativeShortcutHook()
    {
        if (!_nativeShortcutHookAttached)
        {
            return;
        }

        ComponentDispatcher.ThreadPreprocessMessage -= OnThreadPreprocessMessage;
        _keyboardHook?.Dispose();
        _keyboardHook = null;
        _nativeShortcutHookAttached = false;
    }

    private void OnThreadPreprocessMessage(ref MSG message, ref bool handled)
    {
        if (handled || message.message != WmKeyDown || _disposed || !IsVisible)
        {
            return;
        }

        var key = KeyInterop.KeyFromVirtualKey(message.wParam.ToInt32());
        handled = HandleShortcut(key, Keyboard.Modifiers);
    }

    private void PlayEpisode(int index, long seekMs = 0) => _ = PlayEpisodeAsync(index, seekMs);

    private async Task PlayEpisodeAsync(int index, long seekMs = 0)
    {
        if (_controller is null || index < 0 || index >= _episodes.Count)
        {
            return;
        }

        if (index != _episodeIndex)
        {
            CancelSourceSwitchScan();
            SourceSwitchPanel.Visibility = Visibility.Collapsed;
            SaveRecord();
            _resumeContext = null;
            _returningToDetail = false;
        }

        _episodeIndex = index;
        var playSeq = ++_playSeq;
        DisarmStartupWatchdog();
        _awaitingVideoOutput = false;
        _playbackConfirmed = false;
        _resumePositionReached = _resumeContext is null || seekMs <= 0;
        ShowElapsedLoadingNotice(
            "第 2/2 步 · 正在准备视频",
            "正在连接播放源，程序没有卡死…",
            playSeq);
        _lengthMs = 0;
        _currentTimeMs = 0;
        _lastSavedSecond = 0;
        _adRanges = new List<AdRange>();
        _lastSkippedAdEndMs = -1;
        TitleText.Text = $"{_title}  {EpisodeLabelFormatter.DisplayAt(_episodeLabels, index)}";
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
        _currentPlaybackStartMs = seekMs;
        _awaitingVideoOutput = true;
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
                ? "正在使用 LibVLC 连接播放源并等待首帧…"
                : "正在打开本地文件（便携 mpv 内嵌）…",
            _startupWatchdog.Timeout,
            playSeq);

        // 本地：便携 mpv 内嵌（FFmpeg demux + d3d11va）；无 mpv 时回退 LibVLC。
        // 在线：始终使用 LibVLC，与线路探测的播放器口径保持一致。
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
                if (_disposed || playSeq != _playSeq)
                {
                    return;
                }

                UseLocalSurface(false);
                _awaitingVideoOutput = true;
                ArmStartupWatchdog();
                ShowCountdownLoadingNotice(
                    "正在切换兼容播放器",
                    "内嵌 mpv 启动失败，正在改用 LibVLC 打开本地文件…",
                    _startupWatchdog.Timeout,
                    playSeq);
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

        var networkCachingMs = isRemote ? 6000 : 300;
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

        // Hidden 会参与布局并创建 HWND，同时不会在首帧前把原生黑底暴露给用户。
        LocalVideoHost.Visibility = Visibility.Hidden;
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
        _localEngine.VideoOutputChanged += Controller_VideoOutputChanged;
        _localEngine.Volume = (int)Math.Round(VolumeSlider.Value);
        _localEngine.Mute = IsActiveMuted();
        LocalVideoHost.SyncNativeBounds();
        return true;
    }

    private void UseLocalSurface(bool local)
    {
        var mute = IsActiveMuted();
        _usingLocalEngine = local;
        SetActiveVolume((int)Math.Round(VolumeSlider.Value));
        SetActiveMute(mute);
        if (local)
        {
            // mpv 必须先在最终尺寸的隐藏 HWND 上完成输出初始化；收到视频输出事件后再显示。
            LocalVideoHost.Visibility = Visibility.Hidden;
            LocalVideoHost.UpdateLayout();
            LocalVideoHost.SyncNativeBounds();
            try { _controller?.Stop(); } catch { }

            // Keep VideoSurface mounted so LibVLCSharp's detached foreground window
            // continues to host the application controls during local mpv playback.
            // Detach only the VLC player; its transparent HWND stays behind the mpv host.
            VideoSurface.MediaPlayer = null;
            VideoSurface.Visibility = Visibility.Visible;
        }
        else
        {
            LocalVideoHost.Visibility = Visibility.Hidden;
            VideoSurface.MediaPlayer = _controller?.Player;
            VideoSurface.Visibility = Visibility.Visible;
            try { _localEngine?.Stop(); } catch { }
        }
    }

    private void RevealLocalSurface()
    {
        if (!_usingLocalEngine || LocalVideoHost.Visibility == Visibility.Visible)
        {
            return;
        }

        LocalVideoHost.UpdateLayout();
        LocalVideoHost.SyncNativeBounds();
        LocalVideoHost.Visibility = Visibility.Visible;
        LocalVideoHost.SyncNativeBounds();
    }

    private void RequestLocalLayoutRefresh()
    {
        if (_disposed)
        {
            return;
        }

        LocalVideoHost.SyncNativeBounds();
        var version = ++_localLayoutRefreshVersion;
        _ = RefreshLocalLayoutAsync(version);
    }

    private async Task RefreshLocalLayoutAsync(int version)
    {
        await Dispatcher.Yield(DispatcherPriority.Render);
        if (_disposed || version != _localLayoutRefreshVersion)
        {
            return;
        }

        LocalVideoHost.UpdateLayout();
        LocalVideoHost.SyncNativeBounds();
        await Task.Delay(100).ConfigureAwait(true);
        if (_disposed || version != _localLayoutRefreshVersion || _localEngine is null)
        {
            return;
        }

        LocalVideoHost.SyncNativeBounds();
    }

    private static bool IsRemoteHttpUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https";

    private bool IsActivePlaybackSender(object? sender) =>
        _usingLocalEngine
            ? ReferenceEquals(sender, _localEngine)
            : ReferenceEquals(sender, _controller);

    private bool HasActiveVideoOutput() =>
        _usingLocalEngine
            ? _localEngine?.HasVideoOutput == true
            : _controller?.HasVideoOutput == true;

    private void Controller_VideoOutputChanged(object? sender, bool available)
    {
        if (available && IsActivePlaybackSender(sender))
        {
            CompleteStartupIfVideoReady();
        }
    }

    private bool CompleteStartupIfVideoReady()
    {
        if (!HasActiveVideoOutput()
            || !PlaybackStartupPolicy.ResolvesStartup(PlaybackStartupSignal.VideoOutputStarted))
        {
            return false;
        }

        if (_usingLocalEngine)
        {
            RevealLocalSurface();
        }

        if (_awaitingVideoOutput)
        {
            _awaitingVideoOutput = false;
            DisarmStartupWatchdog();
        }

        HideLoadingNotice();
        ErrorOverlay.Visibility = Visibility.Collapsed;
        _playbackConfirmed = true;
        return true;
    }

    private void FailPlayback(string message)
    {
        _awaitingVideoOutput = false;
        DisarmStartupWatchdog();
        if (_usingLocalEngine)
        {
            _localEngine?.Stop();
        }
        else
        {
            _controller?.Stop();
        }

        HideLoadingNotice();
        if (QueueReturnToHistoryDetail())
        {
            return;
        }

        ErrorText.Text = message;
        ErrorOverlay.Visibility = Visibility.Visible;
        PlayPauseButton.Content = PlayGlyph;
    }

    private bool IsOnlineHistoryPlayback =>
        !string.Equals(_sourceKey, "local", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(_historySource)
        && !string.IsNullOrWhiteSpace(_historyId);

    private void UpdateHistoryPlaybackControls()
    {
        SourceSwitchButton.Visibility = IsOnlineHistoryPlayback
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (!IsOnlineHistoryPlayback)
        {
            SourceSwitchPanel.Visibility = Visibility.Collapsed;
        }
    }

    private bool QueueReturnToHistoryDetail()
    {
        if (!IsOnlineHistoryPlayback)
        {
            return false;
        }

        if (_returningToDetail)
        {
            return true;
        }

        _returningToDetail = true;
        CancelSourceSwitchScan();
        SaveRecord();
        var resume = CurrentResumeContext();
        var parameter = new DetailParameter(
            _title,
            _sourceKey,
            _id,
            PlaybackSourceKey: _playbackSourceKey,
            Resume: resume);
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() => _host.Navigate(AppPage.Detail, parameter)));
        return true;
    }

    private PlaybackResumeContext CurrentResumeContext()
    {
        var label = EpisodeLabelFormatter.DisplayAt(_episodeLabels, _episodeIndex);
        var episodeNumber = EpisodeLabelFormatter.TryGetNumber(label, out var number)
            ? number
            : (int?)null;
        var currentPositionMs = ActivePlaybackPositionMs();
        var positionMs = _playbackConfirmed && _resumePositionReached && currentPositionMs > 0
            ? currentPositionMs
            : _resumeContext?.PositionMs ?? _currentPlaybackStartMs;
        return new PlaybackResumeContext(
            _episodeIndex,
            positionMs,
            label,
            episodeNumber,
            _historySource,
            _historyId);
    }

    private long ActivePlaybackPositionMs() => _usingLocalEngine && _localEngine is not null
        ? Math.Max(0, _localEngine.Time)
        : Math.Max(0, _currentTimeMs);

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
        if (!IsActivePlaybackSender(sender))
        {
            return;
        }

        if (state == PlaybackState.Error)
        {
            FailPlayback("LibVLC 播放失败，请重试或返回详情页更换线路");
            return;
        }

        if (state is PlaybackState.Paused or PlaybackState.Ended)
        {
            SaveRecord();
        }

        if (state is PlaybackState.Opening or PlaybackState.Buffering)
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
        else if (state == PlaybackState.Playing)
        {
            CompleteStartupIfVideoReady();
        }

        PlayPauseButton.Content = state == PlaybackState.Playing ? PauseGlyph : PlayGlyph;
    }

    private void Controller_LengthChanged(object? sender, long length)
    {
        if (!IsActivePlaybackSender(sender))
        {
            return;
        }

        _lengthMs = length;
        DurationText.Text = FormatTime(_lengthMs);
    }

    private void Controller_TimeChanged(object? sender, long time)
    {
        if (!IsActivePlaybackSender(sender))
        {
            return;
        }

        _currentTimeMs = time;
        if (_resumeContext is not null
            && !_resumePositionReached
            && time >= Math.Max(0, EffectiveResumeTargetMs() - 3000))
        {
            _resumePositionReached = true;
        }

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

    private long EffectiveResumeTargetMs() => _lengthMs > 0
        ? Math.Clamp(_currentPlaybackStartMs, 0, Math.Max(0, _lengthMs - 1000))
        : Math.Max(0, _currentPlaybackStartMs);

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
        if (CompleteStartupIfVideoReady())
        {
            return;
        }

        if (!_startupWatchdog.IsTimedOut(DateTimeOffset.UtcNow))
        {
            return;
        }

        DisarmStartupWatchdog();
        FailPlayback("LibVLC 播放启动超时，请返回详情页更换线路");
    }

    private void MaybeSkipAd()
    {
        if (_adRanges.Count == 0 || !_host.Services.Settings.AutoSkipAds)
        {
            return;
        }

        var range = _adRanges.FirstOrDefault(r => _currentTimeMs >= r.StartMs && _currentTimeMs < r.EndMs);
        if (range is not null)
        {
            // 只有 seek 真正生效才消费跳过标记:时长未知时 LibVLC 会丢弃 seek,
            // 此时保留标记让后续进度回调重试,否则片头广告会完整播完。
            if (_lastSkippedAdEndMs != range.EndMs
                && (_usingLocalEngine
                    ? _localEngine?.TrySeek((long)range.EndMs) == true
                    : _controller?.TrySeek((long)range.EndMs) == true))
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
        if (!IsActivePlaybackSender(sender)
            || !PlayerEpisodeAdvancePolicy.ShouldAdvance(
                _playbackConfirmed,
                ActivePlaybackPositionMs(),
                _lengthMs))
        {
            return;
        }

        if (_episodeIndex + 1 < _episodes.Count)
        {
            PlayEpisode(_episodeIndex + 1);
        }
    }

    private void BuildEpisodeButtons()
    {
        EpisodeBar.Children.Clear();
        for (var index = 0; index < _episodes.Count; index++)
        {
            var button = new Button
            {
                Content = EpisodeLabelFormatter.CompactAt(_episodeLabels, index),
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
        SaveRecord();
        _playSeq++;
        _awaitingVideoOutput = false;
        DisarmStartupWatchdog();
        if (_usingLocalEngine)
        {
            _localEngine?.Stop();
        }
        else
        {
            _controller?.Stop();
        }

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
        if (show)
        {
            CloseSourceSwitchPanel();
        }
        SidePanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        _osd?.Pin(show);
    }

    private void SourceSwitchButton_Click(object sender, RoutedEventArgs e)
    {
        if (SourceSwitchPanel.Visibility == Visibility.Visible)
        {
            CloseSourceSwitchPanel();
            return;
        }

        if (!IsOnlineHistoryPlayback)
        {
            return;
        }

        SidePanel.Visibility = Visibility.Collapsed;
        SourceSwitchPanel.Visibility = Visibility.Visible;
        SourceSwitchEpisodeText.Text = $"{EpisodeLabelFormatter.DisplayAt(_episodeLabels, _episodeIndex)} · {_sourceName}";
        _osd?.Pin(true);
        StartSourceSwitchScan();
    }

    private void SourceScanButton_Click(object sender, RoutedEventArgs e)
    {
        if (_sourceSwitchCts is not null)
        {
            CancelSourceSwitchScan();
            SourceScanSummaryText.Text = "测速已停止";
            RenderSourceSwitchResults();
            return;
        }

        StartSourceSwitchScan();
    }

    private void StartSourceSwitchScan()
    {
        if (_disposed || !IsOnlineHistoryPlayback)
        {
            return;
        }

        CancelSourceSwitchScan();
        var cts = new CancellationTokenSource();
        _sourceSwitchCts = cts;
        _sourceSwitchCandidates = [];
        _sourceSwitchHealth.Clear();
        SourceScanButton.Content = "停止测速";
        SourceScanSummaryText.Text = "正在查找线路…";
        ShowSourceSwitchMessage("正在查找线路…");
        _ = RunSourceSwitchScanAsync(cts);
    }

    private async Task RunSourceSwitchScanAsync(CancellationTokenSource cts)
    {
        var scanFailed = false;
        var target = CurrentResumeContext();
        var current = new SearchResult(
            _id,
            _title,
            _poster,
            _episodes.ToArray(),
            _sourceKey,
            _sourceName,
            "",
            PlaybackSourceKey: _playbackSourceKey,
            EpisodeLabels: _episodeLabels.ToArray());

        try
        {
            var loaded = (await _host.Services.Catalog.GetDetailSourcesAsync(
                    _host.Services.Sources,
                    _title,
                    cts.Token)
                .ConfigureAwait(true)).ToList();
            cts.Token.ThrowIfCancellationRequested();

            var currentKey = SearchResultSources.SelectionKeyOf(current);
            loaded.RemoveAll(source => SearchResultSources.SelectionKeyOf(source) == currentKey);
            loaded.Insert(0, current);

            var hydrated = new List<SearchResult>(loaded.Count);
            foreach (var source in loaded)
            {
                cts.Token.ThrowIfCancellationRequested();
                var item = source.Episodes.Count == 0
                    ? await _host.Services.Catalog.HydrateEpisodesAsync(
                            _host.Services.Sources,
                            source,
                            cts.Token)
                        .ConfigureAwait(true) ?? source
                    : source;
                hydrated.Add(item);
            }

            var candidates = PlayerSourceSwitchPolicy.SelectCandidates(hydrated, target);
            if (!ReferenceEquals(_sourceSwitchCts, cts) || cts.IsCancellationRequested || _disposed)
            {
                return;
            }

            _sourceSwitchCandidates = candidates;
            if (candidates.Count == 0)
            {
                SourceScanSummaryText.Text = "没有包含当前剧集的线路";
                ShowSourceSwitchMessage("没有找到包含当前剧集的线路");
                return;
            }

            var done = 0;
            SourceScanSummaryText.Text = $"测速中 0/{candidates.Count}";
            ShowSourceSwitchMessage("正在检测可播放线路…");
            await _host.Services.VodSourceHealth.ProbeManyAsync(
                    candidates.Select(candidate => candidate.ProbeSource).ToList(),
                    (key, health) => Dispatcher.Invoke(() =>
                    {
                        if (!ReferenceEquals(_sourceSwitchCts, cts)
                            || cts.IsCancellationRequested
                            || _disposed)
                        {
                            return;
                        }

                        _sourceSwitchHealth[key] = health;
                        done++;
                        SourceScanSummaryText.Text = $"测速中 {done}/{candidates.Count}";
                        RenderSourceSwitchResults();
                    }),
                    concurrency: 4,
                    cts.Token)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            scanFailed = true;
            if (ReferenceEquals(_sourceSwitchCts, cts) && !_disposed)
            {
                SourceScanSummaryText.Text = "测速失败";
                ShowSourceSwitchMessage($"测速失败：{ShortSourceSwitchError(ex)}");
            }
        }
        finally
        {
            if (ReferenceEquals(_sourceSwitchCts, cts))
            {
                _sourceSwitchCts = null;
                if (!_disposed)
                {
                    SourceScanButton.Content = "重新测速";
                    if (!scanFailed && _sourceSwitchCandidates.Count > 0)
                    {
                        SourceScanSummaryText.Text = VodSourceHealthDisplay.SummaryOf(_sourceSwitchHealth.Values);
                        RenderSourceSwitchResults();
                    }
                }
            }

            cts.Dispose();
        }
    }

    private void RenderSourceSwitchResults()
    {
        SourceSwitchList.Children.Clear();
        var playableCount = 0;
        var currentKey = SearchResultSources.SelectionKeyOf(_sourceKey, _id, _playbackSourceKey);
        foreach (var candidate in _sourceSwitchCandidates)
        {
            var key = SearchResultSources.SelectionKeyOf(candidate.Source);
            if (!_sourceSwitchHealth.TryGetValue(key, out var health)
                || health.Status != VodSourceProbeStatus.Playable)
            {
                continue;
            }

            playableCount++;
            var sourceName = string.IsNullOrWhiteSpace(candidate.Source.SourceName)
                ? candidate.Source.Source
                : candidate.Source.SourceName;
            if (key == currentKey)
            {
                sourceName += " · 当前";
            }

            var content = new StackPanel();
            content.Children.Add(new TextBlock
            {
                Text = sourceName,
                Foreground = (System.Windows.Media.Brush)FindResource("TextBrush"),
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            });
            content.Children.Add(new TextBlock
            {
                Text = health.DisplayLabel,
                Foreground = (System.Windows.Media.Brush)FindResource("SoftTextBrush"),
                FontSize = 12,
                Margin = new Thickness(0, 4, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            });

            var button = new Button
            {
                Style = (Style)FindResource("SecondaryButtonStyle"),
                Tag = candidate,
                Content = content,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                MinHeight = 58,
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 8),
            };
            button.Click += SourceSwitchCandidate_Click;
            System.Windows.Automation.AutomationProperties.SetName(
                button,
                $"{sourceName} · {health.DisplayLabel}");
            SourceSwitchList.Children.Add(button);
        }

        if (playableCount == 0)
        {
            ShowSourceSwitchMessage(_sourceSwitchCts is null
                ? "当前未检测到可播放线路"
                : "正在检测可播放线路…");
        }
    }

    private void ShowSourceSwitchMessage(string message)
    {
        SourceSwitchList.Children.Clear();
        SourceSwitchList.Children.Add(new TextBlock
        {
            Text = message,
            Foreground = (System.Windows.Media.Brush)FindResource("SoftTextBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 8, 2, 0),
        });
    }

    private void SourceSwitchCandidate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: PlayerSourceSwitchCandidate candidate }
            || candidate.EpisodeIndex < 0
            || candidate.EpisodeIndex >= candidate.Source.Episodes.Count)
        {
            return;
        }

        var activePositionMs = ActivePlaybackPositionMs();
        var positionMs = activePositionMs > 0
            ? activePositionMs
            : CurrentResumeContext().PositionMs;
        SaveRecord();

        _sourceKey = candidate.Source.Source;
        _sourceName = candidate.Source.SourceName;
        _playbackSourceKey = candidate.Source.PlaybackSourceKey;
        _id = candidate.Source.Id;
        _poster = string.IsNullOrWhiteSpace(candidate.Source.Poster)
            ? _poster
            : candidate.Source.Poster;
        _episodes = candidate.Source.Episodes.ToList();
        _episodeLabels = candidate.Source.EpisodeLabels?.ToList() ?? [];
        _episodeIndex = candidate.EpisodeIndex;

        var label = EpisodeLabelFormatter.DisplayAt(_episodeLabels, _episodeIndex);
        var episodeNumber = EpisodeLabelFormatter.TryGetNumber(label, out var number)
            ? number
            : (int?)null;
        _resumeContext = new PlaybackResumeContext(
            _episodeIndex,
            positionMs,
            label,
            episodeNumber,
            _historySource,
            _historyId);
        _returningToDetail = false;

        CancelSourceSwitchScan();
        SourceSwitchPanel.Visibility = Visibility.Collapsed;
        _osd?.Pin(false);
        ErrorOverlay.Visibility = Visibility.Collapsed;
        BuildEpisodeButtons();
        UpdateHistoryPlaybackControls();
        PlayEpisode(_episodeIndex, (long)_resumeContext.StartPositionMs);
    }

    /// <summary>
    /// 被隐藏的成人源发生变化：换源面板里按旧状态查到的线路作废（再次打开面板时按当前状态重新查找）。
    /// </summary>
    internal void DropSourceSwitchResults()
    {
        if (_disposed)
        {
            return;
        }

        CloseSourceSwitchPanel();
        _sourceSwitchCandidates = [];
        _sourceSwitchHealth.Clear();
    }

    private void CloseSourceSwitchPanel()
    {
        CancelSourceSwitchScan();
        SourceSwitchPanel.Visibility = Visibility.Collapsed;
        _osd?.Pin(SidePanel.Visibility == Visibility.Visible);
    }

    private void CancelSourceSwitchScan()
    {
        var cts = _sourceSwitchCts;
        if (cts is null)
        {
            return;
        }

        _sourceSwitchCts = null;
        cts.Cancel();
        if (!_disposed)
        {
            SourceScanButton.Content = "重新测速";
        }
    }

    private static string ShortSourceSwitchError(Exception error)
    {
        var message = error.Message?.Trim() ?? "未知错误";
        return message.Length <= 80 ? message : message[..80] + "…";
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

    private void BackButton_Click(object sender, RoutedEventArgs e) => ReturnFromPlayer();

    private void ReturnFromPlayer()
    {
        if (!QueueReturnToHistoryDetail())
        {
            _host.GoBack();
        }
    }

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

    private bool IsFullscreenActive() => _host.IsFullscreen;

    private void ToggleFullscreen() => SetFullscreen(!IsFullscreenActive());

    private void SetFullscreen(bool enabled)
    {
        if (_host.IsFullscreen != enabled)
        {
            _host.SetFullscreen(enabled);
        }

        if (_usingLocalEngine)
        {
            RequestLocalLayoutRefresh();
        }

        FullscreenButton.Content = enabled ? ExitFullscreenGlyph : FullscreenGlyph;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (HandleShortcut(e.Key, Keyboard.Modifiers))
        {
            e.Handled = true;
        }
    }

    private bool QueueShortcutFromNativeHook(Key key, ModifierKeys modifiers)
    {
        if (_disposed || !IsVisible)
        {
            return false;
        }

        if (!PlayerShortcutMap.TryResolve(key, modifiers, out var command))
        {
            return false;
        }

        _ = Dispatcher.InvokeAsync(() => ExecuteShortcut(command), DispatcherPriority.Input);
        return true;
    }

    private bool HandleShortcut(Key key, ModifierKeys modifiers)
    {
        if (!PlayerShortcutMap.TryResolve(key, modifiers, out var command))
        {
            return false;
        }

        ExecuteShortcut(command);
        return true;
    }

    private void ExecuteShortcut(PlayerShortcutCommand command)
    {
        if (_disposed)
        {
            return;
        }

        switch (command)
        {
            case PlayerShortcutCommand.TogglePlayPause:
                ToggleActivePlayPause();
                break;
            case PlayerShortcutCommand.SeekBackward:
                SeekBy(-5000);
                break;
            case PlayerShortcutCommand.SeekForward:
                SeekBy(5000);
                break;
            case PlayerShortcutCommand.VolumeUp:
                AdjustVolume(5);
                break;
            case PlayerShortcutCommand.VolumeDown:
                AdjustVolume(-5);
                break;
            case PlayerShortcutCommand.ToggleFullscreen:
                ToggleFullscreen();
                break;
            case PlayerShortcutCommand.ToggleMute:
                ToggleMute();
                break;
            case PlayerShortcutCommand.Escape:
                if (IsFullscreenActive())
                {
                    SetFullscreen(false);
                }
                else
                {
                    ReturnFromPlayer();
                }

                break;
        }

        _osd?.Show();
    }

    private void SeekBy(long deltaMs)
    {
        if (_lengthMs <= 0)
        {
            return;
        }

        if (_usingLocalEngine && _localEngine is not null)
        {
            _localEngine.TrySeekBy(deltaMs);
            return;
        }

        if (_controller is not null)
        {
            var target = Math.Clamp(_currentTimeMs + deltaMs, 0, _lengthMs);
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
        _osd?.Pin(
            SidePanel.Visibility == Visibility.Visible
            || SourceSwitchPanel.Visibility == Visibility.Visible);

    private void SaveRecordThrottled()
    {
        var second = _currentTimeMs / 1000;
        if (second <= 0
            || Math.Abs(second - _lastSavedSecond) < 5
            || string.IsNullOrEmpty(_sourceKey)
            || string.IsNullOrEmpty(_id))
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

        if (_resumeContext is not null && (!_playbackConfirmed || !_resumePositionReached))
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

            var episodeLabel = EpisodeLabelFormatter.DisplayAt(_episodeLabels, _episodeIndex);
            var episodeNumber = EpisodeLabelFormatter.TryGetNumber(episodeLabel, out var number)
                ? number
                : (int?)null;

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
                LocalEpisodes: isLocal ? _episodes.ToArray() : null,
                PlaybackSourceKey: _playbackSourceKey,
                EpisodeLabel: episodeLabel,
                EpisodeNumber: episodeNumber));
            if (_playbackConfirmed
                && !string.IsNullOrWhiteSpace(_historySource)
                && !string.IsNullOrWhiteSpace(_historyId)
                && (!string.Equals(_historySource, _sourceKey, StringComparison.Ordinal)
                    || !string.Equals(_historyId, _id, StringComparison.Ordinal)))
            {
                _host.Services.PlayRecords.Remove(
                    _historySource,
                    _historyId);
                _historySource = _sourceKey;
                _historyId = _id;
                if (_resumeContext is not null)
                {
                    _resumeContext = _resumeContext with
                    {
                        HistorySource = _historySource,
                        HistoryId = _historyId,
                    };
                }
            }
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

        CancelSourceSwitchScan();
        _disposed = true;
        DetachNativeShortcutHook();
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
            _localEngine.VideoOutputChanged -= Controller_VideoOutputChanged;
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
            _controller.VideoOutputChanged -= Controller_VideoOutputChanged;
            VideoSurface.MediaPlayer = null;
            _host.Services.DisposePlaybackInBackground(_controller);
            _controller = null;
        }
    }
}
