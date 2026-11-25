using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using OrionTV.Desktop.App.LiveTranslation;
using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.App.Views;

public partial class LiveView : UserControl, INavigationAware, INavigationVisibilityAware, IDisposable
{
    private const string PlayGlyph = "";
    private const string PauseGlyph = "";
    private const string VolumeGlyph = "";
    private const string MuteGlyph = "";
    private const string FullscreenGlyph = "";
    private const string ExitFullscreenGlyph = "";

    private readonly IAppHost _host;

    private static readonly Brush VerdictGoodBrush = Freeze("#6FDCA8");
    private static readonly Brush VerdictFairBrush = Freeze("#E6A700");
    private static readonly Brush VerdictBadBrush = Freeze("#E5484D");
    private static readonly Brush TranslationActiveBrush = Freeze("#8CE8BC");
    private static readonly Brush TranslationErrorBrush = Freeze("#FF6B6B");
    private static readonly Brush TranslationInactiveBrush = Freeze("#F5F5F5");

    private const int ProbeConcurrency = 6;

    private PlaybackController? _controller;
    private OsdController? _osd;
    private DispatcherTimer? _singleClickTimer;
    private DispatcherTimer? _badgeTimer;

    private Dictionary<string, List<LiveChannelViewModel>> _grouped = new();
    private Dictionary<string, LiveChannelViewModel> _vmByKey = new();
    private LiveChannelViewModel? _currentChannel;
    private bool _updatingChannelLists;
    private bool _updatingVolume;
    private bool _disposed;

    private CancellationTokenSource? _scanCts;

    private DispatcherTimer? _statsTimer;
    private bool _statsVisible;
    private long _lastSampleTicks;
    private long _lastReadBytes;
    private long _lastDisplayed;
    private long _lastLost;
    private double _lastFps;
    private float _lastCachePct;
    private int _rebufferCount;
    private bool _hasPlayed;
    private bool _inBuffer;

    private readonly RollingSubtitleBuffer _translationSubtitles = new(108);
    private LiveTranslationController? _translationController;
    private CancellationTokenSource? _translationCts;
    private DispatcherTimer? _subtitleHideTimer;
    private bool _translationRequested;
    private int _translationGeneration;
    private bool _navigationActive;
    private bool _pendingInitialPlayback;
    private bool _stoppedForNavigation;

    public LiveView(IAppHost host)
    {
        InitializeComponent();
        _host = host;
        Header.BackRequested += (_, _) => _host.GoBack();
        Loaded += OnLoaded;
    }

    public void OnNavigatedTo(object? parameter)
    {
    }

    public void OnShown()
    {
        if (_disposed)
        {
            return;
        }

        _navigationActive = true;
        if (_statsVisible)
        {
            StartStatsTimer();
        }

        if (_pendingInitialPlayback && _currentChannel is not null)
        {
            _pendingInitialPlayback = false;
            PlayChannel(_currentChannel);
        }
    }

    public void OnHidden()
    {
        if (_disposed || !_navigationActive)
        {
            return;
        }

        _navigationActive = false;
        _singleClickTimer?.Stop();
        _badgeTimer?.Stop();
        StopStatsTimer();
        _stoppedForNavigation = _currentChannel is not null;
        if (_controller?.IsPlaying == true)
        {
            _controller.Pause();
        }

        _controller?.Stop();
        PlayPauseButton.Content = PlayGlyph;
        LoadingOverlay.Visibility = Visibility.Collapsed;

        if (_translationRequested || _translationController is not null)
        {
            _translationRequested = false;
            UpdateTranslationButton();
            _ = StopTranslationAsync(clearUi: true);
        }

        if (_host.IsFullscreen)
        {
            _host.SetFullscreen(false);
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_controller is not null)
        {
            return;
        }

        _controller = new PlaybackController(_host.Services.LibVlc, Dispatcher);
        VideoSurface.MediaPlayer = _controller.Player;
        _controller.StateChanged += Controller_StateChanged;
        _controller.Buffering += Controller_Buffering;

        var volume = Math.Clamp(_host.Services.Settings.Volume, 0, 100);
        _controller.Volume = volume;
        _updatingVolume = true;
        VolumeSlider.Value = volume;
        _updatingVolume = false;
        UpdateMuteIcon();

        _singleClickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _singleClickTimer.Tick += (_, _) =>
        {
            _singleClickTimer!.Stop();
            TogglePlayPause();
        };

        _osd = new OsdController(OverlayRoot, ControlBar, ChannelTitleBadge);
        _osd.Show();
        Focus();

        var liveSources = DesktopLiveSourceSettings.Resolve(_host.Services.Settings);
        if (liveSources.Count == 0)
        {
            HintText.Visibility = Visibility.Visible;
            return;
        }

        var primary = liveSources.FirstOrDefault(source => source.IsPrimary);
        var additionalTasks = liveSources
            .Where(source => !source.IsPrimary)
            .Select(async source =>
                (Source: source, Channels: await _host.Services.M3u.FetchAndParseAsync(source.Url)))
            .ToArray();

        IReadOnlyList<LiveChannel> primaryChannels = [];
        if (primary is not null)
        {
            primaryChannels = await _host.Services.M3u.FetchAndParseAsync(primary.Url);
        }

        if (_disposed)
        {
            return;
        }

        if (primaryChannels.Count > 0 && primary is not null)
        {
            ApplyChannels(DesktopLiveSourceSettings.MergeChannels([(primary, primaryChannels)]));
        }

        var additionalResults = await Task.WhenAll(additionalTasks);
        if (_disposed)
        {
            return;
        }

        var channelSets = new List<(ConfiguredLiveSource Source, IReadOnlyList<LiveChannel> Channels)>();
        if (primary is not null)
        {
            channelSets.Add((primary, primaryChannels));
        }

        channelSets.AddRange(additionalResults);
        var channels = DesktopLiveSourceSettings.MergeChannels(channelSets);
        if (channels.Count == 0)
        {
            HintText.Text = "未能解析任何直播源，请检查设置中的地址";
            HintText.Visibility = Visibility.Visible;
            return;
        }

        if (additionalResults.Length > 0 || primaryChannels.Count == 0)
        {
            ApplyChannels(channels);
        }
    }

    private void ApplyChannels(IReadOnlyList<LiveChannel> channels)
    {
        var currentUrl = _currentChannel?.Url;
        var viewModels = channels
            .Select((channel, index) => new LiveChannelViewModel($"ch{index}", channel))
            .ToList();
        _vmByKey = viewModels.ToDictionary(vm => vm.Key);
        _grouped = viewModels
            .GroupBy(vm => vm.Channel.Group)
            .ToDictionary(group => group.Key, group => group.ToList());
        var keys = _grouped.Keys.ToList();
        var selected = currentUrl is null
            ? viewModels.FirstOrDefault()
            : viewModels.FirstOrDefault(vm => vm.Url.Equals(currentUrl, StringComparison.OrdinalIgnoreCase))
                ?? viewModels.FirstOrDefault();
        if (selected is null)
        {
            return;
        }

        var selectedGroup = selected.Channel.Group;
        var selectedGroupChannels = _grouped[selectedGroup];
        _updatingChannelLists = true;
        try
        {
            GroupList.ItemsSource = keys;
            GroupListFs.ItemsSource = keys.ToList();
            GroupList.SelectedItem = selectedGroup;
            GroupListFs.SelectedItem = selectedGroup;
            ChannelList.ItemsSource = selectedGroupChannels;
            ChannelListFs.ItemsSource = selectedGroupChannels;
            ChannelList.SelectedItem = selected;
            ChannelListFs.SelectedItem = selected;
        }
        finally
        {
            _updatingChannelLists = false;
        }

        HintText.Visibility = Visibility.Collapsed;
        if (currentUrl is null)
        {
            _currentChannel = selected;
            NowPlayingText.Text = selected.Name;
            if (_navigationActive)
            {
                PlayChannel(selected);
            }
            else
            {
                _pendingInitialPlayback = true;
            }
        }
        else
        {
            _currentChannel = selected;
        }
    }

    private void GroupList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingChannelLists
            && GroupList.SelectedItem is string group
            && _grouped.TryGetValue(group, out var list))
        {
            ChannelList.ItemsSource = list;
        }
    }

    private void ChannelList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingChannelLists && ChannelList.SelectedItem is LiveChannelViewModel channel)
        {
            PlayChannel(channel);
        }
    }

    private void GroupListFs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingChannelLists
            && GroupListFs.SelectedItem is string group
            && _grouped.TryGetValue(group, out var list))
        {
            ChannelListFs.ItemsSource = list;
        }
    }

    private void ChannelListFs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingChannelLists && ChannelListFs.SelectedItem is LiveChannelViewModel channel)
        {
            PlayChannel(channel);
        }
    }

    private void PlayChannel(LiveChannelViewModel channel)
    {
        if (_controller is null)
        {
            return;
        }

        _pendingInitialPlayback = false;
        _stoppedForNavigation = false;
        _currentChannel = channel;
        NowPlayingText.Text = channel.Name;
        ShowChannelBadge(channel.Name);
        ResetStats();
        _controller.SwitchTo(channel.Url);
        if (_translationRequested)
        {
            _ = RestartTranslationAsync(channel);
        }
    }

    private void ShowChannelBadge(string name)
    {
        ChannelTitleText.Text = name;
        ChannelTitleBadge.Visibility = Visibility.Visible;
        ChannelTitleBadge.Opacity = 1;

        _badgeTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _badgeTimer.Stop();
        _badgeTimer.Tick -= BadgeTimer_Tick;
        _badgeTimer.Tick += BadgeTimer_Tick;
        _badgeTimer.Start();
    }

    private void BadgeTimer_Tick(object? sender, EventArgs e)
    {
        _badgeTimer!.Stop();
        ChannelTitleBadge.Visibility = Visibility.Collapsed;
    }

    private void Controller_StateChanged(object? sender, PlaybackState state)
    {
        if (!_navigationActive)
        {
            if (state == PlaybackState.Playing)
            {
                _controller?.Stop();
            }

            return;
        }

        if (state == PlaybackState.Playing)
        {
            _hasPlayed = true;
        }

        PlayPauseButton.Content = state == PlaybackState.Playing ? PauseGlyph : PlayGlyph;
        LoadingOverlay.Visibility = state is PlaybackState.Opening or PlaybackState.Buffering ? Visibility.Visible : Visibility.Collapsed;
        ErrorOverlay.Visibility = state == PlaybackState.Error ? Visibility.Visible : Visibility.Collapsed;

        if (state == PlaybackState.Paused)
        {
            _translationController?.SetPaused(true);
        }
        else if (state == PlaybackState.Playing)
        {
            _translationController?.SetPaused(false);
        }
    }

    private void Controller_Buffering(object? sender, float cache)
    {
        if (!_navigationActive)
        {
            return;
        }

        _lastCachePct = cache;
        if (cache >= 100f)
        {
            _inBuffer = false;
        }
        else if (!_inBuffer)
        {
            _inBuffer = true;
            if (_hasPlayed)
            {
                _rebufferCount++;
            }
        }
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e) => TogglePlayPause();

    private void TogglePlayPause()
    {
        if (_controller is null)
        {
            return;
        }

        if (_stoppedForNavigation && _currentChannel is not null)
        {
            PlayChannel(_currentChannel);
            return;
        }

        _controller.TogglePlayPause();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => Reconnect();

    private void RetryButton_Click(object sender, RoutedEventArgs e) => Reconnect();

    private void Reconnect()
    {
        if (_currentChannel is not null)
        {
            PlayChannel(_currentChannel);
        }
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        if (_scanCts is not null)
        {
            // 正在测速时再次点击 → 取消本轮。
            _scanCts.Cancel();
            return;
        }

        await StartScanAsync();
    }

    private async Task StartScanAsync()
    {
        if (_vmByKey.Count == 0)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _scanCts = cts;

        var items = _vmByKey.Values.Select(vm => (vm.Key, vm.Url)).ToList();
        foreach (var vm in _vmByKey.Values)
        {
            vm.IsProbing = true;
        }

        SetScanButtonsRunning(true);
        var total = items.Count;
        var done = 0;
        ReportScanProgress(done, total);

        try
        {
            await _host.Services.ChannelHealth.ProbeManyAsync(
                items,
                (key, result) => Dispatcher.BeginInvoke(() =>
                {
                    if (cts.IsCancellationRequested)
                    {
                        return;
                    }

                    if (_vmByKey.TryGetValue(key, out var vm))
                    {
                        vm.Health = result;
                    }

                    done++;
                    ReportScanProgress(done, total);
                }),
                ProbeConcurrency,
                cts.Token);
        }
        catch (OperationCanceledException)
        {
            // 用户取消或离开页面。
        }
        catch
        {
            // 单次扫描整体失败不影响页面。
        }
        finally
        {
            if (ReferenceEquals(_scanCts, cts))
            {
                _scanCts = null;
            }

            cts.Dispose();
            SetScanButtonsRunning(false);

            foreach (var vm in _vmByKey.Values.Where(vm => vm.IsProbing))
            {
                vm.IsProbing = false;
            }

            FinalizeScanSummary();
        }
    }

    private void SetScanButtonsRunning(bool running)
    {
        var label = running ? "测速中" : "测速";
        ScanButton.Content = label;
        ScanButtonFs.Content = label;
    }

    private void ReportScanProgress(int done, int total)
    {
        ScanProgressText.Visibility = Visibility.Visible;
        ScanProgressText.Text = $"测速中 {done}/{total}";
    }

    private void FinalizeScanSummary()
    {
        var summary = ChannelHealthDisplay.SummaryOf(_vmByKey.Values.Select(vm => vm.Health));
        if (string.IsNullOrEmpty(summary))
        {
            ScanProgressText.Visibility = Visibility.Collapsed;
            return;
        }

        ScanProgressText.Visibility = Visibility.Visible;
        ScanProgressText.Text = summary;
    }

    private void StatsButton_Click(object sender, RoutedEventArgs e) => ToggleStats();

    private async void TranslationButton_Click(object sender, RoutedEventArgs e) =>
        await ToggleTranslationAsync();

    private async Task ToggleTranslationAsync()
    {
        if (_translationRequested)
        {
            _translationRequested = false;
            await StopTranslationAsync(clearUi: true);
            return;
        }

        var settings = _host.Services.Settings;
        var provider = GeminiLiveTranslateProtocol.ResolveProvider(
            settings.LiveTranslationProvider,
            settings.LiveTranslationModel);

        string? apiKey;
        try
        {
            apiKey = OpenAiCredentialStore.ReadApiKeyForProvider(provider);
        }
        catch
        {
            ShowTranslationStatus("无法读取 Windows 凭据管理器", isError: true);
            return;
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            ShowTranslationStatus(
                provider == "gemini"
                    ? "请先在设置中配置 Gemini API Key（AI Studio 免费申请）"
                    : provider == "doubao"
                        ? "请先在设置中配置豆包 Access Token"
                        : provider == "deepgram"
                            ? "请先在设置中配置 Deepgram API Key"
                            : "请先在设置中配置翻译 API Key",
                isError: true);
            return;
        }

        if (provider == "doubao"
            && string.IsNullOrWhiteSpace(settings.DoubaoAppKey))
        {
            ShowTranslationStatus("请先在设置中填写豆包 APP ID", isError: true);
            return;
        }

        if (provider == "deepgram"
            && string.IsNullOrWhiteSpace(OpenAiCredentialStore.ReadApiKeyForProvider("openai")))
        {
            ShowTranslationStatus("Deepgram 仅做英语识别；请再配置中转 Chat API Key 用于译中文", isError: true);
            return;
        }

        if (_currentChannel is null)
        {
            ShowTranslationStatus("请先选择一个英语频道", isError: true);
            return;
        }

        _translationRequested = true;
        UpdateTranslationButton();
        await RestartTranslationAsync(_currentChannel);
    }

    private int _volumeBeforeDubbing = -1;

    private async Task RestartTranslationAsync(LiveChannelViewModel channel)
    {
        var generation = Interlocked.Increment(ref _translationGeneration);
        _translationCts?.Cancel();
        _translationCts?.Dispose();
        var cts = new CancellationTokenSource();
        _translationCts = cts;

        await StopTranslationControllerAsync();
        if (_disposed || !_translationRequested || generation != _translationGeneration)
        {
            return;
        }

        var settings = _host.Services.Settings;
        var provider = GeminiLiveTranslateProtocol.ResolveProvider(
            settings.LiveTranslationProvider,
            settings.LiveTranslationModel);

        string? apiKey;
        try
        {
            apiKey = OpenAiCredentialStore.ReadApiKeyForProvider(provider);
        }
        catch
        {
            _translationRequested = false;
            UpdateTranslationButton();
            ShowTranslationStatus("无法读取 Windows 凭据管理器", isError: true);
            return;
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _translationRequested = false;
            UpdateTranslationButton();
            ShowTranslationStatus(
                provider == "gemini"
                    ? "Gemini API Key 未配置"
                    : provider == "doubao"
                        ? "豆包 Access Token 未配置"
                        : provider == "deepgram"
                            ? "Deepgram API Key 未配置"
                            : "翻译 API Key 未配置",
                isError: true);
            return;
        }

        if (provider == "doubao" && string.IsNullOrWhiteSpace(settings.DoubaoAppKey))
        {
            _translationRequested = false;
            UpdateTranslationButton();
            ShowTranslationStatus("豆包 APP ID 未配置", isError: true);
            return;
        }

        var chatKey = OpenAiCredentialStore.ReadApiKeyForProvider("openai");
        if (provider == "deepgram" && string.IsNullOrWhiteSpace(chatKey))
        {
            _translationRequested = false;
            UpdateTranslationButton();
            ShowTranslationStatus("中转 Chat API Key 未配置（Deepgram 只识别英语）", isError: true);
            return;
        }

        _translationSubtitles.Clear();
        TranslationSubtitlePanel.Visibility = Visibility.Collapsed;
        var modeHint = provider is "gemini" or "doubao"
            ? (settings.LiveTranslationDubbing ? "字幕+配音" : "仅字幕")
            : "字幕";
        ShowTranslationStatus($"正在连接中译（{modeHint}）· {channel.Name}", isError: false);

        // 配音时把原声音量压低，避免英中叠在一起。
        if (provider is "gemini" or "doubao" && settings.LiveTranslationDubbing && _controller is not null)
        {
            if (_volumeBeforeDubbing < 0)
            {
                _volumeBeforeDubbing = _controller.Volume;
            }

            _controller.Volume = Math.Min(_controller.Volume, 25);
            _updatingVolume = true;
            VolumeSlider.Value = _controller.Volume;
            _updatingVolume = false;
            UpdateMuteIcon();
        }

        var translation = new LiveTranslationController();
        translation.SubtitleDelta += delta => OnTranslationSubtitleDelta(translation, generation, delta);
        translation.StateChanged += (state, message) => OnTranslationStateChanged(translation, generation, state, message);
        _translationController = translation;

        try
        {
            await translation.StartAsync(
                channel.Url,
                apiKey,
                settings.LiveTranslationModel,
                settings.LiveTranslationBaseUrl,
                provider,
                settings.LiveTranslationDubbing,
                settings.DoubaoAppKey,
                settings.DoubaoResourceId,
                chatKey,
                settings.LiveTranslationModel,
                cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!_disposed && generation == _translationGeneration)
            {
                _translationRequested = false;
                UpdateTranslationButton();
                var message = exception.Message.Contains("语音识别", StringComparison.Ordinal)
                    || exception.Message.Contains("en-US", StringComparison.OrdinalIgnoreCase)
                    || exception.Message.Contains("Gemini", StringComparison.OrdinalIgnoreCase)
                    || exception.Message.Contains("API Key", StringComparison.OrdinalIgnoreCase)
                    ? exception.Message
                    : "实时翻译连接失败，请检查引擎、模型名和 API Key";
                ShowTranslationStatus(message, isError: true);
            }

            if (ReferenceEquals(_translationController, translation))
            {
                _translationController = null;
            }

            translation.Dispose();
        }
    }

    private void OnTranslationSubtitleDelta(
        LiveTranslationController source,
        int generation,
        string delta)
    {
        if (_disposed)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (_disposed
                || generation != _translationGeneration
                || !ReferenceEquals(_translationController, source))
            {
                return;
            }

            var text = _translationSubtitles.Append(delta);
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            TranslationStatusBadge.Visibility = Visibility.Collapsed;
            TranslationSubtitleText.Text = text;
            TranslationSubtitlePanel.Visibility = Visibility.Visible;

            _subtitleHideTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
            _subtitleHideTimer.Stop();
            _subtitleHideTimer.Tick -= SubtitleHideTimer_Tick;
            _subtitleHideTimer.Tick += SubtitleHideTimer_Tick;
            _subtitleHideTimer.Start();
        });
    }

    private void OnTranslationStateChanged(
        LiveTranslationController source,
        int generation,
        LiveTranslationState state,
        string? message)
    {
        if (_disposed)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (_disposed
                || generation != _translationGeneration
                || !ReferenceEquals(_translationController, source))
            {
                return;
            }

            switch (state)
            {
                case LiveTranslationState.Connecting:
                    ShowTranslationStatus("正在连接实时翻译…", isError: false);
                    break;
                case LiveTranslationState.Listening:
                    ShowTranslationStatus(
                        string.IsNullOrWhiteSpace(message)
                            ? "正在聆听英语并生成中文字幕…"
                            : message,
                        isError: false);
                    break;
                case LiveTranslationState.Error:
                    _translationRequested = false;
                    UpdateTranslationButton();
                    ShowTranslationStatus(message ?? "实时翻译暂时不可用", isError: true);
                    break;
            }
        });
    }

    private void SubtitleHideTimer_Tick(object? sender, EventArgs e)
    {
        _subtitleHideTimer?.Stop();
        _translationSubtitles.Clear();
        TranslationSubtitlePanel.Visibility = Visibility.Collapsed;
    }

    private void ShowTranslationStatus(string message, bool isError)
    {
        TranslationStatusText.Text = message;
        TranslationStatusText.Foreground = isError ? TranslationErrorBrush : TranslationActiveBrush;
        TranslationStatusBadge.Visibility = Visibility.Visible;
    }

    private void UpdateTranslationButton()
    {
        TranslationButton.Content = _translationRequested ? "译中" : "中译";
        TranslationButton.Foreground = _translationRequested ? TranslationActiveBrush : TranslationInactiveBrush;
        TranslationButton.ToolTip = _translationRequested
            ? "关闭中译字幕 (T)"
            : "英语实时翻译为中文字幕 (T)";
    }

    private async Task StopTranslationControllerAsync()
    {
        var translation = _translationController;
        _translationController = null;
        if (translation is not null)
        {
            await translation.DisposeAsync();
        }
    }

    private async Task StopTranslationAsync(bool clearUi)
    {
        var generation = Interlocked.Increment(ref _translationGeneration);
        _translationCts?.Cancel();
        _translationCts?.Dispose();
        _translationCts = null;
        await StopTranslationControllerAsync();
        if (generation != _translationGeneration)
        {
            return;
        }

        if (_volumeBeforeDubbing >= 0 && _controller is not null)
        {
            _controller.Volume = _volumeBeforeDubbing;
            _updatingVolume = true;
            VolumeSlider.Value = _volumeBeforeDubbing;
            _updatingVolume = false;
            UpdateMuteIcon();
            _volumeBeforeDubbing = -1;
        }

        UpdateTranslationButton();
        if (clearUi)
        {
            _subtitleHideTimer?.Stop();
            _translationSubtitles.Clear();
            TranslationSubtitlePanel.Visibility = Visibility.Collapsed;
            TranslationStatusBadge.Visibility = Visibility.Collapsed;
        }
    }

    private void ToggleStats()
    {
        _statsVisible = !_statsVisible;
        StatsOverlay.Visibility = _statsVisible ? Visibility.Visible : Visibility.Collapsed;
        if (_statsVisible)
        {
            StartStatsTimer();
        }
        else
        {
            StopStatsTimer();
        }
    }

    private void StartStatsTimer()
    {
        _lastSampleTicks = 0;
        _statsTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statsTimer.Tick -= StatsTimer_Tick;
        _statsTimer.Tick += StatsTimer_Tick;
        _statsTimer.Start();
        SampleStats();
    }

    private void StopStatsTimer() => _statsTimer?.Stop();

    private void StatsTimer_Tick(object? sender, EventArgs e) => SampleStats();

    private void ResetStats()
    {
        _rebufferCount = 0;
        _inBuffer = false;
        _hasPlayed = false;
        _lastCachePct = 0;
        _lastSampleTicks = 0;
        _lastReadBytes = 0;
        _lastDisplayed = 0;
        _lastLost = 0;
        _lastFps = 0;
    }

    private void SampleStats()
    {
        var player = _controller?.Player;
        // MediaPlayer.Media 的 getter 每次都会新建一个持有 native 引用的 Media 包装,
        // 统计定时器每秒调一次,不释放就每秒泄漏一个引用。
        using var media = player?.Media;
        if (_controller is null || player is null || media is null)
        {
            StatsVerdictText.Text = "—";
            StatsVerdictText.Foreground = VerdictFairBrush;
            StatsBitrateText.Text = "码率 —";
            StatsFpsText.Text = "帧率 —";
            StatsDropText.Text = "丢帧 —";
            StatsBufferText.Text = "缓冲 —";
            return;
        }

        long readBytes;
        long displayed;
        long lost;
        try
        {
            var stats = media.Statistics;
            readBytes = stats.DemuxReadBytes;
            displayed = stats.DisplayedPictures;
            lost = stats.LostPictures;
        }
        catch
        {
            // 切台瞬间媒体可能正在重建，读取统计失败时跳过本次采样。
            return;
        }

        var nowTicks = Stopwatch.GetTimestamp();

        double mbps = -1;
        double dropDelta = 0;
        if (_lastSampleTicks != 0)
        {
            var seconds = Stopwatch.GetElapsedTime(_lastSampleTicks, nowTicks).TotalSeconds;
            if (seconds > 0)
            {
                var deltaBytes = readBytes - _lastReadBytes;
                if (deltaBytes >= 0)
                {
                    mbps = deltaBytes * 8d / seconds / 1_000_000d;
                }

                var deltaDisplayed = displayed - _lastDisplayed;
                if (deltaDisplayed >= 0)
                {
                    _lastFps = deltaDisplayed / seconds;
                }
            }

            dropDelta = lost - _lastLost;
        }

        StatsBitrateText.Text = mbps >= 0 ? $"码率 {mbps:0.0} Mbps" : "码率 …";
        StatsFpsText.Text = _lastFps > 0 ? $"帧率 {_lastFps:0} fps" : "帧率 …";
        StatsDropText.Text = $"丢帧 {lost}";
        StatsBufferText.Text = $"缓冲 {_rebufferCount} 次 · 缓存 {_lastCachePct:0}%";

        var buffering = _controller.State == PlaybackState.Buffering;
        var stutter = dropDelta > 2 || (_lastFps > 0 && _lastFps < 12);
        StatsVerdictText.Text = $"缓存 {_lastCachePct:0}%";
        StatsVerdictText.Foreground = buffering ? VerdictBadBrush : stutter ? VerdictFairBrush : VerdictGoodBrush;

        _lastReadBytes = readBytes;
        _lastDisplayed = displayed;
        _lastLost = lost;
        _lastSampleTicks = nowTicks;
    }

    private static Brush Freeze(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_controller is null || _updatingVolume)
        {
            return;
        }

        _controller.Volume = (int)e.NewValue;
        if (e.NewValue > 0 && _controller.Mute)
        {
            _controller.Mute = false;
        }

        UpdateMuteIcon();
    }

    private void MuteButton_Click(object sender, RoutedEventArgs e) => ToggleMute();

    private void ToggleMute()
    {
        if (_controller is null)
        {
            return;
        }

        _controller.Mute = !_controller.Mute;
        UpdateMuteIcon();
    }

    private void UpdateMuteIcon() =>
        MuteButton.Content = (_controller?.Mute ?? false) || VolumeSlider.Value <= 0 ? MuteGlyph : VolumeGlyph;

    private void AdjustVolume(int delta)
    {
        VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + delta, 0, 100);
    }

    private void ChannelToggleButton_Click(object sender, RoutedEventArgs e)
    {
        var show = FsChannelPanel.Visibility != Visibility.Visible;
        FsChannelPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        _osd?.Pin(show);
    }

    private void FullscreenButton_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void ToggleFullscreen()
    {
        _host.SetFullscreen(!_host.IsFullscreen);
        ApplyFullscreenLayout(_host.IsFullscreen);
    }

    private void ApplyFullscreenLayout(bool fullscreen)
    {
        Header.Visibility = fullscreen ? Visibility.Collapsed : Visibility.Visible;
        ChannelDock.Visibility = fullscreen ? Visibility.Collapsed : Visibility.Visible;
        ChannelColumn.Width = fullscreen ? new GridLength(0) : new GridLength(340);
        ChannelToggleButton.Visibility = fullscreen ? Visibility.Visible : Visibility.Collapsed;
        FullscreenButton.Content = fullscreen ? ExitFullscreenGlyph : FullscreenGlyph;

        if (!fullscreen)
        {
            FsChannelPanel.Visibility = Visibility.Collapsed;
            _osd?.Pin(false);
        }
    }

    private void StepChannel(int delta)
    {
        var list = _host.IsFullscreen ? ChannelListFs : ChannelList;
        var count = list.Items.Count;
        if (count == 0)
        {
            return;
        }

        var index = list.SelectedIndex < 0 ? 0 : ((list.SelectedIndex + delta) % count + count) % count;
        list.SelectedIndex = index;
        list.ScrollIntoView(list.SelectedItem);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Space:
                TogglePlayPause();
                break;
            case Key.Left:
                StepChannel(-1);
                break;
            case Key.Right:
                StepChannel(1);
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
            case Key.I:
                ToggleStats();
                break;
            case Key.T:
                _ = ToggleTranslationAsync();
                break;
            case Key.Escape:
                if (FsChannelPanel.Visibility == Visibility.Visible)
                {
                    ChannelToggleButton_Click(this, e);
                }
                else if (_host.IsFullscreen)
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

    private void Overlay_MouseDown(object sender, MouseButtonEventArgs e)
    {
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
        _osd?.Pin(FsChannelPanel.Visibility == Visibility.Visible);

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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _badgeTimer?.Stop();
        _singleClickTimer?.Stop();
        _statsTimer?.Stop();
        _subtitleHideTimer?.Stop();
        _scanCts?.Cancel();
        _translationRequested = false;
        Interlocked.Increment(ref _translationGeneration);
        _translationCts?.Cancel();
        _translationCts?.Dispose();
        _translationCts = null;
        var translation = _translationController;
        _translationController = null;
        if (translation is not null)
        {
            _ = translation.DisposeAsync().AsTask();
        }
        SaveVolumeSetting();

        if (_host.IsFullscreen)
        {
            _host.SetFullscreen(false);
        }

        if (_controller is not null)
        {
            _controller.StateChanged -= Controller_StateChanged;
            _controller.Buffering -= Controller_Buffering;
            VideoSurface.MediaPlayer = null;
            _host.Services.DisposePlaybackInBackground(_controller);
            _controller = null;
        }
    }
}
