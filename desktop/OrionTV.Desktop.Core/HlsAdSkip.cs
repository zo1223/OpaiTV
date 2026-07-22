using System.Globalization;
using System.Text.RegularExpressions;

namespace OrionTV.Desktop.Core;

/// <summary>HLS 广告时间区间（毫秒）。</summary>
public sealed record AdRange(double StartMs, double EndMs);

/// <summary>
/// 跳广告检测。两种安全信号：
/// 1) 显式广告标记 #EXT-X-CUE-OUT / #EXT-X-CUE-IN（移植自安卓 services/m3u8.ts，可靠）。
/// 2) 异源段启发式：广告段来自与正片不同的 CDN 时，将这些连续段识别为广告（带保护阈值，避免误伤）。
/// 对单 host、无标记的源不会误判；对烧录进画面的广告无法处理（播放列表层面无信号）。
/// </summary>
public sealed partial class HlsAdSkip(HttpClient httpClient)
{
    // 异源段总占比超过该比例则视为无法可靠区分正片/广告，放弃跳过。
    private const double MaxForeignFraction = 0.5;

    // 单段广告区间上限（毫秒），超过则不视为广告，避免误跳长段正片。
    private const double MaxAdRunMs = 600_000;

    [GeneratedRegex(@"^#EXT-X-CUE-OUT(?::|$)")]
    private static partial Regex CueOutRegex();

    [GeneratedRegex(@"^#EXT-X-CUE-IN(?::|$)")]
    private static partial Regex CueInRegex();

    [GeneratedRegex(@"^#EXTINF:([\d.]+)")]
    private static partial Regex ExtInfRegex();

    [GeneratedRegex(@"\.m3u8(?:[?#]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex M3u8UrlRegex();

    /// <summary>按 CUE-OUT / CUE-IN 标记解析广告区间（与安卓算法一致）。</summary>
    public static IReadOnlyList<AdRange> ParseAdRanges(string playlist)
    {
        var ranges = new List<AdRange>();
        double elapsedMillis = 0;
        double? currentAdStartMillis = null;

        foreach (var rawLine in playlist.Split('\n'))
        {
            var line = rawLine.Trim();

            if (CueOutRegex().IsMatch(line))
            {
                currentAdStartMillis = elapsedMillis;
                continue;
            }

            if (CueInRegex().IsMatch(line))
            {
                if (currentAdStartMillis is { } start && elapsedMillis > start)
                {
                    ranges.Add(new AdRange(start, elapsedMillis));
                }

                currentAdStartMillis = null;
                continue;
            }

            elapsedMillis += SegmentMillis(line);
        }

        return ranges;
    }

    /// <summary>异源段启发式：把来自非主 host 的连续段识别为广告区间。</summary>
    public static IReadOnlyList<AdRange> ParseForeignHostAdRanges(string playlist, string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
        {
            return [];
        }

        var segments = new List<(double Start, double Duration, string Host)>();
        double elapsed = 0;
        var lines = playlist.Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var millis = SegmentMillis(lines[i].Trim());
            if (millis <= 0)
            {
                continue;
            }

            var host = "";
            for (var j = i + 1; j < lines.Length; j++)
            {
                var candidate = lines[j].Trim();
                if (candidate.Length == 0 || candidate.StartsWith('#'))
                {
                    continue;
                }

                if (Uri.TryCreate(baseUri, candidate, out var segmentUri))
                {
                    host = segmentUri.Host;
                }

                break;
            }

            segments.Add((elapsed, millis, host));
            elapsed += millis;
        }

        if (segments.Count == 0)
        {
            return [];
        }

        var durationByHost = segments
            .GroupBy(segment => segment.Host)
            .ToDictionary(group => group.Key, group => group.Sum(segment => segment.Duration));
        if (durationByHost.Count < 2)
        {
            return [];
        }

        var dominantHost = durationByHost.OrderByDescending(entry => entry.Value).First().Key;
        var total = segments.Sum(segment => segment.Duration);
        var foreignTotal = total - durationByHost[dominantHost];
        if (total <= 0 || foreignTotal > total * MaxForeignFraction)
        {
            return [];
        }

        var ranges = new List<AdRange>();
        double? runStart = null;
        double runEnd = 0;

        foreach (var segment in segments)
        {
            var isForeign = !string.IsNullOrEmpty(segment.Host) && segment.Host != dominantHost;
            if (isForeign)
            {
                runStart ??= segment.Start;
                runEnd = segment.Start + segment.Duration;
            }
            else
            {
                AppendRun(ranges, runStart, runEnd);
                runStart = null;
            }
        }

        AppendRun(ranges, runStart, runEnd);
        return ranges;
    }

    public async Task<IReadOnlyList<AdRange>> GetAdRangesAsync(string url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url) || !M3u8UrlRegex().IsMatch(url.Trim()))
        {
            return [];
        }

        try
        {
            var (mediaUrl, playlist) = await FetchResolvingMasterAsync(url, cancellationToken).ConfigureAwait(false);

            var markerRanges = ParseAdRanges(playlist);
            if (markerRanges.Count > 0)
            {
                return markerRanges;
            }

            return ParseForeignHostAdRanges(playlist, mediaUrl);
        }
        catch
        {
            return [];
        }
    }

    private async Task<(string Url, string Playlist)> FetchResolvingMasterAsync(string url, CancellationToken cancellationToken)
    {
        var playlist = await FetchTextAsync(url, cancellationToken).ConfigureAwait(false);

        if (playlist.Contains("#EXT-X-STREAM-INF", StringComparison.Ordinal))
        {
            var variant = FirstVariant(playlist);
            if (variant is not null && Uri.TryCreate(new Uri(url), variant, out var variantUri))
            {
                try
                {
                    return (variantUri.ToString(), await FetchTextAsync(variantUri.ToString(), cancellationToken).ConfigureAwait(false));
                }
                catch
                {
                    // 变体取失败时退回主列表。
                }
            }
        }

        return (url, playlist);
    }

    private async Task<string> FetchTextAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string? FirstVariant(string playlist)
    {
        foreach (var rawLine in playlist.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length > 0 && !line.StartsWith('#') && line.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
            {
                return line;
            }
        }

        return null;
    }

    private static double SegmentMillis(string line)
    {
        var match = ExtInfRegex().Match(line);
        if (match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            return Math.Round(seconds * 1000, MidpointRounding.AwayFromZero);
        }

        return 0;
    }

    private static void AppendRun(List<AdRange> ranges, double? runStart, double runEnd)
    {
        if (runStart is { } start && runEnd > start && runEnd - start <= MaxAdRunMs)
        {
            ranges.Add(new AdRange(start, runEnd));
        }
    }
}
