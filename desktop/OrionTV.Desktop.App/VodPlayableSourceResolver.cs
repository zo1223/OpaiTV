using System.Collections.Concurrent;
using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.App;

internal sealed record VodPlayableSourceResolution(
    IReadOnlyList<SearchResult> Candidates,
    IReadOnlyList<SearchResult> PlayableSources)
{
    public CatalogPlayabilityStatus Status => PlayableSources.Count > 0
        ? CatalogPlayabilityStatus.Playable
        : CatalogPlayabilityStatus.Unavailable;

    public CatalogItem ApplyTo(CatalogItem item) => item with
    {
        PlayabilityStatus = Status,
        PlayableSourceCount = PlayableSources.Count,
        CandidateSources = Candidates,
        PlayableSources = PlayableSources,
    };
}

internal sealed class VodPlayableSourceResolver : IDisposable
{
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<SearchResult>>> _loadCandidates;
    private readonly Func<SearchResult, CancellationToken, Task<SearchResult?>> _hydrate;
    private readonly Func<
        IReadOnlyList<SearchResult>,
        Action<string, VodSourceHealthResult>,
        CancellationToken,
        Task> _probeMany;
    private readonly object _sync = new();
    private readonly Dictionary<string, VodPlayableSourceResolution> _cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, VodPlayableSourceResolution> _latestByTitle = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<VodPlayableSourceResolution>> _inflight = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private int _generation;
    private bool _disposed;

    public VodPlayableSourceResolver(
        Func<string, CancellationToken, Task<IReadOnlyList<SearchResult>>> loadCandidates,
        Func<SearchResult, CancellationToken, Task<SearchResult?>> hydrate,
        Func<
            IReadOnlyList<SearchResult>,
            Action<string, VodSourceHealthResult>,
            CancellationToken,
            Task> probeMany)
    {
        _loadCandidates = loadCandidates;
        _hydrate = hydrate;
        _probeMany = probeMany;
    }

    public bool TryGetLatest(string title, out VodPlayableSourceResolution resolution)
    {
        var key = CatalogTitleMatcher.Normalize(title);
        lock (_sync)
        {
            return _latestByTitle.TryGetValue(key, out resolution!);
        }
    }

    public async Task<VodPlayableSourceResolution> ResolveAsync(
        string title,
        IReadOnlyList<SearchResult>? candidateSnapshot = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedTitle = CatalogTitleMatcher.Normalize(title);
        var suppliedCandidates = candidateSnapshot is null
            ? null
            : NormalizeCandidates(title, candidateSnapshot);
        var cacheKey = suppliedCandidates is null
            ? $"auto:{normalizedTitle}"
            : $"snapshot:{normalizedTitle}:{Fingerprint(suppliedCandidates)}";

        Task<VodPlayableSourceResolution> task;
        CancellationTokenSource? taskCancellation = null;
        var created = false;
        lock (_sync)
        {
            if (_cache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }

            if (!_inflight.TryGetValue(cacheKey, out task!))
            {
                var generation = _generation;
                taskCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    _lifetime.Token,
                    cancellationToken);
                task = ResolveAndCacheAsync(
                    cacheKey,
                    normalizedTitle,
                    title,
                    suppliedCandidates,
                    generation,
                    taskCancellation.Token);
                _inflight[cacheKey] = task;
                created = true;
            }
        }

        if (created)
        {
            _ = RemoveInflightWhenCompleteAsync(cacheKey, task, taskCancellation!);
        }

        return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Invalidate()
    {
        lock (_sync)
        {
            unchecked
            {
                _generation++;
            }

            _cache.Clear();
            _latestByTitle.Clear();
            _inflight.Clear();
        }
    }

    private async Task<VodPlayableSourceResolution> ResolveAndCacheAsync(
        string cacheKey,
        string normalizedTitle,
        string title,
        IReadOnlyList<SearchResult>? suppliedCandidates,
        int generation,
        CancellationToken cancellationToken)
    {
        await Task.Yield();
        var loaded = suppliedCandidates
            ?? NormalizeCandidates(
                title,
                await _loadCandidates(title, cancellationToken).ConfigureAwait(false));
        var hydrated = new List<SearchResult>(loaded.Count);
        foreach (var candidate in loaded)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = candidate.Episodes.Count == 0
                ? await _hydrate(candidate, cancellationToken).ConfigureAwait(false) ?? candidate
                : candidate;
            hydrated.Add(item);
        }

        var candidates = NormalizeCandidates(title, hydrated);
        var health = new ConcurrentDictionary<string, VodSourceHealthResult>(StringComparer.Ordinal);
        if (candidates.Count > 0)
        {
            await _probeMany(
                    candidates,
                    (key, result) => health[key] = result,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var playable = candidates
            .Where(item =>
                health.TryGetValue(SearchResultSources.SelectionKeyOf(item), out var result)
                && result.Status == VodSourceProbeStatus.Playable)
            .ToList();
        var resolution = new VodPlayableSourceResolution(candidates, playable);
        lock (_sync)
        {
            if (!_disposed && generation == _generation)
            {
                _cache[cacheKey] = resolution;
                _latestByTitle[normalizedTitle] = resolution;
            }
        }

        return resolution;
    }

    private async Task RemoveInflightWhenCompleteAsync(
        string cacheKey,
        Task<VodPlayableSourceResolution> task,
        CancellationTokenSource taskCancellation)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
        }

        lock (_sync)
        {
            if (_inflight.TryGetValue(cacheKey, out var current) && ReferenceEquals(current, task))
            {
                _inflight.Remove(cacheKey);
            }
        }

        taskCancellation.Dispose();
    }

    private static IReadOnlyList<SearchResult> NormalizeCandidates(
        string title,
        IEnumerable<SearchResult> candidates)
    {
        return candidates
            .SelectMany(item => item.PlaybackSourceKey is null
                ? SearchResultSources.Expand(item)
                : [item])
            .Where(item => CatalogTitleMatcher.Matches(item.Title, title))
            .GroupBy(SearchResultSources.SelectionKeyOf, StringComparer.Ordinal)
            .Select(items => items.First())
            .ToList();
    }

    private static string Fingerprint(IEnumerable<SearchResult> candidates) =>
        string.Join(
            '\u001e',
            candidates
                .OrderBy(SearchResultSources.SelectionKeyOf, StringComparer.Ordinal)
                .Select(item =>
                    $"{SearchResultSources.SelectionKeyOf(item)}\u001d{item.Episodes.Count}\u001d{item.Episodes.FirstOrDefault()}\u001d{item.Episodes.LastOrDefault()}"));

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _cache.Clear();
            _latestByTitle.Clear();
            _inflight.Clear();
        }

        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
