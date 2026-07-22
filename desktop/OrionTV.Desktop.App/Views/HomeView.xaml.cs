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

            var displayResults = PreparePlayabilityResults(results);
            Grid.ShowResults(displayResults, "该分类下暂无内容");
            _shownKeys.Clear();
            foreach (var item in displayResults)
            {
                _shownKeys.Add($"{item.Source}+{item.Id}");
            }

            // 固定步长推进,保证订阅源站的 pg 页码单调递增,不会同页重复拉取
            _pageStart = PageSize;
            _hasMore = results.Count > 0;
            StartPlayabilityProbes(displayResults, token);
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
            var displayResults = PreparePlayabilityResults(results)
                .Where(item => _shownKeys.Add($"{item.Source}+{item.Id}"))
                .ToList();
            Grid.AppendResults(displayResults);
            _pageStart += PageSize;
            // 用原始返回数判断是否还有更多:整页都是重复时也要继续向后翻页
            _hasMore = results.Count > 0;
            StartPlayabilityProbes(displayResults, token);
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
                var progress = record.TotalTime > 0 ? record.PlayTime / record.TotalTime : 0;
                return new CatalogItem(
                    id, record.Title, record.Cover, source, record.SourceName, record.Year, null,
                    progress, record.Index, record.TotalEpisodes, record.PlayTime);
            })
            .ToList();
    }

    private List<CatalogItem> PreparePlayabilityResults(IEnumerable<CatalogItem> results)
    {
        return results.Select(item =>
        {
            if (item.Source != "douban")
            {
                return item;
            }

            return _host.Services.PlayabilityCache.TryGet(item, out var cached)
                ? item with
                {
                    PlayabilityStatus = cached.Status,
                    PlayableSourceCount = cached.PlayableSourceCount,
                }
                : item with
                {
                    PlayabilityStatus = CatalogPlayabilityStatus.Checking,
                    PlayableSourceCount = null,
                };
        }).ToList();
    }

    private void StartPlayabilityProbes(IReadOnlyList<CatalogItem> items, CancellationToken token)
    {
        var doubanItems = items
            .Where(item => item.Source == "douban"
                && item.PlayabilityStatus != CatalogPlayabilityStatus.Playable
                && item.PlayabilityStatus != CatalogPlayabilityStatus.Unavailable)
            .ToList();
        if (doubanItems.Count == 0)
        {
            return;
        }

        _ = ProbeDoubanPlayabilityAsync(doubanItems, token);
    }

    private async Task ProbeDoubanPlayabilityAsync(IReadOnlyList<CatalogItem> items, CancellationToken token)
    {
        foreach (var item in items)
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            try
            {
                if (_host.Services.PlayabilityCache.TryGet(item, out var cached))
                {
                    Grid.ReplaceItem(item, item with
                    {
                        PlayabilityStatus = cached.Status,
                        PlayableSourceCount = cached.PlayableSourceCount,
                    });
                    continue;
                }

                var cacheGeneration = _host.Services.PlayabilityCache.Generation;
                var sourceSnapshot = _host.Services.Sources;
                var sources = await _host.Services.Catalog.GetDetailSourcesAsync(sourceSnapshot, item.Title, token);
                if (token.IsCancellationRequested)
                {
                    return;
                }

                var playability = CatalogPlayability.FromDetailSources(item.Title, sources);
                if (!_host.Services.PlayabilityCache.Store(item, playability, cacheGeneration))
                {
                    if (_host.Services.PlayabilityCache.TryGet(item, out var current))
                    {
                        Grid.ReplaceItem(item, item with
                        {
                            PlayabilityStatus = current.Status,
                            PlayableSourceCount = current.PlayableSourceCount,
                        });
                    }

                    continue;
                }

                Grid.ReplaceItem(item, item with
                {
                    PlayabilityStatus = playability.Status,
                    PlayableSourceCount = playability.PlayableSourceCount,
                });
            }
            catch when (token.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                Grid.ReplaceItem(item, item with { PlayabilityStatus = CatalogPlayabilityStatus.Unknown, PlayableSourceCount = null });
            }
        }
    }

    private void OnCardClicked(CatalogItem item)
    {
        if (_selected?.Type == "record")
        {
            _ = ResumeRecordAsync(item);
            return;
        }

        var parameter = item.Source == "douban"
            ? new DetailParameter(item.Title)
            : new DetailParameter(item.Title, item.Source, item.Id);
        _host.Navigate(AppPage.Detail, parameter);
    }

    private async Task ResumeRecordAsync(CatalogItem item)
    {
        var source = _host.Services.FindSource(item.Source);
        if (source is not null)
        {
            try
            {
                var results = await _host.Services.VodClients.For(source).GetByIdAsync(source, item.Id);
                var match = results.FirstOrDefault(r => r.Id == item.Id) ?? results.FirstOrDefault();
                if (match is not null && match.Episodes.Count > 0)
                {
                    var index = Math.Clamp(item.EpisodeIndex ?? 0, 0, match.Episodes.Count - 1);
                    _host.Navigate(AppPage.Player, new PlayParameter(
                        item.Title, item.Source, item.SourceName, item.Id, match.Episodes, index, item.PlayTime * 1000, [], item.Poster));
                    return;
                }
            }
            catch
            {
                // 取剧集失败时回退到详情页。
            }
        }

        _host.Navigate(AppPage.Detail, new DetailParameter(item.Title, item.Source, item.Id));
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
