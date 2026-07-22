using System.Text.RegularExpressions;

namespace OrionTV.Desktop.Core;

public sealed record LocalMediaEpisode(
    string Path,
    int? Number = null);

public sealed record LocalMediaItem(
    string Id,
    string Title,
    string Poster,
    IReadOnlyList<LocalMediaEpisode> Episodes,
    long SaveTime = 0);

public sealed record LocalMediaName(string Title, int? EpisodeNumber);

/// <summary>从常见下载文件名中提取影视标题和集数。</summary>
public static partial class LocalMediaNaming
{
    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".webm", ".ts", ".m2ts", ".flv", ".m3u8",
        ".mp3", ".flac", ".wav",
    };

    private static readonly HashSet<string> PosterExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".bmp",
    };

    public static bool IsSupportedMediaFile(string path) =>
        !string.IsNullOrWhiteSpace(path)
        && MediaExtensions.Contains(Path.GetExtension(path));

    public static LocalMediaName Parse(string value)
    {
        var fileName = Path.GetFileName(value)?.Trim() ?? "";
        var raw = IsSupportedMediaFile(fileName)
            ? Path.GetFileNameWithoutExtension(fileName)?.Trim() ?? ""
            : fileName;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new LocalMediaName("本地视频", null);
        }

        var numericName = raw.Trim(' ', '[', ']', '(', ')');
        if (int.TryParse(numericName, out var numericEpisode)
            && numericEpisode is > 0 and <= 999
            && Path.GetDirectoryName(value) is { Length: > 0 } directory)
        {
            var directoryTitle = CleanTitle(Path.GetFileName(directory));
            if (!string.IsNullOrWhiteSpace(directoryTitle))
            {
                return new LocalMediaName(directoryTitle, numericEpisode);
            }
        }

        foreach (var pattern in EpisodePatterns())
        {
            var match = pattern.Match(raw);
            if (!match.Success
                || !int.TryParse(match.Groups["episode"].Value, out var episode)
                || episode <= 0)
            {
                continue;
            }

            var title = CleanTitle(match.Groups["title"].Value);
            if (!string.IsNullOrWhiteSpace(title))
            {
                return new LocalMediaName(title, episode);
            }
        }

        return new LocalMediaName(CleanTitle(raw), null);
    }

    public static string NormalizeTitle(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? ""
            : new string(value.Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    internal static IReadOnlyList<LocalMediaImportGroup> GroupFiles(IEnumerable<string> paths)
    {
        var files = paths
            .Where(IsSupportedMediaFile)
            .Select(TryGetFullPath)
            .Where(path => path is not null && File.Exists(path))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => new { Path = path, Name = Parse(path) })
            .ToList();

        var groups = files.GroupBy(file =>
            file.Name.EpisodeNumber is not null
                ? $"series:{NormalizeTitle(file.Name.Title)}"
                : $"file:{file.Path}",
            StringComparer.OrdinalIgnoreCase);

        return groups.Select(group =>
        {
            var first = group.First();
            var episodes = group
                .Select(file => new LocalMediaEpisode(
                    file.Path,
                    file.Name.EpisodeNumber))
                .OrderBy(episode => episode.Number ?? int.MaxValue)
                .ThenBy(episode => episode.Path, NaturalPathComparer.Instance)
                .ToList();
            return new LocalMediaImportGroup(
                first.Name.Title,
                ResolvePoster(episodes.Select(episode => episode.Path), first.Name.Title),
                episodes);
        }).ToList();
    }

    private static string CleanTitle(string value)
    {
        var cleaned = value.Replace('.', ' ').Replace('_', ' ').Trim(' ', '-', '[', ']', '(', ')');
        return WhitespaceRegex().Replace(cleaned, " ");
    }

    private static string? TryGetFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return null;
        }
    }

    private static string ResolvePoster(IEnumerable<string> mediaPaths, string title)
    {
        foreach (var mediaPath in mediaPaths)
        {
            var directory = Path.GetDirectoryName(mediaPath);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                continue;
            }

            var stem = Path.GetFileNameWithoutExtension(mediaPath);
            var preferredNames = new[] { stem, title, "poster", "cover", "folder", "thumb" };
            foreach (var name in preferredNames.Where(name => !string.IsNullOrWhiteSpace(name)))
            {
                foreach (var extension in PosterExtensions)
                {
                    var candidate = Path.Combine(directory, name + extension);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
        }

        return "";
    }

    [GeneratedRegex(@"^(?<title>.+?)[\s._\-\[]*[Ss]\d{1,2}[\s._\-]*[Ee](?<episode>\d{1,3})(?:\D.*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex SeasonEpisodeRegex();

    [GeneratedRegex(@"^(?<title>.+?)[\s._\-\[]+[Ee](?:[Pp])?(?<episode>\d{1,3})(?:\D.*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex EpisodeRegex();

    [GeneratedRegex(@"^(?<title>.+?)[\s._\-]*第\s*(?<episode>\d{1,3})\s*[集话](?:.*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex ChineseEpisodeRegex();

    [GeneratedRegex(@"^(?<title>.+?)[\s._\-]+(?<episode>\d{1,3})(?:\s*[集话])?(?:\D.*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrailingEpisodeRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    private static IEnumerable<Regex> EpisodePatterns()
    {
        yield return SeasonEpisodeRegex();
        yield return EpisodeRegex();
        yield return ChineseEpisodeRegex();
        yield return TrailingEpisodeRegex();
    }

    private sealed class NaturalPathComparer : IComparer<string>
    {
        internal static NaturalPathComparer Instance { get; } = new();

        public int Compare(string? left, string? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;

            var leftParts = NaturalPartRegex().Matches(left);
            var rightParts = NaturalPartRegex().Matches(right);
            for (var index = 0; index < Math.Min(leftParts.Count, rightParts.Count); index++)
            {
                var leftPart = leftParts[index].Value;
                var rightPart = rightParts[index].Value;
                int comparison;
                if (long.TryParse(leftPart, out var leftNumber) && long.TryParse(rightPart, out var rightNumber))
                {
                    comparison = leftNumber.CompareTo(rightNumber);
                }
                else
                {
                    comparison = string.Compare(leftPart, rightPart, StringComparison.OrdinalIgnoreCase);
                }

                if (comparison != 0)
                {
                    return comparison;
                }
            }

            return leftParts.Count != rightParts.Count
                ? leftParts.Count.CompareTo(rightParts.Count)
                : string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    [GeneratedRegex(@"\d+|\D+")]
    private static partial Regex NaturalPartRegex();
}

internal sealed record LocalMediaImportGroup(
    string Title,
    string Poster,
    IReadOnlyList<LocalMediaEpisode> Episodes);

public sealed class LocalMediaStore : JsonDictionaryStore<LocalMediaItem>
{
    public LocalMediaStore() : this(DesktopPaths.LocalMediaFile)
    {
    }

    public LocalMediaStore(string filePath) : base(filePath)
    {
    }

    public LocalMediaItem? Get(string id) =>
        string.IsNullOrWhiteSpace(id) ? null : GetAll().GetValueOrDefault(id);

    public IReadOnlyList<LocalMediaItem> GetItems() =>
        GetAll().Values.OrderByDescending(item => item.SaveTime).ToList();

    public IReadOnlyList<LocalMediaItem> Import(IEnumerable<string> paths)
    {
        var groups = LocalMediaNaming.GroupFiles(paths);
        if (groups.Count == 0)
        {
            return [];
        }

        var importedIds = new List<string>();
        Mutate(all =>
        {
            foreach (var group in groups)
            {
                var normalizedTitle = LocalMediaNaming.NormalizeTitle(group.Title);
                var existing = all.Values.FirstOrDefault(item =>
                    LocalMediaNaming.NormalizeTitle(item.Title) == normalizedTitle);
                var id = existing?.Id ?? Guid.NewGuid().ToString("N");
                var episodes = (existing?.Episodes ?? [])
                    .Concat(group.Episodes)
                    .GroupBy(episode => episode.Path, StringComparer.OrdinalIgnoreCase)
                    .Select(values => values.Last())
                    .OrderBy(episode => episode.Number ?? int.MaxValue)
                    .ThenBy(episode => episode.Path, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var poster = !string.IsNullOrWhiteSpace(group.Poster)
                    ? group.Poster
                    : existing?.Poster ?? "";
                all[id] = new LocalMediaItem(
                    id,
                    group.Title,
                    poster,
                    episodes,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                importedIds.Add(id);
            }
        });

        var current = GetAll();
        return importedIds.Distinct(StringComparer.Ordinal)
            .Select(id => current.GetValueOrDefault(id))
            .Where(item => item is not null)
            .Cast<LocalMediaItem>()
            .ToList();
    }

    public void Remove(string id)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            Mutate(all => all.Remove(id));
        }
    }
}
