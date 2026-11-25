using System.Windows;
using System.Windows.Controls;
using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.App.Views;

public partial class HomeView : UserControl, INavigationAware, IKeyboardFocusTarget, IDisposable
{
    /// <summary>键盘切换到首页后的默认焦点：卡片区里第一个能拿焦点的元素；卡片区没有时落在页面第一个可聚焦元素上。</summary>
    public void FocusDefaultElement()
    {
        var first = new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.First);
        if (!Grid.MoveFocus(first))
        {
            MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.First));
        }
    }

    private const int PageSize = 30;

    private static readonly string[] MovieTags =
        ["热门", "最新", "经典", "可播放", "豆瓣高分", "冷门佳片", "华语", "欧美", "韩国", "日本", "动作", "喜剧", "爱情", "科幻", "悬疑", "恐怖", "文艺"];

    private static readonly string[] TvTags =
        ["热门", "美剧", "英剧", "韩剧", "日剧", "国产剧", "港剧", "日本动画", "综艺", "纪录片"];

    private readonly IAppHost _host;
    private readonly List<HomeCategory> _categories = new();
    private HomeCategory? _selected;
    private string? _selectedTag;
    private int _pageStart;
    private bool _hasMore = true;
    private bool _loading;
    private bool _loadingMore;
    private bool _disposed;
    private CancellationTokenSource? _cts;
    private readonly HashSet<string> _shownKeys = new(StringComparer.Ordinal);

    // 首页大横幅：挂在卡片网格的头部插槽里，随卡片一起滚动
    private readonly Controls.HeroBannerControl _banner = new();
    private readonly List<CatalogItem> _bannerHomeItems = new();
    private CancellationTokenSource? _bannerPlayCts;
    private bool _bannerPreparing;

    public HomeView(IAppHost host)
    {
        InitializeComponent();
        _host = host;
        Grid.CardClicked += OnCardClicked;
        Grid.EndReached += OnEndReached;
        Grid.CardRemoveRequested += OnRecordRemoveRequested;
        _host.Services.RecordDeletion.Changed += OnRecordDeletionChanged;
        _host.Services.Updates.AvailableUpdateChanged += OnAvailableUpdateChanged;
        RefreshUpdateBadge();
        RefreshUndoBar();

        _banner.PlayRequested += OnBannerPlayRequested;
        _banner.DetailRequested += OpenBannerDetail;
        _banner.DescriptionProvider = BannerDescriptionOf;
        Grid.Header = _banner;
        Grid.SizeChanged += (_, _) => SyncBannerVisibility();
        _host.Services.AdultVisibilityChanged += OnAdultVisibilityChangedForBanner;
        // 离开首页（切到别的页面 / 进了播放页）后，还没准备好的横幅起播作废
        Unloaded += (_, _) => CancelBannerPlay();
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
            {
                CancelBannerPlay();
            }
        };
    }

    // ---------- 首页大横幅：数据只来自个性化推荐与首页已经加载的条目，不为横幅发任何请求 ----------

    /// <summary>
    /// 把首页刚显示的条目喂给横幅。传进来的都是已经过成人过滤 / 片源模式过滤的可见条目。
    /// 首页数据只用来把横幅凑满：凑够以后不再换，切分类时横幅内容保持稳定。
    /// </summary>
    private void FeedBanner(IEnumerable<CatalogItem> items)
    {
        foreach (var item in items)
        {
            if (_bannerHomeItems.Count >= HeroBanner.MaxItems * 2)
            {
                break;
            }

            // 本地视频不进横幅（它的续播走另一条本地路径）
            if (string.Equals(item.Source, "local", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(item.Poster)
                || _bannerHomeItems.Any(existing => HeroBanner.KeyOf(existing) == HeroBanner.KeyOf(item)))
            {
                continue;
            }

            _bannerHomeItems.Add(item);
        }

        RefreshBanner();
    }

    /// <summary>重算横幅：个性化推荐（开关开且有结果）在前，其后是首页已加载的条目。</summary>
    private void RefreshBanner()
    {
        if (_disposed)
        {
            return;
        }

        _banner.SetItems(HeroBanner.Pick(LoadRecommendations(), _bannerHomeItems));
        SyncBannerVisibility();
    }

    /// <summary>
    /// CardGrid 的提示文字（加载中 / 空态 / 失败）居中显示在整个网格区域里：
    /// 区域不够高时横幅会压到它，这时先收起横幅，有卡片了再放出来。
    /// </summary>
    private void SyncBannerVisibility()
    {
        var showsMessage = Grid.Count == 0;
        var roomy = Grid.ActualHeight >= 2 * (Controls.HeroBannerControl.BannerHeight + 70);
        _banner.Suppressed = showsMessage && !roomy;
    }

    /// <summary>首页条目自己不带简介：可播性探测已经取到过候选线路时，借用其中的简介（只查本机缓存）。</summary>
    private string? BannerDescriptionOf(CatalogItem item)
    {
        var candidates = item.CandidateSources;
        if (candidates is not { Count: > 0 } && _host.Services.PlayabilityCache.TryGet(item, out var cached))
        {
            candidates = cached.CandidateSources;
        }

        return HeroBanner.DescriptionOf(
            candidates?.Where(candidate => !_host.Services.Catalog.IsResultHidden(candidate)));
    }

    // 18+ 开关或成人源变化：按旧状态取到的首页条目全部丢掉，横幅只按当前状态重算
    private void OnAdultVisibilityChangedForBanner() => Dispatcher.BeginInvoke(() =>
    {
        if (_disposed)
        {
            return;
        }

        CancelBannerPlay();
        _bannerHomeItems.Clear();
        RefreshBanner();
    });

    private void CancelBannerPlay()
    {
        _bannerPlayCts?.Cancel();
        _bannerPlayCts = null;
        _bannerPreparing = false;
        _banner.SetPreparing(false);
    }

    /// <summary>
    /// 横幅“播放”：与搜索结果点一下起播同一套选线（有观看记录续播，否则第一集）。
    /// 找不到可播线路或准备失败时退回详情页；准备期间离开了首页则作废。
    /// </summary>
    private async void OnBannerPlayRequested(CatalogItem item)
    {
        if (_bannerPreparing || _disposed)
        {
            return;
        }

        _bannerPreparing = true;
        _banner.SetPreparing(true);
        var cts = new CancellationTokenSource();
        _bannerPlayCts = cts;
        PlayParameter? play = null;
        try
        {
            play = await ResolveBannerPlayAsync(item, cts.Token);
        }
        catch
        {
            // 起播准备失败时退回详情页
        }

        var stale = cts.IsCancellationRequested || !ReferenceEquals(_bannerPlayCts, cts)
            || _disposed || !IsLoaded || !IsVisible;
        if (ReferenceEquals(_bannerPlayCts, cts))
        {
            _bannerPlayCts = null;
            _bannerPreparing = false;
            _banner.SetPreparing(false);
        }

        cts.Dispose();
        if (stale)
        {
            return;
        }

        if (play is not null)
        {
            _host.Navigate(AppPage.Player, play);
            return;
        }

        OpenBannerDetail(item);
    }

    private async Task<PlayParameter?> ResolveBannerPlayAsync(CatalogItem item, CancellationToken token)
    {
        var services = _host.Services;
        var target = item;
        if (target.CandidateSources is not { Count: > 0 })
        {
            if (services.PlayabilityCache.TryGet(target, out var cached) && cached.CandidateSources is { Count: > 0 })
            {
                // 可播性探测已经取到过候选线路：直接用，不再发请求
                target = target with { CandidateSources = cached.CandidateSources };
            }
            else if (target.Source == "douban" || services.FindSource(target.Source) is null)
            {
                // 豆瓣条目没有绑定片源：按标题走详情页同一条取源路径（已带成人过滤与片源模式）
                var sources = await services.Catalog.GetDetailSourcesAsync(services.Sources, target.Title, token);
                token.ThrowIfCancellationRequested();
                var candidates = CatalogPlayability.FromDetailSources(target.Title, sources).CandidateSources;
                if (candidates is not { Count: > 0 })
                {
                    return null;
                }

                target = target with { CandidateSources = candidates };
            }
        }

        return await DirectPlay.ResolveAsync(services, target);
    }

    private void OpenBannerDetail(CatalogItem item)
    {
        var isDouban = item.Source == "douban";
        _host.Navigate(AppPage.Detail, new DetailParameter(
            item.Title,
            isDouban ? null : item.Source,
            isDouban ? null : item.Id,
            item.CandidateSources,
            item.PlayableSources));
    }

    /// <summary>“接着看”列表：成人过滤之外，再去掉正在等待撤销的删除。</summary>
    private IReadOnlyList<PlayRecordHistoryEntry> VisibleRecords() =>
        _host.Services.PlayRecords.GetLatestByMedia(
            _host.Services.IsAdultSourceHidden,
            _host.Services.IsAdultCategoryHidden,
            _host.Services.IsPlayRecordPendingDeletion);

    public void OnNavigatedTo(object? parameter) => BuildCategories();

    // ---------- 为你推荐：只读本机的观看历史、收藏和首页已加载过的条目，不发任何请求 ----------

    private IReadOnlyList<RecommendationWatched> RecommendationWatched() =>
        VisibleRecords()
            .Where(entry => !string.Equals(entry.Source, "local", StringComparison.OrdinalIgnoreCase))
            .Select(entry => new RecommendationWatched(entry.Record.Title, entry.Record.Category, entry.Record.SaveTime))
            .ToList();

    private IReadOnlyList<RecommendationFavorite> RecommendationFavorites() =>
        _host.Services.Favorites
            .GetVisibleEntries(_host.Services.IsAdultSourceHidden, _host.Services.IsAdultCategoryHidden)
            .Select(entry => new RecommendationFavorite(
                entry.Item.Title, entry.Item.Category, entry.Source, entry.Id, entry.Item.Cover, entry.Item.SourceName, entry.Item.Year))
            .ToList();

    private List<CatalogItem> LoadRecommendations()
    {
        var services = _host.Services;
        // 候选池同样走现有的成人过滤（成人源 / 成人分类在开关关闭时不进入推荐）
        var candidates = services.RecommendationPool.Snapshot()
            .Where(candidate => !services.IsAdultSourceHidden(candidate.Item.Source)
                                && !services.IsAdultCategoryHidden(candidate.Category))
            .ToList();
        return LocalRecommender.Recommend(
                services.Settings.PersonalizedRecommendations,
                RecommendationWatched(),
                RecommendationFavorites(),
                candidates,
                DateTimeOffset.UtcNow)
            .Select(item => item.Item with { Reason = item.Reason })
            .ToList();
    }

    private void BuildCategories()
    {
        _categories.Clear();
        if (VisibleRecords().Count > 0)
        {
            _categories.Add(new HomeCategory("观看历史", "record"));
        }

        // 为你推荐：开关开着，且本机有观看历史或收藏时才出现
        if (_host.Services.Settings.PersonalizedRecommendations
            && LocalRecommender.HasSignals(RecommendationWatched(), RecommendationFavorites()))
        {
            _categories.Add(new HomeCategory(LocalRecommender.Title, "recommend"));
        }

        _categories.Add(new HomeCategory("热门剧集", "tv", "热门"));
        _categories.Add(new HomeCategory("电视剧", "tv", Tags: TvTags));
        _categories.Add(new HomeCategory("电影", "movie", Tags: MovieTags));
        _categories.Add(new HomeCategory("综艺", "tv", "综艺"));
        _categories.Add(new HomeCategory("豆瓣 Top250", "movie", "top250"));

        CategoryBar.Children.Clear();
        RadioButton? first = null;
        foreach (var category in _categories)
        {
            var chip = new RadioButton
            {
                Style = (Style)FindResource("ChipRadioStyle"),
                GroupName = "homeCat",
                Content = category.Title,
                Tag = category,
            };
            chip.Checked += CategoryChip_Checked;
            CategoryBar.Children.Add(chip);
            first ??= chip;
        }

        if (first is not null)
        {
            first.IsChecked = true;
        }
    }

    private void CategoryChip_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: HomeCategory category })
        {
            SelectCategory(category);
        }
    }

    private void SelectCategory(HomeCategory category)
    {
        _selected = category;
        _selectedTag = null;
        ClearHistoryButton.Visibility = category.Type == "record" ? Visibility.Visible : Visibility.Collapsed;
        // 只有“观看历史”里的卡片能右键删除
        Grid.RemoveMenuText = category.Type == "record" ? "删除这条观看记录" : null;

        if (category.Tags is { Count: > 0 } tags)
        {
            TagScroll.Visibility = Visibility.Visible;
            BuildTagChips(tags);
        }
        else
        {
            TagScroll.Visibility = Visibility.Collapsed;
            TagBar.Children.Clear();
            _selectedTag = category.Tag;
            _ = FetchAsync();
        }
    }

    private void BuildTagChips(IReadOnlyList<string> tags)
    {
        TagBar.Children.Clear();
        RadioButton? first = null;
        foreach (var tag in tags)
        {
            var chip = new RadioButton
            {
                Style = (Style)FindResource("ChipRadioStyle"),
                GroupName = "homeTag",
                Content = tag,
                Tag = tag,
            };
            chip.Checked += TagChip_Checked;
            TagBar.Children.Add(chip);
            first ??= chip;
        }

        if (first is not null)
        {
            first.IsChecked = true;
        }
    }

    private void TagChip_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag })
        {
            _selectedTag = tag;
            _ = FetchAsync();
        }
    }

    private async Task FetchAsync()
    {
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        var token = cts.Token;

        _pageStart = 0;
        _hasMore = true;
        _loading = true;
        var category = _selected;
        if (category is null)
        {
            return;
        }

        Grid.ShowMessage("加载中…");
        SyncBannerVisibility();

        try
        {
            if (category.Type == "record")
            {
                var records = LoadRecords();
                if (token.IsCancellationRequested)
                {
                    return;
                }

                Grid.ShowResults(records, "暂无观看记录");
                FeedBanner(records);
                _hasMore = false;
                _ = HydrateMissingRecordPostersAsync(records, token);
                return;
            }

            if (category.Type == "recommend")
            {
                Grid.ShowResults(
                    LoadRecommendations(),
                    "还没有可推荐的内容：多逛逛首页的其它分类，这里会根据你的观看历史与收藏给出推荐");
                RefreshBanner();
                _hasMore = false;
                return;
            }

            var tag = _selectedTag ?? category.Tag;
            if (string.IsNullOrEmpty(category.Type) || string.IsNullOrEmpty(tag))
            {
                Grid.ShowResults([], "请选择一个子分类");
                _hasMore = false;
                return;
            }

            var results = await _host.Services.Catalog.GetHomeDataAsync(
                _host.Services.Sources, category.Type, tag, PageSize, 0, token);
            if (token.IsCancellationRequested)
            {
                return;
            }

            // 加载过的条目留在内存里，作为“为你推荐”的候选（不落盘、不上传）
            _host.Services.RecommendationPool.Add(results, $"{category.Title},{tag}");
            var displayResults = PrepareSourceCountResults(results);
            Grid.ShowResults(displayResults, "该分类下暂无内容");
            FeedBanner(displayResults);
            _shownKeys.Clear();
            foreach (var item in displayResults)
            {
                _shownKeys.Add($"{item.Source}+{item.Id}");
            }

            // 固定步长推进,保证订阅源站的 pg 页码单调递增,不会同页重复拉取
            _pageStart = PageSize;
            _hasMore = results.Count > 0;
            StartSourceCountQueries(displayResults, token);
        }
        catch (Exception)
        {
            if (!token.IsCancellationRequested)
            {
                Grid.ShowMessage("加载失败，请重试");
            }
        }
        finally
        {
            if (_cts == cts)
            {
                _loading = false;
                SyncBannerVisibility();
            }
        }
    }

    private async void OnEndReached()
    {
        if (_loading || _loadingMore || !_hasMore)
        {
            return;
        }

        var category = _selected;
        if (category is null || category.Type is "record" or "recommend")
        {
            return;
        }

        var tag = _selectedTag ?? category.Tag;
        if (string.IsNullOrEmpty(category.Type) || string.IsNullOrEmpty(tag))
        {
            return;
        }

        var token = _cts?.Token ?? CancellationToken.None;
        _loadingMore = true;
        Grid.SetLoadingMore(true);

        try
        {
            var results = await _host.Services.Catalog.GetHomeDataAsync(
                _host.Services.Sources, category.Type, tag, PageSize, _pageStart, token);
            if (token.IsCancellationRequested)
            {
                return;
            }

            _host.Services.RecommendationPool.Add(results, $"{category.Title},{tag}");
            // 跨页去重:源站不同页可能返回重复条目(HashSet.Add 返回 false 即为重复)
            var displayResults = PrepareSourceCountResults(results)
                .Where(item => _shownKeys.Add($"{item.Source}+{item.Id}"))
                .ToList();
            Grid.AppendResults(displayResults);
            _pageStart += PageSize;
            // 用原始返回数判断是否还有更多:整页都是重复时也要继续向后翻页
            _hasMore = results.Count > 0;
            StartSourceCountQueries(displayResults, token);
        }
        catch
        {
            // 忽略加载更多失败。
        }
        finally
        {
            _loadingMore = false;
            Grid.SetLoadingMore(false);
        }
    }

    private List<CatalogItem> LoadRecords()
    {
        return VisibleRecords()
            .Select(entry =>
            {
                var source = entry.Source;
                var id = entry.Id;
                var record = entry.Record;
                var localItem = string.Equals(source, "local", StringComparison.OrdinalIgnoreCase)
                    ? _host.Services.LocalMedia.Get(record.LocalMediaId ?? id)
                    : null;
                var cover = !string.IsNullOrWhiteSpace(record.Cover)
                    ? record.Cover
                    : localItem?.Poster ?? "";
                var progress = record.TotalTime > 0 ? record.PlayTime / record.TotalTime : 0;
                return new CatalogItem(
                    id, record.Title, cover, source,
                    string.Equals(source, "local", StringComparison.OrdinalIgnoreCase) ? "本地" : record.SourceName,
                    record.Year, null, progress, record.Index,
                    localItem?.Episodes.Count ?? record.TotalEpisodes, record.PlayTime);
            })
            .ToList();
    }

    private async Task HydrateMissingRecordPostersAsync(IReadOnlyList<CatalogItem> records, CancellationToken token)
    {
        foreach (var item in records.Where(item =>
                     string.IsNullOrWhiteSpace(item.Poster)
                     && !string.Equals(item.Source, "local", StringComparison.OrdinalIgnoreCase)))
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            var source = _host.Services.FindSource(item.Source);
            if (source is null)
            {
                continue;
            }

            try
            {
                var results = await _host.Services.VodClients.For(source)
                    .GetByIdAsync(source, item.Id, token);
                if (token.IsCancellationRequested)
                {
                    return;
                }

                var match = results.FirstOrDefault(result => result.Id == item.Id) ?? results.FirstOrDefault();
                if (string.IsNullOrWhiteSpace(match?.Poster))
                {
                    continue;
                }

                Grid.ReplaceItem(item, item with { Poster = match.Poster });
                _host.Services.PlayRecords.UpdateCover(item.Source, item.Id, match.Poster);
            }
            catch when (token.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // 单条历史的海报补全失败不影响其它记录。
            }
        }
    }

    private List<CatalogItem> PrepareSourceCountResults(IEnumerable<CatalogItem> results)
    {
        return results.Select(item =>
        {
            if (_host.Services.PlayabilityCache.TryGet(item, out var cached))
            {
                return cached.ApplyTo(item);
            }

            return item with
            {
                PlayabilityStatus = CatalogPlayabilityStatus.Checking,
                PlayableSourceCount = null,
                PlayableSources = null,
            };
        }).ToList();
    }

    private void StartSourceCountQueries(IReadOnlyList<CatalogItem> items, CancellationToken token)
    {
        var pendingItems = items
            .Where(item => item.PlayabilityStatus != CatalogPlayabilityStatus.Available
                && item.PlayabilityStatus != CatalogPlayabilityStatus.Unavailable)
            .ToList();
        if (pendingItems.Count == 0)
        {
            return;
        }

        _ = QueryCatalogSourceCountsAsync(pendingItems, token);
    }

    private async Task QueryCatalogSourceCountsAsync(IReadOnlyList<CatalogItem> items, CancellationToken token)
    {
        using var gate = new SemaphoreSlim(2);
        var tasks = items.Select(async item =>
        {
            var entered = false;
            try
            {
                await gate.WaitAsync(token);
                entered = true;
                if (token.IsCancellationRequested)
                {
                    return;
                }

                if (_host.Services.PlayabilityCache.TryGet(item, out var cached))
                {
                    Grid.ReplaceItem(item, cached.ApplyTo(item));
                    return;
                }

                var cacheGeneration = _host.Services.PlayabilityCache.Generation;
                var sourceSnapshot = _host.Services.Sources;
                var sources = await _host.Services.Catalog.GetDetailSourcesAsync(
                    sourceSnapshot,
                    item.Title,
                    token);
                if (token.IsCancellationRequested)
                {
                    return;
                }

                var availability = CatalogPlayability.FromDetailSources(item.Title, sources);
                if (!_host.Services.PlayabilityCache.Store(item, availability, cacheGeneration))
                {
                    if (_host.Services.PlayabilityCache.TryGet(item, out var current))
                    {
                        Grid.ReplaceItem(item, current.ApplyTo(item));
                    }

                    return;
                }

                Grid.ReplaceItem(item, availability.ApplyTo(item));
            }
            catch when (token.IsCancellationRequested)
            {
            }
            catch
            {
                Grid.ReplaceItem(item, item with { PlayabilityStatus = CatalogPlayabilityStatus.Unknown, PlayableSourceCount = null });
            }
            finally
            {
                if (entered)
                {
                    gate.Release();
                }
            }
        }).ToList();

        await Task.WhenAll(tasks);
    }

    private void OnCardClicked(CatalogItem item)
    {
        if (_selected?.Type == "record")
        {
            _ = ResumeRecordAsync(item);
            return;
        }

        var parameter = new DetailParameter(
            item.Title,
            item.Source == "douban" ? null : item.Source,
            item.Source == "douban" ? null : item.Id,
            item.CandidateSources,
            item.PlayableSources);
        _host.Navigate(AppPage.Detail, parameter);
    }

    private async Task ResumeRecordAsync(CatalogItem item)
    {
        if (string.Equals(item.Source, "local", StringComparison.OrdinalIgnoreCase))
        {
            ResumeLocalRecord(item);
            return;
        }

        var record = _host.Services.PlayRecords.Get(item.Source, item.Id);
        var resume = CreateResumeContext(item, record);
        var source = _host.Services.FindSource(item.Source);
        if (source is not null)
        {
            try
            {
                // 旧记录没有保存分类：按 id 取回后再按成人分类过滤一次（开关关闭时不直接起播）
                var results = (await _host.Services.VodClients.For(source).GetByIdAsync(source, item.Id))
                    .Where(result => !_host.Services.Catalog.IsResultHidden(result))
                    .ToList();
                var line = PlaybackResumePolicy.FindOriginalPlaybackLine(
                    results,
                    item.Id,
                    record?.PlaybackSourceKey,
                    record?.SourceName);
                if (line is not null && line.Episodes.Count > 0)
                {
                    var index = resume.FindEpisodeIndex(line.EpisodeLabels, line.Episodes.Count);
                    if (index >= 0)
                    {
                        var poster = string.IsNullOrWhiteSpace(item.Poster) ? line.Poster : item.Poster;
                        _host.Navigate(AppPage.Player, new PlayParameter(
                            item.Title,
                            line.Source,
                            line.SourceName,
                            line.Id,
                            line.Episodes,
                            index,
                            resume.StartPositionMs,
                            [],
                            poster,
                            line.PlaybackSourceKey,
                            line.EpisodeLabels,
                            resume,
                            AdultContentFilter.CategoryOf(line)));
                        return;
                    }
                }
            }
            catch
            {
                // 取剧集失败时回退到详情页。
            }
        }

        _host.Navigate(AppPage.Detail, new DetailParameter(
            item.Title,
            item.Source,
            item.Id,
            PlaybackSourceKey: record?.PlaybackSourceKey,
            Resume: resume));
    }

    private void ResumeLocalRecord(CatalogItem item)
    {
        var record = _host.Services.PlayRecords.Get(item.Source, item.Id);
        var resume = CreateResumeContext(item, record);
        var libraryItem = _host.Services.LocalMedia.Get(record?.LocalMediaId ?? item.Id);
        var importedEpisodes = libraryItem?.Episodes
            .Select(episode => new
            {
                Uri = LocalCachePlaybackCatalog.NormalizePlayableUri(episode.Path),
                Label = episode.Number is { } number ? $"第{number}集" : "",
            })
            .ToList();

        var playbackId = libraryItem?.Id ?? item.Id;
        var title = !string.IsNullOrWhiteSpace(libraryItem?.Title)
            ? libraryItem.Title
            : string.IsNullOrWhiteSpace(item.Title) ? "本地视频" : item.Title;
        var poster = !string.IsNullOrWhiteSpace(item.Poster)
            ? item.Poster
            : libraryItem?.Poster ?? record?.Cover ?? "";
        var episodes = importedEpisodes?.Select(episode => episode.Uri).ToList() ?? [];
        IReadOnlyList<string>? episodeLabels = importedEpisodes?.Select(episode => episode.Label).ToList();

        if (episodes.All(string.IsNullOrWhiteSpace))
        {
            var savedEpisodes = record?.LocalEpisodes?.ToList() ?? [item.Id];
            var preferredSavedEpisodes = new List<string>();
            if (record is { Index: >= 0 } && record.Index < savedEpisodes.Count)
            {
                preferredSavedEpisodes.Add(savedEpisodes[record.Index]);
            }

            preferredSavedEpisodes.AddRange(savedEpisodes);
            var cacheGroups = LocalCachePlaybackCatalog.Build(
                _host.Services.VodCache.GetEntries(_host.Services.Settings.VodCacheDirectory));
            var cacheSelection = LocalCachePlaybackCatalog.FindByEpisodePath(
                cacheGroups,
                preferredSavedEpisodes);
            if (cacheSelection is null
                && LocalCachePlaybackCatalog.FindByTitle(cacheGroups, item.Title) is { } titleMatch)
            {
                var targetIndex = resume.FindEpisodeIndex(
                    titleMatch.EpisodeLabels,
                    titleMatch.Episodes.Count);
                if (record?.TotalEpisodes <= 1
                    && LocalMediaNaming.Parse(item.Title).EpisodeNumber is { } titleEpisodeNumber)
                {
                    targetIndex = titleMatch.EpisodeLabels.ToList().FindIndex(label =>
                        EpisodeLabelFormatter.TryGetNumber(label, out var number)
                        && number == titleEpisodeNumber);
                }

                cacheSelection = new LocalCachePlaybackSelection(
                    titleMatch,
                    Math.Max(0, targetIndex));
            }

            if (cacheSelection is not null)
            {
                var group = cacheSelection.Group;
                playbackId = group.Id;
                title = group.Title;
                poster = !string.IsNullOrWhiteSpace(item.Poster)
                    ? item.Poster
                    : !string.IsNullOrWhiteSpace(group.Poster) ? group.Poster : record?.Cover ?? "";
                episodes = group.Episodes.ToList();
                episodeLabels = group.EpisodeLabels;
                var targetIndex = Math.Clamp(cacheSelection.EpisodeIndex, 0, episodes.Count - 1);
                var targetLabel = EpisodeLabelFormatter.DisplayAt(episodeLabels, targetIndex);
                var targetNumber = EpisodeLabelFormatter.TryGetNumber(targetLabel, out var number)
                    ? number
                    : (int?)null;
                resume = resume with
                {
                    EpisodeIndex = targetIndex,
                    EpisodeLabel = targetLabel,
                    EpisodeNumber = targetNumber,
                };
            }
            else
            {
                episodes = savedEpisodes
                    .Select(LocalCachePlaybackCatalog.NormalizePlayableUri)
                    .ToList();
                episodeLabels = null;
            }
        }

        if (episodes.All(string.IsNullOrWhiteSpace))
        {
            MessageBox.Show(
                Window.GetWindow(this),
                "本地视频文件已移动或删除，请在“本地视频”中重新添加。",
                "无法继续播放",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var index = resume.FindEpisodeIndex(episodeLabels, episodes.Count);
        if (index < 0 || string.IsNullOrWhiteSpace(episodes[index]))
        {
            index = episodes.FindIndex(path => !string.IsNullOrWhiteSpace(path));
        }

        var localSource = new SearchResult(
            playbackId,
            title,
            poster,
            episodes,
            "local",
            "本地",
            "",
            EpisodeLabels: episodeLabels);
        _host.Navigate(AppPage.Player, new PlayParameter(
            title,
            "local",
            "本地",
            playbackId,
            episodes,
            Math.Max(0, index),
            resume.StartPositionMs,
            [localSource],
            poster,
            EpisodeLabels: episodeLabels,
            Resume: resume));
    }

    private static PlaybackResumeContext CreateResumeContext(CatalogItem item, PlayRecordItem? record)
    {
        var episodeIndex = item.EpisodeIndex ?? record?.Index ?? 0;
        var episodeLabel = record?.EpisodeLabel;
        var episodeNumber = record?.EpisodeNumber;
        if (episodeNumber is null
            && EpisodeLabelFormatter.TryGetNumber(episodeLabel, out var parsedNumber))
        {
            episodeNumber = parsedNumber;
        }

        return new PlaybackResumeContext(
            episodeIndex,
            (record?.PlayTime ?? item.PlayTime) * 1000,
            episodeLabel,
            episodeNumber,
            item.Source,
            item.Id);
    }

    private void LiveLink_Click(object sender, RoutedEventArgs e) => _host.Navigate(AppPage.Live);

    private void ClearHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selected?.Type != "record")
        {
            return;
        }

        var result = MessageBox.Show(
            Window.GetWindow(this),
            "确定要清空所有观看历史吗？",
            "清空观看历史",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        // 清空是整库删除：还在撤销窗口里的那条先生效，清空之后不能再被“撤销”回来。
        _host.Services.RecordDeletion.Flush();
        _host.Services.PlayRecords.Clear();
        BuildCategories();
    }

    private void OnRecordRemoveRequested(CatalogItem item)
    {
        if (_selected?.Type != "record")
        {
            return;
        }

        // 记录先进入撤销窗口：列表里立即消失，文件不动；Changed 事件里刷新列表和提示条。
        _host.Services.RecordDeletion.Delete(item.Source, item.Id, item.Title);
    }

    private void UndoButton_Click(object sender, RoutedEventArgs e) => _host.Services.RecordDeletion.Undo();

    // 删除 / 撤销 / 超时提交之后都会触发；定时器在 UI 线程，但 Flush 可能来自别处，统一切回 UI 线程。
    private void OnRecordDeletionChanged() => Dispatcher.BeginInvoke(() =>
    {
        if (_disposed)
        {
            return;
        }

        RefreshUndoBar();
        var hasRecords = VisibleRecords().Count > 0;
        var hasRecordCategory = _categories.Any(category => category.Type == "record");
        if (_selected?.Type == "record")
        {
            if (!hasRecords && _host.Services.RecordDeletion.Pending is null)
            {
                // 最后一条真的删掉了：“观看历史”分类随之消失。
                BuildCategories();
            }
            else
            {
                _ = FetchAsync();
            }
        }
        else if (hasRecords && !hasRecordCategory)
        {
            // 撤销把唯一的一条恢复回来了，但“观看历史”分类已经不在：重建分类条。
            BuildCategories();
        }
    });

    private void RefreshUndoBar()
    {
        var pending = _host.Services.RecordDeletion.Pending;
        if (pending is null)
        {
            UndoBar.Visibility = Visibility.Collapsed;
            return;
        }

        UndoText.Text = string.IsNullOrWhiteSpace(pending.Title)
            ? "已删除这条观看记录"
            : $"已删除《{pending.Title}》的观看记录";
        UndoBar.Visibility = Visibility.Visible;
    }

    // 更新检查在后台线程完成
    private void OnAvailableUpdateChanged() => Dispatcher.BeginInvoke(RefreshUpdateBadge);

    private void RefreshUpdateBadge()
    {
        var hasUpdate = _host.Services.Updates.HasUpdateBadge;
        SettingsUpdateDot.Visibility = hasUpdate ? Visibility.Visible : Visibility.Collapsed;
        SettingsButton.ToolTip = hasUpdate
            ? $"设置（有新版本 {_host.Services.Updates.AvailableUpdate?.VersionText} 可更新）"
            : "设置";
    }

    private void FavoritesButton_Click(object sender, RoutedEventArgs e) => _host.ShowFavoritesPanel();

    private void SearchButton_Click(object sender, RoutedEventArgs e) => _host.Navigate(AppPage.Search);

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => _host.OpenSettings();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        // 离开首页时取消进行中的加载与可播性探测循环,避免每次进首页都叠加一条全源探测循环。
        _disposed = true;
        _host.Services.RecordDeletion.Changed -= OnRecordDeletionChanged;
        _host.Services.Updates.AvailableUpdateChanged -= OnAvailableUpdateChanged;
        _host.Services.AdultVisibilityChanged -= OnAdultVisibilityChangedForBanner;
        CancelBannerPlay();
        _cts?.Cancel();
        _cts = null;
    }
}
