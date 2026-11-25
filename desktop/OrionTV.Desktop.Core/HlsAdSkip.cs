using System.Globalization;
using System.Text.RegularExpressions;

namespace OrionTV.Desktop.Core;

/// <summary>HLS 广告时间区间（毫秒）。</summary>
public sealed record AdRange(double StartMs, double EndMs);

/// <summary>
/// 跳广告检测，三级逐级回落（见 <see cref="DetectAsync"/>）：
/// 1) 显式广告标记 #EXT-X-CUE-OUT / #EXT-X-CUE-IN（移植自安卓 services/m3u8.ts，可靠）。
/// 2) 片头片尾黑场 + 静音段（<see cref="BlackSilenceAdDetector"/>，按分片字节率近似，不解码）。
/// 3) 异源段启发式：广告段来自与正片不同的 CDN 时，将这些连续段识别为广告（带保护阈值，避免误伤）。
/// 对单 host、无标记、片头片尾也没有黑场静音的源不会误判；烧录进正片中段的广告无法处理。
/// </summary>
public sealed partial class HlsAdSkip(HttpClient httpClient, ISegmentProbe? segmentProbe = null)
{
    private readonly ISegmentProbe _segmentProbe = segmentProbe ?? new HttpSegmentProbe(httpClient);

    /// <summary>第二级（黑场 + 静音探测）的限时；超时算这一级失败，降到异源段启发式。</summary>
    public TimeSpan BlackSilenceTimeout { get; init; } = TimeSpan.FromSeconds(8);

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
        return (await DetectAsync(url, cancellationToken).ConfigureAwait(false)).Ranges;
    }

    /// <summary>
    /// 多级识别，逐级回落：CUE 标记 → 片头片尾黑场 + 静音段 → 异源段启发式。
    /// 某一级没结果或失败就降到下一级；全部失败返回空区间（正常播放），任何情况下都不抛异常。
    /// </summary>
    public async Task<AdDetectionResult> DetectAsync(string url, CancellationToken cancellationToken = default)
    {
        var attempts = new List<AdTierAttempt>();
        if (string.IsNullOrWhiteSpace(url) || !M3u8UrlRegex().IsMatch(url.Trim()))
        {
            return AdDetectionResult.Empty();
        }

        string mediaUrl;
        string playlist;
        try
        {
            (mediaUrl, playlist) = await FetchResolvingMasterAsync(url, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return AdDetectionResult.Empty();
        }

        // 第一级：显式 CUE 标记。
        try
        {
            var markerRanges = ParseAdRanges(playlist);
            if (markerRanges.Count > 0)
            {
                attempts.Add(new AdTierAttempt(AdDetectionTier.Cue, AdTierOutcome.Hit));
                return new AdDetectionResult(markerRanges, AdDetectionTier.Cue, attempts);
            }

            attempts.Add(new AdTierAttempt(AdDetectionTier.Cue, AdTierOutcome.Miss, "没有 CUE 标记"));
        }
        catch (Exception ex)
        {
            attempts.Add(new AdTierAttempt(AdDetectionTier.Cue, AdTierOutcome.Error, ex.Message));
        }

        // 第二级：片头片尾黑场 + 静音段（要联网探测分片，单独限时）。
        try
        {
            using var tierCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            tierCts.CancelAfter(BlackSilenceTimeout);
            var outcome = await BlackSilenceAdDetector
                .DetectAsync(playlist, mediaUrl, _segmentProbe, tierCts.Token)
                .ConfigureAwait(false);
            attempts.Add(new AdTierAttempt(AdDetectionTier.BlackSilence, outcome.Result, outcome.Detail));
            if (outcome.Result == AdTierOutcome.Hit && outcome.Ranges.Count > 0)
            {
                return new AdDetectionResult(outcome.Ranges, AdDetectionTier.BlackSilence, attempts);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return AdDetectionResult.Empty(attempts);
        }
        catch (OperationCanceledException)
        {
            attempts.Add(new AdTierAttempt(AdDetectionTier.BlackSilence, AdTierOutcome.Error, "探测超时"));
        }
        catch (Exception ex)
        {
            attempts.Add(new AdTierAttempt(AdDetectionTier.BlackSilence, AdTierOutcome.Error, ex.Message));
        }

        // 第三级：异源段启发式。
        try
        {
            var foreignRanges = ParseForeignHostAdRanges(playlist, mediaUrl);
            if (foreignRanges.Count > 0)
            {
                attempts.Add(new AdTierAttempt(AdDetectionTier.ForeignHost, AdTierOutcome.Hit));
                return new AdDetectionResult(foreignRanges, AdDetectionTier.ForeignHost, attempts);
            }

            attempts.Add(new AdTierAttempt(AdDetectionTier.ForeignHost, AdTierOutcome.Miss, "没有异源段"));
        }
        catch (Exception ex)
        {
            attempts.Add(new AdTierAttempt(AdDetectionTier.ForeignHost, AdTierOutcome.Error, ex.Message));
        }

        return AdDetectionResult.Empty(attempts);
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
