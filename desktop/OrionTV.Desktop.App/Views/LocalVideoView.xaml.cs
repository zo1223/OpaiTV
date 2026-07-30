using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.App.Views;

public partial class LocalVideoView : UserControl, INavigationAware, IDisposable
{
    private readonly IAppHost _host;
    private List<LocalCacheItem> _items = [];
    private List<LocalCachePlaybackGroup> _cachePlaybackGroups = [];
    private List<LocalMediaCardItem> _mediaItems = [];
    private List<ActiveJobItem> _activeJobs = [];
    private readonly HashSet<string> _decryptingDirectories = new(StringComparer.OrdinalIgnoreCase);
    private bool _listening;
    private bool _disposed;
    private bool _batchDecryptRunning;
    private DispatcherTimer? _refreshTimer;

    public LocalVideoView(IAppHost host)
    {
        InitializeComponent();
        _host = host;
        Header.BackRequested += (_, _) => _host.GoBack();
        Loaded += (_, _) => EnsureListener();
        Unloaded += (_, _) => DetachListener();
    }

    public void OnNavigatedTo(object? parameter)
    {
        EnsureListener();
        RefreshAll();
    }

    private void EnsureListener()
    {
        if (_listening || _disposed)
        {
            return;
        }

        _host.Services.Downloads.Changed += Downloads_Changed;
        _listening = true;
        _refreshTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick -= RefreshTimer_Tick;
        _refreshTimer.Tick += RefreshTimer_Tick;
        _refreshTimer.Start();
    }

    private void DetachListener()
    {
        if (_listening)
        {
            _host.Services.Downloads.Changed -= Downloads_Changed;
            _listening = false;
        }

        _refreshTimer?.Stop();
    }

    private void Downloads_Changed(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        Dispatcher.BeginInvoke(RefreshAll);
    }

    private void RefreshTimer_Tick(object? sender, EventArgs e)
    {
        if (_disposed || !IsLoaded)
        {
            return;
        }

        // 速度/进度由管理器推送；定时刷新保证离开详情后本地页也能及时更新。
        if (_host.Services.Downloads.GetActiveJobs().Count > 0)
        {
            RefreshActiveJobs();
        }
    }

    private void OpenLocalFileButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择本地视频",
            Filter = "视频文件|*.mp4;*.mkv;*.avi;*.mov;*.webm;*.ts;*.m2ts;*.flv;*.m3u8;*.mp3;*.flac;*.wav|所有文件|*.*",
            Multiselect = true,
        };
        if (dialog.ShowDialog() == true)
        {
            var imported = _host.Services.LocalMedia.Import(dialog.FileNames);
            RefreshEntries();
            StatusText.Text = imported.Count == 0
                ? "没有找到可添加的视频文件"
                : $"已添加 {imported.Count} 部本地影视，共 {imported.Sum(item => item.Episodes.Count)} 个文件";
        }
    }

    private void OpenCacheFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var directory = VodCacheSettings.ResolveDirectory(_host.Services.Settings.VodCacheDirectory);
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo("explorer.exe", directory) { UseShellExecute = true });
        }
        catch
        {
            StatusText.Text = "无法打开缓存目录";
        }
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => RefreshAll();

    private void RefreshAll()
    {
        RefreshActiveJobs();
        RefreshEntries();
    }

    private void RefreshActiveJobs()
    {
        _activeJobs = _host.Services.Downloads.GetJobs()
            .Where(job => job.Status is VodDownloadJobStatus.Queued
                or VodDownloadJobStatus.Running
                or VodDownloadJobStatus.Completed
                or VodDownloadJobStatus.Failed
                or VodDownloadJobStatus.Cancelled)
            .Select(job => new ActiveJobItem(job))
            .ToList();
        ActiveDownloadList.ItemsSource = _activeJobs;
        ActiveEmptyText.Visibility = _activeJobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ActiveHeaderText.Text = _activeJobs.Count == 0
            ? "后台下载"
            : $"后台下载（{_activeJobs.Count(job => job.IsActive)} 个进行中）";
    }

    private void RefreshEntries()
    {
        var entries = _host.Services.VodCache.GetEntries(_host.Services.Settings.VodCacheDirectory);
        _items = entries
            .Select(entry => new LocalCacheItem(
                entry,
                !_batchDecryptRunning
                && !_decryptingDirectories.Contains(CacheDirectoryKey(entry.Directory))))
            .ToList();
        CacheList.ItemsSource = _items;
        EmptyText.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshDecryptMergeAllButtonState();

        _cachePlaybackGroups = LocalCachePlaybackCatalog.Build(entries).ToList();
        _mediaItems = BuildMediaItems(_host.Services.LocalMedia.GetItems(), _cachePlaybackGroups);
        MediaGrid.ItemsSource = _mediaItems;
        MediaEmptyText.Visibility = _mediaItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var activeCount = _host.Services.Downloads.GetActiveJobs().Count;
        StatusText.Text = activeCount > 0
            ? $"后台下载 {activeCount} 个 · 本地媒体 {_mediaItems.Count} 部 · 缓存 {_items.Count} 集"
            : _mediaItems.Count == 0
                ? ""
                : $"共 {_mediaItems.Count} 部本地影视 · {_items.Count} 个应用缓存";
    }

    private void RefreshDecryptMergeAllButtonState()
    {
        var count = _items.Count(item => item.CanDecrypt);
        if (_batchDecryptRunning)
        {
            DecryptMergeAllButton.IsEnabled = false;
            return;
        }

        DecryptMergeAllButton.Content = count > 0 ? $"解密合并全部 ({count})" : "解密合并全部";
        DecryptMergeAllButton.IsEnabled = count > 0 && _decryptingDirectories.Count == 0;
        DecryptMergeAllButton.ToolTip = count > 0
            ? $"将列表中 {count} 个已下载完成的 AES-128 缓存解密并合并为单文件"
            : "当前没有需要解密合并的已下载缓存";
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

    private static List<LocalMediaCardItem> BuildMediaItems(
        IReadOnlyList<LocalMediaItem> importedItems,
        IReadOnlyList<LocalCachePlaybackGroup> cacheGroups)
    {
        var cards = importedItems
            .Select(item =>
            {
                var episodes = item.Episodes
                    .Select(episode => new
                    {
                        Uri = ToPlayableUri(episode.Path),
                        Label = episode.Number is { } number ? $"第{number}集" : "",
                    })
                    .Where(episode => !string.IsNullOrWhiteSpace(episode.Uri))
                    .ToList();
                return episodes.Count == 0
                    ? null
                    : LocalMediaCardItem.FromImported(
                        item,
                        episodes.Select(episode => episode.Uri).ToList(),
                        episodes.Select(episode => episode.Label).ToList());
            })
            .Where(item => item is not null)
            .Cast<LocalMediaCardItem>()
            .ToList();

        cards.AddRange(cacheGroups.Select(group => LocalMediaCardItem.FromCache(
            group.Id,
            group.Title,
            group.Poster,
            group.Episodes,
            group.EpisodeLabels,
            group.SortTime)));

        return cards.OrderByDescending(item => item.SortTime).ToList();
    }

    private static string ToPlayableUri(string path) =>
        LocalCachePlaybackCatalog.NormalizePlayableUri(path);

    private void PlayCacheButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: LocalCacheItem item })
        {
            return;
        }

        var selection = LocalCachePlaybackCatalog.FindByDirectory(
            _cachePlaybackGroups,
            item.Directory);
        if (selection is null)
        {
            PlayLocalFile(item.PlaylistPath, item.Title, item.Poster);
            return;
        }

        _host.Navigate(AppPage.Player, selection.ToPlayParameter());
    }

    private void PlayMediaCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LocalMediaCardItem item })
        {
            PlayLocalMedia(
                item.Id,
                item.Card.Title,
                item.Card.Poster,
                item.Episodes,
                episodeLabels: item.EpisodeLabels);
        }
    }

    private void RemoveMediaButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: LocalMediaCardItem { LibraryId: { } libraryId } item })
        {
            return;
        }

        var result = MessageBox.Show(
            Window.GetWindow(this),
            $"从本地媒体列表移除《{item.Card.Title}》？\n\n视频文件不会被删除。",
            "移除本地媒体",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        _host.Services.LocalMedia.Remove(libraryId);
        RefreshEntries();
        StatusText.Text = $"已从列表移除《{item.Card.Title}》，视频文件仍保留在原位置";
    }

    private async void CancelActiveButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ActiveJobItem item })
        {
            return;
        }

        try
        {
            StatusText.Text = $"正在取消《{item.Title}》…";
            await _host.Services.Downloads.CancelAsync(item.Id).ConfigureAwait(true);
            StatusText.Text = $"已取消《{item.Title}》（已下部分会保留）";
            RefreshAll();
        }
        catch
        {
            StatusText.Text = "取消失败";
        }
    }

    private async void DecryptMergeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_batchDecryptRunning
            || sender is not Button { DataContext: LocalCacheItem item } button)
        {
            return;
        }

        var directoryKey = CacheDirectoryKey(item.Directory);
        if (!_decryptingDirectories.Add(directoryKey))
        {
            return;
        }

        button.IsEnabled = false;
        RefreshDecryptMergeAllButtonState();
        string finalMessage;
        try
        {
            StatusText.Text = $"正在解密合并《{item.Title}》…";
            var progress = new Progress<double>(ratio =>
            {
                if (_disposed)
                {
                    return;
                }

                StatusText.Text = $"正在解密合并《{item.Title}》… {Math.Clamp((int)Math.Round(ratio * 100), 0, 100)}%";
            });
            var path = await _host.Services.VodCache
                .DecryptAndMergeAsync(item.Directory, progress)
                .ConfigureAwait(true);
            finalMessage = $"《{item.Title}》已解密合并为单文件（{new FileInfo(path).Length / 1024d / 1024d:0.0} MB）";
        }
        catch (Exception error)
        {
            finalMessage = $"解密合并失败：{ShortError(error)}";
        }
        finally
        {
            _decryptingDirectories.Remove(directoryKey);
            if (!_disposed)
            {
                RefreshAll();
            }
        }

        if (!_disposed)
        {
            StatusText.Text = finalMessage;
        }
    }

    private async void DecryptMergeAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (_batchDecryptRunning || _decryptingDirectories.Count > 0)
        {
            return;
        }

        var entries = _items
            .Where(item => item.CanDecrypt)
            .GroupBy(item => CacheDirectoryKey(item.Directory), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First().Entry)
            .ToList();
        if (entries.Count == 0)
        {
            StatusText.Text = "当前没有需要解密合并的已下载缓存";
            RefreshDecryptMergeAllButtonState();
            return;
        }

        _batchDecryptRunning = true;
        RefreshEntries();
        string finalMessage;
        try
        {
            StatusText.Text = $"正在解密合并 1/{entries.Count} 项…";
            var progress = new Progress<VodCacheBatchDecryptProgress>(update =>
            {
                if (_disposed)
                {
                    return;
                }

                var ordinal = Math.Clamp(update.EntryIndex + 1, 1, update.TotalEntries);
                var percent = Math.Clamp((int)Math.Round(update.CurrentEntryProgress * 100), 0, 100);
                StatusText.Text = $"正在解密合并《{update.CurrentEntry.Title}》（{ordinal}/{update.TotalEntries}）… {percent}%";
                DecryptMergeAllButton.Content = $"解密中 {ordinal}/{update.TotalEntries}";
            });
            var result = await _host.Services.VodCache
                .DecryptAndMergeManyAsync(entries, progress)
                .ConfigureAwait(true);
            finalMessage = BatchDecryptResultMessage(result, entries.Count);
        }
        catch (Exception error)
        {
            finalMessage = $"批量解密合并失败：{ShortError(error)}";
        }
        finally
        {
            _batchDecryptRunning = false;
            if (!_disposed)
            {
                RefreshAll();
            }
        }

        if (!_disposed)
        {
            StatusText.Text = finalMessage;
        }
    }

    private async void ResumeCacheButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: LocalCacheItem item } || string.IsNullOrWhiteSpace(item.SourceUrl))
        {
            return;
        }

        try
        {
            StatusText.Text = $"正在续下《{item.Title}》…";
            var job = await _host.Services.Downloads.StartAsync(
                item.SourceUrl,
                item.Title,
                _host.Services.Settings.VodCacheDirectory,
                poster: item.Poster).ConfigureAwait(true);
            StatusText.Text = $"《{item.Title}》已加入后台下载（{job.Percent}%）";
            RefreshAll();
        }
        catch
        {
            StatusText.Text = "续下失败，请检查网络或源是否仍有效";
        }
    }

    private async void DeleteCacheButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: LocalCacheItem item })
        {
            return;
        }

        try
        {
            await _host.Services.VodCache.DeleteEntryAsync(
                item.Directory,
                _host.Services.Settings.VodCacheDirectory);
            StatusText.Text = $"已删除《{item.Title}》缓存";
            RefreshAll();
        }
        catch
        {
            StatusText.Text = "删除失败，缓存可能正在下载或播放";
        }
    }

    private void PlayLocalFile(string path, string title, string poster = "")
    {
        var uri = ToPlayableUri(path);
        if (string.IsNullOrWhiteSpace(uri))
        {
            StatusText.Text = "文件不存在";
            return;
        }

        PlayLocalMedia(path, title, poster, [uri]);
    }

    private void PlayLocalMedia(
        string id,
        string title,
        string poster,
        IReadOnlyList<string> episodes,
        int episodeIndex = 0,
        IReadOnlyList<string>? episodeLabels = null)
    {
        var playable = episodes
            .Select((path, index) => new
            {
                Path = path,
                OriginalIndex = index,
                Label = episodeLabels is not null && index < episodeLabels.Count
                    ? episodeLabels[index]
                    : "",
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Path))
            .ToList();
        if (playable.Count == 0)
        {
            StatusText.Text = "没有找到可播放的本地文件";
            return;
        }

        var selectedIndex = playable.FindIndex(item => item.OriginalIndex == episodeIndex);
        if (selectedIndex < 0)
        {
            selectedIndex = 0;
        }

        var playableEpisodes = playable.Select(item => item.Path).ToList();
        IReadOnlyList<string>? playableLabels = episodeLabels is null
            ? null
            : playable.Select(item => item.Label).ToList();
        var result = new SearchResult(
            id,
            title,
            poster,
            playableEpisodes,
            "local",
            "本地",
            "",
            EpisodeLabels: playableLabels);
        _host.Navigate(AppPage.Player, new PlayParameter(
            title,
            "local",
            "本地",
            id,
            playableEpisodes,
            selectedIndex,
            0,
            [result],
            poster,
            EpisodeLabels: playableLabels));
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
            return $"已解密合并全部 {result.SucceededEntries.Count} 项";
        }

        var failedTitles = string.Join("、", result.Failures
            .Select(failure => failure.Entry.Title)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3));
        if (result.Failures.Count > 3)
        {
            failedTitles += " 等";
        }

        return $"批量解密合并完成：成功 {result.SucceededEntries.Count}/{total} 项，失败 {result.Failures.Count} 项（{failedTitles}）";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DetachListener();
        if (_refreshTimer is not null)
        {
            _refreshTimer.Tick -= RefreshTimer_Tick;
            _refreshTimer = null;
        }
    }

    private sealed class ActiveJobItem
    {
        internal ActiveJobItem(VodDownloadJobSnapshot job)
        {
            Id = job.Id;
            Title = job.Title;
            IsActive = job.Status is VodDownloadJobStatus.Queued or VodDownloadJobStatus.Running;
            CanCancel = IsActive;
            StatusLabel = job.Status switch
            {
                VodDownloadJobStatus.Queued => "排队中",
                VodDownloadJobStatus.Running => "下载中",
                VodDownloadJobStatus.Completed => "已完成",
                VodDownloadJobStatus.Failed => "失败",
                VodDownloadJobStatus.Cancelled => "已取消",
                _ => job.Status.ToString(),
            };
            Summary = job.Status switch
            {
                VodDownloadJobStatus.Running or VodDownloadJobStatus.Queued =>
                    $"{job.Percent}% · {job.CompletedSegments}/{Math.Max(job.TotalSegments, job.CompletedSegments)} 段 · {FormatSize(job.DownloadedBytes)} · {FormatSpeed(job.BytesPerSecond)}",
                VodDownloadJobStatus.Completed =>
                    $"完成 · {FormatSize(job.DownloadedBytes)}",
                VodDownloadJobStatus.Failed =>
                    $"失败 · {job.Error ?? "未知错误"}",
                VodDownloadJobStatus.Cancelled =>
                    $"已取消 · 已下 {FormatSize(job.DownloadedBytes)}",
                _ => FormatSize(job.DownloadedBytes),
            };
        }

        public string Id { get; }
        public string Title { get; }
        public string StatusLabel { get; }
        public string Summary { get; }
        public bool IsActive { get; }
        public bool CanCancel { get; }
        public Visibility CancelVisibility => CanCancel ? Visibility.Visible : Visibility.Collapsed;
    }

    private sealed class LocalCacheItem
    {
        internal LocalCacheItem(VodCacheEntry entry, bool decryptEnabled)
        {
            Entry = entry;
            Directory = entry.Directory;
            PlaylistPath = entry.PlaylistPath;
            Title = entry.Title;
            Poster = entry.Poster;
            SourceUrl = entry.SourceUrl;
            CanResume = entry.CanResumeDownload;
            CanDecrypt = entry.CanDecryptMerge;
            DecryptEnabled = CanDecrypt && decryptEnabled;

            var state = entry.IsComplete
                ? "整集已完成"
                : $"已下载 {entry.CompletedSegments}/{Math.Max(entry.TotalSegments, entry.CompletedSegments)} 段";
            var merged = !string.IsNullOrWhiteSpace(entry.MergedFilePath)
                ? (entry.IsComplete ? " · 单文件 TS" : $" · 单文件已合并 {entry.MergedSegments} 段")
                : entry.CanDecryptMerge
                    ? " · AES 加密，可一键解密合并"
                    : entry.MergeSupported
                        ? ""
                        : " · 加密源（需解密后才能合成单文件）";
            Summary = $"{state}{merged} · {entry.SizeBytes / 1024d / 1024d:0.0} MB · {entry.CreatedAt:yyyy-MM-dd HH:mm}";
        }

        internal VodCacheEntry Entry { get; }
        public string Directory { get; }
        public string PlaylistPath { get; }
        public string Title { get; }
        public string Poster { get; }
        public string Summary { get; }
        public string SourceUrl { get; }
        public bool CanResume { get; }
        public bool CanDecrypt { get; }
        public bool DecryptEnabled { get; }
        public Visibility ResumeVisibility => CanResume ? Visibility.Visible : Visibility.Collapsed;
        public Visibility DecryptVisibility => CanDecrypt ? Visibility.Visible : Visibility.Collapsed;
    }

    private sealed class LocalMediaCardItem
    {
        private LocalMediaCardItem(
            string id,
            string? libraryId,
            string title,
            string poster,
            IReadOnlyList<string> episodes,
            IReadOnlyList<string> episodeLabels,
            DateTimeOffset sortTime)
        {
            Id = id;
            LibraryId = libraryId;
            Episodes = episodes;
            EpisodeLabels = episodeLabels;
            SortTime = sortTime;
            Card = new CatalogItem(
                id,
                title,
                poster,
                "local",
                "本地",
                episodes.Count > 1 ? $"{episodes.Count}集" : null);
        }

        public string Id { get; }
        public string? LibraryId { get; }
        public CatalogItem Card { get; }
        public IReadOnlyList<string> Episodes { get; }
        public IReadOnlyList<string> EpisodeLabels { get; }
        public DateTimeOffset SortTime { get; }
        public Visibility RemoveVisibility => LibraryId is null ? Visibility.Collapsed : Visibility.Visible;

        internal static LocalMediaCardItem FromImported(
            LocalMediaItem item,
            IReadOnlyList<string> episodes,
            IReadOnlyList<string> episodeLabels) =>
            new(
                item.Id,
                item.Id,
                item.Title,
                item.Poster,
                episodes,
                episodeLabels,
                DateTimeOffset.FromUnixTimeMilliseconds(Math.Max(0, item.SaveTime)));

        internal static LocalMediaCardItem FromCache(
            string id,
            string title,
            string poster,
            IReadOnlyList<string> episodes,
            IReadOnlyList<string> episodeLabels,
            DateTimeOffset sortTime) =>
            new(id, null, title, poster, episodes, episodeLabels, sortTime);
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
            return "—/s";
        }

        var megabitsPerSecond = bytesPerSecond * 8d / 1_000_000d;
        return $"{FormatSize(bytesPerSecond)}/s · {megabitsPerSecond:0.0} Mbps";
    }
}
