using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
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
    private readonly Dictionary<string, VodSourceHealthResult> _health = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RadioButton> _sourceChips = new(StringComparer.Ordinal);
    private readonly Dictionary<int, Button> _downloadButtons = new();
    private readonly HashSet<string> _decryptingDirectories = new(StringComparer.OrdinalIgnoreCase);
    private VodCacheLookup _cacheLookup = VodCacheLookup.Empty;
    private Task? _cacheRefreshTask;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _scanCts;
    private PlaybackResumeContext? _resumeContext;
    private bool _disposed;
    private bool _listeningDownloads;
    private bool _batchDecryptRunning;

    public DetailView(IAppHost host)
    {
        InitializeComponent();
        _host = host;
        Header.BackRequested += (_, _) => _host.GoBack();
    }

    public void OnNavigatedTo(object? parameter)
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
        _scanCts?.Cancel();
        _health.Clear();
        _cacheLookup = VodCacheLookup.Empty;
        _cacheRefreshTask = null;
        EnsureDownloadListener();
        if (parameter is not DetailParameter request)
        {
            ShowMessage("缺少详情参数");
            return;
        }

        _title = request.Title;
        _resumeContext = request.Resume;
        Header.Title = request.Title;
        ShowMessage("加载中…");
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        _ = LoadAsync(request, cts.Token);
    }

    private async Task LoadAsync(DetailParameter request, CancellationToken cancellationToken)
    {
        if (!_host.Services.HasSources)
        {
            ShowMessage("请先在设置中配置影视源订阅地址");
            return;
        }

        await Dispatcher.Yield(DispatcherPriority.Render);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (request.PlayableSources is { } verifiedSnapshot)
            {
                if (ShowSources(request, verifiedSnapshot))
                {
                    StartDetailBackgroundWork(cancellationToken);
                }
                return;
            }

            IReadOnlyList<SearchResult>? candidates = request.CandidateSources;
            if (candidates is null)
            {
                var sourceSnapshot = _host.Services.Sources;
                if (!string.IsNullOrWhiteSpace(request.Source)
                    && !string.IsNullOrWhiteSpace(request.Id))
                {
                    var preferredTask = _host.Services.Catalog.GetPreferredDetailSourcesAsync(
                        sourceSnapshot,
                        request.Source,
                        request.Id,
                        request.Title,
                        cancellationToken);
                    var allTask = _host.Services.Catalog.GetDetailSourcesAsync(
                        sourceSnapshot,
                        request.Title,
                        cancellationToken);
                    await Task.WhenAll(preferredTask, allTask);
                    candidates = MergeSources(
                        await preferredTask,
                        await allTask);
                }
                else
                {
                    candidates = await _host.Services.Catalog.GetDetailSourcesAsync(
                        sourceSnapshot,
                        request.Title,
                        cancellationToken);
                }
            }

            if (cancellationToken.IsCancellationRequested || _disposed)
            {
                return;
            }

            if (ShowSources(request, candidates))
            {
                StartDetailBackgroundWork(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            if (!cancellationToken.IsCancellationRequested && !_disposed)
            {
                ShowMessage("加载失败，请稍后重试");
            }
        }
    }

    private bool ShowSources(
        DetailParameter request,
        IReadOnlyList<SearchResult> sources)
    {
        if (sources.Count == 0)
        {
            ShowMessage($"未找到\"{request.Title}\" 的播放源");
            return false;
        }

        var selected = sources.FirstOrDefault(item =>
                string.Equals(item.Source, request.Source, StringComparison.Ordinal)
                && string.Equals(item.Id, request.Id, StringComparison.Ordinal)
                && (string.IsNullOrWhiteSpace(request.PlaybackSourceKey)
                    || string.Equals(
                        item.PlaybackSourceKey,
                        request.PlaybackSourceKey,
                        StringComparison.Ordinal)))
            ?? sources.FirstOrDefault(item =>
                string.Equals(item.Source, request.Source, StringComparison.Ordinal)
                && string.Equals(item.Id, request.Id, StringComparison.Ordinal))
            ?? sources[0];
        ShowResults(sources, selected);
        return true;
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

        var episodes = new string[maxEpisodes];
        var completed = 0;
        for (var index = 0; index < maxEpisodes; index++)
        {
            var path = FindCompletedPath(remotes, index);
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
            TypeName: template.TypeName,
            EpisodeLabels: template.EpisodeLabels);
    }

    private string? FindCompletedPath(IReadOnlyList<SearchResult> remotes, int episodeIndex)
    {
        var cacheTitle = CacheTitleOf(episodeIndex);
        foreach (var remote in remotes)
        {
            if (episodeIndex >= remote.Episodes.Count)
            {
                continue;
            }

            var entry = _cacheLookup.FindCompleted(cacheTitle, remote.Episodes[episodeIndex]);
            if (entry is not null)
            {
                return entry.PlaylistPath;
            }
        }

        return _cacheLookup.FindCompleted(cacheTitle, "")?.PlaylistPath;
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
        BuildEpisodes(result);
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
            BuildEpisodes(hydrated);
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

    private void BuildEpisodes(SearchResult result)
    {
        EpisodeBar.Children.Clear();
        var resumeEpisodeIndex = ResumeEpisodeIndexOf(result);
        for (var index = 0; index < result.Episodes.Count; index++)
        {
            var hasUrl = !string.IsNullOrWhiteSpace(result.Episodes[index]);
            var isResumeEpisode = index == resumeEpisodeIndex;
            var button = new Button
            {
                Content = EpisodeLabelFormatter.DisplayAt(result.EpisodeLabels, index),
                Style = (Style)FindResource(isResumeEpisode ? "PrimaryButtonStyle" : "SecondaryButtonStyle"),
                Height = 38,
                MinWidth = 64,
                Padding = new Thickness(12, 0, 12, 0),
                Margin = new Thickness(0, 0, 8, 8),
                FontSize = 13,
                Tag = index,
                IsEnabled = hasUrl,
                Opacity = hasUrl ? 1 : 0.45,
                ToolTip = !hasUrl
                    ? "本集暂无播放地址"
                    : isResumeEpisode
                        ? $"从 {FormatResumeTime(_resumeContext!.StartPositionMs)} 继续播放"
                        : "播放",
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
            RefreshDecryptMergeAllButtonState();
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
        RefreshDecryptMergeAllButtonState();
    }

    private SearchResult? GetDownloadSource()
    {
        if (_selected is not null && !IsLocalSource(_selected))
        {
            return _selected;
        }

        return _remoteResults.FirstOrDefault();
    }

    private List<DecryptEpisodeTarget> GetDecryptableEpisodeTargets(SearchResult? remote = null)
    {
        remote ??= GetDownloadSource();
        if (remote is null)
        {
            return [];
        }

        var seenDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targets = new List<DecryptEpisodeTarget>();
        for (var index = 0; index < remote.Episodes.Count; index++)
        {
            var entry = _cacheLookup.FindCompleted(CacheTitleOf(index), remote.Episodes[index]);
            if (entry is null || !entry.CanDecryptMerge)
            {
                continue;
            }

            var directoryKey = CacheDirectoryKey(entry.Directory);
            if (seenDirectories.Add(directoryKey))
            {
                targets.Add(new DecryptEpisodeTarget(index, entry, directoryKey));
            }
        }

        return targets;
    }

    private void RefreshDecryptMergeAllButtonState()
    {
        var count = GetDecryptableEpisodeTargets().Count;
        if (_batchDecryptRunning)
        {
            DecryptMergeAllButton.IsEnabled = false;
            return;
        }

        DecryptMergeAllButton.Content = count > 0 ? $"解密合并全部 ({count})" : "解密合并全部";
        DecryptMergeAllButton.IsEnabled = count > 0 && _decryptingDirectories.Count == 0;
        DecryptMergeAllButton.ToolTip = count > 0
            ? $"将当前影片 {count} 集已下载完成的 AES-128 缓存解密并合并为单文件"
            : "当前没有需要解密合并的已下载剧集";
    }

    private static string CacheDirectoryKey(string directory)
    {
        try
        {
            return Path.GetFullPath(directory);
        }
        catch
        {
            return directory;
        }
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
        var completedEntry = _cacheLookup.FindCompleted(cacheTitle, url);
        if (completedEntry is not null)
        {
            if (completedEntry.CanDecryptMerge)
            {
                var decrypting = _batchDecryptRunning
                    || _decryptingDirectories.Contains(CacheDirectoryKey(completedEntry.Directory));
                button.Style = (Style)FindResource("PrimaryButtonStyle");
                button.Content = decrypting
                    ? $"第{index + 1}集 · 解密中"
                    : $"第{index + 1}集 · 解密合并";
                button.IsEnabled = !decrypting;
                button.ToolTip = decrypting
                    ? "正在解密合并"
                    : "已下载完成（AES 加密）；点击解密合并为单个文件";
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

        var resume = index == ResumeEpisodeIndexOf(_selected) ? _resumeContext : null;
        // 备用源只供失败后返回详情时展示，不在播放器内自动换源。
        _host.Navigate(AppPage.Player, new PlayParameter(
            _selected.Title,
            _selected.Source,
            _selected.SourceName,
            _selected.Id,
            _selected.Episodes,
            index,
            resume?.StartPositionMs ?? 0,
            _searchResults,
            _selected.Poster,
            _selected.PlaybackSourceKey,
            _selected.EpisodeLabels,
            resume));
    }

    private int ResumeEpisodeIndexOf(SearchResult result) =>
        _resumeContext?.FindEpisodeIndex(result.EpisodeLabels, result.Episodes.Count) ?? -1;

    private static string FormatResumeTime(double positionMs)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(0, positionMs));
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"mm\:ss");
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
        if (_batchDecryptRunning)
        {
            return;
        }

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

        var directoryKey = CacheDirectoryKey(directory);
        if (!_decryptingDirectories.Add(directoryKey))
        {
            return;
        }

        RefreshDecryptMergeAllButtonState();
        if (_downloadButtons.TryGetValue(episodeIndex, out var button))
        {
            button.IsEnabled = false;
            button.Content = $"第{episodeIndex + 1}集 · 解密中";
        }

        string finalMessage;
        try
        {
            DownloadStatusText.Text = $"正在解密合并第{episodeIndex + 1} 集…";
            var progress = new Progress<double>(ratio =>
            {
                if (_disposed)
                {
                    return;
                }

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
            finalMessage =
                $"第{episodeIndex + 1} 集已解密合并（{new FileInfo(path).Length / 1024d / 1024d:0.0} MB）";
        }
        catch (Exception error)
        {
            finalMessage = $"第{episodeIndex + 1} 集解密失败：{ShortError(error)}";
        }
        finally
        {
            _decryptingDirectories.Remove(directoryKey);
            await RefreshCacheLookupAfterDecryptAsync().ConfigureAwait(true);
            if (!_disposed)
            {
                BuildDownloadBar();
            }
        }

        if (!_disposed)
        {
            DownloadStatusText.Text = finalMessage;
        }
    }

    private async void DecryptMergeAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (_batchDecryptRunning || _decryptingDirectories.Count > 0)
        {
            return;
        }

        var remote = GetDownloadSource();
        var targets = GetDecryptableEpisodeTargets(remote);
        if (remote is null || targets.Count == 0)
        {
            DownloadStatusText.Text = "当前没有需要解密合并的已下载剧集";
            RefreshDecryptMergeAllButtonState();
            return;
        }

        _batchDecryptRunning = true;
        RefreshDecryptMergeAllButtonState();
        foreach (var (index, _, _) in targets)
        {
            if (_downloadButtons.TryGetValue(index, out var button))
            {
                ApplyDownloadButtonState(button, remote, index);
            }
        }

        var targetsByDirectory = targets.ToDictionary(
            target => target.DirectoryKey,
            StringComparer.OrdinalIgnoreCase);
        string finalMessage;
        try
        {
            DownloadStatusText.Text = $"正在解密合并 1/{targets.Count} 集…";
            var progress = new Progress<VodCacheBatchDecryptProgress>(update =>
            {
                if (_disposed)
                {
                    return;
                }

                var ordinal = Math.Clamp(update.EntryIndex + 1, 1, update.TotalEntries);
                var percent = Math.Clamp((int)Math.Round(update.CurrentEntryProgress * 100), 0, 100);
                DownloadStatusText.Text = $"正在解密合并第{ordinal}/{update.TotalEntries} 集…{percent}%";
                DecryptMergeAllButton.Content = $"解密中 {ordinal}/{update.TotalEntries}";
                if (targetsByDirectory.TryGetValue(CacheDirectoryKey(update.CurrentEntry.Directory), out var target)
                    && _downloadButtons.TryGetValue(target.Index, out var button))
                {
                    button.Content = $"第{target.Index + 1}集 · 解密 {percent}%";
                }
            });
            var result = await _host.Services.VodCache
                .DecryptAndMergeManyAsync(targets.Select(target => target.Entry), progress)
                .ConfigureAwait(true);
            finalMessage = BatchDecryptResultMessage(result, targets.Count);
        }
        catch (Exception error)
        {
            finalMessage = $"批量解密合并失败：{ShortError(error)}";
        }
        finally
        {
            _batchDecryptRunning = false;
            await RefreshCacheLookupAfterDecryptAsync().ConfigureAwait(true);
            if (!_disposed)
            {
                BuildDownloadBar();
            }
        }

        if (!_disposed)
        {
            DownloadStatusText.Text = finalMessage;
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

                if (_cacheLookup.FindCompleted(cacheTitle, url) is not null)
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
            RefreshDecryptMergeAllButtonState();
            if (_host.Services.Downloads.GetJobs().Any(job =>
                    job.Status == VodDownloadJobStatus.Completed
                    && (job.Title.StartsWith(_title + " ", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(job.Title, _title, StringComparison.OrdinalIgnoreCase))
                    && _cacheLookup.FindCompleted(job.Title, "") is null))
            {
                var token = _loadCts?.Token ?? CancellationToken.None;
                QueueCacheLookupRefresh(token);
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
            if (_cacheLookup.FindCompleted(CacheTitleOf(index), remote.Episodes[index]) is not null)
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
                BuildEpisodes(local);
            }
        }
    }


    private static string ShortError(Exception error)
    {
        var message = error.Message?.Trim() ?? "未知错误";
        return message.Length <= 80 ? message : message[..80] + "…";
    }

    private static string BatchDecryptResultMessage(VodCacheBatchDecryptResult result, int total)
    {
        if (result.Failures.Count == 0)
        {
            return $"已解密合并全部 {result.SucceededEntries.Count} 集；可在「本地已下载」播放";
        }

        var failedTitles = string.Join("、", result.Failures
            .Select(failure => failure.Entry.Title)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3));
        if (result.Failures.Count > 3)
        {
            failedTitles += " 等";
        }

        return $"批量解密合并完成：成功 {result.SucceededEntries.Count}/{total} 集，失败 {result.Failures.Count} 集（{failedTitles}）";
    }

    private string SourceLabel(SearchResult result)
    {
        var baseName = string.IsNullOrWhiteSpace(result.SourceName) ? result.Source : result.SourceName;
        var label = result.Episodes.Count > 1
            ? $"{baseName} · {EpisodeSummary(result)}"
            : result.Episodes.Count == 0 && !IsLocalSource(result)
                ? $"{baseName} · 待解析"
                : baseName;
        if (_resumeContext is not null && ResumeEpisodeIndexOf(result) < 0)
        {
            var target = !string.IsNullOrWhiteSpace(_resumeContext.EpisodeLabel)
                ? _resumeContext.EpisodeLabel
                : _resumeContext.EpisodeNumber is > 0
                    ? $"第{_resumeContext.EpisodeNumber}集"
                    : "续播目标集";
            return $"{label} · 缺少{target}";
        }

        if (IsLocalSource(result))
        {
            return label;
        }

        if (!_health.TryGetValue(SearchResultSources.SelectionKeyOf(result), out var health))
        {
            return label;
        }

        return $"{label} · {health.DisplayLabel}";
    }

    private static string EpisodeSummary(SearchResult result)
    {
        var latestNumber = 0;
        if (result.EpisodeLabels is not null)
        {
            foreach (var episodeLabel in result.EpisodeLabels)
            {
                if (EpisodeLabelFormatter.TryGetNumber(episodeLabel, out var number))
                {
                    latestNumber = Math.Max(latestNumber, number);
                }
            }
        }

        return latestNumber > 0 && latestNumber != result.Episodes.Count
            ? $"更新至{latestNumber}集"
            : $"{result.Episodes.Count}集";
    }

    private void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        if (_scanCts is not null)
        {
            _scanCts.Cancel();
            return;
        }

        StartSourceScan();
    }

    private void StartSourceScan()
    {
        if (_disposed || _scanCts is not null)
        {
            return;
        }

        var items = _searchResults
            .Where(result =>
                !IsLocalSource(result)
                && result.Episodes.Count > 0
                && result.Episodes.Any(episode => !string.IsNullOrWhiteSpace(episode)))
            .ToList();
        if (items.Count == 0)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _scanCts = cts;
        _health.Clear();
        RefreshSourceChipLabels();
        ScanButton.Content = $"测速中 0/{items.Count}";
        ScanSummaryText.Text = "";
        _ = RunSourceScanAsync(items, cts);
    }

    private void StartDetailBackgroundWork(CancellationToken cancellationToken)
    {
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(StartSourceScan));
        QueueCacheLookupRefresh(cancellationToken);
    }

    private void QueueCacheLookupRefresh(CancellationToken cancellationToken)
    {
        if (_cacheRefreshTask is { IsCompleted: false })
        {
            return;
        }

        _cacheRefreshTask = RefreshCacheLookupAsync(cancellationToken);
    }

    private async Task RefreshCacheLookupAfterDecryptAsync()
    {
        if (_cacheRefreshTask is { IsCompleted: false } pendingRefresh)
        {
            await pendingRefresh.ConfigureAwait(true);
        }

        if (_disposed)
        {
            return;
        }

        var cancellationToken = _loadCts?.Token ?? CancellationToken.None;
        await RefreshCacheLookupAsync(cancellationToken).ConfigureAwait(true);
    }

    private async Task RefreshCacheLookupAsync(CancellationToken cancellationToken)
    {
        try
        {
            var cacheDirectory = _host.Services.Settings.VodCacheDirectory;
            var lookup = await Task.Run(
                () => VodCacheLookup.FromEntries(_host.Services.VodCache.GetEntries(cacheDirectory)),
                cancellationToken);
            if (cancellationToken.IsCancellationRequested || _disposed)
            {
                return;
            }

            _cacheLookup = lookup;
            RefreshDecryptMergeAllButtonState();
            if (HasCompletedCachedEpisode())
            {
                RefreshLocalSourceInList();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            // 缓存索引失败不影响在线播放与测速。
        }
    }

    private bool HasCompletedCachedEpisode()
    {
        var maxEpisodes = _remoteResults.Count == 0
            ? 0
            : _remoteResults.Max(result => result.Episodes.Count);
        for (var index = 0; index < maxEpisodes; index++)
        {
            if (_cacheLookup.FindCompleted(CacheTitleOf(index), "") is not null)
            {
                return true;
            }
        }

        return false;
    }

    private void RefreshSourceChipLabels()
    {
        foreach (var chip in _sourceChips.Values)
        {
            if (chip.Tag is SearchResult source)
            {
                chip.Content = SourceLabel(source);
            }
        }
    }

    private async Task RunSourceScanAsync(
        IReadOnlyList<SearchResult> items,
        CancellationTokenSource cts)
    {
        var done = 0;

        try
        {
            Func<SearchResult, int?>? preferredEpisodeIndex = _resumeContext is null
                ? null
                : source =>
                {
                    var index = ResumeEpisodeIndexOf(source);
                    return index >= 0 ? index : null;
                };
            await _host.Services.VodSourceHealth.ProbeManyAsync(
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
                concurrency: 4,
                cts.Token,
                preferredEpisodeIndex);
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
                ScanSummaryText.Text = VodSourceHealthDisplay.SummaryOf(_health.Values);
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
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;

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

    private sealed record DecryptEpisodeTarget(int Index, VodCacheEntry Entry, string DirectoryKey);
}
