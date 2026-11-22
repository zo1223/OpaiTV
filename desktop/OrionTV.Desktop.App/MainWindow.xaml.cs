using System.Windows;
using System.Windows.Controls;
using OrionTV.Desktop.App.Views;

namespace OrionTV.Desktop.App;

/// <summary>导航壳：左侧侧边栏 + 内容区，实现 <see cref="IAppHost"/>。</summary>
public partial class MainWindow : Window, IAppHost
{
    private static readonly HashSet<AppPage> TopLevelPages =
        [AppPage.Home, AppPage.Search, AppPage.Favorites, AppPage.Live, AppPage.LocalVideo, AppPage.Settings];

    private readonly Dictionary<AppPage, SectionNavigationState> _sectionStates = new();
    private AppPage? _activeSection;
    private UIElement? _current;
    private PlayerView? _playerView;
    private AiSourceWatcher? _aiSourceWatcher;
    private int _lastNotifiedSourceCount = -1;

    public MainWindow()
    {
        InitializeComponent();
        Services = new AppServices();
        Services.AdultVisibilityChanged += OnAdultVisibilityChanged;
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
    }

    public AppServices Services { get; }

    public void Navigate(AppPage page, object? parameter = null)
    {
        if (page == AppPage.Player)
        {
            NavigateToPlayer(parameter);
            return;
        }

        if (TopLevelPages.Contains(page))
        {
            NavigateToSection(page, parameter);
            return;
        }

        if (page == AppPage.Detail && TryNavigateFromPlayerToDetail(parameter))
        {
            return;
        }

        var section = EnsureActiveSection();
        NotifyHidden(section.Current);
        section.BackStack.Push(section.Current);
        var view = CreateView(page);
        section.Current = view;
        _current = view;
        ShowContentView(view);

        if (view is INavigationAware aware)
        {
            aware.OnNavigatedTo(parameter);
        }
    }

    public void GoBack()
    {
        if (_current is PlayerView)
        {
            ClosePlayer();
            ShowActiveSection();
            return;
        }

        var section = EnsureActiveSection();
        if (section.BackStack.Count > 0)
        {
            NotifyHidden(section.Current);
            Discard(section.Current);
            var previous = section.BackStack.Pop();
            section.Current = previous;
            _current = previous;
            ShowContentView(previous);
            return;
        }

        if (_activeSection != AppPage.Home)
        {
            Navigate(AppPage.Home);
        }
    }

    private void NavigateToSection(AppPage page, object? parameter)
    {
        var created = !_sectionStates.TryGetValue(page, out var section);
        if (created)
        {
            section = new SectionNavigationState(CreateView(page));
            _sectionStates.Add(page, section);
        }

        if (_current is PlayerView)
        {
            // 侧栏切换只隐藏播放页，不能销毁当前媒体与播放进度。
            HidePlayerForNavigation();
        }
        else if (!ReferenceEquals(_current, section!.Current))
        {
            NotifyHidden(_current);
        }

        _activeSection = page;
        _current = section!.Current;
        ShowContentView(section.Current);
        SyncSidebar(page);

        if ((created || parameter is not null) && section.Current is INavigationAware aware)
        {
            aware.OnNavigatedTo(parameter);
        }
    }

    private SectionNavigationState EnsureActiveSection()
    {
        var page = _activeSection ?? AppPage.Home;
        _activeSection = page;
        if (_sectionStates.TryGetValue(page, out var section))
        {
            return section;
        }

        var view = CreateView(page);
        section = new SectionNavigationState(view);
        _sectionStates.Add(page, section);
        if (view is INavigationAware aware)
        {
            aware.OnNavigatedTo(null);
        }

        return section;
    }

    private void ShowActiveSection()
    {
        var section = EnsureActiveSection();
        _current = section.Current;
        ShowContentView(section.Current);
        SyncSidebar(_activeSection!.Value);
    }

    private WindowStyle _prevWindowStyle;
    private WindowState _prevWindowState;
    private ResizeMode _prevResizeMode;

    public bool IsFullscreen { get; private set; }

    public void SetFullscreen(bool enabled)
    {
        if (enabled == IsFullscreen)
        {
            return;
        }

        if (enabled)
        {
            _prevWindowStyle = WindowStyle;
            _prevWindowState = WindowState;
            _prevResizeMode = ResizeMode;

            Sidebar.Visibility = Visibility.Collapsed;
            SidebarColumn.Width = new GridLength(0);
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            // 先 Normal 再 Maximized，确保无边框窗口铺满并覆盖任务栏。
            WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
            IsFullscreen = true;
        }
        else
        {
            Sidebar.Visibility = Visibility.Visible;
            SidebarColumn.Width = new GridLength(210);
            WindowStyle = _prevWindowStyle;
            ResizeMode = _prevResizeMode;
            WindowState = _prevWindowState;
            IsFullscreen = false;
        }
    }

    private UIElement CreateView(AppPage page) => page switch
    {
        AppPage.Home => new HomeView(this),
        AppPage.Search => new SearchView(this),
        AppPage.Favorites => new FavoritesView(this),
        AppPage.Live => new LiveView(this),
        AppPage.LocalVideo => new LocalVideoView(this),
        AppPage.Settings => new SettingsView(this),
        AppPage.Detail => new DetailView(this),
        AppPage.Player => _playerView ??= new PlayerView(this),
        _ => new HomeView(this),
    };

    private void NavigateToPlayer(object? parameter)
    {
        _ = EnsureActiveSection();
        if (_current is not PlayerView)
        {
            NotifyHidden(_current);
        }

        var player = (PlayerView)CreateView(AppPage.Player);
        _current = player;
        if (parameter is not null)
        {
            player.OnNavigatedTo(parameter);
        }

        // 常驻播放页先切换到新媒体和加载态，再显示，避免旧原生画布短暂露出。
        ShowPlayerView(player);
        NavPlayer.Visibility = Visibility.Visible;
        NavPlayer.IsChecked = true;
    }

    private bool TryNavigateFromPlayerToDetail(object? parameter)
    {
        if (_current is not PlayerView)
        {
            return false;
        }

        var section = EnsureActiveSection();
        DetailView detail;
        if (section.Current is DetailView existing)
        {
            detail = existing;
        }
        else
        {
            detail = new DetailView(this);
            section.BackStack.Push(section.Current);
            section.Current = detail;
        }

        ClosePlayer();
        _current = detail;
        ShowContentView(detail);
        SyncSidebar(_activeSection!.Value);
        detail.OnNavigatedTo(parameter);
        return true;
    }

    private void ShowContentView(UIElement view)
    {
        ContentHost.Content = view;
        ContentHost.Visibility = Visibility.Visible;
        if (_playerView is not null)
        {
            PlayerHost.Visibility = Visibility.Hidden;
        }

        if (view is INavigationVisibilityAware aware)
        {
            aware.OnShown();
        }
    }

    private void ShowPlayerView(PlayerView player)
    {
        if (!ReferenceEquals(PlayerHost.Content, player))
        {
            PlayerHost.Content = player;
        }

        ContentHost.Visibility = Visibility.Collapsed;
        PlayerHost.Visibility = Visibility.Visible;
    }

    private void HidePlayerForNavigation()
    {
        if (_playerView is null)
        {
            return;
        }

        PlayerHost.Visibility = Visibility.Hidden;
        ContentHost.Visibility = Visibility.Visible;
    }

    private void ClosePlayer()
    {
        if (_playerView is null)
        {
            return;
        }

        _playerView.Dispose();
        PlayerHost.Content = null;
        PlayerHost.Visibility = Visibility.Collapsed;
        _playerView = null;
        NavPlayer.Visibility = Visibility.Collapsed;
    }

    private void SyncSidebar(AppPage page)
    {
        var target = page switch
        {
            AppPage.Player => NavPlayer,
            AppPage.Home => NavHome,
            AppPage.Search => NavSearch,
            AppPage.Live => NavLive,
            AppPage.LocalVideo => NavLocalVideo,
            AppPage.Favorites => NavFavorites,
            AppPage.Settings => NavSettings,
            _ => null,
        };

        if (target is not null)
        {
            if (target == NavPlayer)
            {
                NavPlayer.Visibility = Visibility.Visible;
            }

            target.IsChecked = true;
        }
    }

    private static void Discard(UIElement? view)
    {
        if (view is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private static void NotifyHidden(UIElement? view)
    {
        if (view is INavigationVisibilityAware aware)
        {
            aware.OnHidden();
        }
    }

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender == NavHome)
        {
            Navigate(AppPage.Home);
        }
        else if (sender == NavPlayer)
        {
            Navigate(AppPage.Player);
        }
        else if (sender == NavSearch)
        {
            Navigate(AppPage.Search);
        }
        else if (sender == NavLive)
        {
            Navigate(AppPage.Live);
        }
        else if (sender == NavLocalVideo)
        {
            Navigate(AppPage.LocalVideo);
        }
        else if (sender == NavClaude)
        {
            OpenAiImport(AiCliTool.ClaudeCode);
        }
        else if (sender == NavCodex)
        {
            OpenAiImport(AiCliTool.Codex);
        }
        else if (sender == NavFavorites)
        {
            Navigate(AppPage.Favorites);
        }
        else if (sender == NavSettings)
        {
            Navigate(AppPage.Settings);
        }
    }

    private void OpenAiImport(AiCliTool tool)
    {
        // AI 项不是常驻页面：弹窗后恢复侧栏选中到当前顶层页
        var dialog = new AiImportDialog(this, tool) { Owner = this };
        dialog.ShowDialog();
        RestoreSidebarSelection();
    }

    private void RestoreSidebarSelection()
    {
        var page = _current is PlayerView
            ? AppPage.Player
            : _activeSection ?? AppPage.Home;
        SyncSidebar(page);
    }

    private sealed class SectionNavigationState
    {
        internal SectionNavigationState(UIElement current)
        {
            Current = current;
        }

        internal UIElement Current { get; set; }
        internal Stack<UIElement> BackStack { get; } = new();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        ContentHost.Content = new TextBlock
        {
            Text = "正在加载视频源…",
            Foreground = (System.Windows.Media.Brush)FindResource("MutedTextBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 16,
        };

        // 启动时合并磁盘上已有的 AI 额外源（订阅尚未加载时也能看到）
        Services.ReloadExtraSources();

        if (!string.IsNullOrWhiteSpace(Services.Settings.SubscriptionUrl))
        {
            try
            {
                await Services.LoadSubscriptionAsync(Services.Settings.SubscriptionUrl);
            }
            catch
            {
                // 订阅加载失败时仍进入首页，由设置页提示重新配置。
            }
        }

        _aiSourceWatcher = new AiSourceWatcher(OnAiSourcesFileChanged, Dispatcher);
        _aiSourceWatcher.Start();
        _lastNotifiedSourceCount = Services.Sources.Count;

        Navigate(AppPage.Home);
        _ = CheckForUpdatesOnStartupAsync();
    }

    private async Task CheckForUpdatesOnStartupAsync()
    {
        try
        {
            if (!Services.Updates.IsAutomaticCheckDue())
            {
                return;
            }

            var update = await Services.Updates.CheckForUpdateAsync();
            if (update is null || !IsLoaded)
            {
                return;
            }

            if (_current is PlayerView)
            {
                Title = $"OpaiTV · 可更新至 {update.VersionText}";
                return;
            }

            var result = MessageBox.Show(
                this,
                $"发现 OpaiTV {update.VersionText}，是否前往设置页下载并安装？",
                "发现新版本",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);
            if (result == MessageBoxResult.Yes)
            {
                Navigate(AppPage.Settings, update);
            }
        }
        catch
        {
            // Automatic update checks never interrupt startup or playback.
        }
    }

    // 可能在后台线程触发（订阅加载完成后重算源列表）
    private void OnAdultVisibilityChanged() => Dispatcher.BeginInvoke(ResetCatalogPagesForAdultFilter);

    /// <summary>
    /// 成人内容(18+)开关切换，或成人源被添加/删除/改标记之后：已打开的首页、搜索、收藏、详情页
    /// 里还留着按旧状态取到的内容，全部丢弃，下次进入时按当前状态重新取数；正在播放被隐藏源时关闭播放页。
    /// </summary>
    private void ResetCatalogPagesForAdultFilter()
    {
        if (!IsLoaded)
        {
            return;
        }

        var refresh = false;
        if (_playerView is { } player)
        {
            if (Services.IsAdultSourceHidden(player.CurrentSourceKey))
            {
                refresh = _current is PlayerView;
                ClosePlayer();
            }
            else
            {
                player.DropSourceSwitchResults();
            }
        }

        foreach (var (page, section) in _sectionStates.ToList())
        {
            var isCatalogPage = page is AppPage.Home or AppPage.Search or AppPage.Favorites;
            if (!isCatalogPage && section.BackStack.Count == 0)
            {
                continue;
            }

            if (ReferenceEquals(_current, section.Current))
            {
                NotifyHidden(_current);
                refresh = true;
            }

            // 其它分区只可能在根页面之上叠了详情页：退回根页面
            while (section.BackStack.Count > 0)
            {
                Discard(section.Current);
                section.Current = section.BackStack.Pop();
            }

            if (isCatalogPage)
            {
                Discard(section.Current);
                _sectionStates.Remove(page);
            }
        }

        if (_sectionStates.TryGetValue(AppPage.Settings, out var settingsSection)
            && settingsSection.Current is SettingsView settingsView)
        {
            settingsView.RefreshVideoSourceState();
        }

        if (refresh)
        {
            // 被丢弃的分区在这里重建，并按当前状态重新取数
            ShowActiveSection();
        }
    }

    private void OnAiSourcesFileChanged(AiSourceWatcher.ChangeKind kind)
    {
        _ = ReloadAiSourcesAsync(kind);
    }

    private async Task ReloadAiSourcesAsync(AiSourceWatcher.ChangeKind kind)
    {
        try
        {
            var before = Services.Sources.Count;
            var after = await Services.ReloadSourcesFromDiskAsync().ConfigureAwait(true);
            if (after == _lastNotifiedSourceCount && after == before && kind == AiSourceWatcher.ChangeKind.ExtraSources)
            {
                return;
            }

            _lastNotifiedSourceCount = after;
            Title = after > before
                ? $"OpaiTV · 已导入 AI 源（共 {after} 个）"
                : $"OpaiTV · 视频源已更新（共 {after} 个）";
        }
        catch
        {
            // 后台重载失败不打断 UI
        }
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        Services.AdultVisibilityChanged -= OnAdultVisibilityChanged;
        _aiSourceWatcher?.Dispose();
        _aiSourceWatcher = null;

        if (_playerView is not null)
        {
            ClosePlayer();
        }

        foreach (var section in _sectionStates.Values)
        {
            Discard(section.Current);
            while (section.BackStack.Count > 0)
            {
                Discard(section.BackStack.Pop());
            }
        }

        _sectionStates.Clear();
        _current = null;

        Services.Dispose();
    }
}
