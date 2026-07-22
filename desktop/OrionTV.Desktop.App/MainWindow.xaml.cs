using System.Windows;
using System.Windows.Controls;
using OrionTV.Desktop.App.Views;

namespace OrionTV.Desktop.App;

/// <summary>导航壳：左侧侧边栏 + 内容区，实现 <see cref="IAppHost"/>。</summary>
public partial class MainWindow : Window, IAppHost
{
    private static readonly HashSet<AppPage> TopLevelPages =
        [AppPage.Home, AppPage.Search, AppPage.Favorites, AppPage.Live, AppPage.LocalVideo, AppPage.Settings];

    private readonly Stack<UIElement> _backStack = new();
    private UIElement? _current;
    private AiSourceWatcher? _aiSourceWatcher;
    private int _lastNotifiedSourceCount = -1;

    public MainWindow()
    {
        InitializeComponent();
        Services = new AppServices();
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
    }

    public AppServices Services { get; }

    public void Navigate(AppPage page, object? parameter = null)
    {
        var isTopLevel = TopLevelPages.Contains(page);
        if (isTopLevel)
        {
            while (_backStack.Count > 0)
            {
                Discard(_backStack.Pop());
            }

            Discard(_current);
        }
        else if (_current is not null)
        {
            _backStack.Push(_current);
        }

        var view = CreateView(page);
        _current = view;
        ContentHost.Content = view;

        if (isTopLevel)
        {
            SyncSidebar(page);
        }

        if (view is INavigationAware aware)
        {
            aware.OnNavigatedTo(parameter);
        }
    }

    public void GoBack()
    {
        if (_backStack.Count > 0)
        {
            Discard(_current);
            var previous = _backStack.Pop();
            _current = previous;
            ContentHost.Content = previous;
            return;
        }

        if (_current is not HomeView)
        {
            Navigate(AppPage.Home);
        }
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
        AppPage.Player => new PlayerView(this),
        _ => new HomeView(this),
    };

    private void SyncSidebar(AppPage page)
    {
        var target = page switch
        {
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

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender == NavHome)
        {
            Navigate(AppPage.Home);
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
        var page = _current switch
        {
            HomeView => AppPage.Home,
            SearchView => AppPage.Search,
            LiveView => AppPage.Live,
            LocalVideoView => AppPage.LocalVideo,
            FavoritesView => AppPage.Favorites,
            SettingsView => AppPage.Settings,
            _ => AppPage.Home,
        };
        SyncSidebar(page);
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
        _aiSourceWatcher?.Dispose();
        _aiSourceWatcher = null;

        while (_backStack.Count > 0)
        {
            Discard(_backStack.Pop());
        }

        Discard(_current);
        Services.Dispose();
    }
}
