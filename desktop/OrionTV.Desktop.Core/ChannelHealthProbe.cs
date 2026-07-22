using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace OrionTV.Desktop.Core;

/// <summary>直播频道流畅度等级（对齐 RN 端 channelHealth.ts）。</summary>
public enum ChannelHealth
{
    /// <summary>尚未探测 / 无法探测。</summary>
    Unknown,

    /// <summary>不流畅（无法连接或下载速率跟不上码率）。</summary>
    Bad,

    /// <summary>一般（勉强能播，存在卡顿风险）。</summary>
    Fair,

    /// <summary>流畅（下载速率明显高于码率）。</summary>
    Good,
}

/// <summary>单个频道的流畅度探测结果。</summary>
public sealed record ChannelHealthResult(
    ChannelHealth Health,
    int Score,
    string Detail,
    double ThroughputMbps = 0,
    long TtfbMs = 0,
    string? Resolution = null)
{
    /// <summary>非 HTTP(S) 地址无法用网络探测（如 rtp/udp 组播）。</summary>
    public static readonly ChannelHealthResult NotProbeable = new(ChannelHealth.Unknown, 0, "不可探测");

    /// <summary>无法连接。</summary>
    public static readonly ChannelHealthResult Unreachable = new(ChannelHealth.Bad, 0, "无法连接");
}

/// <summary>
/// 流畅度评分（纯函数，便于单测；常量与 RN 端 channelHealth.ts 保持一致）。
/// 核心指标 ratio = 下载速率 / 所需码率（等价于 分片时长 / 下载耗时）：
/// ratio≥1.5 流畅、≥1.0 一般、否则不畅；首字节延迟过高再降一档。
/// </summary>
public static class ChannelHealthScoring
{
    public const double GoodRatio = 1.5;
    public const double FairRatio = 1.0;
    public const double GoodAbsBps = 3_000_000; // 无声明码率时按绝对速率：≥3Mbps 流畅
    public const double FairAbsBps = 1_000_000; // ≥1Mbps 一般
    public const long SlowTtfbMs = 3000;        // 首字节超过 3s 视为启动慢，降一档

    public static ChannelHealthResult Evaluate(
        bool reachable,
        long ttfbMs,
        double throughputBps,
        double? requiredBps,
        string? resolution = null)
    {
        if (!reachable)
        {
            return ChannelHealthResult.Unreachable with { TtfbMs = ttfbMs };
        }

        if (throughputBps <= 0)
        {
            // 能连通但未能测到速率（如纯主清单、无分片）。
            return new ChannelHealthResult(ChannelHealth.Fair, 40, "已连接·未测速", 0, ttfbMs, resolution);
        }

        double? ratio = requiredBps is > 0 ? throughputBps / requiredBps.Value : null;

        ChannelHealth category;
        double throughputScore;
        if (ratio is { } r)
        {
            category = r >= GoodRatio ? ChannelHealth.Good : r >= FairRatio ? ChannelHealth.Fair : ChannelHealth.Bad;
            throughputScore = Math.Clamp(r / GoodRatio, 0, 1) * 70;
        }
        else
        {
            category = throughputBps >= GoodAbsBps ? ChannelHealth.Good
                : throughputBps >= FairAbsBps ? ChannelHealth.Fair
                : ChannelHealth.Bad;
            throughputScore = Math.Clamp(throughputBps / GoodAbsBps, 0, 1) * 70;
        }

        if (ttfbMs > SlowTtfbMs)
        {
            category = Downgrade(category);
        }

        double latencyScore = Math.Clamp(1 - ttfbMs / (double)SlowTtfbMs, 0, 1) * 30;
        int score = ClampScoreToCategory((int)Math.Round(throughputScore + latencyScore), category);

        double mbps = throughputBps / 1_000_000d;
        string detail = $"{mbps:0.0} Mbps";
        if (ratio is { } rr && rr < FairRatio)
        {
            detail += " · 跟不上";
        }

        if (!string.IsNullOrEmpty(resolution))
        {
            detail += $" · {resolution}";
        }

        return new ChannelHealthResult(category, score, detail, mbps, ttfbMs, resolution);
    }

    /// <summary>等级对应的中文短标签（列表徽标用）。</summary>
    public static string LabelOf(ChannelHealth health) => health switch
    {
        ChannelHealth.Good => "流畅",
        ChannelHealth.Fair => "一般",
        ChannelHealth.Bad => "不畅",
        _ => string.Empty,
    };

    private static ChannelHealth Downgrade(ChannelHealth health) => health switch
    {
        ChannelHealth.Good => ChannelHealth.Fair,
        ChannelHealth.Fair => ChannelHealth.Bad,
        _ => ChannelHealth.Bad,
    };

    private static int ClampScoreToCategory(int score, ChannelHealth category) => category switch
    {
        ChannelHealth.Good => Math.Max(70, Math.Min(100, score)),
        ChannelHealth.Fair => Math.Clamp(score, 40, 69),
        ChannelHealth.Bad => Math.Clamp(score, 0, 39),
        _ => 0,
    };
}

public static class ChannelHealthDisplay
{
    public static string MetricsLabelOf(ChannelHealthResult result)
    {
        var parts = new List<string>(2);
        if (result.TtfbMs > 0)
        {
            parts.Add($"{result.TtfbMs}ms");
        }

        if (result.ThroughputMbps > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{result.ThroughputMbps:0.0}Mbps"));
        }

        return parts.Count > 0 ? string.Join(" · ", parts) : "未测速";
    }

    public static string SummaryOf(IEnumerable<ChannelHealthResult> results)
    {
        var measured = results
            .Where(result => result.TtfbMs > 0 || result.ThroughputMbps > 0)
            .ToList();
        if (measured.Count == 0)
        {
            return string.Empty;
        }

        var parts = new List<string> { $"已测 {measured.Count}" };
        var latencyValues = measured.Where(result => result.TtfbMs > 0).Select(result => result.TtfbMs).ToList();
        if (latencyValues.Count > 0)
        {
            parts.Add($"平均延迟 {(long)Math.Round(latencyValues.Average())}ms");
        }

        var throughputValues = measured.Where(result => result.ThroughputMbps > 0).Select(result => result.ThroughputMbps).ToList();
        if (throughputValues.Count > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"平均速率 {throughputValues.Average():0.0}Mbps"));
        }

        return string.Join(" · ", parts);
    }
}

/// <summary>
/// 频道流畅度网络探测：抓取 m3u8 → 选轨 → 下载首个分片测速 →（对比声明码率/分片时长）评分。
/// 不做完整解码，纯网络层估算，可对整张频道列表批量并发探测。
/// </summary>
public sealed class ChannelHealthProbe(HttpClient httpClient)
{
    private const int MaxProbeBytes = 2_500_000;            // 单个分片最多下载 2.5MB 用于测速
    private static readonly TimeSpan PlaylistTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan SegmentTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan SegmentReadTimeout = TimeSpan.FromSeconds(10); // 正文读取独立时限,防涓流占死并发槽

    private static readonly Regex BandwidthRegex = new(@"BANDWIDTH=(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ResolutionRegex = new(@"RESOLUTION=(\d+x\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ExtInfRegex = new(@"^#EXTINF:([\d.]+)", RegexOptions.Compiled);

    private readonly HttpClient _httpClient = httpClient;

    /// <summary>批量并发探测：每个频道完成即回调（回调在工作线程触发，调用方需自行 marshal 回 UI 线程）。</summary>
    public async Task ProbeManyAsync(
        IReadOnlyList<(string Key, string Url)> items,
        Action<string, ChannelHealthResult> onResult,
        int concurrency,
        CancellationToken cancellationToken)
    {
        using var gate = new SemaphoreSlim(Math.Max(1, concurrency));
        var tasks = items.Select(async item =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ChannelHealthResult result;
                try
                {
                    result = await ProbeAsync(item.Url, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    result = ChannelHealthResult.Unreachable;
                }

                onResult(item.Key, result);
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>探测单个频道地址。</summary>
    public async Task<ChannelHealthResult> ProbeAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return ChannelHealthResult.NotProbeable;
        }

        try
        {
            var (response, ttfbMs) = await TimedGetAsync(url, PlaylistTimeout, cancellationToken).ConfigureAwait(false);
            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    return ChannelHealthScoring.Evaluate(false, ttfbMs, 0, null);
                }

                var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
                var responseUri = response.RequestMessage?.RequestUri ?? uri;
                var looksHls = !LooksLikeRawStream(responseUri, contentType)
                    && (LooksLikeHls(responseUri, contentType) || LooksLikeHls(uri, contentType));

                if (looksHls)
                {
                    var playlist = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    return await ProbeHlsAsync(uri, playlist, ttfbMs, cancellationToken).ConfigureAwait(false);
                }

                // 原始流（.ts/.flv/直连）：直接对这条响应测速，无声明码率走绝对阈值。
                var (bps, _, _) = await MeasureStreamAsync(response, cancellationToken).ConfigureAwait(false);
                return ChannelHealthScoring.Evaluate(true, ttfbMs, bps, null);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return ChannelHealthScoring.Evaluate(false, (long)PlaylistTimeout.TotalMilliseconds, 0, null);
        }
    }

    private async Task<ChannelHealthResult> ProbeHlsAsync(Uri playlistUri, string playlistText, long ttfbMs, CancellationToken cancellationToken)
    {
        double? declaredBps = null;
        string? resolution = null;
        var mediaUri = playlistUri;
        var mediaText = playlistText;

        if (IsMasterPlaylist(playlistText))
        {
            var variant = SelectFirstVariant(playlistText);
            if (variant is null)
            {
                return ChannelHealthScoring.Evaluate(true, ttfbMs, 0, null, null);
            }

            declaredBps = variant.Value.Bps;
            resolution = variant.Value.Resolution;
            mediaUri = ResolveUri(playlistUri, variant.Value.Uri);

            var (variantResponse, _) = await TimedGetAsync(mediaUri.ToString(), PlaylistTimeout, cancellationToken).ConfigureAwait(false);
            using (variantResponse)
            {
                if (!variantResponse.IsSuccessStatusCode)
                {
                    return ChannelHealthScoring.Evaluate(true, ttfbMs, 0, declaredBps, resolution);
                }

                mediaText = await variantResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        var segment = SelectFirstSegment(mediaText);
        if (segment is null)
        {
            return ChannelHealthScoring.Evaluate(true, ttfbMs, 0, declaredBps, resolution);
        }

        var segmentUri = ResolveUri(mediaUri, segment.Value.Uri);
        var (segmentResponse, _) = await TimedGetAsync(segmentUri.ToString(), SegmentTimeout, cancellationToken).ConfigureAwait(false);
        using (segmentResponse)
        {
            if (!segmentResponse.IsSuccessStatusCode)
            {
                return ChannelHealthScoring.Evaluate(false, ttfbMs, 0, null);
            }

            var (bps, downloadedBytes, contentLength) = await MeasureStreamAsync(segmentResponse, cancellationToken).ConfigureAwait(false);

            double? requiredBps = declaredBps is > 0
                ? declaredBps
                : segment.Value.DurationSec > 0
                    ? (contentLength > 0 ? contentLength : downloadedBytes) * 8d / segment.Value.DurationSec
                    : null;

            return ChannelHealthScoring.Evaluate(true, ttfbMs, bps, requiredBps, resolution);
        }
    }

    private async Task<(HttpResponseMessage Response, long TtfbMs)> TimedGetAsync(string url, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", PlaybackMediaOptions.AndroidLikeUserAgent);
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && PlaybackMediaOptions.TryBuildHttpReferrer(uri) is { } referrer)
        {
            request.Headers.Referrer = new Uri(referrer);
        }

        var stopwatch = Stopwatch.StartNew();
        var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
            .ConfigureAwait(false);
        stopwatch.Stop();

        return (response, stopwatch.ElapsedMilliseconds);
    }

    private static async Task<(double Bps, long Bytes, long ContentLength)> MeasureStreamAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var contentLength = response.Content.Headers.ContentLength ?? 0;
        var stopwatch = Stopwatch.StartNew();
        // TimedGetAsync 的超时只覆盖响应头;正文必须有独立时限,
        // 否则涓流服务器(每秒几字节)会无限占住一个并发探测槽。
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readCts.CancelAfter(SegmentReadTimeout);

        long total = 0;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(readCts.Token).ConfigureAwait(false);

            var buffer = new byte[64 * 1024];
            int read;
            while (total < MaxProbeBytes
                && (read = await stream.ReadAsync(buffer.AsMemory(), readCts.Token).ConfigureAwait(false)) > 0)
            {
                total += read;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 读满时限:按已读到的字节计速(涓流会得到接近 0 的速率,评为不畅)。
        }

        stopwatch.Stop();
        var seconds = Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
        return (total * 8d / seconds, total, contentLength);
    }

    private static bool IsMasterPlaylist(string playlist) =>
        playlist.Contains("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeHls(Uri uri, string contentType) =>
        uri.AbsolutePath.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)
        || contentType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeRawStream(Uri uri, string contentType) =>
        contentType.Contains("video/", StringComparison.OrdinalIgnoreCase)
        && !contentType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase)
        || uri.AbsolutePath.EndsWith(".flv", StringComparison.OrdinalIgnoreCase)
        || uri.AbsolutePath.EndsWith(".ts", StringComparison.OrdinalIgnoreCase);

    private static (string Uri, double? Bps, string? Resolution)? SelectFirstVariant(string playlist)
    {
        var lines = playlist.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (!line.StartsWith("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            double? bps = BandwidthRegex.Match(line) is { Success: true } b ? double.Parse(b.Groups[1].Value) : null;
            string? resolution = ResolutionRegex.Match(line) is { Success: true } r ? r.Groups[1].Value : null;

            for (var j = i + 1; j < lines.Length; j++)
            {
                var candidate = lines[j].Trim();
                if (candidate.Length > 0 && !candidate.StartsWith('#'))
                {
                    return (candidate, bps, resolution);
                }
            }
        }

        return null;
    }

    private static (string Uri, double DurationSec)? SelectFirstSegment(string playlist)
    {
        var lines = playlist.Split('\n');
        double duration = 0;
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var match = ExtInfRegex.Match(line);
            if (match.Success)
            {
                // EXTINF 时长固定为点号小数,必须按 InvariantCulture 解析,
                // 逗号小数区域(如 de-DE)下默认区域解析会把 6.006 读成 6006。
                duration = double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
                continue;
            }

            if (line.StartsWith('#'))
            {
                continue;
            }

            return (line, duration);
        }

        return null;
    }

    private static Uri ResolveUri(Uri baseUri, string candidate) =>
        Uri.TryCreate(candidate, UriKind.Absolute, out var absolute) ? absolute : new Uri(baseUri, candidate);
}
