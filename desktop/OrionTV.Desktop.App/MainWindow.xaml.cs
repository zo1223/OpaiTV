using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using OrionTV.Desktop.App.Views;
using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.App;

/// <summary>导航壳：顶部标题栏 + 可折叠侧边栏 + 内容区，实现 <see cref="IAppHost"/>。</summary>
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
        ApplySidebarMode(animate: false);
        Services = new AppServices();
        Services.AdultVisibilityChanged += OnAdultVisibilityChanged;
        Services.Updates.AvailableUpdateChanged += OnAvailableUpdateChanged;
        FavoritesPanel.EntryClicked += OnFavoritesPanelEntryClicked;
        FavoritesPanel.RemoveRequested += OnFavoritesPanelRemoveRequested;
        FavoritesPanel.ViewAllRequested += OnFavoritesPanelViewAllRequested;
        FavoritesPanel.Closed += OnFavoritesPanelClosed;
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
    }

    // ---- 首页 / 搜索页 / 详情页 键盘切换（规则见 PageKeyboardSwitcher）----

    // 最近一次打开的详情：数字键 3 / Ctrl+→ 回到它。视图还活着就直接切回去，已被“返回”销毁则按参数重新打开。
    private DetailView? _lastDetailView;
    private DetailParameter? _lastDetailParameter;

    private void RememberDetail(DetailView view, object? parameter)
    {
        if (parameter is DetailParameter detail)
        {
            _lastDetailView = view;
            // 重新打开时不带上一轮的源快照：让它重新走取数入口（成人过滤、片源模式都按当时的状态算）
            _lastDetailParameter = detail with { CandidateSources = null, PlayableSources = null };
        }
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 收藏面板开着时不切页（它自己处理 Esc）；输入法正在组字的按键是 ImeProcessed，不会映射到下面的键
        if (e.Handled || FavoritesPanel.IsOpen || !TryMapPageSwitchKey(e.Key, out var key))
        {
            return;
        }

        var modifiers = Keyboard.Modifiers;
        var target = PageKeyboardSwitcher.Resolve(
            CurrentSwitchablePage(),
            key,
            ctrl: modifiers == ModifierKeys.Control,
            focusInTextInput: Keyboard.FocusedElement is TextBoxBase or PasswordBox or ComboBox or ComboBoxItem,
            detailAvailable: _lastDetailParameter is not null,
            otherModifiers: (modifiers & ~ModifierKeys.Control) != ModifierKeys.None);
        if (target is { } page && SwitchToPage(page))
        {
            e.Handled = true;
        }
    }

    private static bool TryMapPageSwitchKey(Key key, out PageSwitchKey mapped)
    {
        PageSwitchKey? result = key switch
        {
            Key.D1 or Key.NumPad1 => PageSwitchKey.Digit1,
            Key.D2 or Key.NumPad2 => PageSwitchKey.Digit2,
            Key.D3 or Key.NumPad3 => PageSwitchKey.Digit3,
            Key.Left => PageSwitchKey.Left,
            Key.Right => PageSwitchKey.Right,
            _ => null,
        };
        mapped = result ?? default;
        return result is not null;
    }

    private SwitchablePage? CurrentSwitchablePage() => _current switch
    {
        HomeView => SwitchablePage.Home,
        SearchView => SwitchablePage.Search,
        DetailView => SwitchablePage.Detail,
        _ => null,
    };

    private bool SwitchToPage(SwitchablePage target)
    {
        if (target == SwitchablePage.Detail)
        {
            var live = _lastDetailView is null
                ? default
                : _sectionStates.FirstOrDefault(pair => ReferenceEquals(pair.Value.Current, _lastDetailView));
            if (live.Value is not null)
            {
                // 详情页还叠在某个分区上：切回那个分区，不重新加载
                Navigate(live.Key);
            }
            else if (_lastDetailParameter is { } parameter)
            {
                // 已经被“返回”销毁：和从卡片点进去一样重新打开（压入当前分区的返回栈）
                Navigate(AppPage.Detail, parameter);
            }
            else
            {
                return false;
            }
        }
        else
        {
            // 与点侧栏相同：切换分区，不改各分区自己的返回栈……
            var page = target == SwitchablePage.Home ? AppPage.Home : AppPage.Search;
            Navigate(page);
            // ……但这个分区上还叠着详情页时，退回它的根页面（等同于按“返回”；详情可以再按 3 回来）
            while (_activeSection == page
                && _sectionStates.TryGetValue(page, out var section)
                && section.BackStack.Count > 0)
            {
                GoBack();
            }
        }

        // 焦点跟着走：等新页面完成布局后再设焦点
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (_current is IKeyboardFocusTarget focusTarget)
            {
                focusTarget.FocusDefaultElement();
            }
        }));
        return true;
    }

    public AppServices Services { get; }

    public void ShowFavoritesPanel()
    {
        // 原生视频宿主（播放、直播、本地视频）永远画在 WPF 内容之上，面板会被视频盖住：这时走原来的收藏页。
        if (_current is PlayerView or LiveView or LocalVideoView)
        {
            Navigate(AppPage.Favorites);
            return;
        }

        RefreshFavoritesPanel();
        FavoritesPanel.Open();
        // 面板开着的时候侧栏高亮“收藏”，关闭后恢复（OnFavoritesPanelClosed）
        NavFavorites.IsChecked = true;
    }

    // 设置页顶部就是“软件更新”；它自己跟随 DesktopUpdateService 的检查结果显示“下载并安装”，
    // 这里不带参数，避免重新载入设置页冲掉还没保存的输入。
    public void OpenSettings() => Navigate(AppPage.Settings);

    private void RefreshFavoritesPanel() =>
        // 与收藏页同一份数据、同一套成人过滤
        FavoritesPanel.SetEntries(Services.Favorites.GetVisibleEntries(
            Services.IsAdultSourceHidden,
            Services.IsAdultCategoryHidden));

    private void OnFavoritesPanelEntryClicked(FavoriteEntry entry)
    {
        FavoritesPanel.Close(animate: false);
        Navigate(AppPage.Detail, new DetailParameter(entry.Item.Title, entry.Source, entry.Id));
    }

    private void OnFavoritesPanelRemoveRequested(FavoriteEntry entry)
    {
        Services.Favorites.Remove(entry.Source, entry.Id);
        RefreshFavoritesPanel();
        Services.NotifyFavoritesChanged();
    }

    private void OnFavoritesPanelViewAllRequested()
    {
        FavoritesPanel.Close(animate: false);
        Navigate(AppPage.Favorites);
    }

    // 面板不是常驻页面：关闭后侧栏选中恢复到当前顶层页
    private void OnFavoritesPanelClosed() => RestoreSidebarSelection();

    // 更新检查在后台线程完成
    private void OnAvailableUpdateChanged() => Dispatcher.BeginInvoke(RefreshUpdateBadge);

    private void RefreshUpdateBadge()
    {
        var visibility = Services.Updates.HasUpdateBadge ? Visibility.Visible : Visibility.Collapsed;
        SettingsUpdateDot.Visibility = visibility;
        TopSettingsUpdateDot.Visibility = visibility;
    }

    // ---- 侧栏折叠（纯外观状态：展开 → 仅图标 → 隐藏；只记在本机 ui_sidebar.txt，不进设置数据）----

    private static readonly string SidebarStateFile =
        System.IO.Path.Combine(DesktopPaths.DataDirectory, "ui_sidebar.txt");

    private SidebarMode _sidebarMode = LoadSidebarMode();

    private static SidebarMode LoadSidebarMode()
    {
        try
        {
            return System.IO.File.Exists(SidebarStateFile)
                ? SidebarLayout.Parse(System.IO.File.ReadAllText(SidebarStateFile))
                : SidebarMode.Expanded;
        }
        catch
        {
            return SidebarMode.Expanded;
        }
    }

    private void SidebarToggle_Click(object sender, RoutedEventArgs e)
    {
        _sidebarMode = SidebarLayout.Next(_sidebarMode);
        try
        {
            System.IO.Directory.CreateDirectory(DesktopPaths.DataDirectory);
            System.IO.File.WriteAllText(SidebarStateFile, _sidebarMode.ToString());
        }
        catch
        {
            // 记不住折叠状态不影响使用
        }

        ApplySidebarMode(animate: true);
    }

    private void ApplySidebarMode(bool animate)
    {
        if (IsFullscreen)
        {
            // 全屏时侧栏与顶部栏都收起；退出全屏时 SetFullscreen 会再调一次
            return;
        }

        var compact = _sidebarMode == SidebarMode.Compact;
        var hidden = _sidebarMode == SidebarMode.Hidden;
        // SidebarItemStyle 按窗口 Tag 决定是否只显示图标
        Tag = compact ? "compact" : null;
        var headers = compact ? Visibility.Collapsed : Visibility.Visible;
        NavHeaderMain.Visibility = headers;
        NavHeaderAi.Visibility = headers;
        NavHeaderUser.Visibility = headers;
        SettingsUpdateDot.Margin = new Thickness(0, 0, compact ? 16 : 22, 0);
        Sidebar.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
        SidebarColumn.Width = new GridLength(SidebarLayout.Width(_sidebarMode));
        SidebarToggle.ToolTip = SidebarLayout.ToggleToolTip(_sidebarMode);
        if (animate && !hidden)
        {
            Motion.FadeIn(Sidebar, 180);
        }
    }

    // 顶部栏按钮：与侧栏对应项走同一条路
    private void TopSearch_Click(object sender, RoutedEventArgs e) => Navigate(AppPage.Search);

    private void TopFavorites_Click(object sender, RoutedEventArgs e) => ShowFavoritesPanel();

    private void TopSettings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    public void Navigate(AppPage page, object? parameter = null)
    {
        if (FavoritesPanel.IsOpen)
        {
            FavoritesPanel.Close(animate: false);
        }

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

        if (view is DetailView openedDetail)
        {
            RememberDetail(openedDetail, parameter);
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
            TopBar.Visibility = Visibility.Collapsed;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            // 先 Normal 再 Maximized，确保无边框窗口铺满并覆盖任务栏。
            WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
            IsFullscreen = true;
        }
        else
        {
            // 先清全屏标记：ApplySidebarMode 在全屏期间不动侧栏
            IsFullscreen = false;
            TopBar.Visibility = Visibility.Visible;
            ApplySidebarMode(animate: false);
            WindowStyle = _prevWindowStyle;
            ResizeMode = _prevResizeMode;
            WindowState = _prevWindowState;
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
        AppPage.Player => _playerView ??= CreatePlayerView(),
        _ => new HomeView(this),
    };

    // ---- 迷你播放窗口（灵动岛）----

    private MiniPlayerWindow? _miniPlayer;

    private PlayerView CreatePlayerView()
    {
        var player = new PlayerView(this);
        player.MiniPlayerRequested += OnMiniPlayerRequested;
        return player;
    }

    /// <summary>播放页点了“小窗播放”：先像按“返回”一样离开播放页（保存记录、释放播放器），再让小窗从同一进度接着播。</summary>
    private void OnMiniPlayerRequested(PlayParameter request)
    {
        if (_current is PlayerView)
        {
            GoBack();
        }
        else
        {
            ClosePlayer();
        }

        CloseMiniPlayer();
        var window = new MiniPlayerWindow(Services, request);
        window.RestoreRequested += OnMiniPlayerRestoreRequested;
        window.Closed += (sender, _) =>
        {
            if (ReferenceEquals(_miniPlayer, sender))
            {
                _miniPlayer = null;
            }
        };
        _miniPlayer = window;
        window.Show();
    }

    /// <summary>点小窗画面 / “回到播放器”：关掉小窗（它会存一次记录并释放播放器），主窗口回到播放页从小窗的进度续播。</summary>
    private void OnMiniPlayerRestoreRequested(PlayParameter request)
    {
        CloseMiniPlayer();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Navigate(AppPage.Player, request);
    }

    private void CloseMiniPlayer()
    {
        var window = _miniPlayer;
        _miniPlayer = null;
        window?.Close();
    }

    private void NavigateToPlayer(object? parameter)
    {
        // 从任何入口进入播放页都先关掉小窗，避免两个播放器同时出声
        CloseMiniPlayer();
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
        RememberDetail(detail, parameter);
        return true;
    }

    // 页面切换动效的时长（毫秒）与上移距离（像素）。
    private const int PageTransitionMs = 160;
    private const double PageTransitionRise = 8;

    private void ShowContentView(UIElement view)
    {
        // 只有真的换了页面（或从播放页回到内容区）才播切换动效；同一页面重复显示不闪。
        var changed = !ReferenceEquals(ContentHost.Content, view) || ContentHost.Visibility != Visibility.Visible;
        ContentHost.Content = view;
        ContentHost.Visibility = Visibility.Visible;
        if (changed && SupportsPageTransition(view))
        {
            Motion.FadeRiseIn(ContentHost, ContentShift, PageTransitionMs, rise: PageTransitionRise);
        }
        else
        {
            // 上一次切换的动画可能还没播完：立即停掉，新页面直接完整显示。
            ContentHost.BeginAnimation(OpacityProperty, null);
            ContentShift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
        }

        if (_playerView is not null)
        {
            PlayerHost.Visibility = Visibility.Hidden;
        }

        if (view is INavigationVisibilityAware aware)
        {
            aware.OnShown();
        }
    }

    /// <summary>
    /// 只给纯 WPF 内容的页面加切换动效。直播、本地视频、AI 工具页里有原生窗口，
    /// 原生窗口不跟随 WPF 的透明度与位移，所以这些页面直接显示。
    /// </summary>
    private static bool SupportsPageTransition(UIElement view) =>
        view is HomeView or SearchView or DetailView or FavoritesView or SettingsView;

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
            ShowFavoritesPanel();
        }
        else if (sender == NavSettings)
        {
            OpenSettings();
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
            var due = Services.Updates.IsAutomaticCheckDue();
            if (!due && !Services.Updates.HasKnownNewerVersion())
            {
                return;
            }

            // 检查完成后服务触发 AvailableUpdateChanged，设置入口的小红点随之刷新。
            var update = await Services.Updates.CheckForUpdateAsync();
            if (update is null || !IsLoaded || !due)
            {
                // 间隔内的重启只是把小红点恢复出来，不再弹窗打扰。
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

        // 记住的“最近详情”也是按旧状态打开的：一并忘掉，数字键 3 不能把它带回来
        _lastDetailView = null;
        _lastDetailParameter = null;

        // 小窗正在播被隐藏的内容时一并关掉（与下面关闭播放页用的是同一组判断）
        if (_miniPlayer is { } mini
            && (Services.IsAdultSourceHidden(mini.SourceKey) || Services.IsAdultCategoryHidden(mini.Category)))
        {
            CloseMiniPlayer();
        }

        var refresh = false;
        if (_playerView is { } player)
        {
            if (Services.IsAdultSourceHidden(player.CurrentSourceKey)
                || Services.IsAdultCategoryHidden(player.CurrentCategory))
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
        Services.Updates.AvailableUpdateChanged -= OnAvailableUpdateChanged;
        _aiSourceWatcher?.Dispose();
        _aiSourceWatcher = null;
        CloseMiniPlayer();

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
