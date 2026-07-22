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
        VodClients = new VodSourceClientFactory(Http);
        AppleCms = VodClients.AppleCms;
        Douban = new DoubanClient(Http);
        Catalog = new CatalogService(VodClients, Douban);
        M3u = new M3uParser(Http);
        Hls = new HlsAdSkip(Http);
        ChannelHealth = new ChannelHealthProbe(Http);
        VodCache = new VodCacheService();
        Downloads = new VodDownloadManager(VodCache);
        Settings = SettingsStore.Load();
        // 启动时先挂上磁盘里已有的 AI 额外源（订阅随后异步加载再合并）
        RebuildSources();
    }

    public HttpClient Http { get; }
    public VodSourceClientFactory VodClients { get; }
    public AppleCmsClient AppleCms { get; }
    public DoubanClient Douban { get; }
    public CatalogService Catalog { get; }
    public M3uParser M3u { get; }
    public HlsAdSkip Hls { get; }
    public ChannelHealthProbe ChannelHealth { get; }
    public VodCacheService VodCache { get; }
    public VodDownloadManager Downloads { get; }
    public CatalogPlayabilityCache PlayabilityCache { get; } = new();

    public FavoriteStore Favorites { get; } = new();
    public PlayRecordStore PlayRecords { get; } = new();
    public LocalMediaStore LocalMedia { get; } = new();
    public DesktopSettings Settings { get; set; }

    private IReadOnlyList<VideoSource> _subscriptionSources = [];
    private IReadOnlyList<VideoSource> _extraSources = ExtraVideoSourcesStore.Load();

    /// <summary>当前已加载的视频源(订阅 + AI 额外源;只读快照;整体替换引用,后台线程在途的枚举不受影响)。</summary>
    public IReadOnlyList<VideoSource> Sources { get; private set; } = [];

    /// <summary>仅 AI/CLI 写入的额外源数量。</summary>
    public int ExtraSourceCount => _extraSources.Count;

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

    private void RebuildSources()
    {
        Sources = VideoSourceMerger.Merge(_subscriptionSources, _extraSources);
        PlayabilityCache.Invalidate();
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
        Downloads.Dispose();
        VodCache.Dispose();
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
    }
}
