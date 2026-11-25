using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.App;

public sealed record VodCacheProgress(
    int CompletedSegments,
    int TotalSegments,
    double DownloadedSeconds,
    double TotalSeconds,
    long DownloadedBytes,
    bool IsComplete,
    string? Error = null);

public sealed record VodCacheEntry(
    string Directory,
    string PlaylistPath,
    string Title,
    DateTimeOffset CreatedAt,
    bool IsComplete,
    int CompletedSegments,
    int TotalSegments,
    long SizeBytes,
    string? MergedFilePath = null,
    int MergedSegments = 0,
    bool MergeSupported = true,
    string SourceUrl = "",
    bool CanResumeDownload = false,
    /// <summary>已下完且带 AES-128 密钥，可一键解密合并为 merged.ts。</summary>
    bool CanDecryptMerge = false,
    string Poster = "");

internal sealed record VodCacheBatchDecryptProgress(
    int EntryIndex,
    int TotalEntries,
    VodCacheEntry CurrentEntry,
    double CurrentEntryProgress);

internal sealed record VodCacheBatchDecryptFailure(VodCacheEntry Entry, Exception Error);

internal sealed record VodCacheBatchDecryptResult(
    IReadOnlyList<VodCacheEntry> SucceededEntries,
    IReadOnlyList<VodCacheBatchDecryptFailure> Failures);

public sealed class VodCacheSession
{
    private readonly TaskCompletionSource<string> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal VodCacheSession(string id, string sourceUrl, string title, string directory, int prebufferSeconds)
    {
        Id = id;
        SourceUrl = sourceUrl;
        Title = title;
        Directory = directory;
        PrebufferSeconds = prebufferSeconds;
    }

    public string Id { get; }
    public string SourceUrl { get; }
    public string Title { get; }
    public string Poster { get; internal set; } = "";
    public string Directory { get; }
    public int PrebufferSeconds { get; }
    public string PlaybackUrl { get; internal set; } = "";
    public string OfflinePlaylistPath => Path.Combine(Directory, "offline.m3u8");
    public string MergedFilePath => Path.Combine(Directory, MergedFileName);
    public const string MergedFileName = "merged.ts";
    public Task<string> Ready => _ready.Task;
    public Task DownloadTask { get; internal set; } = Task.CompletedTask;
    public VodCacheProgress Progress { get; internal set; } = new(0, 0, 0, 0, 0, false);
    public event EventHandler<VodCacheProgress>? ProgressChanged;

    internal CancellationTokenSource Cancellation { get; } = new();
    internal List<CacheToken> Tokens { get; } = [];
    internal List<CacheFile> Files { get; } = [];
    internal List<CacheFile> Segments { get; } = [];
    internal object LifecycleGate { get; } = new();
    internal object MergeStateGate { get; } = new();
    internal List<long> MergedSegmentLengths { get; } = [];
    /// <summary>merged.ts 的时间轴状态：分片时间戳不连续时在合并处接成一条连续时间轴。</summary>
    internal MpegTsTimelineStitcher MergeTimeline { get; } = new();
    internal bool KeepFilesOnServiceDispose { get; set; }
    internal bool Detached { get; set; }
    internal bool MergeSupported { get; set; } = true;
    internal int MergedSegments { get; set; }
    internal string MediaPlaylistUrl { get; set; } = "";
    internal DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    internal void MarkReady() => _ready.TrySetResult(PlaybackUrl);
    internal void MarkReadyFailed(Exception error) => _ready.TrySetException(error);

    internal void Report(VodCacheProgress progress)
    {
        Progress = progress;
        ProgressChanged?.Invoke(this, progress);
    }
}

internal sealed class CacheFile
{
    internal required string FileName { get; init; }
    internal required string Url { get; init; }
    internal string? RangeHeader { get; init; }
    /// <summary>下载时优先使用的 Referer；为空则回退到文件 URL 的站点根。</summary>
    internal string? Referrer { get; init; }
    internal double DurationSeconds { get; init; }
    internal bool IsSegment { get; init; }
    internal int SegmentIndex { get; init; } = -1;
    internal required string FullPath { get; init; }
    internal TaskCompletionSource<string> Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal long Length { get; set; }
}

internal abstract record CacheToken
{
    internal sealed record Static(string Line) : CacheToken;
    internal sealed record FileLine(CacheFile File) : CacheToken;
    internal sealed record AssetLine(CacheFile File, string PlaybackLine, string OfflineLine) : CacheToken;
}

internal sealed record CacheMetadata(
    string Title,
    string SourceUrl,
    DateTimeOffset CreatedAt,
    bool IsComplete,
    int CompletedSegments,
    int TotalSegments,
    int MergedSegments = 0,
    string? MergedFileName = null,
    bool MergeSupported = true,
    string? MediaPlaylistUrl = null,
    long[]? MergedSegmentLengths = null,
    string Poster = "");

/// <summary>
/// Windows 点播弱网缓存：独立下载 HLS 分片到磁盘；LibVLC 只从本机服务读取已经完整落盘的文件。
/// 暂停播放不会暂停下载，下载可一直进行到整集完成。
/// </summary>
public sealed class VodCacheService : IDisposable
{
    private static readonly Regex StreamBandwidthRegex = new(@"BANDWIDTH=(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ExtInfRegex = new(@"^#EXTINF:([\d.]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex UriAttributeRegex = new("URI=\"([^\"]+)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SegmentFileNameRegex = new(@"^segment_(\d+)(?:\.|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex EpisodeCacheTitleRegex = new(
        @"^(?<series>.+?)\s+第\s*(?<episode>\d+)\s*集$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly JsonSerializerOptions MetadataJsonOptions = new() { WriteIndented = true };
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(25);
    private const int MaxDownloadAttempts = 3;
    private const int MaxResumePlanAttempts = 3;
    private const int MetadataReplaceAttempts = 8;
    private const int MaxConcurrentSegmentDownloadsPerSession = 4;
    private const int MaxConcurrentSegmentDownloadsTotal = 8;
    private const int MpegTsProbeBytes = 64 * 1024;

    private readonly ConcurrentDictionary<string, VodCacheSession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _decryptMergeGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _segmentDownloadGate = new(
        MaxConcurrentSegmentDownloadsTotal,
        MaxConcurrentSegmentDownloadsTotal);
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _acceptLoop;
    private readonly int _port;
    private bool _disposed;

    /// <summary>
    /// 下载分片/密钥时，正文连续这么久收不到数据就按断流处理并重试。响应头返回后 SendWithTimeoutAsync 的超时不再生效，
    /// 没有这一条时，连接中途静默断开（路由器重拨、休眠唤醒、CDN 卡死）会让下载永远挂住并一直占着并发名额。
    /// </summary>
    internal TimeSpan BodyStallTimeout { get; set; } = RequestTimeout;

    public VodCacheService()
    {
        // 直连优先、代理兜底（SmartRouteHandler）：每个请求现读当前代理设置，切换后下一个请求生效。
        // 先直连（等响应头最多约 5 秒），直连不通才走设置里的代理；代理模式为“直连”或没有可用代理时只走直连。
        var handler = SmartRouteHandler.Create(
            static () => PlaybackProxyRuntime.Current(),
            PlaybackProxyRuntime.Routes,
            static inner =>
            {
                inner.AllowAutoRedirect = true;
                inner.AutomaticDecompression = DecompressionMethods.All;
                inner.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
            });
        // 单次请求超时由 SendWithTimeoutAsync 控制；这里保持无限，避免长整集下载被全局超时打断。
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", PlaybackMediaOptions.AndroidLikeUserAgent);

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
        VodCacheLog.Info(
            $"service-start port={_port} segmentConcurrency={MaxConcurrentSegmentDownloadsPerSession}/{MaxConcurrentSegmentDownloadsTotal}");
    }

    public static bool IsHlsUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https"
        && (uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
            || url.Contains("m3u8", StringComparison.OrdinalIgnoreCase));

    public string? FindCompletedPlaybackPath(
        string cacheDirectory,
        string sourceUrl,
        string title)
    {
        var root = VodCacheSettings.ResolveDirectory(cacheDirectory);
        if (!Directory.Exists(root) || string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        try
        {
            var candidates = new List<(string Path, CacheMetadata Metadata, bool SameSource)>();
            foreach (var directory in EnumerateCacheDirectories(root))
            {
                var metadata = ReadMetadata(directory);
                if (metadata is null
                    || !metadata.IsComplete
                    || !string.Equals(metadata.Title.Trim(), title.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var mergedPath = Path.Combine(
                    directory,
                    metadata.MergedFileName ?? VodCacheSession.MergedFileName);
                string? playbackPath = null;
                if (File.Exists(mergedPath) && new FileInfo(mergedPath).Length > 0)
                {
                    playbackPath = mergedPath;
                }
                else
                {
                    var offline = Path.Combine(directory, "offline.m3u8");
                    if (File.Exists(offline))
                    {
                        playbackPath = offline;
                    }
                }

                if (string.IsNullOrWhiteSpace(playbackPath))
                {
                    continue;
                }

                candidates.Add((
                    playbackPath,
                    metadata,
                    string.Equals(metadata.SourceUrl, sourceUrl, StringComparison.OrdinalIgnoreCase)));
            }

            var match = candidates
                .OrderByDescending(candidate => candidate.SameSource)
                .ThenByDescending(candidate => candidate.Metadata.CreatedAt)
                .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(match.Path))
            {
                return null;
            }

            VodCacheLog.Info(
                $"complete-cache-hit title={title} sourceMatch={match.SameSource} path={match.Path}");
            return match.Path;
        }
        catch (Exception error)
        {
            VodCacheLog.Warn($"find-complete-cache-failed title={title} err={error.Message}");
            return null;
        }
    }

    public async Task<VodCacheSession?> StartAsync(
        string sourceUrl,
        string title,
        string cacheDirectory,
        int prebufferSeconds,
        CancellationToken cancellationToken = default,
        string poster = "")
    {
        if (!IsHlsUrl(sourceUrl))
        {
            return null;
        }

        var root = VodCacheSettings.ResolveDirectory(cacheDirectory);
        Directory.CreateDirectory(root);

        // 从播放器退出后仍在后台下载的同一任务，重新进入时直接接管，避免创建重复缓存目录。
        if (TryAttachActiveSession(root, sourceUrl, title) is { } active)
        {
            if (string.IsNullOrWhiteSpace(active.Poster) && !string.IsNullOrWhiteSpace(poster))
            {
                active.Poster = poster;
            }

            return active;
        }

        // 同一源未下完的缓存：复用目录，接着下并把新分片追加到 merged.ts。
        if (TryFindResumableDirectory(root, sourceUrl, title) is { } existingDirectory)
        {
            var resumed = await TryResumeSessionAsync(
                    existingDirectory,
                    sourceUrl,
                    title,
                    prebufferSeconds,
                    cancellationToken,
                    poster)
                .ConfigureAwait(false);
            if (resumed is not null)
            {
                return resumed;
            }

            // 已有同源进度时绝不新建目录从零下载；保留缓存，等待下次重试。
            throw new InvalidOperationException($"VOD_CACHE_RESUME_FAILED {existingDirectory}");
        }

        var id = Guid.NewGuid().ToString("N");
        var directory = BuildCacheDirectory(root, title, id);
        Directory.CreateDirectory(directory);

        var session = new VodCacheSession(
            id,
            sourceUrl,
            title,
            directory,
            VodCacheSettings.NormalizePrebufferSeconds(prebufferSeconds))
        {
            Poster = poster,
        };
        session.PlaybackUrl = $"http://127.0.0.1:{_port}/vod/{id}/playlist.m3u8";

        try
        {
            VodCacheLog.Info($"start title={title} prebuffer={session.PrebufferSeconds}s url={sourceUrl}");
            var media = await ResolveMediaPlaylistAsync(sourceUrl, cancellationToken).ConfigureAwait(false);
            session.MediaPlaylistUrl = media.Url;
            BuildPlan(session, media.Url, media.Text);
            if (session.Segments.Count == 0)
            {
                throw new InvalidOperationException("HLS_PLAYLIST_HAS_NO_SEGMENTS");
            }

            session.MergeSupported = CanMergeSegments(session);
            await WriteMetadataAsync(session, false, 0, cancellationToken).ConfigureAwait(false);
            WriteOfflinePlaylist(session);
            _sessions[id] = session;
            session.DownloadTask = Task.Run(() => DownloadSessionAsync(session), CancellationToken.None);
            VodCacheLog.Info(
                $"planned id={id} segments={session.Segments.Count} merge={session.MergeSupported} dir={directory}");
            return session;
        }
        catch (Exception error)
        {
            VodCacheLog.Error($"start-failed title={title} url={sourceUrl}", error);
            TryDeleteDirectory(directory);
            throw;
        }
    }

    public async Task CloseAsync(VodCacheSession? session, bool deleteFiles, bool continueDownload)
    {
        if (session is null)
        {
            return;
        }

        session.KeepFilesOnServiceDispose = !deleteFiles;
        if (!continueDownload)
        {
            session.Cancellation.Cancel();
        }

        if (deleteFiles)
        {
            _sessions.TryRemove(session.Id, out _);
            try
            {
                await session.DownloadTask.ConfigureAwait(false);
            }
            catch
            {
                // 取消/下载失败后仍执行清理。
            }

            await DeleteDirectoryWithRetryAsync(session.Directory).ConfigureAwait(false);
            return;
        }

        // 保留缓存时：等当前下载节奏停一下，再把已落盘分片追加合并成单个文件。
        if (!continueDownload)
        {
            try
            {
                await session.DownloadTask.ConfigureAwait(false);
            }
            catch
            {
                // 取消后仍尝试合并已完成分片。
            }

            try
            {
                await FinalizeMergeAsync(session, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                VodCacheLog.Error($"merge-on-close-failed id={session.Id}", error);
            }

            lock (session.LifecycleGate)
            {
                DeleteMergedSegmentFiles(session);
                _sessions.TryRemove(session.Id, out _);
            }
        }
        else if (session.Progress.IsComplete)
        {
            try
            {
                await FinalizeMergeAsync(session, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                VodCacheLog.Error($"merge-on-complete-failed id={session.Id}", error);
            }

            lock (session.LifecycleGate)
            {
                DeleteMergedSegmentFiles(session);
                _sessions.TryRemove(session.Id, out _);
            }
        }
        else
        {
            lock (session.LifecycleGate)
            {
                session.Detached = true;
            }

            _ = session.DownloadTask.ContinueWith(
                _ => RemoveDetachedSession(session),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    public IReadOnlyList<VodCacheEntry> GetEntries(string cacheDirectory)
    {
        var root = VodCacheSettings.ResolveDirectory(cacheDirectory);
        if (!Directory.Exists(root))
        {
            return [];
        }

        var active = _sessions.Values.Select(session => session.Directory).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<VodCacheEntry>();
        foreach (var directory in EnumerateCacheDirectories(root))
        {
            if (active.Contains(directory))
            {
                continue;
            }

            if (!File.Exists(Path.Combine(directory, "cache.json"))
                && !File.Exists(Path.Combine(directory, "offline.m3u8"))
                && !File.Exists(Path.Combine(directory, VodCacheSession.MergedFileName)))
            {
                continue;
            }

            var metadata = ReadMetadata(directory);
            var mergedPath = Path.Combine(directory, metadata?.MergedFileName ?? VodCacheSession.MergedFileName);
            var hasMerged = File.Exists(mergedPath) && new FileInfo(mergedPath).Length > 0;
            if (metadata is not null && hasMerged)
            {
                var mergedCount = Math.Max(0, metadata.MergedSegments);
                var resolvedLengths = ResolveMergedSegmentLengths(
                    directory,
                    mergedPath,
                    mergedCount,
                    metadata.MergedSegmentLengths);
                if (resolvedLengths is not null
                    && !AreEqual(metadata.MergedSegmentLengths, resolvedLengths))
                {
                    metadata = metadata with { MergedSegmentLengths = resolvedLengths };
                    TryWriteMetadata(directory, metadata);
                }

                DeleteMergedSegmentFiles(
                    directory,
                    mergedPath,
                    metadata.MergeSupported,
                    metadata.MergedSegments,
                    metadata.MergedSegmentLengths,
                    Path.GetFileName(directory));
            }

            var playlist = Path.Combine(directory, "offline.m3u8");
            var playPath = hasMerged ? mergedPath : playlist;
            if (!File.Exists(playPath))
            {
                continue;
            }

            var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .ToList();
            var completed = metadata?.CompletedSegments ?? CountExistingSegments(directory);
            var total = metadata?.TotalSegments ?? completed;
            var canResume = !string.IsNullOrWhiteSpace(metadata?.SourceUrl)
                && IsHlsUrl(metadata!.SourceUrl)
                && !(metadata.IsComplete)
                && completed < Math.Max(total, 1);
            var isComplete = metadata?.IsComplete ?? hasMerged;
            var canDecryptMerge = isComplete
                && !hasMerged
                && HlsAesDecryptMerger.LooksDecryptable(directory);

            result.Add(new VodCacheEntry(
                directory,
                playPath,
                metadata?.Title ?? Path.GetFileName(directory),
                metadata?.CreatedAt ?? Directory.GetCreationTimeUtc(directory),
                isComplete,
                completed,
                total,
                files.Sum(file => file.Length),
                hasMerged ? mergedPath : null,
                metadata?.MergedSegments ?? (hasMerged ? completed : 0),
                metadata?.MergeSupported ?? hasMerged,
                metadata?.SourceUrl ?? "",
                canResume,
                canDecryptMerge,
                metadata?.Poster ?? ""));
        }

        return result.OrderByDescending(entry => entry.CreatedAt).ToList();
    }

    /// <summary>
    /// 已完成且含 AES-128 的缓存可一键解密合并为 merged.ts。
    /// </summary>
    public bool CanDecryptMerge(string directory) =>
        !string.IsNullOrWhiteSpace(directory)
        && Directory.Exists(directory)
        && HlsAesDecryptMerger.LooksDecryptable(directory);

    /// <summary>
    /// 解密 AES-128 分片并合并为 merged.ts，成功后清理小分片并更新 cache.json。
    /// 返回合并后的文件路径。
    /// </summary>
    public async Task<string> DecryptAndMergeAsync(
        string directory,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new DirectoryNotFoundException(directory);
        }

        var resolvedDirectory = Path.GetFullPath(directory);
        if (!Directory.Exists(resolvedDirectory))
        {
            throw new DirectoryNotFoundException(resolvedDirectory);
        }

        var gate = _decryptMergeGates.GetOrAdd(resolvedDirectory, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await DecryptAndMergeCoreAsync(resolvedDirectory, progress, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<VodCacheBatchDecryptResult> DecryptAndMergeManyAsync(
        IEnumerable<VodCacheEntry> entries,
        IProgress<VodCacheBatchDecryptProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var candidates = entries
            .Where(entry => entry.IsComplete && entry.CanDecryptMerge && !string.IsNullOrWhiteSpace(entry.Directory))
            .GroupBy(entry => Path.GetFullPath(entry.Directory), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(entry => entry.CreatedAt).First())
            .ToList();
        var succeeded = new List<VodCacheEntry>(candidates.Count);
        var failures = new List<VodCacheBatchDecryptFailure>();

        for (var index = 0; index < candidates.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = candidates[index];
            progress?.Report(new VodCacheBatchDecryptProgress(index, candidates.Count, entry, 0));
            var itemProgress = new InlineProgress<double>(ratio =>
                progress?.Report(new VodCacheBatchDecryptProgress(
                    index,
                    candidates.Count,
                    entry,
                    Math.Clamp(ratio, 0, 1))));
            try
            {
                await DecryptAndMergeAsync(entry.Directory, itemProgress, cancellationToken).ConfigureAwait(false);
                succeeded.Add(entry);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                failures.Add(new VodCacheBatchDecryptFailure(entry, error));
            }

            progress?.Report(new VodCacheBatchDecryptProgress(index, candidates.Count, entry, 1));
        }

        return new VodCacheBatchDecryptResult(succeeded, failures);
    }

    private async Task<string> DecryptAndMergeCoreAsync(
        string directory,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        if (_sessions.Values.Any(session => session.Directory.Equals(directory, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("CACHE_ENTRY_IS_ACTIVE");
        }

        var existingMerged = Path.Combine(directory, VodCacheSession.MergedFileName);
        if (File.Exists(existingMerged) && new FileInfo(existingMerged).Length > 0)
        {
            return existingMerged;
        }

        if (!HlsAesDecryptMerger.LooksDecryptable(directory))
        {
            throw new InvalidOperationException("CACHE_NOT_DECRYPTABLE");
        }

        VodCacheLog.Info($"decrypt-merge-start dir={directory}");
        var (mergedPath, lengths) = await HlsAesDecryptMerger.DecryptAndMergeAsync(directory, progress, cancellationToken)
            .ConfigureAwait(false);

        var metadata = ReadMetadata(directory);
        var completed = metadata?.CompletedSegments ?? CountExistingSegments(directory);
        var total = metadata?.TotalSegments ?? completed;
        var mergedSegments = lengths.Length;
        var mergedInfo = new FileInfo(mergedPath);

        var updated = new CacheMetadata(
            Title: metadata?.Title ?? Path.GetFileName(directory),
            SourceUrl: metadata?.SourceUrl ?? "",
            CreatedAt: metadata?.CreatedAt ?? Directory.GetCreationTimeUtc(directory),
            IsComplete: true,
            CompletedSegments: Math.Max(completed, mergedSegments),
            TotalSegments: Math.Max(total, mergedSegments),
            MergedSegments: mergedSegments,
            MergedFileName: VodCacheSession.MergedFileName,
            MergeSupported: true,
            MediaPlaylistUrl: metadata?.MediaPlaylistUrl,
            MergedSegmentLengths: lengths,
            Poster: metadata?.Poster ?? "");
        TryWriteMetadata(directory, updated);
        DeleteMergedSegmentFiles(
            directory,
            mergedPath,
            mergeSupported: true,
            mergedSegments,
            lengths,
            Path.GetFileName(directory));
        // 密钥文件也可删：单文件已是明文。
        TryDeleteKeyAssets(directory);
        VodCacheLog.Info(
            $"decrypt-merge-done dir={directory} segs={mergedSegments} size={mergedInfo.Length}");
        return mergedPath;
    }

    /// <summary>按标题查找已完成缓存目录（含加密多片完成）。</summary>
    public string? FindCompletedDirectory(string cacheDirectory, string sourceUrl, string title)
    {
        var root = VodCacheSettings.ResolveDirectory(cacheDirectory);
        if (!Directory.Exists(root) || string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        try
        {
            var candidates = new List<(string Directory, CacheMetadata Metadata, bool SameSource, bool HasMerged)>();
            foreach (var directory in EnumerateCacheDirectories(root))
            {
                var metadata = ReadMetadata(directory);
                if (metadata is null
                    || !metadata.IsComplete
                    || !string.Equals(metadata.Title.Trim(), title.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var mergedPath = Path.Combine(directory, metadata.MergedFileName ?? VodCacheSession.MergedFileName);
                var hasMerged = File.Exists(mergedPath) && new FileInfo(mergedPath).Length > 0;
                var offline = Path.Combine(directory, "offline.m3u8");
                if (!hasMerged && !File.Exists(offline))
                {
                    continue;
                }

                candidates.Add((
                    directory,
                    metadata,
                    string.Equals(metadata.SourceUrl, sourceUrl, StringComparison.OrdinalIgnoreCase),
                    hasMerged));
            }

            var match = candidates
                .OrderByDescending(candidate => candidate.SameSource)
                .ThenByDescending(candidate => candidate.HasMerged)
                .ThenByDescending(candidate => candidate.Metadata.CreatedAt)
                .FirstOrDefault();
            return string.IsNullOrWhiteSpace(match.Directory) ? null : match.Directory;
        }
        catch (Exception error)
        {
            VodCacheLog.Warn($"find-complete-dir-failed title={title} err={error.Message}");
            return null;
        }
    }

    private static void TryDeleteKeyAssets(string directory)
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, "asset_*", SearchOption.TopDirectoryOnly))
            {
                TryDeleteFile(path);
            }
        }
        catch
        {
        }
    }

    public async Task DeleteEntryAsync(string directory, string cacheDirectory)
    {
        var root = VodCacheSettings.ResolveDirectory(cacheDirectory);
        var target = Path.GetFullPath(directory);
        var parent = Path.GetDirectoryName(target);
        var isTopLevelEntry = parent is not null && PathsEqual(parent, root);
        var groupDirectory = !isTopLevelEntry
            && parent is not null
            && Path.GetDirectoryName(parent) is { } grandparent
            && PathsEqual(grandparent, root)
                ? parent
                : null;
        if (!isTopLevelEntry && groupDirectory is null)
        {
            throw new InvalidOperationException("CACHE_ENTRY_OUTSIDE_ROOT");
        }

        if (_sessions.Values.Any(session => PathsEqual(session.Directory, target)))
        {
            throw new InvalidOperationException("CACHE_ENTRY_IS_ACTIVE");
        }

        if (!File.Exists(Path.Combine(target, "cache.json"))
            && !File.Exists(Path.Combine(target, "offline.m3u8")))
        {
            throw new InvalidOperationException("NOT_ORION_CACHE_ENTRY");
        }

        await DeleteDirectoryWithRetryAsync(target).ConfigureAwait(false);
        if (groupDirectory is not null)
        {
            TryDeleteEmptyDirectory(groupDirectory);
        }
    }

    public async Task<int> ClearAsync(string cacheDirectory)
    {
        var root = VodCacheSettings.ResolveDirectory(cacheDirectory);
        if (!Directory.Exists(root))
        {
            return 0;
        }

        var active = _sessions.Values.Select(session => session.Directory).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var deleted = 0;
        var cacheDirectories = EnumerateCacheDirectories(root);
        var groupDirectories = cacheDirectories
            .Select(Path.GetDirectoryName)
            .Where(parent => !string.IsNullOrWhiteSpace(parent) && !PathsEqual(parent!, root))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var directory in cacheDirectories)
        {
            if (active.Contains(directory))
            {
                continue;
            }

            if (!File.Exists(Path.Combine(directory, "cache.json"))
                && !File.Exists(Path.Combine(directory, "offline.m3u8")))
            {
                continue;
            }

            await DeleteDirectoryWithRetryAsync(directory).ConfigureAwait(false);
            deleted++;
        }

        foreach (var groupDirectory in groupDirectories)
        {
            TryDeleteEmptyDirectory(groupDirectory);
        }

        return deleted;
    }

    private async Task<(string Url, string Text)> ResolveMediaPlaylistAsync(
        string url,
        CancellationToken cancellationToken,
        int depth = 0)
    {
        if (depth > 3)
        {
            throw new InvalidOperationException("HLS_MASTER_DEPTH_EXCEEDED");
        }

        // 远程源可能经代理 302 到 CDN；必须用最终 URL 解析相对分片/密钥，否则会拼成代理路径导致 404。
        var fetched = await FetchTextAsync(url, cancellationToken).ConfigureAwait(false);
        var resolvedUrl = fetched.Url;
        var text = fetched.Text;
        if (!text.Contains("#EXTM3U", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("NOT_HLS_PLAYLIST");
        }

        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var variants = new List<(long Bandwidth, string Url)>();
        for (var index = 0; index < lines.Length - 1; index++)
        {
            var line = lines[index].Trim();
            if (!line.StartsWith("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var uriLine = lines
                .Skip(index + 1)
                .Select(value => value.Trim())
                .FirstOrDefault(value => !string.IsNullOrEmpty(value) && !value.StartsWith('#'));
            if (string.IsNullOrEmpty(uriLine))
            {
                continue;
            }

            var match = StreamBandwidthRegex.Match(line);
            var bandwidth = match.Success && long.TryParse(match.Groups[1].Value, out var parsed) ? parsed : long.MaxValue;
            variants.Add((bandwidth, ResolveUrl(resolvedUrl, uriLine)));
        }

        if (variants.Count == 0)
        {
            return (resolvedUrl, text);
        }

        // 弱网缓存选择最低声明码率，优先保证能持续下载在播放进度之前。
        var selected = variants.OrderBy(variant => variant.Bandwidth).First();
        return await ResolveMediaPlaylistAsync(selected.Url, cancellationToken, depth + 1).ConfigureAwait(false);
    }

    private void BuildPlan(VodCacheSession session, string playlistUrl, string playlistText)
    {
        var lines = playlistText.Split(['\r', '\n'], StringSplitOptions.None);
        var segmentIndex = 0;
        var assetIndex = 0;
        double pendingDuration = 0;
        string? pendingRange = null;
        long nextImplicitOffset = 0;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line) || line.Equals("#EXT-X-ENDLIST", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var extInf = ExtInfRegex.Match(line);
            if (extInf.Success)
            {
                _ = double.TryParse(extInf.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out pendingDuration);
                session.Tokens.Add(new CacheToken.Static(line));
                continue;
            }

            if (line.StartsWith("#EXT-X-BYTERANGE:", StringComparison.OrdinalIgnoreCase))
            {
                pendingRange = BuildRangeHeader(line["#EXT-X-BYTERANGE:".Length..], ref nextImplicitOffset);
                continue;
            }

            if (line.StartsWith("#EXT-X-KEY:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("#EXT-X-MAP:", StringComparison.OrdinalIgnoreCase))
            {
                var match = UriAttributeRegex.Match(line);
                if (!match.Success)
                {
                    session.Tokens.Add(new CacheToken.Static(line));
                    continue;
                }

                var absolute = ResolveUrl(playlistUrl, match.Groups[1].Value);
                var fileName = $"asset_{assetIndex++:D3}{GetExtension(absolute, ".bin")}";
                var file = new CacheFile
                {
                    FileName = fileName,
                    Url = absolute,
                    Referrer = PlaylistReferrerOf(playlistUrl),
                    FullPath = Path.Combine(session.Directory, fileName),
                    IsSegment = false,
                };
                session.Files.Add(file);
                var playbackLine = UriAttributeRegex.Replace(line, $"URI=\"http://127.0.0.1:{_port}/vod/{session.Id}/file/{fileName}\"", 1);
                var offlineLine = UriAttributeRegex.Replace(line, $"URI=\"{fileName}\"", 1);
                session.Tokens.Add(new CacheToken.AssetLine(file, playbackLine, offlineLine));
                continue;
            }

            if (line.StartsWith('#'))
            {
                session.Tokens.Add(new CacheToken.Static(line));
                continue;
            }

            var absoluteSegment = ResolveUrl(playlistUrl, line);
            var currentSegmentIndex = segmentIndex++;
            var segmentName = $"segment_{currentSegmentIndex:D5}{GetExtension(absoluteSegment, ".ts")}";
            var segment = new CacheFile
            {
                FileName = segmentName,
                Url = absoluteSegment,
                RangeHeader = pendingRange,
                Referrer = PlaylistReferrerOf(playlistUrl),
                DurationSeconds = pendingDuration > 0 ? pendingDuration : 6,
                FullPath = Path.Combine(session.Directory, segmentName),
                IsSegment = true,
                SegmentIndex = currentSegmentIndex,
            };
            session.Files.Add(segment);
            session.Segments.Add(segment);
            session.Tokens.Add(new CacheToken.FileLine(segment));
            pendingDuration = 0;
            pendingRange = null;
        }
    }

    private async Task DownloadSessionAsync(VodCacheSession session)
    {
        var token = session.Cancellation.Token;
        var totalSeconds = session.Segments.Sum(segment => segment.DurationSeconds);
        var completedSegments = 0;
        double downloadedSeconds = 0;
        long downloadedBytes = 0;

        // 续传：已合并分片从 merged.ts 的长度索引恢复，即使原始小分片已删除也不重新请求。
        var mergedLengths = GetMergedSegmentLengthsSnapshot(session);
        var mergedCount = Math.Min(session.MergedSegments, mergedLengths.Length);
        for (var index = 0; index < mergedCount; index++)
        {
            var segment = session.Segments[index];
            segment.Length = mergedLengths[index];
            segment.Completed.TrySetResult(segment.FullPath);
            completedSegments++;
            downloadedSeconds += segment.DurationSeconds;
            downloadedBytes += segment.Length;
        }

        // 兼容退出/崩溃前已经下载、但尚未追加进 merged.ts 的连续小分片。
        for (var index = completedSegments; index < session.Segments.Count; index++)
        {
            var segment = session.Segments[index];
            if (!File.Exists(segment.FullPath))
            {
                break;
            }

            segment.Length = new FileInfo(segment.FullPath).Length;
            segment.Completed.TrySetResult(segment.FullPath);
            completedSegments++;
            downloadedSeconds += segment.DurationSeconds;
            downloadedBytes += segment.Length;
        }

        if (completedSegments > 0)
        {
            WriteOfflinePlaylist(session);
            session.Report(new VodCacheProgress(
                completedSegments,
                session.Segments.Count,
                downloadedSeconds,
                totalSeconds,
                downloadedBytes,
                false));
            if (downloadedSeconds >= session.PrebufferSeconds || completedSegments >= 1)
            {
                session.MarkReady();
            }
        }

        try
        {
            foreach (var file in session.Files.Where(file => !file.IsSegment && !file.Completed.Task.IsCompletedSuccessfully))
            {
                if (File.Exists(file.FullPath))
                {
                    file.Length = new FileInfo(file.FullPath).Length;
                    file.Completed.TrySetResult(file.FullPath);
                    continue;
                }

                await DownloadFileAsync(file, token).ConfigureAwait(false);
            }

            using var segmentPipelineCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            var segmentToken = segmentPipelineCancellation.Token;
            var pendingSegments = new Dictionary<int, Task>();
            var nextSegmentToSchedule = completedSegments;

            try
            {
                while (completedSegments < session.Segments.Count)
                {
                    token.ThrowIfCancellationRequested();
                    ScheduleSegmentDownloads(
                        session,
                        pendingSegments,
                        ref nextSegmentToSchedule,
                        segmentToken);

                    if (!pendingSegments.TryGetValue(completedSegments, out var nextInOrder))
                    {
                        throw new InvalidOperationException($"SEGMENT_PIPELINE_GAP {completedSegments}");
                    }

                    await nextInOrder.ConfigureAwait(false);

                    // Commit only the contiguous prefix. Later segments may finish first, but the
                    // offline playlist and merged.ts must always remain in media order.
                    while (pendingSegments.TryGetValue(completedSegments, out var completedTask)
                           && completedTask.IsCompletedSuccessfully)
                    {
                        pendingSegments.Remove(completedSegments);
                        var segment = session.Segments[completedSegments];
                        completedSegments++;
                        downloadedSeconds += segment.DurationSeconds;
                        downloadedBytes += segment.Length;
                    }

                    // Refill the window before disk bookkeeping so network requests continue while
                    // the contiguous batch is merged and metadata is persisted.
                    ScheduleSegmentDownloads(
                        session,
                        pendingSegments,
                        ref nextSegmentToSchedule,
                        segmentToken);

                    if (session.MergeSupported)
                    {
                        await AppendSegmentsToMergedAsync(session, upToExclusive: completedSegments, token).ConfigureAwait(false);
                    }

                    WriteOfflinePlaylist(session);
                    await WriteMetadataAsync(session, false, completedSegments, token).ConfigureAwait(false);

                    var progress = new VodCacheProgress(
                        completedSegments,
                        session.Segments.Count,
                        downloadedSeconds,
                        totalSeconds,
                        downloadedBytes,
                        false);
                    session.Report(progress);
                    if (downloadedSeconds >= session.PrebufferSeconds || completedSegments >= 1)
                    {
                        session.MarkReady();
                    }
                }
            }
            catch
            {
                segmentPipelineCancellation.Cancel();
                await ObservePendingDownloadsAsync(pendingSegments.Values).ConfigureAwait(false);
                throw;
            }

            WriteOfflinePlaylist(session, complete: true);
            if (session.MergeSupported)
            {
                await AppendSegmentsToMergedAsync(session, upToExclusive: completedSegments, token).ConfigureAwait(false);
            }

            await WriteMetadataAsync(session, true, completedSegments, token).ConfigureAwait(false);
            session.Report(new VodCacheProgress(
                completedSegments,
                session.Segments.Count,
                downloadedSeconds,
                totalSeconds,
                downloadedBytes,
                true));
            session.MarkReady();
            VodCacheLog.Info(
                $"complete id={session.Id} title={session.Title} segs={completedSegments}/{session.Segments.Count} merged={session.MergedSegments} discontinuities={session.MergeTimeline.Discontinuities} bytes={downloadedBytes}");

            RemoveDetachedSession(session);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            VodCacheLog.Info($"cancelled id={session.Id} title={session.Title} segs={completedSegments}/{session.Segments.Count}");
            try
            {
                if (session.MergeSupported && completedSegments > session.MergedSegments)
                {
                    await AppendSegmentsToMergedAsync(session, upToExclusive: completedSegments, CancellationToken.None)
                        .ConfigureAwait(false);
                    await WriteMetadataAsync(session, false, completedSegments, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception mergeError)
            {
                VodCacheLog.Error($"merge-on-cancel-failed id={session.Id}", mergeError);
            }

            if (!session.Ready.IsCompleted)
            {
                session.MarkReadyFailed(new OperationCanceledException("VOD_CACHE_CANCELLED"));
            }
        }
        catch (Exception error)
        {
            VodCacheLog.Error(
                $"download-failed id={session.Id} title={session.Title} segs={completedSegments}/{session.Segments.Count}",
                error);
            foreach (var file in session.Files.Where(file => !file.Completed.Task.IsCompleted))
            {
                file.Completed.TrySetException(error);
            }

            try
            {
                if (session.MergeSupported && completedSegments > session.MergedSegments)
                {
                    await AppendSegmentsToMergedAsync(session, upToExclusive: completedSegments, CancellationToken.None)
                        .ConfigureAwait(false);
                    await WriteMetadataAsync(session, false, completedSegments, CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            catch
            {
                // 合并失败不影响错误上报。
            }

            session.Report(new VodCacheProgress(
                completedSegments,
                session.Segments.Count,
                downloadedSeconds,
                totalSeconds,
                downloadedBytes,
                false,
                error.Message));
            if (completedSegments > 0)
            {
                session.MarkReady();
            }
            else
            {
                session.MarkReadyFailed(error);
            }

            RemoveDetachedSession(session);
        }
    }

    private async Task FinalizeMergeAsync(VodCacheSession session, CancellationToken cancellationToken)
    {
        if (!session.MergeSupported)
        {
            await WriteMetadataAsync(session, session.Progress.IsComplete, session.Progress.CompletedSegments, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var completed = Math.Max(session.MergedSegments, session.Progress.CompletedSegments);
        while (completed < session.Segments.Count && File.Exists(session.Segments[completed].FullPath))
        {
            completed++;
        }
        if (completed <= 0)
        {
            return;
        }

        await AppendSegmentsToMergedAsync(session, upToExclusive: completed, cancellationToken).ConfigureAwait(false);
        await WriteMetadataAsync(session, session.Progress.IsComplete || completed >= session.Segments.Count, completed, cancellationToken)
            .ConfigureAwait(false);
        VodCacheLog.Info(
            $"merge-finalized id={session.Id} merged={session.MergedSegments}/{session.Segments.Count} path={session.MergedFilePath}");
    }

    /// <summary>
    /// 把 segment_[merged..upTo) 追加到 merged.ts。
    /// MPEG-TS 可按包拼接，但分片之间时间戳可能重新起算（插播、换片源、逐片切片），
    /// 所以追加前先把每一片接到同一条时间轴上，否则播放器只能读到最后一段连续区间的时长。
    /// 加密分片/非 TS 会在 CanMergeSegments 中禁用。
    /// </summary>
    private static async Task AppendSegmentsToMergedAsync(
        VodCacheSession session,
        int upToExclusive,
        CancellationToken cancellationToken)
    {
        if (!session.MergeSupported)
        {
            return;
        }

        upToExclusive = Math.Min(upToExclusive, session.Segments.Count);
        int firstUnmerged;
        long committedLength;
        long lastMergedLength;
        lock (session.MergeStateGate)
        {
            firstUnmerged = Math.Min(session.MergedSegments, session.MergedSegmentLengths.Count);
            if (session.MergedSegmentLengths.Count > firstUnmerged)
            {
                session.MergedSegmentLengths.RemoveRange(
                    firstUnmerged,
                    session.MergedSegmentLengths.Count - firstUnmerged);
            }
            session.MergedSegments = firstUnmerged;
            committedLength = session.MergedSegmentLengths.Sum();
            lastMergedLength = firstUnmerged > 0 ? session.MergedSegmentLengths[firstUnmerged - 1] : 0;
        }

        if (upToExclusive <= firstUnmerged)
        {
            return;
        }

        var timeline = session.MergeTimeline;
        if (timeline.SegmentCount != firstUnmerged)
        {
            // 续传或上次追加中断后，内存里的时间轴状态与 merged.ts 对不上：从已合并的最后一片恢复。
            var lastMerged = await ReadMergedSegmentAsync(
                    session.MergedFilePath,
                    committedLength - lastMergedLength,
                    lastMergedLength,
                    cancellationToken)
                .ConfigureAwait(false);
            timeline.Resume(firstUnmerged, lastMerged);
        }

        await using var output = new FileStream(
            session.MergedFilePath,
            FileMode.OpenOrCreate,
            FileAccess.Write,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (output.Length < committedLength)
        {
            throw new IOException("MERGED_FILE_IS_TRUNCATED");
        }
        if (output.Length != committedLength)
        {
            output.SetLength(committedLength);
        }
        output.Position = committedLength;

        for (var index = firstUnmerged; index < upToExclusive; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var segment = session.Segments[index];
            if (!File.Exists(segment.FullPath))
            {
                break;
            }

            long segmentLength;
            await using (var input = new FileStream(
                             segment.FullPath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.ReadWrite,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                segmentLength = input.Length;
                if (segmentLength <= MpegTsTimelineStitcher.MaxSegmentBytes)
                {
                    var bytes = new byte[segmentLength];
                    await input.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
                    timeline.Restamp(bytes);
                    await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    // 超大的单个分片不读进内存，按原样拷贝。
                    await input.CopyToAsync(output, 64 * 1024, cancellationToken).ConfigureAwait(false);
                    timeline.Skip();
                }
            }

            segment.Length = segmentLength;
            lock (session.MergeStateGate)
            {
                session.MergedSegmentLengths.Add(segmentLength);
                session.MergedSegments = index + 1;
            }
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>读出 merged.ts 中的一个已合并分片；读不到（文件缺失/被截断/分片过大）时返回空。</summary>
    private static async Task<byte[]> ReadMergedSegmentAsync(
        string mergedFilePath,
        long offset,
        long length,
        CancellationToken cancellationToken)
    {
        if (offset < 0 || length <= 0 || length > MpegTsTimelineStitcher.MaxSegmentBytes || !File.Exists(mergedFilePath))
        {
            return [];
        }

        await using var input = new FileStream(
            mergedFilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length < offset + length)
        {
            return [];
        }

        input.Position = offset;
        var bytes = new byte[length];
        await input.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return bytes;
    }

    private void ScheduleSegmentDownloads(
        VodCacheSession session,
        IDictionary<int, Task> pendingSegments,
        ref int nextSegmentToSchedule,
        CancellationToken cancellationToken)
    {
        while (nextSegmentToSchedule < session.Segments.Count
               && pendingSegments.Count < MaxConcurrentSegmentDownloadsPerSession)
        {
            var index = nextSegmentToSchedule++;
            pendingSegments[index] = EnsureSegmentDownloadedAsync(
                session.Segments[index],
                session.MergeSupported,
                cancellationToken);
        }
    }

    private async Task EnsureSegmentDownloadedAsync(
        CacheFile segment,
        bool requireMpegTs,
        CancellationToken cancellationToken)
    {
        if (TryUseExistingFile(segment))
        {
            return;
        }

        await _segmentDownloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!TryUseExistingFile(segment))
            {
                await DownloadFileAsync(segment, cancellationToken, requireMpegTs).ConfigureAwait(false);
            }
        }
        finally
        {
            _segmentDownloadGate.Release();
        }
    }

    private static bool TryUseExistingFile(CacheFile file)
    {
        if (file.Completed.Task.IsCompletedSuccessfully)
        {
            return true;
        }

        if (!File.Exists(file.FullPath))
        {
            return false;
        }

        file.Length = new FileInfo(file.FullPath).Length;
        file.Completed.TrySetResult(file.FullPath);
        return true;
    }

    private static async Task ObservePendingDownloadsAsync(IEnumerable<Task> pendingDownloads)
    {
        try
        {
            await Task.WhenAll(pendingDownloads).ConfigureAwait(false);
        }
        catch
        {
            // The original ordered-pipeline failure is reported by the caller.
        }
    }

    private static bool CanMergeSegments(VodCacheSession session)
    {
        // 有 EXT-X-KEY 的加密流不能直接二进制拼接。
        if (session.Tokens.OfType<CacheToken.AssetLine>()
            .Any(token => token.PlaybackLine.Contains("#EXT-X-KEY", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (session.Tokens.OfType<CacheToken.Static>()
            .Any(token => token.Line.StartsWith("#EXT-X-KEY:", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        // 仅合并纯 TS 分片；fMP4 (.m4s/.mp4) 需要 init map，不做盲拼。
        return session.Segments.Count > 0
            && session.Segments.All(segment =>
                segment.FileName.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
                || segment.FileName.EndsWith(".m2ts", StringComparison.OrdinalIgnoreCase));
    }

    private VodCacheSession? TryAttachActiveSession(string root, string sourceUrl, string title)
    {
        foreach (var session in _sessions.Values)
        {
            if (!string.Equals(session.SourceUrl, sourceUrl, StringComparison.OrdinalIgnoreCase)
                || !CacheTitlesEqual(session.Title, title)
                || !HasCacheRoot(session.Directory, root))
            {
                continue;
            }

            lock (session.LifecycleGate)
            {
                if (!_sessions.TryGetValue(session.Id, out var current)
                    || !ReferenceEquals(current, session)
                    || session.Cancellation.IsCancellationRequested)
                {
                    continue;
                }

                if (session.Progress.Error is not null)
                {
                    if (session.DownloadTask.IsCompleted)
                    {
                        _sessions.TryRemove(session.Id, out _);
                    }

                    continue;
                }

                if (session.Progress.IsComplete || session.DownloadTask.IsCompleted)
                {
                    if (session.Detached)
                    {
                        DeleteMergedSegmentFiles(session);
                        _sessions.TryRemove(session.Id, out _);
                        continue;
                    }

                    // 完成但仍被播放器持有时也复用同一会话，避免并发进入再建目录。
                    session.Detached = false;
                    return session;
                }

                session.Detached = false;
                VodCacheLog.Info(
                    $"reattach id={session.Id} title={session.Title} existing={session.Progress.CompletedSegments}/{session.Progress.TotalSegments} dir={session.Directory}");
                return session;
            }
        }

        return null;
    }

    private static bool HasCacheRoot(string sessionDirectory, string root)
    {
        try
        {
            var relative = Path.GetRelativePath(
                Path.GetFullPath(root),
                Path.GetFullPath(sessionDirectory));
            return relative != "."
                && relative != ".."
                && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !Path.IsPathRooted(relative);
        }
        catch
        {
            return false;
        }
    }

    private void RemoveDetachedSession(VodCacheSession session)
    {
        lock (session.LifecycleGate)
        {
            if (!session.Detached)
            {
                return;
            }

            DeleteMergedSegmentFiles(session);
            _sessions.TryRemove(session.Id, out _);
        }
    }

    private static void DeleteMergedSegmentFiles(VodCacheSession session)
    {
        var lengths = GetMergedSegmentLengthsSnapshot(session);
        DeleteMergedSegmentFiles(
            session.Directory,
            session.MergedFilePath,
            session.MergeSupported,
            session.MergedSegments,
            lengths,
            session.Id);
    }

    private static void DeleteMergedSegmentFiles(
        string directory,
        string mergedFilePath,
        bool mergeSupported,
        int mergedSegments,
        IReadOnlyList<long>? mergedSegmentLengths,
        string logId)
    {
        if (!mergeSupported
            || mergedSegments <= 0
            || mergedSegmentLengths is null
            || mergedSegmentLengths.Count < mergedSegments
            || !File.Exists(mergedFilePath))
        {
            return;
        }

        string[] segmentFiles;
        try
        {
            segmentFiles = Directory.GetFiles(directory, "segment_*.*", SearchOption.TopDirectoryOnly);
        }
        catch
        {
            return;
        }

        foreach (var path in segmentFiles)
        {
            var match = SegmentFileNameRegex.Match(Path.GetFileName(path));
            if (match.Success
                && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                && index >= 0
                && index < mergedSegments)
            {
                TryDeleteFile(path);
            }
        }

        var remaining = segmentFiles.Count(File.Exists);
        VodCacheLog.Info(
            $"segments-cleaned id={logId} deleted={segmentFiles.Length - remaining} remaining={remaining} merged={mergedFilePath}");
    }

    private string? TryFindResumableDirectory(string root, string sourceUrl, string title)
    {
        try
        {
            foreach (var directory in EnumerateCacheDirectories(root)
                         .OrderByDescending(Directory.GetCreationTimeUtc))
            {
                if (_sessions.Values.Any(session => session.Directory.Equals(directory, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var metadata = ReadMetadata(directory);
                if (metadata is null
                    || metadata.IsComplete
                    || string.IsNullOrWhiteSpace(metadata.SourceUrl)
                    || !string.Equals(metadata.SourceUrl, sourceUrl, StringComparison.OrdinalIgnoreCase)
                    || !CacheTitlesEqual(metadata.Title, title))
                {
                    continue;
                }

                if (metadata.CompletedSegments > 0 && metadata.CompletedSegments < Math.Max(metadata.TotalSegments, 1))
                {
                    return directory;
                }
            }
        }
        catch (Exception error)
        {
            VodCacheLog.Warn($"find-resume-failed err={error.Message}");
        }

        return null;
    }

    private async Task<VodCacheSession?> TryResumeSessionAsync(
        string directory,
        string sourceUrl,
        string title,
        int prebufferSeconds,
        CancellationToken cancellationToken,
        string poster)
    {
        try
        {
            var metadata = ReadMetadata(directory);
            if (metadata is null)
            {
                return null;
            }

            var id = Path.GetFileName(directory);
            if (id.Length > 8)
            {
                // 目录名形如 20260718-...-d47e6b06，取末尾短 id 仅用于本地服务路径。
                var dash = id.LastIndexOf('-');
                if (dash >= 0 && dash < id.Length - 1)
                {
                    id = id[(dash + 1)..];
                }
            }

            id = Guid.NewGuid().ToString("N")[..8] + id;
            var session = new VodCacheSession(
                id,
                sourceUrl,
                string.IsNullOrWhiteSpace(title) ? metadata.Title : title,
                directory,
                VodCacheSettings.NormalizePrebufferSeconds(prebufferSeconds))
            {
                MergeSupported = metadata.MergeSupported,
                MediaPlaylistUrl = metadata.MediaPlaylistUrl ?? "",
                CreatedAt = metadata.CreatedAt,
                Poster = string.IsNullOrWhiteSpace(poster) ? metadata.Poster : poster,
            };
            session.PlaybackUrl = $"http://127.0.0.1:{_port}/vod/{session.Id}/playlist.m3u8";

            var mediaUrl = string.IsNullOrWhiteSpace(metadata.MediaPlaylistUrl) ? sourceUrl : metadata.MediaPlaylistUrl;
            var media = await ResolveResumeMediaPlaylistAsync(mediaUrl, cancellationToken).ConfigureAwait(false);
            session.MediaPlaylistUrl = media.Url;
            BuildPlan(session, media.Url, media.Text);
            if (session.Segments.Count == 0)
            {
                return null;
            }

            session.MergeSupported = metadata.MergeSupported && CanMergeSegments(session);
            RestoreMergedState(session, metadata);

            var existingSegments = CountExistingSegments(directory);
            var completedSegments = Math.Min(
                session.Segments.Count,
                Math.Max(session.MergedSegments, Math.Min(metadata.CompletedSegments, session.Segments.Count)));
            await WriteMetadataAsync(session, false, completedSegments, cancellationToken).ConfigureAwait(false);
            WriteOfflinePlaylist(session);
            _sessions[session.Id] = session;
            session.DownloadTask = Task.Run(() => DownloadSessionAsync(session), CancellationToken.None);
            VodCacheLog.Info(
                $"resume id={session.Id} title={session.Title} existing={existingSegments}/{session.Segments.Count} merged={session.MergedSegments} dir={directory}");
            return session;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            VodCacheLog.Error($"resume-failed dir={directory}", error);
            return null;
        }
    }

    private async Task<(string Url, string Text)> ResolveResumeMediaPlaylistAsync(
        string url,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= MaxResumePlanAttempts; attempt++)
        {
            try
            {
                return await ResolveMediaPlaylistAsync(url, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                lastError = error;
                VodCacheLog.Warn(
                    $"resume-plan-retry attempt={attempt}/{MaxResumePlanAttempts} url={url} err={error.GetType().Name}: {error.Message}");
                if (attempt < MaxResumePlanAttempts)
                {
                    await Task.Delay(500 * attempt, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        throw new InvalidOperationException("VOD_CACHE_RESUME_PLAYLIST_FAILED", lastError);
    }

    private static int CountExistingSegments(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "segment_*.*")
                .Count(path => !path.EndsWith(".part", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return 0;
        }
    }

    private static void RestoreMergedState(VodCacheSession session, CacheMetadata metadata)
    {
        if (!session.MergeSupported || !File.Exists(session.MergedFilePath))
        {
            session.MergedSegments = 0;
            return;
        }

        var requested = Math.Clamp(metadata.MergedSegments, 0, session.Segments.Count);
        var lengths = ResolveMergedSegmentLengths(
            session.Directory,
            session.MergedFilePath,
            requested,
            metadata.MergedSegmentLengths);
        if (lengths is null)
        {
            session.MergedSegments = 0;
            return;
        }

        lock (session.MergeStateGate)
        {
            session.MergedSegmentLengths.Clear();
            session.MergedSegmentLengths.AddRange(lengths);
            session.MergedSegments = lengths.Length;
        }
    }

    private static long[]? ResolveMergedSegmentLengths(
        string directory,
        string mergedFilePath,
        int mergedSegments,
        IReadOnlyList<long>? storedLengths)
    {
        if (mergedSegments <= 0)
        {
            return [];
        }

        if (!File.Exists(mergedFilePath))
        {
            return null;
        }

        var mergedLength = new FileInfo(mergedFilePath).Length;
        if (storedLengths is not null
            && storedLengths.Count >= mergedSegments
            && storedLengths.Take(mergedSegments).All(length => length > 0)
            && storedLengths.Take(mergedSegments).Sum() <= mergedLength)
        {
            return storedLengths.Take(mergedSegments).ToArray();
        }

        var lengths = new long[mergedSegments];
        var found = new bool[mergedSegments];
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, "segment_*.*", SearchOption.TopDirectoryOnly))
            {
                if (path.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var match = SegmentFileNameRegex.Match(Path.GetFileName(path));
                if (!match.Success
                    || !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                    || index < 0
                    || index >= mergedSegments)
                {
                    continue;
                }

                lengths[index] = new FileInfo(path).Length;
                found[index] = true;
            }
        }
        catch
        {
            return null;
        }

        return found.All(value => value) && lengths.All(length => length > 0) && lengths.Sum() <= mergedLength
            ? lengths
            : null;
    }

    private static long[] GetMergedSegmentLengthsSnapshot(VodCacheSession session)
    {
        lock (session.MergeStateGate)
        {
            return session.MergedSegmentLengths.Take(session.MergedSegments).ToArray();
        }
    }

    private async Task DownloadFileAsync(
        CacheFile file,
        CancellationToken cancellationToken,
        bool requireMpegTs = false)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= MaxDownloadAttempts; attempt++)
        {
            var tempPath = file.FullPath + ".part";
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, file.Url);
                request.Headers.TryAddWithoutValidation("User-Agent", PlaybackMediaOptions.AndroidLikeUserAgent);
                if (!string.IsNullOrWhiteSpace(file.Referrer)
                    && Uri.TryCreate(file.Referrer, UriKind.Absolute, out var referrerUri))
                {
                    request.Headers.Referrer = referrerUri;
                }
                else if (Uri.TryCreate(file.Url, UriKind.Absolute, out var uri))
                {
                    request.Headers.Referrer = new Uri($"{uri.GetLeftPart(UriPartial.Authority)}/");
                }
                if (!string.IsNullOrWhiteSpace(file.RangeHeader))
                {
                    request.Headers.TryAddWithoutValidation("Range", file.RangeHeader);
                }

                using var response = await SendWithTimeoutAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        RequestTimeout,
                        cancellationToken)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using (var output = new FileStream(
                                 tempPath,
                                 FileMode.Create,
                                 FileAccess.Write,
                                 FileShare.Read,
                                 64 * 1024,
                                 FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await CopyWithStallTimeoutAsync(input, output, BodyStallTimeout, cancellationToken).ConfigureAwait(false);
                }

                ValidateDownloadedFile(file, tempPath, requireMpegTs);
                File.Move(tempPath, file.FullPath, true);
                file.Length = new FileInfo(file.FullPath).Length;
                file.Completed.TrySetResult(file.FullPath);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                TryDeleteFile(tempPath);
                file.Completed.TrySetCanceled(cancellationToken);
                throw;
            }
            catch (Exception error)
            {
                lastError = error;
                TryDeleteFile(tempPath);
                VodCacheLog.Warn(
                    $"download-retry file={file.FileName} attempt={attempt}/{MaxDownloadAttempts} err={error.GetType().Name}: {error.Message}");
                if (attempt < MaxDownloadAttempts)
                {
                    await Task.Delay(400 * attempt, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        var finalError = lastError ?? new IOException($"DOWNLOAD_FAILED {file.FileName}");
        file.Completed.TrySetException(finalError);
        throw finalError;
    }

    /// <summary>
    /// 2xx 不代表正文就是媒体数据：防盗链、地区限制、代理出口异常时，CDN 常以 200 返回空正文或错误页。
    /// 这种“分片”一旦落盘，就会被原样拼进 merged.ts 并把缓存标记为已完成，成品无法正常播放且无法续传修复。
    /// 在落盘前拦下：按下载失败重试，仍不对则让本次下载失败，保留已合并的正确前缀供续传。
    /// </summary>
    private static void ValidateDownloadedFile(CacheFile file, string path, bool requireMpegTs)
    {
        var length = new FileInfo(path).Length;
        if (length <= 0)
        {
            throw new InvalidDataException($"DOWNLOAD_EMPTY_BODY {file.FileName}");
        }

        if (!requireMpegTs)
        {
            return;
        }

        var head = new byte[(int)Math.Min(length, MpegTsProbeBytes)];
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            stream.ReadExactly(head);
        }

        if (!LooksLikeMpegTs(head))
        {
            throw new InvalidDataException($"SEGMENT_NOT_MPEGTS {file.FileName} bytes={length}");
        }
    }

    /// <summary>
    /// 只有 MPEG-TS 才能二进制拼接。允许分片头部带少量伪装数据（如假图片头），
    /// 只要求探测窗口内能找到连续若干个按包长（188/192/204）对齐的同步字节 0x47。
    /// </summary>
    internal static bool LooksLikeMpegTs(ReadOnlySpan<byte> data) =>
        MpegTsTimelineStitcher.TryFindFraming(data, out _, out _);

    private async Task<HttpResponseMessage> SendWithTimeoutAsync(
        HttpRequestMessage request,
        HttpCompletionOption completionOption,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            return await _http.SendAsync(request, completionOption, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException($"HTTP_TIMEOUT {timeout.TotalSeconds:0}s {request.RequestUri}");
        }
    }

    /// <summary>
    /// 边读边写，并按"多久没收到数据"计时：每次读之前重新计时，所以只限制断流，不限制大分片的总下载时长。
    /// </summary>
    private static async Task CopyWithStallTimeoutAsync(
        Stream input,
        Stream output,
        TimeSpan stallTimeout,
        CancellationToken cancellationToken)
    {
        using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var buffer = new byte[64 * 1024];
        while (true)
        {
            stallCts.CancelAfter(stallTimeout);
            int read;
            try
            {
                read = await input.ReadAsync(buffer, stallCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && stallCts.IsCancellationRequested)
            {
                throw new TimeoutException($"HTTP_BODY_STALLED {stallTimeout.TotalSeconds:0}s");
            }

            if (read <= 0)
            {
                return;
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private string BuildPlaybackPlaylist(VodCacheSession session)
    {
        var lines = new List<string> { "#EXTM3U" };
        foreach (var token in session.Tokens)
        {
            switch (token)
            {
                case CacheToken.Static staticLine when !staticLine.Line.Equals("#EXTM3U", StringComparison.OrdinalIgnoreCase):
                    lines.Add(staticLine.Line);
                    break;
                case CacheToken.AssetLine asset:
                    lines.Add(asset.PlaybackLine);
                    break;
                case CacheToken.FileLine file:
                    lines.Add($"http://127.0.0.1:{_port}/vod/{session.Id}/file/{file.File.FileName}");
                    break;
            }
        }
        lines.Add("#EXT-X-ENDLIST");
        return string.Join('\n', lines) + "\n";
    }

    private static void WriteOfflinePlaylist(VodCacheSession session, bool complete = false)
    {
        var lines = new List<string> { "#EXTM3U" };
        var pending = new List<string>();
        foreach (var token in session.Tokens)
        {
            switch (token)
            {
                case CacheToken.Static staticLine when !staticLine.Line.Equals("#EXTM3U", StringComparison.OrdinalIgnoreCase):
                    pending.Add(staticLine.Line);
                    break;
                case CacheToken.AssetLine asset:
                    if (!asset.File.Completed.Task.IsCompletedSuccessfully)
                    {
                        goto Finish;
                    }
                    lines.AddRange(pending);
                    pending.Clear();
                    lines.Add(asset.OfflineLine);
                    break;
                case CacheToken.FileLine file:
                    if (!file.File.Completed.Task.IsCompletedSuccessfully)
                    {
                        goto Finish;
                    }
                    lines.AddRange(pending);
                    pending.Clear();
                    lines.Add(file.File.FileName);
                    break;
            }
        }

    Finish:
        if (complete)
        {
            lines.AddRange(pending);
        }
        lines.Add("#EXT-X-ENDLIST");
        var temp = session.OfflinePlaylistPath + ".tmp";
        File.WriteAllText(temp, string.Join('\n', lines) + "\n", new UTF8Encoding(false));
        File.Move(temp, session.OfflinePlaylistPath, true);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(_lifetime.Token).ConfigureAwait(false);
                _ = Task.Run(() => HandleClientAsync(client), CancellationToken.None);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                break;
            }
            catch when (_lifetime.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                await using var stream = client.GetStream();
                var request = await ReadRequestAsync(stream, _lifetime.Token).ConfigureAwait(false);
                if (request is null || !request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteErrorAsync(stream, 400, "Bad Request").ConfigureAwait(false);
                    return;
                }

                var path = request.Path.Split('?', 2)[0];
                var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3 || parts[0] != "vod" || !_sessions.TryGetValue(parts[1], out var session))
                {
                    await WriteErrorAsync(stream, 404, "Not Found").ConfigureAwait(false);
                    return;
                }

                if (parts[2].Equals("playlist.m3u8", StringComparison.OrdinalIgnoreCase))
                {
                    var bytes = Encoding.UTF8.GetBytes(BuildPlaybackPlaylist(session));
                    await WriteHeaderAsync(stream, 200, "OK", "application/vnd.apple.mpegurl", bytes.Length).ConfigureAwait(false);
                    await stream.WriteAsync(bytes, _lifetime.Token).ConfigureAwait(false);
                    return;
                }

                if (parts.Length == 4 && parts[2] == "file")
                {
                    var file = session.Files.FirstOrDefault(item => item.FileName.Equals(parts[3], StringComparison.OrdinalIgnoreCase));
                    if (file is null)
                    {
                        await WriteErrorAsync(stream, 404, "Not Found").ConfigureAwait(false);
                        return;
                    }

                    var fullPath = await file.Completed.Task.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                    if (File.Exists(fullPath))
                    {
                        await WriteFileAsync(stream, fullPath, request.RangeHeader, _lifetime.Token).ConfigureAwait(false);
                    }
                    else if (TryGetMergedSegmentRange(session, file, out var offset, out var length))
                    {
                        await WriteFileSliceAsync(
                            stream,
                            session.MergedFilePath,
                            offset,
                            length,
                            file.FullPath,
                            request.RangeHeader,
                            _lifetime.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        await WriteErrorAsync(stream, 404, "Not Found").ConfigureAwait(false);
                    }
                    return;
                }

                await WriteErrorAsync(stream, 404, "Not Found").ConfigureAwait(false);
            }
            catch
            {
                // 播放器取消请求、切集或应用退出时直接关闭连接。
            }
        }
    }

    private static bool TryGetMergedSegmentRange(
        VodCacheSession session,
        CacheFile file,
        out long offset,
        out long length)
    {
        offset = 0;
        length = 0;
        if (!file.IsSegment || file.SegmentIndex < 0 || !File.Exists(session.MergedFilePath))
        {
            return false;
        }

        lock (session.MergeStateGate)
        {
            if (file.SegmentIndex >= session.MergedSegments
                || file.SegmentIndex >= session.MergedSegmentLengths.Count)
            {
                return false;
            }

            for (var index = 0; index < file.SegmentIndex; index++)
            {
                offset += session.MergedSegmentLengths[index];
            }
            length = session.MergedSegmentLengths[file.SegmentIndex];
        }

        return length > 0 && new FileInfo(session.MergedFilePath).Length >= offset + length;
    }

    private static async Task WriteFileAsync(Stream stream, string path, string? rangeHeader, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        await WriteFileSliceAsync(
            stream,
            path,
            0,
            info.Length,
            path,
            rangeHeader,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteFileSliceAsync(
        Stream stream,
        string path,
        long fileOffset,
        long availableLength,
        string contentTypePath,
        string? rangeHeader,
        CancellationToken cancellationToken)
    {
        if (availableLength <= 0)
        {
            throw new IOException("CACHE_FILE_IS_EMPTY");
        }

        long start = 0;
        long end = availableLength - 1;
        var partial = false;
        if (!string.IsNullOrWhiteSpace(rangeHeader)
            && rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            var range = rangeHeader[6..].Split('-', 2);
            if (long.TryParse(range[0], out var parsedStart))
            {
                start = Math.Clamp(parsedStart, 0, Math.Max(0, availableLength - 1));
                if (range.Length > 1 && long.TryParse(range[1], out var parsedEnd))
                {
                    end = Math.Clamp(parsedEnd, start, availableLength - 1);
                }
                partial = true;
            }
        }

        var length = end - start + 1;
        var contentType = ContentTypeOf(contentTypePath);
        var extra = partial ? $"Content-Range: bytes {start}-{end}/{availableLength}\r\n" : "";
        await WriteHeaderAsync(stream, partial ? 206 : 200, partial ? "Partial Content" : "OK", contentType, length, extra).ConfigureAwait(false);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        input.Position = fileOffset + start;
        var buffer = new byte[64 * 1024];
        var remaining = length;
        while (remaining > 0)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
            if (read <= 0) break;
            await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            remaining -= read;
        }
    }

    private static async Task<HttpRequest?> ReadRequestAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(count, buffer.Length - count), cancellationToken).ConfigureAwait(false);
            if (read <= 0) return null;
            count += read;
            var text = Encoding.ASCII.GetString(buffer, 0, count);
            var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end < 0) continue;
            var lines = text[..end].Split("\r\n", StringSplitOptions.None);
            var first = lines[0].Split(' ', 3);
            if (first.Length < 2) return null;
            var range = lines.Skip(1)
                .FirstOrDefault(line => line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase))?
                .Split(':', 2)[1]
                .Trim();
            return new HttpRequest(first[0], first[1], range);
        }
        return null;
    }

    private static Task WriteErrorAsync(Stream stream, int status, string reason)
    {
        var body = Encoding.UTF8.GetBytes(reason);
        return WriteResponseAsync(stream, status, reason, "text/plain; charset=utf-8", body);
    }

    private static async Task WriteResponseAsync(Stream stream, int status, string reason, string contentType, byte[] body)
    {
        await WriteHeaderAsync(stream, status, reason, contentType, body.Length).ConfigureAwait(false);
        await stream.WriteAsync(body).ConfigureAwait(false);
    }

    private static async Task WriteHeaderAsync(
        Stream stream,
        int status,
        string reason,
        string contentType,
        long contentLength,
        string extra = "")
    {
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {reason}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {contentLength}\r\n" +
            "Accept-Ranges: bytes\r\n" +
            "Cache-Control: no-store\r\n" +
            extra +
            "Connection: close\r\n\r\n");
        await stream.WriteAsync(header).ConfigureAwait(false);
    }

    private async Task<(string Url, string Text)> FetchTextAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", PlaybackMediaOptions.AndroidLikeUserAgent);
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            request.Headers.Referrer = new Uri($"{uri.GetLeftPart(UriPartial.Authority)}/");
        }

        using var response = await SendWithTimeoutAsync(
                request,
                HttpCompletionOption.ResponseContentRead,
                RequestTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        // HttpClient 自动跟随重定向后，RequestMessage.RequestUri 为最终地址。
        var finalUrl = response.RequestMessage?.RequestUri?.AbsoluteUri
            ?? response.Headers.Location?.AbsoluteUri
            ?? url;
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return (finalUrl, text);
    }

    private static string ResolveUrl(string baseUrl, string value) =>
        new Uri(new Uri(baseUrl), value).AbsoluteUri;

    private static string? PlaylistReferrerOf(string playlistUrl) =>
        Uri.TryCreate(playlistUrl, UriKind.Absolute, out var uri)
            ? $"{uri.GetLeftPart(UriPartial.Authority)}/"
            : null;

    private static string BuildRangeHeader(string value, ref long nextImplicitOffset)
    {
        var parts = value.Split('@', 2);
        if (!long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var length) || length <= 0)
        {
            return "";
        }
        var start = parts.Length > 1 && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var explicitStart)
            ? explicitStart
            : nextImplicitOffset;
        var end = start + length - 1;
        nextImplicitOffset = end + 1;
        return $"bytes={start}-{end}";
    }

    private static string GetExtension(string url, string fallback)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return fallback;
        var extension = Path.GetExtension(uri.AbsolutePath);
        return string.IsNullOrWhiteSpace(extension) || extension.Length > 8 ? fallback : extension;
    }

    private static string BuildCacheDirectory(string root, string title, string id)
    {
        var match = EpisodeCacheTitleRegex.Match(title.Trim());
        if (!match.Success)
        {
            return Path.Combine(root, $"{DateTime.Now:yyyyMMdd-HHmmss}-{MakeSafeName(title)}-{id[..8]}");
        }

        var seriesDirectory = Path.Combine(root, MakeSafeName(match.Groups["series"].Value));
        var episode = match.Groups["episode"].Value;
        return Path.Combine(seriesDirectory, $"第{episode}集-{DateTime.Now:yyyyMMdd-HHmmss}-{id[..8]}");
    }

    private static IReadOnlyList<string> EnumerateCacheDirectories(string root)
    {
        string[] topLevelDirectories;
        try
        {
            topLevelDirectories = Directory.GetDirectories(root, "*", SearchOption.TopDirectoryOnly);
        }
        catch
        {
            return [];
        }

        var result = new List<string>();
        foreach (var directory in topLevelDirectories)
        {
            if (IsCacheDirectory(directory))
            {
                result.Add(directory);
                continue;
            }

            try
            {
                result.AddRange(Directory.GetDirectories(directory, "*", SearchOption.TopDirectoryOnly)
                    .Where(IsCacheDirectory));
            }
            catch
            {
                // 普通媒体目录或无权限目录不影响 OpaiTV 缓存扫描。
            }
        }

        return result;
    }

    private static bool IsCacheDirectory(string directory) =>
        File.Exists(Path.Combine(directory, "cache.json"))
        || File.Exists(Path.Combine(directory, "offline.m3u8"))
        || File.Exists(Path.Combine(directory, VodCacheSession.MergedFileName));

    private static bool CacheTitlesEqual(string left, string right) =>
        string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void TryDeleteEmptyDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch
        {
        }
    }

    private static string MakeSafeName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "video" : cleaned[..Math.Min(cleaned.Length, 48)];
    }

    private static string ContentTypeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".m3u8" => "application/vnd.apple.mpegurl",
        ".ts" => "video/mp2t",
        ".m4s" => "video/iso.segment",
        ".mp4" => "video/mp4",
        ".aac" => "audio/aac",
        ".key" => "application/octet-stream",
        _ => "application/octet-stream",
    };

    private static async Task WriteMetadataAsync(VodCacheSession session, bool complete, int completedSegments, CancellationToken cancellationToken)
    {
        int mergedSegments;
        long[] mergedSegmentLengths;
        lock (session.MergeStateGate)
        {
            mergedSegments = Math.Min(session.MergedSegments, session.MergedSegmentLengths.Count);
            mergedSegmentLengths = session.MergedSegmentLengths.Take(mergedSegments).ToArray();
        }

        var metadata = new CacheMetadata(
            session.Title,
            session.SourceUrl,
            session.CreatedAt,
            complete,
            completedSegments,
            session.Segments.Count,
            mergedSegments,
            session.MergeSupported ? VodCacheSession.MergedFileName : null,
            session.MergeSupported,
            session.MediaPlaylistUrl,
            mergedSegmentLengths,
            session.Poster);
        await WriteMetadataFileAsync(session.Directory, metadata, cancellationToken).ConfigureAwait(false);
    }

    private static CacheMetadata? ReadMetadata(string directory)
    {
        try
        {
            var path = Path.Combine(directory, "cache.json");
            if (!File.Exists(path))
            {
                return null;
            }

            CacheMetadata? metadata;
            using (var stream = new FileStream(
                       path,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete,
                       4096,
                       FileOptions.SequentialScan))
            {
                metadata = JsonSerializer.Deserialize<CacheMetadata>(stream);
            }

            if (metadata is null
                || metadata.IsComplete
                || metadata.TotalSegments <= 0
                || metadata.CompletedSegments < metadata.TotalSegments)
            {
                return metadata;
            }

            var mergedPath = Path.Combine(
                directory,
                metadata.MergedFileName ?? VodCacheSession.MergedFileName);
            var hasCompleteMergedFile = metadata.MergedSegments >= metadata.TotalSegments
                && File.Exists(mergedPath)
                && new FileInfo(mergedPath).Length > 0;
            var offlinePath = Path.Combine(directory, "offline.m3u8");
            var hasCompletedPlaylist = File.Exists(offlinePath)
                && File.ReadLines(offlinePath).Any(line =>
                    string.Equals(line.Trim(), "#EXT-X-ENDLIST", StringComparison.OrdinalIgnoreCase));
            if (!hasCompleteMergedFile && !hasCompletedPlaylist)
            {
                return metadata;
            }

            var repaired = metadata with { IsComplete = true };
            if (TryWriteMetadata(directory, repaired))
            {
                VodCacheLog.Info(
                    $"cache-completion-recovered title={metadata.Title} dir={directory}");
            }

            return repaired;
        }
        catch
        {
            return null;
        }
    }

    private static bool AreEqual(IReadOnlyList<long>? left, IReadOnlyList<long> right) =>
        left is not null && left.Count == right.Count && left.SequenceEqual(right);

    private static async Task WriteMetadataFileAsync(
        string directory,
        CacheMetadata metadata,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory, "cache.json");
        var temp = Path.Combine(directory, $".cache-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(
                temp,
                JsonSerializer.Serialize(metadata, MetadataJsonOptions),
                new UTF8Encoding(false),
                cancellationToken).ConfigureAwait(false);

            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(temp, path, true);
                    return;
                }
                catch (Exception error) when (
                    attempt < MetadataReplaceAttempts
                    && error is IOException or UnauthorizedAccessException)
                {
                    await Task.Delay(50 * attempt, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            TryDeleteFile(temp);
        }
    }

    private static bool TryWriteMetadata(string directory, CacheMetadata metadata)
    {
        var path = Path.Combine(directory, "cache.json");
        var temp = Path.Combine(directory, $".cache-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(
                temp,
                JsonSerializer.Serialize(metadata, MetadataJsonOptions),
                new UTF8Encoding(false));
            File.Move(temp, path, true);
            return true;
        }
        catch
        {
            // 兼容迁移失败不影响本地播放或续传。
            return false;
        }
        finally
        {
            TryDeleteFile(temp);
        }
    }

    private static async Task DeleteDirectoryWithRetryAsync(string directory)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
                return;
            }
            catch when (attempt < 5)
            {
                await Task.Delay(300 * (attempt + 1)).ConfigureAwait(false);
            }
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, true); } catch { }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _listener.Stop();
        foreach (var session in _sessions.Values)
        {
            session.Cancellation.Cancel();
        }
        try { Task.WhenAll(_sessions.Values.Select(session => session.DownloadTask)).GetAwaiter().GetResult(); } catch { }
        foreach (var session in _sessions.Values.Where(session => session.KeepFilesOnServiceDispose))
        {
            DeleteMergedSegmentFiles(session);
        }
        foreach (var session in _sessions.Values.Where(session => !session.KeepFilesOnServiceDispose))
        {
            TryDeleteDirectory(session.Directory);
        }
        try { _acceptLoop.GetAwaiter().GetResult(); } catch { }
        _http.Dispose();
        _segmentDownloadGate.Dispose();
        _lifetime.Dispose();
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed record HttpRequest(string Method, string Path, string? RangeHeader);
}

/// <summary>点播缓存诊断日志，写入 %LocalAppData%/OrionTV.Desktop/vod-cache.log。</summary>
internal static class VodCacheLog
{
    private static readonly object Gate = new();
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OrionTV.Desktop",
        "vod-cache.log");

    public static void Info(string message) => Write("INFO", message, null);

    public static void Warn(string message) => Write("WARN", message, null);

    public static void Error(string message, Exception? error = null) => Write("ERROR", message, error);

    private static void Write(string level, string message, Exception? error)
    {
        try
        {
            var directory = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
            if (error is not null)
            {
                line += $" :: {error.GetType().Name}: {error.Message}";
            }

            lock (Gate)
            {
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
        }
        catch
        {
            // 日志失败不能影响播放/下载主流程。
        }
    }
}
