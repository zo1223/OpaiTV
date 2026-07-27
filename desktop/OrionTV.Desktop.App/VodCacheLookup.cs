namespace OrionTV.Desktop.App;

internal sealed class VodCacheLookup
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<VodCacheEntry>> _entriesByTitle;

    private VodCacheLookup(IReadOnlyDictionary<string, IReadOnlyList<VodCacheEntry>> entriesByTitle)
    {
        _entriesByTitle = entriesByTitle;
    }

    public static VodCacheLookup Empty { get; } = new(
        new Dictionary<string, IReadOnlyList<VodCacheEntry>>(StringComparer.OrdinalIgnoreCase));

    public static VodCacheLookup FromEntries(IEnumerable<VodCacheEntry> entries)
    {
        var byTitle = entries
            .Where(entry => entry.IsComplete && !string.IsNullOrWhiteSpace(entry.PlaylistPath))
            .GroupBy(entry => entry.Title.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<VodCacheEntry>)group
                    .OrderByDescending(entry => entry.CreatedAt)
                    .ToList(),
                StringComparer.OrdinalIgnoreCase);
        return new VodCacheLookup(byTitle);
    }

    public VodCacheEntry? FindCompleted(string title, string sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(title)
            || !_entriesByTitle.TryGetValue(title.Trim(), out var entries))
        {
            return null;
        }

        return entries.FirstOrDefault(entry =>
                !string.IsNullOrWhiteSpace(sourceUrl)
                && string.Equals(entry.SourceUrl, sourceUrl, StringComparison.OrdinalIgnoreCase))
            ?? entries.FirstOrDefault();
    }
}
