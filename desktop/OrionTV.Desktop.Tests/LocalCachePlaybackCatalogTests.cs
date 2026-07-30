using OrionTV.Desktop.App;

namespace OrionTV.Desktop.Tests;

public sealed class LocalCachePlaybackCatalogTests
{
    [Fact]
    public void Selecting_sixth_download_keeps_all_six_episodes_and_actual_episode_labels()
    {
        var root = Path.Combine(Path.GetTempPath(), $"orion-local-cache-series-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var entries = Enumerable.Range(1, 6)
                .Select(episode => CreateEntry(root, "测试剧", episode))
                .ToList();
            var staleThird = CreateEntry(root, "测试剧", 3, "episode-3-stale") with
            {
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
            };
            entries.Add(staleThird);

            var groups = LocalCachePlaybackCatalog.Build(entries);
            var group = Assert.Single(groups);
            var selection = LocalCachePlaybackCatalog.FindByDirectory(groups, entries[5].Directory);

            Assert.NotNull(selection);
            Assert.Same(group, selection.Group);
            Assert.Equal(5, selection.EpisodeIndex);
            var request = selection.ToPlayParameter();
            Assert.Equal(5, request.EpisodeIndex);
            Assert.Equal(6, request.Episodes.Count);
            Assert.Equal(group.EpisodeLabels, request.EpisodeLabels);
            Assert.Equal(6, group.Episodes.Count);
            Assert.Equal(
                ["第1集", "第2集", "第3集", "第4集", "第5集", "第6集"],
                group.EpisodeLabels);
            Assert.Equal(group, LocalCachePlaybackCatalog.FindByTitle(groups, "测试剧 第6集"));
            Assert.Equal(
                5,
                LocalCachePlaybackCatalog.FindByEpisodePath(groups, [entries[5].PlaylistPath])?.EpisodeIndex);
            Assert.Equal(
                2,
                LocalCachePlaybackCatalog.FindByDirectory(groups, staleThird.Directory)?.EpisodeIndex);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Cache_items_without_episode_numbers_remain_independent_movies()
    {
        var root = Path.Combine(Path.GetTempPath(), $"orion-local-cache-movies-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var first = CreateEntry(root, "同名电影", null, "first");
            var second = CreateEntry(root, "同名电影", null, "second");

            var groups = LocalCachePlaybackCatalog.Build([first, second]);

            Assert.Equal(2, groups.Count);
            Assert.All(groups, group => Assert.Single(group.Episodes));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static VodCacheEntry CreateEntry(
        string root,
        string title,
        int? episode,
        string? directoryName = null)
    {
        var directory = Path.Combine(root, directoryName ?? $"episode-{episode}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "merged.ts");
        File.WriteAllBytes(path, [1, 2, 3]);
        return new VodCacheEntry(
            directory,
            path,
            episode is { } number ? $"{title} 第{number}集" : title,
            DateTimeOffset.UtcNow.AddMinutes(episode ?? 0),
            IsComplete: true,
            CompletedSegments: 1,
            TotalSegments: 1,
            SizeBytes: 3);
    }
}
