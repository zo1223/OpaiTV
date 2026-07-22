using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.App.Views;

public partial class DetailView : UserControl, INavigationAware, IDisposable
{
    private const string LocalSourceKey = "local";

    private readonly IAppHost _host;
    private string _title = "";
    private List<SearchResult> _remoteResults = new();
    private List<SearchResult> _searchResults = new();
    private SearchResult? _selected;
    private readonly Dictionary<string, ChannelHealthResult> _health = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RadioButton> _sourceChips = new(StringComparer.Ordinal);
    private readonly Dictionary<int, Button> _downloadButtons = new();
    private CancellationTokenSource? _scanCts;
    private bool _disposed;
    private bool _listeningDownloads;

    public DetailView(IAppHost host)
    {
        InitializeComponent();
        _host = host;
        Header.BackRequested += (_, _) => _host.GoBack();
    }

    public void OnNavigatedTo(object? parameter)
    {
        _scanCts?.Cancel();
        _health.Clear();
        EnsureDownloadListener();
        if (parameter is not DetailParameter request)
        {
            ShowMessage("缺少详情参数");
            return;
        }

        _title = request.Title;
        Header.Title = request.Title;
        ShowMessage("加载中…");
        _ = LoadAsync(request);
    }

    private async Task LoadAsync(DetailParameter request)
    {
        if (!_host.Services.HasSources)
        {
            ShowMessage("请先在设置中配置影视源订阅地址");
            return;
        }

        var preferred = await _host.Services.Catalog.GetPreferredDetailSourcesAsync(
            _host.Services.Sources,
            request.Source,
            request.Id,
            request.Title);
        if (preferred.Count > 0)
        {
            ShowResults(preferred, preferred[0]);
            _ = LoadAllSourcesAsync(request.Title, preferred, false);
            return;
        }

        await LoadAllSourcesAsync(request.Title, [], true);
    }

    private async Task LoadAllSourcesAsync(
        string title,
        IReadOnlyList<SearchResult> preferred,
        bool showErrors)
    {
        try
        {
            var results = await _host.Services.Catalog.GetDetailSourcesAsync(_host.Services.Sources, title);
            var merged = MergeSources(preferred, results);
            if (merged.Count == 0)
            {
                if (showErrors)
                {
                    ShowMessage($"未找到\"{title}\" 的播放源，请检查标题或稍后重试");
                }

                return;
            }

            var selected = _selected is not null && !IsLocalSource(_selected)
                ? merged.FirstOrDefault(result =>
                    SearchResultSources.SelectionKeyOf(result) == SearchResultSources.SelectionKeyOf(_selected))
                : null;
            ShowResults(merged, selected ?? preferred.FirstOrDefault() ?? merged[0]);
        }
        catch
        {
            if (showErrors)
            {
                ShowMessage("加载失败，请稍后重试");
            }
        }
    }

    private static List<SearchResult> MergeSources(
        IReadOnlyList<SearchResult> preferred,
        IReadOnlyList<SearchResult> results)
    {
        var merged = new List<SearchResult>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var result in preferred)
        {
            if (IsLocalSource(result))
            {
                continue;
            }

            if (seen.Add(SearchResultSources.SelectionKeyOf(result)))
            {
                merged.Add(result);
            }
        }

        foreach (var result in results)
        {
            if (IsLocalSource(result))
            {
                continue;
            }

            if (seen.Add(SearchResultSources.SelectionKeyOf(result)))
            {
                merged.Add(result);
            }
        }

        return merged;
    }

    private void ShowResults(IReadOnlyList<SearchResult> results, SearchResult selected)
    {
        _remoteResults = results.Where(result => !IsLocalSource(result)).ToList();
        _searchResults = WithLocalSource(_remoteResults);
        MessageText.Visibility = Visibility.Collapsed;
        ContentPanel.Visibility = Visibility.Visible;

        var preferred = _searchResults.FirstOrDefault(result =>
                SearchResultSources.SelectionKeyOf(result) == SearchResultSources.SelectionKeyOf(selected))
            ?? _searchResults.FirstOrDefault(result => !IsLocalSource(result))
            ?? _searchResults.FirstOrDefault()
            ?? selected;
        BuildSourceChips(preferred);
    }

    private List<SearchResult> WithLocalSource(IReadOnlyList<SearchResult> remotes)
    {
        var list = remotes.ToList();
        var local = TryBuildLocalSource(list);
        if (local is not null)
        {
            list.Insert(0, local);
        }

        return list;
    }

    private SearchResult? TryBuildLocalSource(IReadOnlyList<SearchResult> remotes)
    {
        if (remotes.Count == 0)
        {
            return null;
        }

        var template = remotes[0];
        var maxEpisodes = remotes.Max(result => result.Episodes.Count);
        if (maxEpisodes <= 0)
        {
            return null;
        }

        var cacheDirectory = _host.Services.Settings.VodCacheDirectory;
        var episodes = new string[maxEpisodes];
        var completed = 0;
        for (var index = 0; index < maxEpisodes; index++)
        {
            var path = FindCompletedPath(remotes, index, cacheDirectory);
            if (string.IsNullOrWhiteSpace(path))
            {
                episodes[index] = "";
                continue;
            }

            episodes[index] = new Uri(path).AbsoluteUri;
            completed++;
        }

        if (completed == 0)
        {
            return null;
        }

        return new SearchResult(
            Id: $"{LocalSourceKey}:{_title}",
            Title: string.IsNullOrWhiteSpace(template.Title) ? _title : template.Title,
            Poster: template.Poster,
            Episodes: episodes,
            Source: LocalSourceKey,
            SourceName: $"本地已下载 · {completed}集",
            Year: template.Year,
            Description: template.Description,
            TypeName: template.TypeName);
    }

    private string? FindCompletedPath(IReadOnlyList<SearchResult> remotes, int episodeIndex, string cacheDirectory)
    {
        var cacheTitle = CacheTitleOf(episodeIndex);
        foreach (var remote in remotes)
        {
            if (episodeIndex >= remote.Episodes.Count)
            {
                continue;
            }

            var path = _host.Services.VodCache.FindCompletedPlaybackPath(
                cacheDirectory,
                remote.Episodes[episodeIndex],
                cacheTitle);
            if (!string.IsNullOrWhiteSpace(path))
            {
                return path;
            }
        }

        return _host.Services.VodCache.FindCompletedPlaybackPath(cacheDirectory, "", cacheTitle);
    }

    private static string CacheTitleOf(string seriesTitle, int episodeIndex) =>
        $"{seriesTitle} 第{episodeIndex + 1}集";

    private string CacheTitleOf(int episodeIndex) => CacheTitleOf(_title, episodeIndex);

    private static bool IsLocalSource(SearchResult result) =>
        string.Equals(result.Source, LocalSourceKey, StringComparison.OrdinalIgnoreCase);

    private void BuildSourceChips(SearchResult selected)
    {
        // 与海报徽标同口径：只计远程源（不含「本地已下载」）
        var remoteCount = _searchResults.Count(result => !IsLocalSource(result));
        SourcesHeader.Text = $"选择播放源 · {remoteCount} 个";
        SourceBar.Children.Clear();
        _sourceChips.Clear();

        foreach (var result in _searchResults)
        {
            var key = SearchResultSources.SelectionKeyOf(result);
            var chip = new RadioButton
            {
                Style = (Style)FindResource("ChipRadioStyle"),
                GroupName = "detailSource",
                Content = SourceLabel(result),
                Tag = result,
            };
            chip.Checked += SourceChip_Checked;
            SourceBar.Children.Add(chip);
            _sourceChips[key] = chip;

            if (SearchResultSources.SelectionKeyOf(result) == SearchResultSources.SelectionKeyOf(selected))
            {
                chip.IsChecked = true;
            }
        }
    }

    private void SourceChip_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: SearchResult result })
        {
            SelectSource(result);
        }
    }

    private void SelectSource(SearchResult result)
    {
        _selected = result;
        TitleText.Text = string.IsNullOrWhiteSpace(result.Title) ? _title : result.Title;
        ImageLoader.SetSourceUrl(PosterImage, result.Poster);
        YearText.Text = result.Year ?? "";
        TypeText.Text = result.TypeName ?? "";
        DescText.Text = result.Description ?? "";
        BuildEpisodes(result.Episodes);
        BuildDownloadBar();
        if (!IsLocalSource(result))
        {
            UpdateFavoriteIcon(_host.Services.Favorites.IsFavorited(result.Source, result.Id));
            _ = LoadMetaAsync(result);
            // TVBox/csp 搜索常无剧集地址，选中后再按 id 补拉
            if (result.Episodes.Count == 0)
            {
                _ = HydrateSelectedEpisodesAsync(result);
            }
        }
    }

    private async Task HydrateSelectedEpisodesAsync(SearchResult result)
    {
        try
        {
            var key = SearchResultSources.SelectionKeyOf(result);
            var hydrated = await _host.Services.Catalog.HydrateEpisodesAsync(_host.Services.Sources, result);
            // 用户已切换到其它源则丢弃结果
            if (_selected is null || SearchResultSources.SelectionKeyOf(_selected) != key)
            {
                return;
            }

            if (hydrated is null || hydrated.Episodes.Count == 0)
            {
                return;
            }

            var index = _searchResults.FindIndex(r => SearchResultSources.SelectionKeyOf(r) == key);
            if (index >= 0)
            {
                _searchResults[index] = hydrated;
            }

            // remote 列表同步，避免测速/下载仍用空剧集
            var remoteIndex = _remoteResults.FindIndex(r => SearchResultSources.SelectionKeyOf(r) == key);
            if (remoteIndex >= 0)
            {
                _remoteResults[remoteIndex] = hydrated;
            }

            _selected = hydrated;
            BuildEpisodes(hydrated.Episodes);
            BuildDownloadBar();
            if (_sourceChips.TryGetValue(key, out var chip))
            {
                chip.Content = SourceLabel(hydrated);
            }
        }
        catch
        {
            // 补拉失败时保留空剧集 UI，用户可换源
        }
    }

    private async Task LoadMetaAsync(SearchResult result)
    {
        var source = _host.Services.FindSource(result.Source);
        if (source is null)
        {
            return;
        }

        try
        {
            var detail = await _host.Services.VodClients.For(source).GetDetailAsync(source, result.Id);
            if (!ReferenceEquals(_selected, result))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(detail.Year))
            {
                YearText.Text = detail.Year;
            }

            if (!string.IsNullOrWhiteSpace(detail.TypeName))
            {
                TypeText.Text = detail.TypeName;
            }

            if (!string.IsNullOrWhiteSpace(detail.Description))
            {
                DescText.Text = detail.Description;
            }

            if (!string.IsNullOrWhiteSpace(detail.Poster))
            {
                ImageLoader.SetSourceUrl(PosterImage, detail.Poster);
                var updated = result with { Poster = detail.Poster };
                _selected = updated;
                ReplaceResult(_searchResults, result, updated);
                ReplaceResult(_remoteResults, result, updated);
            }
        }
        catch
        {
            // 元信息获取失败时保留搜索结果中的字段。
        }
    }

    private static void ReplaceResult(List<SearchResult> results, SearchResult current, SearchResult updated)
    {
        var key = SearchResultSources.SelectionKeyOf(current);
        var index = results.FindIndex(item => SearchResultSources.SelectionKeyOf(item) == key);
        if (index >= 0)
        {
            results[index] = updated;
        }
    }

    private void BuildEpisodes(IReadOnlyList<string> episodes)
    {
        EpisodeBar.Children.Clear();
        for (var index = 0; index < episodes.Count; index++)
        {
            var hasUrl = !string.IsNullOrWhiteSpace(episodes[index]);
            var button = new Button
            {
                Content = $"第{index + 1} 集",
                Style = (Style)FindResource(hasUrl ? "SecondaryButtonStyle" : "SecondaryButtonStyle"),
                Height = 38,
                MinWidth = 64,
                Padding = new Thickness(12, 0, 12, 0),
                Margin = new Thickness(0, 0, 8, 8),
                FontSize = 13,
                Tag = index,
                IsEnabled = hasUrl,
                Opacity = hasUrl ? 1 : 0.45,
                ToolTip = hasUrl ? "播放" : "本集尚未下载完成",
            };
            button.Click += EpisodeButton_Click;
            EpisodeBar.Children.Add(button);
        }
    }

    private void BuildDownloadBar()
    {
        DownloadBar.Children.Clear();
        _downloadButtons.Clear();

        var remote = GetDownloadSource();
        if (remote is null || remote.Episodes.Count == 0)
        {
            DownloadAllButton.IsEnabled = false;
            DownloadStatusText.Text = "当前没有可下载的播放源";
            return;
        }

        DownloadAllButton.IsEnabled = true;
        for (var index = 0; index < remote.Episodes.Count; index++)
        {
            var button = new Button
            {
                Height = 34,
                MinWidth = 88,
                Padding = new Thickness(12, 0, 12, 0),
                Margin = new Thickness(0, 0, 8, 8),
                FontSize = 12,
                Tag = new DownloadButtonTag(index, Decrypt: false),
            };
            button.Click += DownloadEpisodeButton_Click;
            ApplyDownloadButtonState(button, remote, index);
            DownloadBar.Children.Add(button);
            _downloadButtons[index] = button;
        }

        RefreshDownloadStatusSummary();
    }

    private SearchResult? GetDownloadSource()
    {
        if (_selected is not null && !IsLocalSource(_selected))
        {
            return _selected;
        }

        return _remoteResults.FirstOrDefault();
    }

    private void ApplyDownloadButtonState(Button button, SearchResult remote, int index)
    {
        var cacheTitle = CacheTitleOf(index);
        var activeJob = _host.Services.Downloads.FindActiveByTitle(cacheTitle);
        if (activeJob is not null)
        {
            button.Style = (Style)FindResource("SecondaryButtonStyle");
            button.Content = $"第{index + 1}集 · {activeJob.Percent}%";
            button.IsEnabled = true;
            button.ToolTip = "再次点击可取消后台下载";
            button.Tag = new DownloadButtonTag(index, Decrypt: false);
            return;
        }

        var url = index < remote.Episodes.Count ? remote.Episodes[index] : "";
        var completedPath = _host.Services.VodCache.FindCompletedPlaybackPath(
            _host.Services.Settings.VodCacheDirectory,
            url,
            cacheTitle);
        var completed = !string.IsNullOrWhiteSpace(completedPath);
        if (completed)
        {
            var directory = _host.Services.VodCache.FindCompletedDirectory(
                _host.Services.Settings.VodCacheDirectory,
                url,
                cacheTitle);
            var canDecrypt = !string.IsNullOrWhiteSpace(directory)
                && _host.Services.VodCache.CanDecryptMerge(directory!);
            if (canDecrypt)
            {
                button.Style = (Style)FindResource("PrimaryButtonStyle");
                button.Content = $"第{index + 1}集 · 解密合并";
                button.IsEnabled = true;
                button.ToolTip = "已下载完成（AES 加密）；点击解密合并为单个文件";
                button.Tag = new DownloadButtonTag(index, Decrypt: true);
                return;
            }

            button.Style = (Style)FindResource("SecondaryButtonStyle");
            button.Content = $"第{index + 1}集 · 已完成";
            button.IsEnabled = false;
            button.ToolTip = "已下载完成，可在「本地已下载」源中播放";
            return;
        }

        if (!VodCacheService.IsHlsUrl(url))
        {
            button.Style = (Style)FindResource("SecondaryButtonStyle");
            button.Content = $"第{index + 1}集 · 不可下";
            button.IsEnabled = false;
            button.ToolTip = "仅支持 HLS（m3u8）源下载";
            return;
        }

        button.Style = (Style)FindResource("PrimaryButtonStyle");
        button.Content = $"下载第{index + 1}集";
        button.IsEnabled = true;
        button.ToolTip = "下载整集到本地；播放仍走源站";
        button.Tag = new DownloadButtonTag(index, Decrypt: false);
    }

    private void EpisodeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null || sender is not Button { Tag: int index })
        {
            return;
        }

        if (index < 0 || index >= _selected.Episodes.Count || string.IsNullOrWhiteSpace(_selected.Episodes[index]))
        {
            DownloadStatusText.Text = IsLocalSource(_selected)
                ? "本集尚未下载完成"
                : "该集暂无播放地址";
            return;
        }

        // 播放页只接收当前选中源 + 全部源列表；本地源也作为 AltSources 的一项出现。
        _host.Navigate(AppPage.Player, new PlayParameter(
            _selected.Title,
            _selected.Source,
            _selected.SourceName,
            _selected.Id,
            _selected.Episodes,
            index,
            0,
            _searchResults,
            _selected.Poster,
            _selected.PlaybackSourceKey));
    }

    private async void DownloadEpisodeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        var index = button.Tag switch
        {
            DownloadButtonTag tag => tag.Index,
            int value => value,
            _ => -1,
        };
        var decrypt = button.Tag is DownloadButtonTag { Decrypt: true };
        if (index < 0)
        {
            return;
        }

        if (decrypt)
        {
            await DecryptEpisodeAsync(index).ConfigureAwait(true);
            return;
        }

        var remote = GetDownloadSource();
        if (remote is null || index >= remote.Episodes.Count)
        {
            return;
        }

        var cacheTitle = CacheTitleOf(index);
        var activeJob = _host.Services.Downloads.FindActiveByTitle(cacheTitle);
        if (activeJob is not null)
        {
            await CancelDownloadJobAsync(activeJob.Id, index).ConfigureAwait(true);
            return;
        }

        await StartDownloadAsync(remote, index).ConfigureAwait(true);
    }

    private async Task DecryptEpisodeAsync(int episodeIndex)
    {
        var remote = GetDownloadSource();
        if (remote is null)
        {
            return;
        }

        var url = episodeIndex < remote.Episodes.Count ? remote.Episodes[episodeIndex] : "";
        var cacheTitle = CacheTitleOf(episodeIndex);
        var directory = _host.Services.VodCache.FindCompletedDirectory(
            _host.Services.Settings.VodCacheDirectory,
            url,
            cacheTitle);
        if (string.IsNullOrWhiteSpace(directory))
        {
            DownloadStatusText.Text = $"第{episodeIndex + 1} 集未找到可解密缓存";
            return;
        }

        if (_downloadButtons.TryGetValue(episodeIndex, out var button))
        {
            button.IsEnabled = false;
            button.Content = $"第{episodeIndex + 1}集 · 解密中";
        }

        try
        {
            DownloadStatusText.Text = $"正在解密合并第{episodeIndex + 1} 集…";
            var progress = new Progress<double>(ratio =>
            {
                var percent = Math.Clamp((int)Math.Round(ratio * 100), 0, 100);
                DownloadStatusText.Text = $"正在解密合并第{episodeIndex + 1} 集…{percent}%";
                if (_downloadButtons.TryGetValue(episodeIndex, out var liveButton))
                {
                    liveButton.Content = $"第{episodeIndex + 1}集 · 解密 {percent}%";
                }
            });
            var path = await _host.Services.VodCache
                .DecryptAndMergeAsync(directory, progress)
                .ConfigureAwait(true);
            DownloadStatusText.Text =
                $"第{episodeIndex + 1} 集已解密合并（{new FileInfo(path).Length / 1024d / 1024d:0.0} MB）";
            RefreshLocalSourceInList();
            if (GetDownloadSource() is { } current
                && _downloadButtons.TryGetValue(episodeIndex, out var doneButton))
            {
                ApplyDownloadButtonState(doneButton, current, episodeIndex);
            }
        }
        catch (Exception error)
        {
            DownloadStatusText.Text = $"第{episodeIndex + 1} 集解密失败：{ShortError(error)}";
            if (GetDownloadSource() is { } current
                && _downloadButtons.TryGetValue(episodeIndex, out var failButton))
            {
                ApplyDownloadButtonState(failButton, current, episodeIndex);
            }
        }
    }

    private async void DownloadAllButton_Click(object sender, RoutedEventArgs e)
    {
        var remote = GetDownloadSource();
        if (remote is null)
        {
            return;
        }

        DownloadAllButton.IsEnabled = false;
        try
        {
            for (var index = 0; index < remote.Episodes.Count; index++)
            {
                var cacheTitle = CacheTitleOf(index);
                if (_host.Services.Downloads.IsActiveTitle(cacheTitle))
                {
                    continue;
                }

                var url = remote.Episodes[index];
                if (!VodCacheService.IsHlsUrl(url))
                {
                    continue;
                }

                var completed = !string.IsNullOrWhiteSpace(
                    _host.Services.VodCache.FindCompletedPlaybackPath(
                        _host.Services.Settings.VodCacheDirectory,
                        url,
                        cacheTitle));
                if (completed)
                {
                    continue;
                }

                await StartDownloadAsync(remote, index).ConfigureAwait(true);
            }
        }
        finally
        {
            DownloadAllButton.IsEnabled = true;
            RefreshDownloadStatusSummary();
        }
    }

    private async Task StartDownloadAsync(SearchResult remote, int episodeIndex)
    {
        var url = remote.Episodes[episodeIndex];
        var cacheTitle = CacheTitleOf(episodeIndex);
        if (!VodCacheService.IsHlsUrl(url))
        {
            DownloadStatusText.Text = $"第{episodeIndex + 1} 集不是 HLS 源，无法下载";
            return;
        }

        if (_host.Services.Downloads.IsActiveTitle(cacheTitle))
        {
            return;
        }

        try
        {
            DownloadStatusText.Text = $"正在开始下载第 {episodeIndex + 1} 集…";
            var job = await _host.Services.Downloads.StartAsync(
                url,
                cacheTitle,
                _host.Services.Settings.VodCacheDirectory,
                poster: _selected?.Poster ?? remote.Poster).ConfigureAwait(true);
            UpdateDownloadButton(episodeIndex, $"{job.Percent}%");
            RefreshDownloadStatusSummary();
        }
        catch (Exception error)
        {
            DownloadStatusText.Text = $"第{episodeIndex + 1} 集下载失败：{ShortError(error)}";
            if (_downloadButtons.TryGetValue(episodeIndex, out var button) && GetDownloadSource() is { } current)
            {
                ApplyDownloadButtonState(button, current, episodeIndex);
            }
        }
    }

    private async Task CancelDownloadJobAsync(string jobId, int episodeIndex)
    {
        DownloadStatusText.Text = $"正在取消第{episodeIndex + 1} 集下载…";
        try
        {
            await _host.Services.Downloads.CancelAsync(jobId).ConfigureAwait(true);
            DownloadStatusText.Text = $"已取消第 {episodeIndex + 1} 集下载（已下部分会保留，可续下）";
        }
        catch
        {
            DownloadStatusText.Text = $"取消第{episodeIndex + 1} 集失败";
        }

        if (GetDownloadSource() is { } remote && _downloadButtons.TryGetValue(episodeIndex, out var button))
        {
            ApplyDownloadButtonState(button, remote, episodeIndex);
        }

        RefreshDownloadStatusSummary();
    }

    private void EnsureDownloadListener()
    {
        if (_listeningDownloads)
        {
            return;
        }

        _host.Services.Downloads.Changed += Downloads_Changed;
        _listeningDownloads = true;
    }

    private void Downloads_Changed(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (_disposed)
            {
                return;
            }

            if (GetDownloadSource() is { } remote)
            {
                foreach (var (index, button) in _downloadButtons)
                {
                    ApplyDownloadButtonState(button, remote, index);
                }
            }

            RefreshDownloadStatusSummary();
            if (_host.Services.Downloads.GetJobs().Any(job =>
                    job.Status == VodDownloadJobStatus.Completed
                    && (job.Title.StartsWith(_title + " ", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(job.Title, _title, StringComparison.OrdinalIgnoreCase))))
            {
                RefreshLocalSourceInList();
            }
        });
    }

    private void UpdateDownloadButton(int episodeIndex, string label)
    {
        if (!_downloadButtons.TryGetValue(episodeIndex, out var button))
        {
            return;
        }

        button.Content = $"第{episodeIndex + 1}集 · {label}";
        button.Style = (Style)FindResource("SecondaryButtonStyle");
        button.IsEnabled = true;
        button.ToolTip = "再次点击可取消下载";
    }

    private void RefreshDownloadStatusSummary()
    {
        var activeForTitle = _host.Services.Downloads.GetActiveJobs()
            .Where(job => job.Title.StartsWith(_title + " ", StringComparison.OrdinalIgnoreCase)
                || string.Equals(job.Title, _title, StringComparison.OrdinalIgnoreCase)
                || job.Title.Contains(_title, StringComparison.OrdinalIgnoreCase))
            .OrderBy(job => job.Title)
            .ToList();
        if (activeForTitle.Count > 0)
        {
            var parts = activeForTitle.Select(job =>
            {
                var speed = FormatSpeed(job.BytesPerSecond);
                var size = FormatSize(job.DownloadedBytes);
                return $"{job.Title} {job.Percent}% · {size} · {speed}";
            });
            DownloadStatusText.Text = "后台下载中（离开本页可继续）：" + string.Join("  |  ", parts);
            return;
        }

        var remote = GetDownloadSource();
        if (remote is null)
        {
            DownloadStatusText.Text = "";
            return;
        }

        var completed = 0;
        for (var index = 0; index < remote.Episodes.Count; index++)
        {
            if (!string.IsNullOrWhiteSpace(
                    _host.Services.VodCache.FindCompletedPlaybackPath(
                        _host.Services.Settings.VodCacheDirectory,
                        remote.Episodes[index],
                        CacheTitleOf(index))))
            {
                completed++;
            }
        }

        DownloadStatusText.Text = completed > 0
            ? $"当前源已下载完成 {completed}/{remote.Episodes.Count} 集；可在上方选择「本地已下载」播放"
            : "尚未下载；下载完成后会出现「本地已下载」播放源";
    }

    private void RefreshLocalSourceInList()
    {
        var previousKey = _selected is null ? null : SearchResultSources.SelectionKeyOf(_selected);
        _searchResults = WithLocalSource(_remoteResults);
        var selected = previousKey is null
            ? _searchResults.FirstOrDefault()
            : _searchResults.FirstOrDefault(result =>
                  SearchResultSources.SelectionKeyOf(result) == previousKey)
              ?? _searchResults.FirstOrDefault();
        if (selected is null)
        {
            return;
        }

        BuildSourceChips(selected);
        // BuildSourceChips 会触发 SelectSource（若选中态变化）；本地源更新时同步刷新选集按钮。
        if (_selected is not null && IsLocalSource(_selected))
        {
            var local = _searchResults.FirstOrDefault(IsLocalSource);
            if (local is not null)
            {
                _selected = local;
                BuildEpisodes(local.Episodes);
            }
        }
    }


    private static string ShortError(Exception error)
    {
        var message = error.Message?.Trim() ?? "未知错误";
        return message.Length <= 80 ? message : message[..80] + "…";
    }

    private string SourceLabel(SearchResult result)
    {
        var baseName = string.IsNullOrWhiteSpace(result.SourceName) ? result.Source : result.SourceName;
        var label = result.Episodes.Count > 1
            ? $"{baseName} · {result.Episodes.Count}集"
            : result.Episodes.Count == 0 && !IsLocalSource(result)
                ? $"{baseName} · 待解析"
                : baseName;
        if (IsLocalSource(result))
        {
            return label;
        }

        if (!_health.TryGetValue(SearchResultSources.SelectionKeyOf(result), out var health))
        {
            return label;
        }

        var verdict = ChannelHealthScoring.LabelOf(health.Health);
        var metrics = ChannelHealthDisplay.MetricsLabelOf(health);
        return $"{label} · {verdict} · {metrics}";
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        if (_scanCts is not null)
        {
            _scanCts.Cancel();
            return;
        }

        var items = _searchResults
            .Where(result =>
                !IsLocalSource(result)
                && result.Episodes.Count > 0
                && !string.IsNullOrWhiteSpace(result.Episodes[0]))
            .Select(result => (Key: SearchResultSources.SelectionKeyOf(result), Url: result.Episodes[0]))
            .ToList();
        if (items.Count == 0)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _scanCts = cts;
        _health.Clear();
        BuildSourceChips(_selected ?? _searchResults[0]);
        ScanButton.Content = $"测速中 0/{items.Count}";
        ScanSummaryText.Text = "";
        var done = 0;

        try
        {
            await _host.Services.ChannelHealth.ProbeManyAsync(
                items,
                (key, result) => Dispatcher.Invoke(() =>
                {
                    if (cts.IsCancellationRequested)
                    {
                        return;
                    }

                    _health[key] = result;
                    done++;
                    if (_sourceChips.TryGetValue(key, out var chip) && chip.Tag is SearchResult source)
                    {
                        chip.Content = SourceLabel(source);
                    }
                    ScanButton.Content = $"测速中 {done}/{items.Count}";
                }),
                concurrency: 5,
                cts.Token);
        }
        catch (OperationCanceledException)
        {
            // 用户再次点击或离开页面时取消本轮测速。
        }
        finally
        {
            if (ReferenceEquals(_scanCts, cts))
            {
                _scanCts = null;
                ScanButton.Content = "测速";
                ScanSummaryText.Text = ChannelHealthDisplay.SummaryOf(_health.Values);
            }
            cts.Dispose();
        }
    }

    private void FavoriteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null || IsLocalSource(_selected))
        {
            return;
        }

        var item = new FavoriteItem(
            Cover: _selected.Poster,
            Title: _selected.Title,
            SourceName: _selected.SourceName,
            TotalEpisodes: _selected.Episodes.Count,
            SearchTitle: _title,
            Year: _selected.Year ?? "");

        var favorited = _host.Services.Favorites.Toggle(_selected.Source, _selected.Id, item);
        UpdateFavoriteIcon(favorited);
    }

    private void UpdateFavoriteIcon(bool favorited)
    {
        FavoriteButton.Content = favorited ? "★" : "☆";
        FavoriteButton.Foreground = favorited
            ? new SolidColorBrush(Color.FromRgb(0xFF, 0xC9, 0x40))
            : (Brush)FindResource("SoftTextBrush");
    }

    private void ShowMessage(string message)
    {
        ContentPanel.Visibility = Visibility.Collapsed;
        MessageText.Text = message;
        MessageText.Visibility = Visibility.Visible;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        var activeJobs = _host.Services.Downloads.GetActiveJobs()
            .Where(job => job.Title.StartsWith(_title + " ", StringComparison.OrdinalIgnoreCase)
                || string.Equals(job.Title, _title, StringComparison.OrdinalIgnoreCase)
                || job.Title.Contains(_title, StringComparison.OrdinalIgnoreCase))
            .ToList();

        _disposed = true;
        if (_listeningDownloads)
        {
            _host.Services.Downloads.Changed -= Downloads_Changed;
            _listeningDownloads = false;
        }

        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = null;

        // 离开详情页：询问是否后台继续；选否则取消本片相关任务。
        if (activeJobs.Count > 0 && !string.IsNullOrWhiteSpace(_title))
        {
            var preview = string.Join("、", activeJobs.Select(job => job.Title).Take(4));
            if (activeJobs.Count > 4)
            {
                preview += $" 等 {activeJobs.Count} 个";
            }

            var keep = MessageBox.Show(
                "《" + _title + "》还有下载未完成：\n" + preview
                + "\n\n是否在后台继续下载？\n（可在“本地视频”查看进度与速度；选“否”将停止这些下载并保留已下部分）",
                "后台下载",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) == MessageBoxResult.Yes;
            if (!keep)
            {
                foreach (var job in activeJobs)
                {
                    _ = _host.Services.Downloads.CancelAsync(job.Id);
                }
            }
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024d:0.0} KB";
        }

        if (bytes < 1024L * 1024 * 1024)
        {
            return $"{bytes / 1024d / 1024d:0.0} MB";
        }

        return $"{bytes / 1024d / 1024d / 1024d:0.00} GB";
    }

    private static string FormatSpeed(long bytesPerSecond)
    {
        if (bytesPerSecond <= 0)
        {
            return "0s";
        }

        return FormatSize(bytesPerSecond) + "/s";
    }

    private sealed record DownloadButtonTag(int Index, bool Decrypt);
}
