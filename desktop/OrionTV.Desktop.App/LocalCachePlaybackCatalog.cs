using System.IO;
using System.Security.Cryptography;
using System.Text;
using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.App;

internal sealed record LocalCachePlaybackGroup(
    string Id,
    string Title,
    string Poster,
    IReadOnlyList<string> Episodes,
    IReadOnlyList<string> EpisodeLabels,
    IReadOnlyDictionary<string, int> EntryIndexes,
    DateTimeOffset SortTime)
{
    internal int FindEpisodeIndexByDirectory(string directory) =>
        EntryIndexes.TryGetValue(directory, out var index) ? index : -1;

    internal int FindEpisodeIndexByPath(string path)
    {
        var episode = LocalCachePlaybackCatalog.NormalizePlayableUri(path);
        return string.IsNullOrWhiteSpace(episode)
            ? -1
            : Episodes.ToList().FindIndex(candidate =>
                string.Equals(candidate, episode, StringComparison.OrdinalIgnoreCase));
    }
}

internal sealed record LocalCachePlaybackSelection(
    LocalCachePlaybackGroup Group,
    int EpisodeIndex)
{
    internal PlayParameter ToPlayParameter()
    {
        var source = new SearchResult(
            Group.Id,
            Group.Title,
            Group.Poster,
            Group.Episodes,
            "local",
            "本地",
            "",
            EpisodeLabels: Group.EpisodeLabels);
        return new PlayParameter(
            Group.Title,
            "local",
            "本地",
            Group.Id,
            Group.Episodes,
            Math.Clamp(EpisodeIndex, 0, Group.Episodes.Count - 1),
            0,
            [source],
            Group.Poster,
            EpisodeLabels: Group.EpisodeLabels);
    }
}

internal static class LocalCachePlaybackCatalog
{
    internal static IReadOnlyList<LocalCachePlaybackGroup> Build(IEnumerable<VodCacheEntry> entries)
    {
        var parsed = entries
            .Select(entry => new ParsedEntry(
                entry,
                LocalMediaNaming.Parse(entry.Title),
                NormalizePlayableUri(entry.PlaylistPath)))
            .Where(item => !string.IsNullOrWhiteSpace(item.Uri))
            .ToList();

        return parsed
            .GroupBy(item =>
                item.Name.EpisodeNumber is not null
                    ? $"series:{LocalMediaNaming.NormalizeTitle(item.Name.Title)}"
                    : $"cache:{item.Entry.Directory}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => BuildGroup(group.Key, group))
            .OrderByDescending(group => group.SortTime)
            .ToList();
    }

    internal static LocalCachePlaybackSelection? FindByDirectory(
        IEnumerable<LocalCachePlaybackGroup> groups,
        string directory)
    {
        foreach (var group in groups)
        {
            var index = group.FindEpisodeIndexByDirectory(directory);
            if (index >= 0)
            {
                return new LocalCachePlaybackSelection(group, index);
            }
        }

        return null;
    }

    internal static LocalCachePlaybackSelection? FindByEpisodePath(
        IEnumerable<LocalCachePlaybackGroup> groups,
        IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            foreach (var group in groups)
            {
                var index = group.FindEpisodeIndexByPath(path);
                if (index >= 0)
                {
                    return new LocalCachePlaybackSelection(group, index);
                }
            }
        }

        return null;
    }

    internal static LocalCachePlaybackGroup? FindByTitle(
        IEnumerable<LocalCachePlaybackGroup> groups,
        string title)
    {
        var parsedTitle = LocalMediaNaming.Parse(title).Title;
        var normalizedTitle = LocalMediaNaming.NormalizeTitle(parsedTitle);
        return groups.FirstOrDefault(group =>
            LocalMediaNaming.NormalizeTitle(group.Title) == normalizedTitle);
    }

    internal static string NormalizePlayableUri(string path)
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

    private static LocalCachePlaybackGroup BuildGroup(
        string key,
        IEnumerable<ParsedEntry> entries)
    {
        var buckets = entries
            .GroupBy(item =>
                item.Name.EpisodeNumber?.ToString()
                ?? item.Entry.Directory,
                StringComparer.OrdinalIgnoreCase)
            .Select(items =>
            {
                var all = items.ToList();
                return new EpisodeBucket(
                    all.OrderByDescending(item => item.Entry.CreatedAt).First(),
                    all);
            })
            .OrderBy(bucket => bucket.Selected.Name.EpisodeNumber ?? int.MaxValue)
            .ThenBy(bucket => bucket.Selected.Entry.CreatedAt)
            .ToList();
        var selected = buckets.Select(bucket => bucket.Selected).ToList();
        var entryIndexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < buckets.Count; index++)
        {
            foreach (var item in buckets[index].Entries)
            {
                entryIndexes[item.Entry.Directory] = index;
            }
        }

        var title = selected[0].Name.Title;
        var poster = selected
            .Select(item => item.Entry.Poster)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            ?? "";

        return new LocalCachePlaybackGroup(
            StableCacheId(key),
            title,
            poster,
            selected.Select(item => item.Uri).ToList(),
            selected.Select(item => item.Name.EpisodeNumber is { } number ? $"第{number}集" : "").ToList(),
            entryIndexes,
            selected.Max(item => item.Entry.CreatedAt));
    }

    private static string StableCacheId(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return "cache-" + Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private sealed record ParsedEntry(
        VodCacheEntry Entry,
        LocalMediaName Name,
        string Uri);

    private sealed record EpisodeBucket(
        ParsedEntry Selected,
        IReadOnlyList<ParsedEntry> Entries);
}
