using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
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
    private List<LocalMediaCardItem> _mediaItems = [];
    private List<ActiveJobItem> _activeJobs = [];
    private bool _listening;
    private bool _disposed;
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
            .Select(entry => new LocalCacheItem(entry))
            .ToList();
        CacheList.ItemsSource = _items;
        EmptyText.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        _mediaItems = BuildMediaItems(_host.Services.LocalMedia.GetItems(), entries);
        MediaGrid.ItemsSource = _mediaItems;
        MediaEmptyText.Visibility = _mediaItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var activeCount = _host.Services.Downloads.GetActiveJobs().Count;
        StatusText.Text = activeCount > 0
            ? $"后台下载 {activeCount} 个 · 本地媒体 {_mediaItems.Count} 部 · 缓存 {_items.Count} 集"
            : _mediaItems.Count == 0
                ? ""
                : $"共 {_mediaItems.Count} 部本地影视 · {_items.Count} 个应用缓存";
    }

    private static List<LocalMediaCardItem> BuildMediaItems(
        IReadOnlyList<LocalMediaItem> importedItems,
        IReadOnlyList<VodCacheEntry> cacheEntries)
    {
        var cards = importedItems
            .Select(item =>
            {
                var episodes = item.Episodes
                    .Select(episode => ToPlayableUri(episode.Path))
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .ToList();
                return episodes.Count == 0
                    ? null
                    : LocalMediaCardItem.FromImported(item, episodes);
            })
            .Where(item => item is not null)
            .Cast<LocalMediaCardItem>()
            .ToList();

        var parsedEntries = cacheEntries.Select(entry => new
        {
            Entry = entry,
            Name = LocalMediaNaming.Parse(entry.Title),
        });
        var cacheGroups = parsedEntries.GroupBy(item =>
            item.Name.EpisodeNumber is not null
                ? $"series:{LocalMediaNaming.NormalizeTitle(item.Name.Title)}"
                : $"cache:{item.Entry.Directory}",
            StringComparer.OrdinalIgnoreCase);
        foreach (var group in cacheGroups)
        {
            var selected = group
                .GroupBy(item => item.Name.EpisodeNumber?.ToString() ?? item.Entry.Directory, StringComparer.OrdinalIgnoreCase)
                .Select(items => items.OrderByDescending(item => item.Entry.CreatedAt).First())
                .OrderBy(item => item.Name.EpisodeNumber ?? int.MaxValue)
                .ThenBy(item => item.Entry.CreatedAt)
                .ToList();
            var episodes = selected
                .Select(item => ToPlayableUri(item.Entry.PlaylistPath))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToList();
            if (episodes.Count == 0)
            {
                continue;
            }

            var title = selected[0].Name.Title;
            var poster = selected.Select(item => item.Entry.Poster)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
            cards.Add(LocalMediaCardItem.FromCache(
                StableCacheId(group.Key),
                title,
                poster,
                episodes,
                selected.Max(item => item.Entry.CreatedAt)));
        }

        return cards.OrderByDescending(item => item.SortTime).ToList();
    }

    private static string StableCacheId(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return "cache-" + Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private static string ToPlayableUri(string path)
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

    private void PlayCacheButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: LocalCacheItem item })
        {
            PlayLocalFile(item.PlaylistPath, item.Title, item.Poster);
        }
    }

    private void PlayMediaCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LocalMediaCardItem item })
        {
            PlayLocalMedia(item.Id, item.Card.Title, item.Card.Poster, item.Episodes);
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

    private async void OptimizeSeekButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: LocalCacheItem item } button)
        {
            return;
        }

        if (!LocalMediaRemuxer.IsFfmpegAvailable())
        {
            StatusText.Text = "未找到 ffmpeg。请安装 ffmpeg 并加入 PATH 后重试（用于 TS→MP4 快速拖动优化）";
            return;
        }

        button.IsEnabled = false;
        try
        {
            StatusText.Text = $"正在优化《{item.Title}》拖动体验（转封装 MP4，不重编码）…";
            var source = item.MergedTsPath;
            if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
            {
                StatusText.Text = "未找到可优化的 merged.ts";
                button.IsEnabled = true;
                return;
            }

            var output = await LocalMediaRemuxer.TryRemuxToFastSeekMp4Async(source, item.Directory)
                .ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(output))
            {
                StatusText.Text = "优化失败：ffmpeg 转封装未成功（仍可继续播 TS）";
                button.IsEnabled = true;
                return;
            }

            StatusText.Text = $"《{item.Title}》已优化为可快速拖动文件（{new FileInfo(output).Length / 1024d / 1024d:0.0} MB）";
            RefreshAll();
        }
        catch (Exception error)
        {
            StatusText.Text = $"优化失败：{ShortError(error)}";
            button.IsEnabled = true;
        }
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
        if (sender is not Button { DataContext: LocalCacheItem item } button)
        {
            return;
        }

        button.IsEnabled = false;
        try
        {
            StatusText.Text = $"正在解密合并《{item.Title}》…";
            var progress = new Progress<double>(ratio =>
            {
                StatusText.Text = $"正在解密合并《{item.Title}》… {Math.Clamp((int)Math.Round(ratio * 100), 0, 100)}%";
            });
            var path = await _host.Services.VodCache
                .DecryptAndMergeAsync(item.Directory, progress)
                .ConfigureAwait(true);
            StatusText.Text = $"《{item.Title}》已解密合并为单文件（{new FileInfo(path).Length / 1024d / 1024d:0.0} MB）";
            RefreshAll();
        }
        catch (Exception error)
        {
            StatusText.Text = $"解密合并失败：{ShortError(error)}";
            button.IsEnabled = true;
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
            await _host.Services.VodCache.DeleteEntryAsync(item.Directory);
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

    private void PlayLocalMedia(string id, string title, string poster, IReadOnlyList<string> episodes)
    {
        var playableEpisodes = episodes.Where(path => !string.IsNullOrWhiteSpace(path)).ToList();
        if (playableEpisodes.Count == 0)
        {
            StatusText.Text = "没有找到可播放的本地文件";
            return;
        }

        var result = new SearchResult(id, title, poster, playableEpisodes, "local", "本地", "");
        _host.Navigate(AppPage.Player, new PlayParameter(
            title,
            "local",
            "本地",
            id,
            playableEpisodes,
            0,
            0,
            [result],
            poster));
    }

    private static string ShortError(Exception error)
    {
        var message = error.Message?.Trim() ?? "未知错误";
        return message.Length <= 80 ? message : message[..80] + "…";
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
        internal LocalCacheItem(VodCacheEntry entry)
        {
            Directory = entry.Directory;
            PlaylistPath = entry.PlaylistPath;
            Title = entry.Title;
            Poster = entry.Poster;
            SourceUrl = entry.SourceUrl;
            CanResume = entry.CanResumeDownload;
            CanDecrypt = entry.CanDecryptMerge;

            var mergedTs = Path.Combine(entry.Directory, VodCacheSession.MergedFileName);
            MergedTsPath = File.Exists(mergedTs) ? mergedTs : null;
            var hasFastSeek = LocalMediaRemuxer.HasFastSeekFile(entry.Directory);
            CanOptimize = !hasFastSeek
                && !string.IsNullOrWhiteSpace(MergedTsPath)
                && !(entry.PlaylistPath?.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ?? false);

            var state = entry.IsComplete
                ? "整集已完成"
                : $"已下载 {entry.CompletedSegments}/{Math.Max(entry.TotalSegments, entry.CompletedSegments)} 段";
            var merged = hasFastSeek
                ? " · 快速拖动 MP4"
                : !string.IsNullOrWhiteSpace(entry.MergedFilePath)
                    ? (entry.IsComplete ? " · 单文件 TS" : $" · 单文件已合并 {entry.MergedSegments} 段")
                    : entry.CanDecryptMerge
                        ? " · AES 加密，可一键解密合并"
                        : entry.MergeSupported
                            ? ""
                            : " · 加密源（需解密后才能合成单文件）";
            Summary = $"{state}{merged} · {entry.SizeBytes / 1024d / 1024d:0.0} MB · {entry.CreatedAt:yyyy-MM-dd HH:mm}";
        }

        public string Directory { get; }
        public string PlaylistPath { get; }
        public string Title { get; }
        public string Poster { get; }
        public string Summary { get; }
        public string SourceUrl { get; }
        public string? MergedTsPath { get; }
        public bool CanResume { get; }
        public bool CanDecrypt { get; }
        public bool CanOptimize { get; }
        public Visibility ResumeVisibility => CanResume ? Visibility.Visible : Visibility.Collapsed;
        public Visibility DecryptVisibility => CanDecrypt ? Visibility.Visible : Visibility.Collapsed;
        public Visibility OptimizeVisibility => CanOptimize ? Visibility.Visible : Visibility.Collapsed;
    }

    private sealed class LocalMediaCardItem
    {
        private LocalMediaCardItem(
            string id,
            string? libraryId,
            string title,
            string poster,
            IReadOnlyList<string> episodes,
            DateTimeOffset sortTime)
        {
            Id = id;
            LibraryId = libraryId;
            Episodes = episodes;
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
        public DateTimeOffset SortTime { get; }
        public Visibility RemoveVisibility => LibraryId is null ? Visibility.Collapsed : Visibility.Visible;

        internal static LocalMediaCardItem FromImported(LocalMediaItem item, IReadOnlyList<string> episodes) =>
            new(
                item.Id,
                item.Id,
                item.Title,
                item.Poster,
                episodes,
                DateTimeOffset.FromUnixTimeMilliseconds(Math.Max(0, item.SaveTime)));

        internal static LocalMediaCardItem FromCache(
            string id,
            string title,
            string poster,
            IReadOnlyList<string> episodes,
            DateTimeOffset sortTime) =>
            new(id, null, title, poster, episodes, sortTime);
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

        return FormatSize(bytesPerSecond) + "/s";
    }
}
