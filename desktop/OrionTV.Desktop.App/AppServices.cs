using System.Net.Http;
using LibVLCSharp.Shared;
using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.App;

/// <summary>跨页面共享的服务与状态容器（单例，由 MainWindow 创建）。</summary>
public sealed class AppServices : IDisposable
{
    private LibVLC? _libVlc;
    private Task<LibVLC>? _libVlcInitialization;
    private readonly object _libVlcSync = new();
    private readonly object _playbackCleanupSync = new();
    private readonly HashSet<Task> _playbackCleanups = [];

    public AppServices()
    {
        Http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        Http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) OpaiTV-Desktop");
        Updates = new DesktopUpdateService();
        // 直连优先的智能分流：每个站点先直连，直连不通才走设置里的代理，走通的路记在本机（route_memory.json）。
        // 代理模式每个请求现读；_settings 在构造函数后半段才赋值，更早被读到时按系统代理处理（与没有这个设置时一致）。
        PlaybackProxyRuntime.Current = () => PlaybackProxyDecision.From(_settings);
        Routes = new RouteMemory(DesktopPaths.RouteMemoryFile);
        PlaybackProxyRuntime.Routes = Routes;
        // 片源站点的请求（搜索、详情、分类、首页取数）走分流；20 秒总时限不变，直连那一次最多占其中约 5 秒。
        SourceHttp = new HttpClient(SmartRouteHandler.Create(static () => PlaybackProxyRuntime.Current(), Routes))
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        SourceHttp.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) OpaiTV-Desktop");
        VodClients = new VodSourceClientFactory(SourceHttp);
        AppleCms = VodClients.AppleCms;
        Douban = new DoubanClient(Http);
        Tmdb = new TmdbClient(Http);
        Catalog = new CatalogService(VodClients, Douban) 
        {
            IsAdultSourceHidden = IsAdultSourceHidden,
            // _settings 在构造函数后半段才赋值；万一更早被调用，按“开关关闭”处理（宁可多拦）
            ShowAdultContent = () => _settings?.ShowAdultContent == true,
            // 片源模式（稳定 / 影视 / 全量 / 关闭）：每次取数时现读设置，切换立即生效
            SourceModeOf = SourceModeOf,
        };
        M3u = new M3uParser(Http);
        // 取媒体地址的请求（广告区间、可播性探测 / 测速）同样直连优先、代理兜底；弱网缓存下载在 VodCacheService 里接同一套。
        PlaybackHttp = new HttpClient(SmartRouteHandler.Create(static () => PlaybackProxyRuntime.Current(), Routes))
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        PlaybackHttp.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) OpaiTV-Desktop");
        Hls = new HlsAdSkip(PlaybackHttp);
        ChannelHealth = new ChannelHealthProbe(PlaybackHttp);
        VodSourceHealth = new VodSourceHealthProbe(ChannelHealth, new LibVlcMediaDecodeProbe(GetLibVlcAsync));
        VodPlayableSources = new VodPlayableSourceResolver(
            (title, cancellationToken) => Catalog.GetDetailSourcesAsync(Sources, title, cancellationToken),
            (item, cancellationToken) => Catalog.HydrateEpisodesAsync(Sources, item, cancellationToken),
            (items, onResult, cancellationToken) => VodSourceHealth.ProbeManyAsync(
                items,
                onResult,
                concurrency: 4,
                cancellationToken));
        VodCache = new VodCacheService();
        Downloads = new VodDownloadManager(VodCache);
        _danmakuHttp = DanmakuHttp.CreateDefaultClient();
        Danmaku = new DanmakuService(new DanmakuHttp(_danmakuHttp));
        RecordDeletion = new PendingPlayRecordDeletion(PlayRecords, ScheduleOnUiThread);
        _settings = SettingsStore.Load();
        // 启动时先挂上磁盘里已有的 AI 额外源（订阅随后异步加载再合并）
        RebuildSources();
    }

    public HttpClient Http { get; }

    /// <summary>
    /// 取媒体地址用的 HttpClient（m3u8 广告区间、频道 / 片源可播性探测）：按播放代理模式选路。
    /// 直连优先、代理兜底（<see cref="SmartRouteHandler"/>）。
    /// </summary>
    public HttpClient PlaybackHttp { get; }

    /// <summary>
    /// 片源站点请求（搜索、详情、分类、首页取数）用的 HttpClient：与播放同一套直连优先、代理兜底的分流。
    /// 豆瓣、TMDB、订阅、直播源列表、海报、更新检查仍用 <see cref="Http"/>（跟随系统代理），不受代理模式影响。
    /// </summary>
    public HttpClient SourceHttp { get; }

    /// <summary>直连优先分流的线路记忆：每个站点上次走通的是直连还是代理。只存本机，不上传。</summary>
    public RouteMemory Routes { get; }

    /// <summary>
    /// 播放代理模式（系统代理 / 强制代理 / 直连）：立即保存，下一次播放生效（正在播放的不打断）。
    /// 强制代理但地址不合法时返回 false，什么都不改。落盘失败时抛出异常，本次运行内仍按新值生效。
    /// </summary>
    public bool SetPlaybackProxy(string? mode, string? proxyUrl)
    {
        if (!PlaybackProxySettings.TryApply(_settings, mode, proxyUrl, out var updated))
        {
            return false;
        }

        Settings = updated;
        SettingsStore.Save(_settings);
        return true;
    }

    public DesktopUpdateService Updates { get; }

    /// <summary>“接着看”可撤销删除：删除先进入 5 秒撤销窗口，超时才真正删记录。</summary>
    public PendingPlayRecordDeletion RecordDeletion { get; }

    /// <summary>收藏在收藏页之外被改动（侧滑面板里删除）之后触发，已打开的收藏页据此刷新。</summary>
    public event Action? FavoritesChanged;

    public void NotifyFavoritesChanged() => FavoritesChanged?.Invoke();

    /// <summary>“接着看”列表里正在等待撤销的删除不显示。</summary>
    public bool IsPlayRecordPendingDeletion(string source, string id) => RecordDeletion.IsPending(source, id);

    private static IDisposable ScheduleOnUiThread(TimeSpan delay, Action callback)
    {
        // 撤销窗口的定时器跑在 UI 线程：到点提交删除后直接刷新界面，不需要跨线程。
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            callback();
        };
        timer.Start();
        return new UiTimerHandle(timer);
    }

    private sealed class UiTimerHandle(System.Windows.Threading.DispatcherTimer timer) : IDisposable
    {
        public void Dispose() => timer.Stop();
    }
    public VodSourceClientFactory VodClients { get; }
    public AppleCmsClient AppleCms { get; }
    public DoubanClient Douban { get; }
    public CatalogService Catalog { get; }
    public M3uParser M3u { get; }
    public HlsAdSkip Hls { get; }
    public ChannelHealthProbe ChannelHealth { get; }
    internal VodSourceHealthProbe VodSourceHealth { get; }
    internal VodPlayableSourceResolver VodPlayableSources { get; }
    public VodCacheService VodCache { get; }
    public VodDownloadManager Downloads { get; }
    public CatalogPlayabilityCache PlayabilityCache { get; } = new();

    /// <summary>弹幕取数（内置直连腾讯视频、哔哩哔哩、爱奇艺、优酷、芒果TV、咪咕视频；可选自定义 danmu_api 服务器）。用独立的 HttpClient，不影响其他请求。</summary>
    public DanmakuService Danmaku { get; }
    private readonly HttpClient _danmakuHttp;

    public FavoriteStore Favorites { get; } = new();
    public PlayRecordStore PlayRecords { get; } = new();
    /// <summary>搜索记录（标签云），只存本机。</summary>
    public SearchHistoryStore SearchHistory { get; } = new();

    /// <summary>顶栏搜索框与搜索页输入框共用的关键词（只在内存里，不落盘）。</summary>
    public SearchQueryState SearchQuery { get; } = new();
    public LocalMediaStore LocalMedia { get; } = new();
    private DesktopSettings _settings;

    public DesktopSettings Settings
    {
        get => _settings;
        set
        {
            var adultChanged = _settings.ShowAdultContent != value.ShowAdultContent;
            _settings = value;
            if (adultChanged)
            {
                // 成人内容开关变化（设置页切换，或 settings.json 被外部改写后重载）：可见源列表立即重算
                RebuildSources(notifyAdultVisibilityChanged: true);
            }
        }
    }

    private IReadOnlyList<VideoSource> _subscriptionSources = [];
    private IReadOnlyList<VideoSource> _extraSources = ExtraVideoSourcesStore.Load();
    private IReadOnlyList<VideoSource> _allSources = [];
    private string _hiddenAdultSignature = "";

    /// <summary>
    /// 当前可用的视频源(订阅 + 额外源;只读快照;整体替换引用,后台线程在途的枚举不受影响)。
    /// 成人内容(18+)开关关闭时不含成人源：所有页面都从这里取源，被隐藏的源对它们不存在。
    /// </summary>
    public IReadOnlyList<VideoSource> Sources { get; private set; } = [];

    /// <summary>额外源（AI/CLI 写入 + 一键加源）里当前可见的那些，供设置页列出。</summary>
    public IReadOnlyList<VideoSource> VisibleExtraSources =>
        AdultContentFilter.VisibleSources(_extraSources, _settings.ShowAdultContent);

    /// <summary>额外源数量（不含被隐藏的成人源）。</summary>
    public int ExtraSourceCount => VisibleExtraSources.Count;

    /// <summary>因成人内容开关关闭而被隐藏的源数量。</summary>
    public int HiddenAdultSourceCount => _allSources.Count - Sources.Count;

    /// <summary>该源是成人源且成人内容开关关闭。收藏、观看历史等按源 key 保存的数据用它过滤。</summary>
    public bool IsAdultSourceHidden(string? sourceKey) =>
        AdultContentFilter.IsHidden(_allSources, _settings.ShowAdultContent, sourceKey);

    /// <summary>
    /// 成人内容开关关闭，且该分类信息（分类名 / 题材标签）属于成人分类。
    /// 收藏、观看历史保存的分类用它过滤；词表与规则集中在 Core 的 AdultCategories。
    /// </summary>
    public bool IsAdultCategoryHidden(string? category) =>
        AdultContentFilter.IsCategoryHidden(_settings.ShowAdultContent, category);

    /// <summary>
    /// 被隐藏的成人内容发生变化（开关切换，或成人源被添加/删除/改标记）。
    /// 已打开的页面要丢弃按旧状态取到的内容；可能在后台线程触发。
    /// </summary>
    public event Action? AdultVisibilityChanged;

    /// <summary>该片源当前的模式；没有设置过的是全量。</summary>
    public SourceMode SourceModeOf(string sourceKey) => SourceModes.ModeOf(_settings?.SourceModes, sourceKey);

    /// <summary>
    /// 片源模式：立即生效并保存，不等“保存设置”。下一次搜索 / 首页取数就按新模式；
    /// 按旧模式算出来的可播源缓存一并作废。落盘失败时抛出异常，本次运行内仍按新值生效。
    /// </summary>
    public void SetSourceMode(string sourceKey, SourceMode mode)
    {
        Settings = _settings with { SourceModes = SourceModes.With(_settings.SourceModes, sourceKey, mode) };
        PlayabilityCache.Invalidate();
        VodPlayableSources.Invalidate();
        SettingsStore.Save(_settings);
    }

    /// <summary>“为你推荐”的候选池：首页加载过的条目，只在内存里。</summary>
    public RecommendationPool RecommendationPool { get; } = new();

    /// <summary>
    /// TMDB 元数据（详情页补全海报 / 简介 / 评分 / 年份）。只有用户在设置里填了自己的 Key 才会发请求，
    /// 请求只发往 TMDB 官方接口；失败时返回 null，详情页照旧显示原有数据。
    /// </summary>
    public TmdbClient Tmdb { get; }

    /// <summary>TMDB Key：立即保存，只写进本机的 settings.json；留空即停用。</summary>
    public void SetTmdbApiKey(string? key)
    {
        Settings = _settings with { TmdbApiKey = (key ?? string.Empty).Trim() };
        SettingsStore.Save(_settings);
    }

    /// <summary>个性化推荐开关：立即生效并保存。</summary>
    public void SetPersonalizedRecommendations(bool enabled)
    {
        Settings = _settings with { PersonalizedRecommendations = enabled };
        SettingsStore.Save(_settings);
    }

    /// <summary>
    /// 成人内容(18+)开关：立即生效并保存，不等“保存设置”——关掉之后不能因为忘了点保存而继续显示。
    /// 先让取数层生效，再落盘；落盘失败时抛出异常，本次运行内开关仍按新值生效。
    /// </summary>
    public void SetShowAdultContent(bool enabled)
    {
        Settings = _settings with { ShowAdultContent = enabled };
        SettingsStore.Save(_settings);
    }

    /// <summary>
    /// 弹幕设置：和成人内容开关一样立即生效并保存，不等“保存设置”。
    /// 没给的项保持原值；落盘失败时抛出异常，本次运行内仍按新值生效。
    /// </summary>
    public void SetDanmakuSettings(
        bool? enabled = null,
        string? serverUrl = null,
        double? opacity = null,
        string? speed = null,
        string? fontSize = null)
    {
        Settings = _settings with
        {
            DanmakuEnabled = enabled ?? _settings.DanmakuEnabled,
            DanmakuServerUrl = serverUrl is null
                ? _settings.DanmakuServerUrl
                : DanmakuSettingsRules.NormalizeServerUrl(serverUrl),
            DanmakuOpacity = opacity is null
                ? _settings.DanmakuOpacity
                : DanmakuSettingsRules.NormalizeOpacity(opacity.Value),
            DanmakuSpeed = speed is null ? _settings.DanmakuSpeed : DanmakuSettingsRules.NormalizeSpeed(speed),
            DanmakuFontSize = fontSize is null
                ? _settings.DanmakuFontSize
                : DanmakuSettingsRules.NormalizeFontSize(fontSize),
        };
        SettingsStore.Save(_settings);
    }

    /// <summary>
    /// 一键加源：粘贴源订阅链接或单个源接口地址，校验通过即写入 extra_sources.json 并生效。
    /// 地址返回的是订阅配置（api_site）时导入其中全部源，否则按单个源接口添加。
    /// 粘贴的是其他设备导出的同步链接（opaitv-sync://…）时，直接导入链接里的全部源，不联网。
    /// </summary>
    public async Task<OneClickVideoSources.Result> AddVideoSourceAsync(string input, bool adult)
    {
        if (VideoSourceSyncLink.IsSyncLink(input))
        {
            // 链接无效时这里抛出异常，不会动现有的源
            var incoming = VideoSourceSyncLink.Decode(input);
            var imported = OneClickVideoSources.ImportSync(
                ExtraVideoSourcesStore.LoadForUpdate(), _subscriptionSources, incoming, adult);
            ExtraVideoSourcesStore.Save(imported.ExtraSources);
            _extraSources = imported.ExtraSources;
            RebuildSources();
            return imported;
        }

        var url = OneClickVideoSources.NormalizeUrl(input);

        IReadOnlyList<VideoSource>? subscription = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            subscription = (await VideoSourceSubscriptionParser.FetchAsync(Http, url, timeout.Token).ConfigureAwait(true)).Sources;
        }
        catch
        {
            // 不是订阅配置（或暂时连不上）：按单个源接口地址添加，与 RN 端一样不要求此刻能连通。
        }

        // 文件存在但读不出来时这里抛出异常，不继续写入，避免把现有源整体覆盖掉。
        var current = ExtraVideoSourcesStore.LoadForUpdate();
        var result = subscription is { Count: > 0 }
            ? OneClickVideoSources.AddSubscription(current, _subscriptionSources, subscription, adult)
            : OneClickVideoSources.AddSingle(current, _subscriptionSources, url, adult);
        ExtraVideoSourcesStore.Save(result.ExtraSources);
        _extraSources = result.ExtraSources;
        RebuildSources();
        return result;
    }

    /// <summary>
    /// 导出当前全部视频源（订阅 + 额外源）的同步链接，供另一台设备粘贴或扫码导入。
    /// 成人内容开关关闭时也包含成人源（带成人标记）：对方设备导入后仍由它自己的开关决定是否显示。
    /// 没有任何源时抛出 <see cref="VideoSourceSyncLink.EmptyError"/>。
    /// </summary>
    public string ExportVideoSourceSyncLink() => VideoSourceSyncLink.Encode(_allSources);

    /// <summary>可导出的全部源数量（含被隐藏的成人源）。</summary>
    public int ExportableSourceCount => _allSources.Count;

    /// <summary>可导出的源里的成人源数量。</summary>
    public int ExportableAdultSourceCount => _allSources.Count(source => source.Adult);

    /// <summary>从 extra_sources.json 删除一个额外源并立即生效。</summary>
    public void RemoveExtraSource(string key)
    {
        var updated = OneClickVideoSources.Remove(ExtraVideoSourcesStore.LoadForUpdate(), key);
        ExtraVideoSourcesStore.Save(updated);
        _extraSources = updated;
        RebuildSources();
    }

    public bool HasSources => Sources.Count > 0;

    public bool IsLibVlcInitialized
    {
        get
        {
            lock (_libVlcSync)
            {
                return _libVlc is not null;
            }
        }
    }

    /// <summary>共享 LibVLC 实例。首次加载原生插件时在线程池初始化，避免阻塞播放器界面。</summary>
    public Task<LibVLC> GetLibVlcAsync()
    {
        lock (_libVlcSync)
        {
            if (_libVlc is not null)
            {
                return Task.FromResult(_libVlc);
            }

            if (_libVlcInitialization?.IsFaulted == true || _libVlcInitialization?.IsCanceled == true)
            {
                _libVlcInitialization = null;
            }

            return _libVlcInitialization ??= Task.Run(() =>
            {
                LibVLCSharp.Shared.Core.Initialize();
                var libVlc = new LibVLC("--no-video-title-show");
                lock (_libVlcSync)
                {
                    _libVlc = libVlc;
                }

                return libVlc;
            });
        }
    }

    public LibVLC LibVlc => GetLibVlcAsync().GetAwaiter().GetResult();

    public VideoSource? FindSource(string key) => Sources.FirstOrDefault(source => source.Key == key);

    /// <summary>
    /// 加载并解析订阅，刷新内存中的视频源列表，返回源数量。
    /// 订阅为空时保留 AI 额外源，不加载任何内置地址。
    /// </summary>
    public async Task<int> LoadSubscriptionAsync(string url, CancellationToken cancellationToken = default)
    {
        var urls = BuildSubscriptionUrlList(url);
        var merged = new List<VideoSource>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var subscriptionUrl in urls)
        {
            try
            {
                var config = await VideoSourceSubscriptionParser.FetchAsync(Http, subscriptionUrl, cancellationToken)
                    .ConfigureAwait(false);
                foreach (var source in config.Sources)
                {
                    if (string.IsNullOrWhiteSpace(source.Key) || string.IsNullOrWhiteSpace(source.Api))
                    {
                        continue;
                    }

                    if (seenKeys.Add(source.Key))
                    {
                        merged.Add(source);
                    }
                }
            }
            catch
            {
                // 单路订阅失败不阻断其它路
            }
        }

        // 不做原地 Clear/AddRange:ConfigureAwait(false) 后运行在线程池,
        // 原地改动会与 UI/后台的并发枚举竞态(InvalidOperationException 表现为"搜索失败")。
        _subscriptionSources = merged;
        RebuildSources();
        return Sources.Count;
    }

    /// <summary>返回用户明确配置的主订阅；空值不会回退到内置地址。</summary>
    public static IReadOnlyList<string> BuildSubscriptionUrlList(string? primaryUrl)
    {
        var list = new List<string>();
        void add(string? u)
        {
            if (string.IsNullOrWhiteSpace(u))
            {
                return;
            }

            u = u.Trim().TrimEnd('/');
            if (list.Any(x => string.Equals(x, u, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            list.Add(u);
        }

        add(primaryUrl);
        return list;
    }

    /// <summary>从磁盘重新读取 AI 额外源并合并，返回合并后的源数量。</summary>
    public int ReloadExtraSources()
    {
        _extraSources = ExtraVideoSourcesStore.Load();
        RebuildSources();
        return Sources.Count;
    }

    /// <summary>
    /// AI/设置变更后：重读 settings、extra_sources，必要时按主订阅重拉源列表。
    /// </summary>
    public async Task<int> ReloadSourcesFromDiskAsync(CancellationToken cancellationToken = default)
    {
        var previousUrl = Settings.SubscriptionUrl;
        Settings = SettingsStore.Load();
        ReloadExtraSources();

        var subscriptionChanged = !string.Equals(
            previousUrl,
            Settings.SubscriptionUrl,
            StringComparison.OrdinalIgnoreCase);

        if (subscriptionChanged || _subscriptionSources.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(Settings.SubscriptionUrl))
            {
                return await LoadSubscriptionAsync(Settings.SubscriptionUrl, cancellationToken).ConfigureAwait(false);
            }

            _subscriptionSources = [];
            RebuildSources();
        }

        return Sources.Count;
    }

    /// <param name="notifyAdultVisibilityChanged">
    /// 开关本身变了：即使没有任何成人源（被隐藏源的签名不变），按分类过滤的结果也变了，必须通知页面重取。
    /// </param>
    private void RebuildSources(bool notifyAdultVisibilityChanged = false)
    {
        var all = VideoSourceMerger.Merge(_subscriptionSources, _extraSources);
        var showAdultContent = _settings.ShowAdultContent;
        var signature = AdultContentFilter.HiddenSignature(all, showAdultContent);
        _allSources = all;
        Sources = AdultContentFilter.VisibleSources(all, showAdultContent);
        PlayabilityCache.Invalidate();
        VodPlayableSources.Invalidate();

        if (!string.Equals(signature, Interlocked.Exchange(ref _hiddenAdultSignature, signature), StringComparison.Ordinal)
            || notifyAdultVisibilityChanged)
        {
            AdultVisibilityChanged?.Invoke();
        }
    }

    public void ClearSources()
    {
        _subscriptionSources = [];
        RebuildSources();
    }

    public void DisposePlaybackInBackground(PlaybackController controller)
    {
        var cleanup = controller.DisposeAsync();
        lock (_playbackCleanupSync)
        {
            _playbackCleanups.Add(cleanup);
        }

        _ = cleanup.ContinueWith(
            completed =>
            {
                lock (_playbackCleanupSync)
                {
                    _playbackCleanups.Remove(completed);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public void Dispose()
    {
        // 退出时还在撤销窗口里的删除直接生效，不留到下次启动。
        RecordDeletion.Flush();
        Updates.Dispose();
        VodPlayableSources.Dispose();
        Downloads.Dispose();
        VodCache.Dispose();
        PlaybackHttp.Dispose();
        SourceHttp.Dispose();
        Routes.Flush();
        Task[] pendingCleanups;
        lock (_playbackCleanupSync)
        {
            pendingCleanups = _playbackCleanups.ToArray();
        }

        try
        {
            Task.WhenAll(pendingCleanups).GetAwaiter().GetResult();
        }
        catch
        {
            // Player cleanup is best-effort during process shutdown.
        }

        Task<LibVLC>? libVlcInitialization;
        lock (_libVlcSync)
        {
            libVlcInitialization = _libVlcInitialization;
        }

        if (libVlcInitialization is not null)
        {
            try
            {
                _ = libVlcInitialization.GetAwaiter().GetResult();
            }
            catch
            {
                // 播放器初始化失败时没有可释放的原生实例。
            }
        }

        _libVlc?.Dispose();
        Http.Dispose();
        _danmakuHttp.Dispose();
    }
}
