using System.Collections.Concurrent;
using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.App;

public enum VodDownloadJobStatus
{
    Queued,
    Running,
    /// <summary>分片已全部下完，正在自动解密合并为单文件（加密源、分片后缀不是 .ts 的源）。此时 Percent 是合并进度。</summary>
    Merging,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>应用级点播下载任务快照（可跨页面观察，互不影响播放）。</summary>
public sealed record VodDownloadJobSnapshot(
    string Id,
    string Title,
    string SourceUrl,
    string CacheDirectory,
    VodDownloadJobStatus Status,
    int Percent,
    int CompletedSegments,
    int TotalSegments,
    long DownloadedBytes,
    long BytesPerSecond,
    string? Error,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// 跨页面后台下载管理器：详情页启动后离开也可继续；本地视频页展示多任务进度/速度。
/// </summary>
public sealed class VodDownloadManager : IDisposable
{
    private readonly VodCacheService _cache;
    private readonly ConcurrentDictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private bool _disposed;

    public VodDownloadManager(VodCacheService cache)
    {
        _cache = cache;
    }

    public event EventHandler? Changed;

    public IReadOnlyList<VodDownloadJobSnapshot> GetJobs()
    {
        return _jobs.Values
            .Select(job => job.ToSnapshot())
            .OrderByDescending(job => job.StartedAt)
            .ToList();
    }

    public IReadOnlyList<VodDownloadJobSnapshot> GetActiveJobs() =>
        GetJobs().Where(job => IsActiveStatus(job.Status)).ToList();

    /// <summary>还占着缓存目录的任务：排队、下载中、下完后合并中。</summary>
    public static bool IsActiveStatus(VodDownloadJobStatus status) =>
        status is VodDownloadJobStatus.Queued or VodDownloadJobStatus.Running or VodDownloadJobStatus.Merging;

    public VodDownloadJobSnapshot? FindActiveByTitle(string title) =>
        GetActiveJobs().FirstOrDefault(job =>
            string.Equals(job.Title, title, StringComparison.OrdinalIgnoreCase));

    public bool IsActiveTitle(string title) => FindActiveByTitle(title) is not null;

    public async Task<VodDownloadJobSnapshot> StartAsync(
        string sourceUrl,
        string title,
        string cacheDirectory,
        CancellationToken cancellationToken = default,
        string poster = "")
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!VodCacheService.IsHlsUrl(sourceUrl))
        {
            throw new InvalidOperationException("NOT_HLS_URL");
        }

        // 同标题已在下载：复用任务，避免重复目录。
        foreach (var existing in _jobs.Values)
        {
            if (IsActiveStatus(existing.Status)
                && string.Equals(existing.Title, title, StringComparison.OrdinalIgnoreCase))
            {
                return existing.ToSnapshot();
            }
        }

        var session = await _cache.StartAsync(
                sourceUrl,
                title,
                cacheDirectory,
                prebufferSeconds: 15,
                cancellationToken,
                poster)
            .ConfigureAwait(false);
        if (session is null)
        {
            throw new InvalidOperationException("DOWNLOAD_START_FAILED");
        }

        session.KeepFilesOnServiceDispose = true;
        var job = new Job(session, cacheDirectory);
        if (!_jobs.TryAdd(job.Id, job))
        {
            await _cache.CloseAsync(session, deleteFiles: false, continueDownload: true).ConfigureAwait(false);
            throw new InvalidOperationException("DOWNLOAD_JOB_ID_CONFLICT");
        }

        session.ProgressChanged += (_, progress) => OnSessionProgress(job, progress);
        job.Runner = Task.Run(() => RunJobAsync(job), CancellationToken.None);
        RaiseChanged();
        return job.ToSnapshot();
    }

    public async Task CancelAsync(string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var job))
        {
            return;
        }

        job.UserCancelled = true;
        try
        {
            job.Session.Cancellation.Cancel();
        }
        catch
        {
        }

        // 已经下完、正在自动合并时：只停合并，缓存保持“已下完、未合并”。
        job.CancelMerge();

        try
        {
            await job.Runner.ConfigureAwait(false);
        }
        catch
        {
        }
    }

    public async Task CancelManyAsync(IEnumerable<string> jobIds)
    {
        var tasks = jobIds.Select(CancelAsync).ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task RunJobAsync(Job job)
    {
        job.Status = VodDownloadJobStatus.Running;
        job.UpdatedAt = DateTimeOffset.Now;
        RaiseChanged();

        try
        {
            await job.Session.DownloadTask.ConfigureAwait(false);
        }
        catch
        {
            // 失败/取消在下面根据 Progress 判定。
        }

        var progress = job.Session.Progress;
        try
        {
            // 始终保留文件；完成后从会话表移除并合并成单文件。
            // 边下边合并覆盖不到的源（加密源、分片后缀不是 .ts 的源）在这里自动解密合并，期间任务显示为“合并中”。
            var mergeProgress = new MergeProgress(ratio => OnMergeProgress(job, ratio));
            await _cache.CloseAsync(
                    job.Session,
                    deleteFiles: false,
                    continueDownload: false,
                    mergeProgress,
                    job.MergeCancellation.Token)
                .ConfigureAwait(false);
        }
        catch
        {
        }

        if (progress.IsComplete)
        {
            // 分片已经全部下完：即使随后的自动合并被用户停掉，下载本身也是完成的。
            job.Status = VodDownloadJobStatus.Completed;
            job.Percent = 100;
            job.CompletedSegments = progress.CompletedSegments;
            job.TotalSegments = progress.TotalSegments;
            job.DownloadedBytes = progress.DownloadedBytes;
            job.Error = null;
        }
        else if (job.UserCancelled)
        {
            job.Status = VodDownloadJobStatus.Cancelled;
            job.Error = null;
        }
        else
        {
            job.Status = VodDownloadJobStatus.Failed;
            job.Error = progress.Error ?? "下载中断";
            job.CompletedSegments = progress.CompletedSegments;
            job.TotalSegments = progress.TotalSegments;
            job.DownloadedBytes = progress.DownloadedBytes;
        }

        job.BytesPerSecond = 0;
        job.UpdatedAt = DateTimeOffset.Now;
        RaiseChanged();

        // 完成/失败条目保留一段时间便于本地页展示，再淡出只留磁盘缓存列表。
        if (job.Status is VodDownloadJobStatus.Completed or VodDownloadJobStatus.Failed or VodDownloadJobStatus.Cancelled)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
                    if (_jobs.TryRemove(job.Id, out _))
                    {
                        RaiseChanged();
                    }
                }
                catch
                {
                }
            });
        }
    }

    private void OnSessionProgress(Job job, VodCacheProgress progress)
    {
        var now = DateTimeOffset.Now;
        var percent = FormatPercent(progress);
        long speed;
        lock (job.SpeedGate)
        {
            var elapsed = (now - job.LastSampleAt).TotalSeconds;
            if (elapsed >= 0.8)
            {
                var delta = progress.DownloadedBytes - job.LastSampleBytes;
                job.BytesPerSecond = elapsed > 0 ? (long)Math.Max(0, delta / elapsed) : 0;
                job.LastSampleAt = now;
                job.LastSampleBytes = progress.DownloadedBytes;
            }

            speed = job.BytesPerSecond;
        }

        job.Status = VodDownloadJobStatus.Running;
        job.Percent = percent;
        job.CompletedSegments = progress.CompletedSegments;
        job.TotalSegments = progress.TotalSegments;
        job.DownloadedBytes = progress.DownloadedBytes;
        job.BytesPerSecond = speed;
        job.Error = progress.Error;
        job.UpdatedAt = now;
        RaiseChanged();
    }

    private void OnMergeProgress(Job job, double ratio)
    {
        var percent = Math.Clamp((int)Math.Round(ratio * 100), 0, 100);
        if (job.Status == VodDownloadJobStatus.Merging && job.Percent == percent)
        {
            // 每合并一片回调一次，只在百分比变化时通知界面。
            return;
        }

        job.Status = VodDownloadJobStatus.Merging;
        job.Percent = percent;
        job.BytesPerSecond = 0;
        job.Error = null;
        job.UpdatedAt = DateTimeOffset.Now;
        RaiseChanged();
    }

    private static int FormatPercent(VodCacheProgress progress)
    {
        double ratio;
        if (progress.TotalSegments > 0)
        {
            ratio = (double)progress.CompletedSegments / progress.TotalSegments;
        }
        else if (progress.TotalSeconds > 0)
        {
            ratio = progress.DownloadedSeconds / progress.TotalSeconds;
        }
        else
        {
            return 0;
        }

        return Math.Clamp((int)Math.Round(ratio * 100), 0, 100);
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var job in _jobs.Values)
        {
            try
            {
                job.Session.Cancellation.Cancel();
            }
            catch
            {
            }

            job.CancelMerge();
        }
    }

    private sealed class MergeProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    private sealed class Job
    {
        internal Job(VodCacheSession session, string cacheDirectory)
        {
            Id = session.Id;
            Title = session.Title;
            SourceUrl = session.SourceUrl;
            CacheDirectory = cacheDirectory;
            Session = session;
            Status = VodDownloadJobStatus.Queued;
            StartedAt = DateTimeOffset.Now;
            UpdatedAt = StartedAt;
            LastSampleAt = StartedAt;
        }

        internal string Id { get; }
        internal string Title { get; }
        internal string SourceUrl { get; }
        internal string CacheDirectory { get; }
        internal VodCacheSession Session { get; }
        internal Task Runner { get; set; } = Task.CompletedTask;
        internal VodDownloadJobStatus Status { get; set; }
        internal int Percent { get; set; }
        internal int CompletedSegments { get; set; }
        internal int TotalSegments { get; set; }
        internal long DownloadedBytes { get; set; }
        internal long BytesPerSecond { get; set; }
        internal string? Error { get; set; }
        internal DateTimeOffset StartedAt { get; }
        internal DateTimeOffset UpdatedAt { get; set; }
        internal bool UserCancelled { get; set; }
        /// <summary>只管下载完成后的自动合并；下载本身由 Session.Cancellation 取消。</summary>
        internal CancellationTokenSource MergeCancellation { get; } = new();
        internal object SpeedGate { get; } = new();
        internal DateTimeOffset LastSampleAt { get; set; }
        internal long LastSampleBytes { get; set; }

        internal void CancelMerge()
        {
            try
            {
                MergeCancellation.Cancel();
            }
            catch
            {
            }
        }

        internal VodDownloadJobSnapshot ToSnapshot() =>
            new(
                Id,
                Title,
                SourceUrl,
                CacheDirectory,
                Status,
                Percent,
                CompletedSegments,
                TotalSegments,
                DownloadedBytes,
                BytesPerSecond,
                Error,
                StartedAt,
                UpdatedAt);
    }
}
