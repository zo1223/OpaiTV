namespace OrionTV.Desktop.Core;

/// <summary>全源搜索时单个片源的状态。</summary>
public enum SourceSearchState
{
    Pending,
    Searching,
    Done,
    Failed,
    TimedOut,
}

public sealed record SourceSearchStatus(string Key, string Name, SourceSearchState State, int Count = 0)
{
    public bool IsCompleted => State is SourceSearchState.Done or SourceSearchState.Failed or SourceSearchState.TimedOut;

    public string StateText => State switch
    {
        SourceSearchState.Pending => "等待",
        SourceSearchState.Searching => "搜索中",
        SourceSearchState.Done => $"{Count} 条",
        SourceSearchState.Failed => "失败",
        SourceSearchState.TimedOut => "超时",
        _ => "",
    };
}

/// <summary>一次增量搜索的进度快照：逐源状态 + 到目前为止按标题归并后的结果。</summary>
public sealed record SearchProgress(
    IReadOnlyList<SourceSearchStatus> Sources,
    IReadOnlyList<CatalogItem> Items)
{
    public int Total => Sources.Count;
    public int Completed => Sources.Count(source => source.IsCompleted);
    public bool IsFinished => Completed == Total;
    public string Summary => IsFinished
        ? $"已搜完 {Total} 个源，共 {Items.Count} 条"
        : $"已搜完 {Completed}/{Total} 个源，共 {Items.Count} 条";
}

/// <summary>
/// 跨片源实时搜索：逐源并发、逐源回报。一个源返回就把它的结果并入列表（按标题归并），不等全部搜完；
/// 失败或超时的源只标记状态，不拖住其它源。取消（新的搜索开始）之后不再回报任何进度。
/// </summary>
public static class IncrementalSearch
{
    /// <summary>单源时限，与手机端 SOURCE_REQUEST_TIMEOUT_MS 一致。</summary>
    public static readonly TimeSpan DefaultSourceTimeout = TimeSpan.FromSeconds(15);

    public static async Task<SearchProgress> RunAsync(
        IReadOnlyList<VideoSource> sources,
        Func<VideoSource, CancellationToken, Task<IReadOnlyList<SearchResult>>> searchOne,
        Action<SearchProgress>? onProgress,
        CancellationToken cancellationToken = default,
        TimeSpan? sourceTimeout = null,
        int maxConcurrency = CatalogService.MaxConcurrentSourceQueries)
    {
        var timeout = sourceTimeout ?? DefaultSourceTimeout;
        var sync = new object();
        var statuses = sources
            .Select(source => new SourceSearchStatus(source.Key, source.Name, SourceSearchState.Pending))
            .ToArray();
        // 按到达顺序累积：后到的源只会在列表末尾追加新标题，或并入已有标题，已显示的条目不会跳位。
        var arrived = new List<SearchResult>();
        var latest = new SearchProgress(statuses.ToArray(), []);

        void Report(int index, SourceSearchState state, IReadOnlyList<SearchResult>? results = null)
        {
            lock (sync)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                if (results is not null)
                {
                    arrived.AddRange(results);
                }

                statuses[index] = statuses[index] with { State = state, Count = results?.Count ?? 0 };
                latest = new SearchProgress(
                    statuses.ToArray(),
                    results is null ? latest.Items : CatalogService.MergeByTitle(arrived));
                onProgress?.Invoke(latest);
            }
        }

        lock (sync)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                onProgress?.Invoke(latest);
            }
        }

        if (sources.Count == 0)
        {
            return latest;
        }

        var concurrency = Math.Clamp(sources.Count, 1, Math.Max(1, maxConcurrency));
        using var gate = new SemaphoreSlim(concurrency, concurrency);
        var tasks = sources.Select(async (source, index) =>
        {
            try
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                Report(index, SourceSearchState.Searching);
                using var sourceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var search = searchOne(source, sourceCts.Token);
                var winner = await Task.WhenAny(search, Task.Delay(timeout, sourceCts.Token)).ConfigureAwait(false);
                if (winner != search)
                {
                    // 超时：让这个源自己收尾，不再等它
                    sourceCts.Cancel();
                    _ = search.ContinueWith(
                        static completed => _ = completed.Exception,
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                    Report(index, SourceSearchState.TimedOut);
                    return;
                }

                sourceCts.Cancel();
                var results = await search.ConfigureAwait(false);
                Report(index, SourceSearchState.Done, results);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 整个搜索被取消：不回报
            }
            catch
            {
                Report(index, SourceSearchState.Failed);
            }
            finally
            {
                gate.Release();
            }
        }).ToArray();

        await Task.WhenAll(tasks).ConfigureAwait(false);
        lock (sync)
        {
            return latest;
        }
    }
}
