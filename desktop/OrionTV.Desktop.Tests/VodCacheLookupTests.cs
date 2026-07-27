using OrionTV.Desktop.App;

namespace OrionTV.Desktop.Tests;

public sealed class VodCacheLookupTests
{
    [Fact]
    public void ExcludesIncompleteEntries()
    {
        var lookup = VodCacheLookup.FromEntries([
            Entry("Series 第1集", "https://video.example/1.m3u8", isComplete: false),
        ]);

        Assert.Null(lookup.FindCompleted("Series 第1集", "https://video.example/1.m3u8"));
    }

    [Fact]
    public void PrefersMatchingSourceOverNewerEntry()
    {
        var matching = Entry(
            "Series 第1集",
            "https://video.example/preferred.m3u8",
            createdAt: new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero));
        var newer = Entry(
            "Series 第1集",
            "https://video.example/newer.m3u8",
            createdAt: new DateTimeOffset(2026, 7, 21, 0, 0, 0, TimeSpan.Zero));
        var lookup = VodCacheLookup.FromEntries([matching, newer]);

        Assert.Same(matching, lookup.FindCompleted("Series 第1集", matching.SourceUrl));
    }

    [Fact]
    public void ReturnsNewestEntryWhenSourceDoesNotMatch()
    {
        var older = Entry(
            "Series 第1集",
            "https://video.example/older.m3u8",
            createdAt: new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero));
        var newer = Entry(
            "Series 第1集",
            "https://video.example/newer.m3u8",
            createdAt: new DateTimeOffset(2026, 7, 21, 0, 0, 0, TimeSpan.Zero));
        var lookup = VodCacheLookup.FromEntries([older, newer]);

        Assert.Same(newer, lookup.FindCompleted("Series 第1集", "https://video.example/missing.m3u8"));
    }

    private static VodCacheEntry Entry(
        string title,
        string sourceUrl,
        bool isComplete = true,
        DateTimeOffset? createdAt = null) =>
        new(
            Directory: $"cache/{Guid.NewGuid():N}",
            PlaylistPath: $"cache/{Guid.NewGuid():N}/offline.m3u8",
            Title: title,
            CreatedAt: createdAt ?? DateTimeOffset.UtcNow,
            IsComplete: isComplete,
            CompletedSegments: isComplete ? 1 : 0,
            TotalSegments: 1,
            SizeBytes: 1,
            SourceUrl: sourceUrl);
}
