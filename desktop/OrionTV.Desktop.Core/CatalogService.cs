namespace OrionTV.Desktop.Core;

/// <summary>首页/搜索/收藏/历史网格统一展示项。</summary>
public sealed record CatalogItem(
    string Id,
    string Title,
    string Poster,
    string Source,
    string SourceName,
    string? Year = null,
    string? Rate = null,
    double? Progress = null,
    int? EpisodeIndex = null,
    int? TotalEpisodes = null,
    double PlayTime = 0,
    CatalogPlayabilityStatus PlayabilityStatus = CatalogPlayabilityStatus.Unknown,
    int? PlayableSourceCount = null,
    IReadOnlyList<SearchResult>? CandidateSources = null,
    IReadOnlyList<SearchResult>? PlayableSources = null);

/// <summary>
/// 编排层：移植自安卓 services/api.ts 的订阅模式数据流。
/// 首页豆瓣分类（豆瓣直连，失败回退各协议源站首页混合）、全源搜索、详情多源聚合。
/// </summary>
public sealed class CatalogService
{
    /// <summary>
    /// 全源搜索并发上限。TVBox 订阅常有几十个站点，无上限 WhenAll 会让共享 HttpClient
    /// 与本地代理排队超时，表现为「加了 TVBox 后原有源反而变少、详情页也看不到 TVBox」。
    /// </summary>
    public const int MaxConcurrentSourceQueries = 8;

    private readonly VodSourceClientFactory _clients;
    private readonly DoubanClient _douban;

    public CatalogService(VodSourceClientFactory clients, DoubanClient douban)
    {
        _clients = clients;
        _douban = douban;
    }

    public CatalogService(HttpClient httpClient, DoubanClient? douban = null)
        : this(new VodSourceClientFactory(httpClient), douban ?? new DoubanClient(httpClient))
    {
    }

    public AppleCmsClient AppleCms => _clients.AppleCms;

    /// <summary>
    /// 成人内容(18+)过滤（对齐 RN 端 services/api.ts）：由宿主提供，给定源 key 返回它当前是否被隐藏
    /// （成人源且开关关闭）。为空表示不过滤。
    /// </summary>
    public Func<string, bool>? IsAdultSourceHidden { get; init; }

    public IVodSourceClient ClientFor(VideoSource source) => _clients.For(source);

    // 所有取数入口（首页、搜索、详情、补剧集）只能用这份列表，不直接用调用方传入的 sources：
    // 调用方手里可能还是开关关闭之前的快照。
    private IReadOnlyList<VideoSource> VisibleSources(IReadOnlyList<VideoSource> sources) =>
        sources.Any(source => IsHidden(source.Key))
            ? sources.Where(source => !IsHidden(source.Key)).ToList()
            : sources;

    // 请求在途时开关可能被关掉（或该源刚被标记为成人源）：结果落地前按当前状态再确认一次。
    private bool IsHidden(string sourceKey) => IsAdultSourceHidden?.Invoke(sourceKey) == true;

    private IReadOnlyList<SearchResult> VisibleResults(IReadOnlyList<SearchResult> results) =>
        results.Any(result => IsHidden(result.Source))
            ? results.Where(result => !IsHidden(result.Source)).ToList()
            : results;

    public async Task<IReadOnlyList<CatalogItem>> GetHomeDataAsync(
        IReadOnlyList<VideoSource> sources,
        string type,
        string tag,
        int pageSize,
        int pageStart,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var doubanList = await _douban.FetchSubjectsAsync(type, tag, pageSize, pageStart, cancellationToken).ConfigureAwait(false);
            if (doubanList.Count > 0)
            {
                return doubanList
                    .Select(item => new CatalogItem(item.Id, item.Title, item.Poster, "douban", "豆瓣", item.Year, item.Rate))
                    .ToList();
            }
        }
        catch
        {
            // 豆瓣不可用时回退到源站首页。
        }

        var perSource = await Task.WhenAll(
            VisibleSources(sources).Select(source => SafeHomePageAsync(source, type, tag, pageStart, pageSize, cancellationToken)));
        // 混合时不按 pageSize 截断:被裁掉的条目在下一次请求(源站已翻页)中永久丢失;
        // 去重与按位交错混合保留。
        var mixed = MixSourceResults(perSource.Select(VisibleResults).ToList(), int.MaxValue, GetDedupeKey);
        return mixed.Select(ToCatalogItem).ToList();
    }

    public async Task<IReadOnlyList<CatalogItem>> SearchAllAsync(
        IReadOnlyList<VideoSource> sources,
        string query,
        CancellationToken cancellationToken = default)
    {
        var perSource = await SearchSourcesConcurrentlyAsync(sources, query, cancellationToken).ConfigureAwait(false);
        return perSource
            .SelectMany(items => items)
            .GroupBy(item => CatalogTitleMatcher.Normalize(item.Title), StringComparer.Ordinal)
            .Select(group =>
            {
                var matches = group.ToList();
                var representative = matches.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.Poster))
                    ?? matches[0];
                var candidates = matches
                    .SelectMany(SearchResultSources.Expand)
                    .GroupBy(SearchResultSources.SelectionKeyOf, StringComparer.Ordinal)
                    .Select(items => items.First())
                    .ToList();
                return ToCatalogItem(representative) with
                {
                    PlayabilityStatus = candidates.Count > 0
                        ? CatalogPlayabilityStatus.Available
                        : CatalogPlayabilityStatus.Unavailable,
                    PlayableSourceCount = candidates.Count,
                    CandidateSources = candidates,
                };
            })
            .ToList();
    }

    /// <summary>已知 source/id 时优先走单源 id 查询，避免进入详情页前再次全源搜索。</summary>
    public async Task<SearchResult?> GetPreferredDetailSourceAsync(
        IReadOnlyList<VideoSource> sources,
        string? sourceKey,
        string? id,
        string? title = null,
        CancellationToken cancellationToken = default)
    {
        return (await GetPreferredDetailSourcesAsync(
                sources,
                sourceKey,
                id,
                title,
                cancellationToken)
            .ConfigureAwait(false)).FirstOrDefault();
    }

    /// <summary>按海报携带的 source/id 直接返回该条目的全部有效播放线路。</summary>
    public async Task<IReadOnlyList<SearchResult>> GetPreferredDetailSourcesAsync(
        IReadOnlyList<VideoSource> sources,
        string? sourceKey,
        string? id,
        string? title = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceKey) || string.IsNullOrWhiteSpace(id))
        {
            return [];
        }

        var source = VisibleSources(sources).FirstOrDefault(item => item.Key == sourceKey);
        if (source is null)
        {
            return [];
        }

        try
        {
            var results = await ClientFor(source).GetByIdAsync(source, id, cancellationToken).ConfigureAwait(false);
            if (IsHidden(source.Key))
            {
                return [];
            }

            var result = results.FirstOrDefault(item => item.Id == id)
                ?? (!string.IsNullOrWhiteSpace(title) ? results.FirstOrDefault(item => item.Title == title) : null)
                ?? results.FirstOrDefault();
            // 详情页需要展示该海报对应的源；TVBox/csp 有时 search 无剧集、detail 才有，
            // 因此这里不过滤 HasPlayableEpisode，由详情页选中后再补拉。
            return result is null
                ? []
                : SearchResultSources.Expand(result).ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// 对齐安卓 detailStore：搜索全部源，标题匹配后作为「播放源」列表。
    /// 不过滤空剧集：TVBox type3 常在 search 只返回元数据，剧集要 ac=detail&ids= 才有。
    /// 限流并发：TVBox 开启后源数可达数十，全开 WhenAll 会拖垮原有源导致「加了 TVBox 反而更少」。
    /// </summary>
    public async Task<IReadOnlyList<SearchResult>> GetDetailSourcesAsync(
        IReadOnlyList<VideoSource> sources,
        string title,
        CancellationToken cancellationToken = default)
    {
        var perSource = await SearchSourcesConcurrentlyAsync(sources, title, cancellationToken).ConfigureAwait(false);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<SearchResult>();
        foreach (var item in perSource
                     .SelectMany(items => items)
                     .SelectMany(SearchResultSources.Expand)
                     .Where(item => CatalogTitleMatcher.Matches(item.Title, title)))
        {
            if (seen.Add(SearchResultSources.SelectionKeyOf(item)))
            {
                result.Add(item);
            }
        }

        return result;
    }

    /// <summary>按 source+id 补全剧集列表（search 无 play_url 时用）。</summary>
    public async Task<SearchResult?> HydrateEpisodesAsync(
        IReadOnlyList<VideoSource> sources,
        SearchResult item,
        CancellationToken cancellationToken = default)
    {
        if (item.Episodes.Count > 0)
        {
            return item;
        }

        var source = VisibleSources(sources).FirstOrDefault(s => s.Key == item.Source);
        if (source is null || string.IsNullOrWhiteSpace(item.Id))
        {
            return item;
        }

        try
        {
            var results = await ClientFor(source).GetByIdAsync(source, item.Id, cancellationToken).ConfigureAwait(false);
            if (IsHidden(source.Key))
            {
                return item;
            }

            var match = results.FirstOrDefault(r => r.Id == item.Id)
                ?? results.FirstOrDefault(r => CatalogTitleMatcher.Matches(r.Title, item.Title))
                ?? results.FirstOrDefault();
            if (match is null)
            {
                return item;
            }

            // 保留原 SourceName（含线路后缀），替换剧集
            var expanded = SearchResultSources.Expand(match);
            var line = expanded.FirstOrDefault(r =>
                    string.Equals(r.PlaybackSourceKey, item.PlaybackSourceKey, StringComparison.Ordinal))
                ?? expanded.FirstOrDefault(r => r.Episodes.Count > 0)
                ?? match;

            return item with
            {
                Episodes = line.Episodes,
                EpisodeLabels = line.EpisodeLabels,
                PlaybackSources = match.PlaybackSources,
                PlaybackSourceKey = line.PlaybackSourceKey ?? item.PlaybackSourceKey,
                Poster = string.IsNullOrWhiteSpace(item.Poster) ? line.Poster : item.Poster,
                Description = string.IsNullOrWhiteSpace(item.Description) ? line.Description : item.Description,
                Year = string.IsNullOrWhiteSpace(item.Year) ? line.Year : item.Year,
            };
        }
        catch
        {
            return item;
        }
    }

    private async Task<IReadOnlyList<SearchResult>[]> SearchSourcesConcurrentlyAsync(
        IReadOnlyList<VideoSource> sources,
        string query,
        CancellationToken cancellationToken)
    {
        sources = VisibleSources(sources);
        if (sources.Count == 0)
        {
            return [];
        }

        // 控制同时在飞的源站请求，避免 TVBox 多源把 HttpClient/代理打满导致原有源超时消失。
        var concurrency = Math.Clamp(sources.Count, 1, MaxConcurrentSourceQueries);
        using var gate = new SemaphoreSlim(concurrency, concurrency);
        var tasks = sources.Select(async source =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await SafeSearchAsync(source, query, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        });
        var perSource = await Task.WhenAll(tasks).ConfigureAwait(false);
        return perSource.Select(VisibleResults).ToArray();
    }

    private async Task<IReadOnlyList<SearchResult>> SafeHomePageAsync(
        VideoSource source, string type, string tag, int pageStart, int pageSize, CancellationToken cancellationToken)
    {
        try
        {
            return await ClientFor(source).GetHomePageAsync(source, type, tag, pageStart, pageSize, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return [];
        }
    }

    private async Task<IReadOnlyList<SearchResult>> SafeSearchAsync(VideoSource source, string query, CancellationToken cancellationToken)
    {
        try
        {
            return await ClientFor(source).SearchAsync(source, query, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return [];
        }
    }

    private static CatalogItem ToCatalogItem(SearchResult result) =>
        new(result.Id, result.Title, result.Poster, result.Source, result.SourceName, result.Year);

    private static string GetDedupeKey(SearchResult item)
    {
        var title = CatalogTitleMatcher.Normalize(item.Title);
        var year = CatalogTitleMatcher.Normalize(item.Year);
        var poster = CatalogTitleMatcher.Normalize(item.Poster);

        if (!string.IsNullOrEmpty(title))
        {
            return string.IsNullOrEmpty(year) ? $"title:{title}" : $"title:{title}:year:{year}";
        }

        return string.IsNullOrEmpty(poster) ? "" : $"poster:{poster}";
    }

    private static List<SearchResult> MixSourceResults(
        IReadOnlyList<IReadOnlyList<SearchResult>> sourceResults,
        int limit,
        Func<SearchResult, string> getDedupeKey)
    {
        var mixed = new List<SearchResult>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;

        while (mixed.Count < limit)
        {
            var appended = false;
            foreach (var results in sourceResults)
            {
                if (index < results.Count)
                {
                    var item = results[index];
                    var key = getDedupeKey(item);
                    if (string.IsNullOrEmpty(key) || !seen.Contains(key))
                    {
                        if (!string.IsNullOrEmpty(key))
                        {
                            seen.Add(key);
                        }

                        mixed.Add(item);
                    }

                    appended = true;
                    if (mixed.Count >= limit)
                    {
                        break;
                    }
                }
            }

            if (!appended)
            {
                break;
            }

            index++;
        }

        return mixed;
    }
}
