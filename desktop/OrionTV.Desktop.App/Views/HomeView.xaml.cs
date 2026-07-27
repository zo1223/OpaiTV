using System.IO;
using System.Windows;
using System.Windows.Controls;
using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.App.Views;

public partial class HomeView : UserControl, INavigationAware, IDisposable
{
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

    public HomeView(IAppHost host)
    {
        InitializeComponent();
        _host = host;
        Grid.CardClicked += OnCardClicked;
        Grid.EndReached += OnEndReached;
    }

    public void OnNavigatedTo(object? parameter) => BuildCategories();

    private void BuildCategories()
    {
        _categories.Clear();
        if (_host.Services.PlayRecords.GetAll().Count > 0)
        {
            _categories.Add(new HomeCategory("观看历史", "record"));
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
                _hasMore = false;
                _ = HydrateMissingRecordPostersAsync(records, token);
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

            var displayResults = PrepareSourceCountResults(results);
            Grid.ShowResults(displayResults, "该分类下暂无内容");
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
        if (category is null || category.Type == "record")
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
        return _host.Services.PlayRecords.GetAll()
            .OrderByDescending(kv => kv.Value.SaveTime)
            .Select(kv =>
            {
                var separator = kv.Key.IndexOf('+');
                var source = separator >= 0 ? kv.Key[..separator] : kv.Key;
                var id = separator >= 0 ? kv.Key[(separator + 1)..] : "";
                var record = kv.Value;
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
                var results = await _host.Services.VodClients.For(source).GetByIdAsync(source, item.Id);
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
                            resume));
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
        var candidates = libraryItem is not null
            ? libraryItem.Episodes.Select(episode => episode.Path).ToList()
            : record?.LocalEpisodes?.ToList() ?? [item.Id];
        var episodes = candidates.Select(NormalizeLocalEpisode).ToList();
        if (episodes.All(string.IsNullOrWhiteSpace))
        {
            episodes = FindCachedLocalEpisodes(item.Title);
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

        var index = resume.FindEpisodeIndex(null, episodes.Count);
        if (index < 0 || string.IsNullOrWhiteSpace(episodes[index]))
        {
            index = episodes.FindIndex(path => !string.IsNullOrWhiteSpace(path));
        }

        var poster = !string.IsNullOrWhiteSpace(item.Poster)
            ? item.Poster
            : libraryItem?.Poster ?? record?.Cover ?? "";
        var title = string.IsNullOrWhiteSpace(item.Title) ? libraryItem?.Title ?? "本地视频" : item.Title;
        var localSource = new SearchResult(item.Id, title, poster, episodes, "local", "本地", "");
        _host.Navigate(AppPage.Player, new PlayParameter(
            title,
            "local",
            "本地",
            item.Id,
            episodes,
            Math.Max(0, index),
            resume.StartPositionMs,
            [localSource],
            poster,
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

    private List<string> FindCachedLocalEpisodes(string title)
    {
        var normalizedTitle = LocalMediaNaming.NormalizeTitle(title);
        return _host.Services.VodCache
            .GetEntries(_host.Services.Settings.VodCacheDirectory)
            .Select(entry => new { Entry = entry, Name = LocalMediaNaming.Parse(entry.Title) })
            .Where(item => LocalMediaNaming.NormalizeTitle(item.Name.Title) == normalizedTitle)
            .GroupBy(item => item.Name.EpisodeNumber?.ToString() ?? item.Entry.Directory, StringComparer.OrdinalIgnoreCase)
            .Select(items => items.OrderByDescending(item => item.Entry.CreatedAt).First())
            .OrderBy(item => item.Name.EpisodeNumber ?? int.MaxValue)
            .Select(item => NormalizeLocalEpisode(item.Entry.PlaylistPath))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToList();
    }

    private static string NormalizeLocalEpisode(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "";
        }

        try
        {
            if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsFile)
            {
                return File.Exists(uri.LocalPath) ? uri.AbsoluteUri : "";
            }

            var fullPath = Path.GetFullPath(path);
            return File.Exists(fullPath) ? new Uri(fullPath).AbsoluteUri : "";
        }
        catch
        {
            return "";
        }
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

        _host.Services.PlayRecords.Clear();
        BuildCategories();
    }

    private void FavoritesButton_Click(object sender, RoutedEventArgs e) => _host.Navigate(AppPage.Favorites);

    private void SearchButton_Click(object sender, RoutedEventArgs e) => _host.Navigate(AppPage.Search);

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => _host.Navigate(AppPage.Settings);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        // 离开首页时取消进行中的加载与可播性探测循环,避免每次进首页都叠加一条全源探测循环。
        _disposed = true;
        _cts?.Cancel();
        _cts = null;
    }
}
