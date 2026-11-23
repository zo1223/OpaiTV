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

public enum MediaProbeStatus
{
    Unknown,
    MediaValidated,
    NeedsDecode,
    UnsupportedContent,
    InvalidMedia,
    Unreachable,
    TimedOut,
}

/// <summary>单个频道的流畅度探测结果。</summary>
public sealed record ChannelHealthResult(
    ChannelHealth Health,
    int Score,
    string Detail,
    double ThroughputMbps = 0,
    long TtfbMs = 0,
    string? Resolution = null,
    MediaProbeStatus MediaStatus = MediaProbeStatus.Unknown)
{
    /// <summary>非 HTTP(S) 地址无法用网络探测（如 rtp/udp 组播）。</summary>
    public static readonly ChannelHealthResult NotProbeable = new(ChannelHealth.Unknown, 0, "不可探测");

    /// <summary>无法连接。</summary>
    public static readonly ChannelHealthResult Unreachable = new(
        ChannelHealth.Bad,
        0,
        "无法连接",
        MediaStatus: MediaProbeStatus.Unreachable);

    public static readonly ChannelHealthResult TimedOut = new(
        ChannelHealth.Unknown,
        0,
        "检测超时",
        MediaStatus: MediaProbeStatus.TimedOut);

    public static ChannelHealthResult Unsupported(string detail, long ttfbMs = 0) => new(
        ChannelHealth.Bad,
        0,
        detail,
        TtfbMs: ttfbMs,
        MediaStatus: MediaProbeStatus.UnsupportedContent);

    public static ChannelHealthResult Invalid(string detail, long ttfbMs = 0) => new(
        ChannelHealth.Bad,
        0,
        detail,
        TtfbMs: ttfbMs,
        MediaStatus: MediaProbeStatus.InvalidMedia);
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
            return new ChannelHealthResult(
                ChannelHealth.Fair,
                40,
                "已连接·未测速",
                0,
                ttfbMs,
                resolution,
                MediaProbeStatus.MediaValidated);
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

        return new ChannelHealthResult(
            category,
            score,
            detail,
            mbps,
            ttfbMs,
            resolution,
            MediaProbeStatus.MediaValidated);
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
        if (result.MediaStatus is MediaProbeStatus.UnsupportedContent
            or MediaProbeStatus.InvalidMedia
            or MediaProbeStatus.Unreachable
            or MediaProbeStatus.TimedOut)
        {
            return result.Detail;
        }

        var parts = new List<string>(3);
        if (ClarityLabelOf(result.Resolution) is { } clarity)
        {
            parts.Add(clarity);
        }

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

    /// <summary>
    /// 把清单里的 RESOLUTION=WxH（如 1920x1080）转成友好清晰度标签（4K/1080p/720p/480p/360p），
    /// 按高度归档；拿不到或无法解析时返回 null（调用方不显示清晰度）。
    /// </summary>
    public static string? ClarityLabelOf(string? resolution)
    {
        if (string.IsNullOrWhiteSpace(resolution))
        {
            return null;
        }

        var parts = resolution.Trim().Split('x', 'X', '×');
        if (parts.Length != 2
            || !int.TryParse(parts[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var width)
            || !int.TryParse(parts[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var height)
            || width <= 0
            || height <= 0)
        {
            return null;
        }

        return height switch
        {
            >= 2160 => "4K",
            >= 1080 => "1080p",
            >= 720 => "720p",
            >= 480 => "480p",
            _ => "360p",
        };
    }

    public static string SummaryOf(IEnumerable<ChannelHealthResult> results)
    {
        var measured = results
            .Where(result => result.MediaStatus is not (
                    MediaProbeStatus.UnsupportedContent
                    or MediaProbeStatus.InvalidMedia
                    or MediaProbeStatus.Unreachable
                    or MediaProbeStatus.TimedOut)
                && (result.TtfbMs > 0 || result.ThroughputMbps > 0))
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
    private const int ValidationProbeBytes = 64 * 1024;
    private const int MaxPlaylistBytes = 2_500_000;
    private const int MaxDependencyBytes = 64 * 1024;
    private const int SniffBytes = 64 * 1024;
    private static readonly TimeSpan PlaylistTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan SegmentTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan SegmentReadTimeout = TimeSpan.FromSeconds(10); // 正文读取独立时限,防涓流占死并发槽

    private static readonly Regex BandwidthRegex = new(@"BANDWIDTH=(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ResolutionRegex = new(@"RESOLUTION=(\d+x\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ExtInfRegex = new(@"^#EXTINF:([\d.]+)", RegexOptions.Compiled);
    private static readonly Regex UriAttributeRegex = new(
        "URI=(?:\"([^\"]+)\"|([^,]+))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private sealed record StreamMeasurement(double Bps, long Bytes, long ContentLength, byte[] Prefix);

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
    public async Task<ChannelHealthResult> ProbeAsync(
        string url,
        CancellationToken cancellationToken,
        bool measureSpeed = true)
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
                    return HttpFailure(response, ttfbMs);
                }

                var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
                var responseUri = response.RequestMessage?.RequestUri ?? uri;
                if (IsUnsupportedContentType(contentType))
                {
                    return ChannelHealthResult.Unsupported(UnsupportedContentDetail(contentType), ttfbMs);
                }

                var looksRaw = LooksLikeRawStream(responseUri, contentType);
                var looksHls = !looksRaw
                    && (LooksLikeHls(responseUri, contentType) || LooksLikeHls(uri, contentType));

                if (looksHls)
                {
                    var playlist = await ReadTextBodyAsync(response, MaxPlaylistBytes, cancellationToken).ConfigureAwait(false);
                    if (LooksLikeUnsupportedText(playlist))
                    {
                        return ChannelHealthResult.Unsupported("网页线路", ttfbMs);
                    }

                    if (!IsHlsPlaylist(playlist))
                    {
                        return ChannelHealthResult.Invalid("播放清单无效", ttfbMs);
                    }

                    return await ProbeHlsAsync(responseUri, playlist, ttfbMs, measureSpeed, cancellationToken).ConfigureAwait(false);
                }

                if (!looksRaw && IsTextContentType(contentType))
                {
                    var text = await ReadTextBodyAsync(response, MaxPlaylistBytes, cancellationToken).ConfigureAwait(false);
                    if (IsHlsPlaylist(text))
                    {
                        return await ProbeHlsAsync(responseUri, text, ttfbMs, measureSpeed, cancellationToken).ConfigureAwait(false);
                    }

                    return LooksLikeUnsupportedText(text)
                        ? ChannelHealthResult.Unsupported("网页线路", ttfbMs)
                        : ChannelHealthResult.Invalid("响应不是媒体内容", ttfbMs);
                }

                var measurement = await MeasureStreamAsync(
                        response,
                        measureSpeed ? MaxProbeBytes : ValidationProbeBytes,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (measurement.Bytes == 0)
                {
                    return ChannelHealthResult.Invalid("媒体响应为空", ttfbMs);
                }

                var prefixText = DecodePrefix(measurement.Prefix);
                if (LooksLikeUnsupportedPayload(measurement.Prefix))
                {
                    return ChannelHealthResult.Unsupported("网页线路", ttfbMs);
                }

                if (IsHlsPlaylist(prefixText))
                {
                    return await ProbeHlsAsync(responseUri, prefixText, ttfbMs, measureSpeed, cancellationToken).ConfigureAwait(false);
                }

                if (looksRaw || HasRecognizedMediaSignature(measurement.Prefix))
                {
                    return measureSpeed
                        ? ChannelHealthScoring.Evaluate(true, ttfbMs, measurement.Bps, null)
                        : ValidatedWithoutSpeed(ttfbMs);
                }

                return new ChannelHealthResult(
                    ChannelHealth.Unknown,
                    0,
                    "需要解码确认",
                    measurement.Bps / 1_000_000d,
                    ttfbMs,
                    MediaStatus: MediaProbeStatus.NeedsDecode);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return ChannelHealthResult.TimedOut with { TtfbMs = (long)PlaylistTimeout.TotalMilliseconds };
        }
        catch
        {
            return ChannelHealthResult.Unreachable with { TtfbMs = (long)PlaylistTimeout.TotalMilliseconds };
        }
    }

    private async Task<ChannelHealthResult> ProbeHlsAsync(
        Uri playlistUri,
        string playlistText,
        long ttfbMs,
        bool measureSpeed,
        CancellationToken cancellationToken)
    {
        if (!IsHlsPlaylist(playlistText))
        {
            return ChannelHealthResult.Invalid("播放清单无效", ttfbMs);
        }

        double? declaredBps = null;
        string? resolution = null;
        var mediaUri = playlistUri;
        var mediaText = playlistText;

        if (IsMasterPlaylist(playlistText))
        {
            var variant = SelectLowestBandwidthVariant(playlistText);
            if (variant is null)
            {
                return ChannelHealthResult.Invalid("主清单没有可用画质", ttfbMs);
            }

            declaredBps = variant.Value.Bps;
            resolution = variant.Value.Resolution;
            mediaUri = ResolveUri(playlistUri, variant.Value.Uri);

            var (variantResponse, _) = await TimedGetAsync(mediaUri.ToString(), PlaylistTimeout, cancellationToken).ConfigureAwait(false);
            using (variantResponse)
            {
                if (!variantResponse.IsSuccessStatusCode)
                {
                    return ChannelHealthResult.Invalid("画质清单无法访问", ttfbMs);
                }

                var contentType = variantResponse.Content.Headers.ContentType?.MediaType ?? string.Empty;
                if (IsUnsupportedContentType(contentType))
                {
                    return ChannelHealthResult.Unsupported(UnsupportedContentDetail(contentType), ttfbMs);
                }

                mediaUri = variantResponse.RequestMessage?.RequestUri ?? mediaUri;
                mediaText = await ReadTextBodyAsync(variantResponse, MaxPlaylistBytes, cancellationToken).ConfigureAwait(false);
                if (LooksLikeUnsupportedText(mediaText))
                {
                    return ChannelHealthResult.Unsupported("网页线路", ttfbMs);
                }

                if (!IsHlsPlaylist(mediaText))
                {
                    return ChannelHealthResult.Invalid("画质清单无效", ttfbMs);
                }
            }
        }

        var segment = SelectFirstSegment(mediaText);
        if (segment is null)
        {
            return ChannelHealthResult.Invalid("清单没有媒体分片", ttfbMs);
        }

        var dependencies = SelectHlsDependencies(mediaText);
        if (dependencies.KeyUri is not null)
        {
            var keyFailure = await ValidateDependencyAsync(
                    mediaUri,
                    dependencies.KeyUri,
                    "加密密钥不可用",
                    minimumBytes: 16,
                    cancellationToken)
                .ConfigureAwait(false);
            if (keyFailure is not null)
            {
                return keyFailure with { TtfbMs = ttfbMs };
            }
        }

        if (dependencies.InitUri is not null)
        {
            var initFailure = await ValidateDependencyAsync(
                    mediaUri,
                    dependencies.InitUri,
                    "初始化分片不可用",
                    minimumBytes: 1,
                    cancellationToken)
                .ConfigureAwait(false);
            if (initFailure is not null)
            {
                return initFailure with { TtfbMs = ttfbMs };
            }
        }

        var segmentUri = ResolveUri(mediaUri, segment.Value.Uri);
        var (segmentResponse, _) = await TimedGetAsync(segmentUri.ToString(), SegmentTimeout, cancellationToken).ConfigureAwait(false);
        using (segmentResponse)
        {
            if (!segmentResponse.IsSuccessStatusCode)
            {
                return ChannelHealthResult.Invalid("媒体分片无法访问", ttfbMs);
            }

            var contentType = segmentResponse.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (IsUnsupportedContentType(contentType))
            {
                return ChannelHealthResult.Unsupported(UnsupportedContentDetail(contentType), ttfbMs);
            }

            var measurement = await MeasureStreamAsync(
                    segmentResponse,
                    measureSpeed ? MaxProbeBytes : ValidationProbeBytes,
                    cancellationToken)
                .ConfigureAwait(false);
            if (measurement.Bytes == 0)
            {
                return ChannelHealthResult.Invalid("媒体分片为空", ttfbMs);
            }

                if (LooksLikeUnsupportedPayload(measurement.Prefix))
                {
                    return ChannelHealthResult.Unsupported("媒体分片返回网页", ttfbMs);
                }

            double? requiredBps = declaredBps is > 0
                ? declaredBps
                : segment.Value.DurationSec > 0
                    ? (measurement.ContentLength > 0 ? measurement.ContentLength : measurement.Bytes) * 8d / segment.Value.DurationSec
                    : null;

            return measureSpeed
                ? ChannelHealthScoring.Evaluate(true, ttfbMs, measurement.Bps, requiredBps, resolution)
                : ValidatedWithoutSpeed(ttfbMs, resolution);
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

    private static async Task<StreamMeasurement> MeasureStreamAsync(
        HttpResponseMessage response,
        int maxProbeBytes,
        CancellationToken cancellationToken)
    {
        var contentLength = response.Content.Headers.ContentLength ?? 0;
        var stopwatch = Stopwatch.StartNew();
        // TimedGetAsync 的超时只覆盖响应头;正文必须有独立时限,
        // 否则涓流服务器(每秒几字节)会无限占住一个并发探测槽。
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readCts.CancelAfter(SegmentReadTimeout);

        long total = 0;
        var prefix = new byte[SniffBytes];
        var prefixLength = 0;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(readCts.Token).ConfigureAwait(false);

            var buffer = new byte[64 * 1024];
            int read;
            while (total < maxProbeBytes
                && (read = await stream.ReadAsync(buffer.AsMemory(), readCts.Token).ConfigureAwait(false)) > 0)
            {
                var copyLength = Math.Min(read, prefix.Length - prefixLength);
                if (copyLength > 0)
                {
                    Buffer.BlockCopy(buffer, 0, prefix, prefixLength, copyLength);
                    prefixLength += copyLength;
                }

                total += read;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 读满时限:按已读到的字节计速(涓流会得到接近 0 的速率,评为不畅)。
        }

        stopwatch.Stop();
        var seconds = Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
        if (prefixLength != prefix.Length)
        {
            Array.Resize(ref prefix, prefixLength);
        }

        return new StreamMeasurement(total * 8d / seconds, total, contentLength, prefix);
    }

    private static async Task<string> ReadTextBodyAsync(
        HttpResponseMessage response,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        var bytes = await ReadBodyAsync(response, maxBytes, cancellationToken).ConfigureAwait(false);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    private static async Task<byte[]> ReadBodyAsync(
        HttpResponseMessage response,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readCts.CancelAfter(SegmentReadTimeout);
        await using var stream = await response.Content.ReadAsStreamAsync(readCts.Token).ConfigureAwait(false);
        using var output = new MemoryStream(Math.Min(maxBytes, 64 * 1024));
        var buffer = new byte[16 * 1024];
        while (output.Length < maxBytes)
        {
            var remaining = maxBytes - (int)output.Length;
            var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), readCts.Token)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    private async Task<ChannelHealthResult?> ValidateDependencyAsync(
        Uri playlistUri,
        string candidate,
        string failureDetail,
        int minimumBytes,
        CancellationToken cancellationToken)
    {
        var dependencyUri = ResolveUri(playlistUri, candidate);
        var (response, _) = await TimedGetAsync(dependencyUri.ToString(), SegmentTimeout, cancellationToken).ConfigureAwait(false);
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                return ChannelHealthResult.Invalid(failureDetail);
            }

            var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (IsUnsupportedContentType(contentType))
            {
                return ChannelHealthResult.Unsupported(failureDetail);
            }

            var body = await ReadBodyAsync(response, MaxDependencyBytes, cancellationToken).ConfigureAwait(false);
            if (body.Length < minimumBytes
                || (body.Length > minimumBytes && LooksLikeUnsupportedPayload(body)))
            {
                return ChannelHealthResult.Invalid(failureDetail);
            }

            return null;
        }
    }

    private static ChannelHealthResult HttpFailure(HttpResponseMessage response, long ttfbMs) => new(
        ChannelHealth.Bad,
        0,
        $"HTTP {(int)response.StatusCode}",
        TtfbMs: ttfbMs,
        MediaStatus: MediaProbeStatus.Unreachable);

    private static ChannelHealthResult ValidatedWithoutSpeed(long ttfbMs, string? resolution = null) => new(
        ChannelHealth.Fair,
        40,
        "媒体有效",
        TtfbMs: ttfbMs,
        Resolution: resolution,
        MediaStatus: MediaProbeStatus.MediaValidated);

    private static bool IsUnsupportedContentType(string contentType) =>
        contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase)
        || contentType.Contains("application/xhtml", StringComparison.OrdinalIgnoreCase)
        || contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase)
        || contentType.Contains("text/json", StringComparison.OrdinalIgnoreCase);

    private static string UnsupportedContentDetail(string contentType) =>
        contentType.Contains("html", StringComparison.OrdinalIgnoreCase)
            ? "网页线路"
            : "接口返回非媒体内容";

    private static bool IsTextContentType(string contentType) =>
        contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
        || contentType.Contains("xml", StringComparison.OrdinalIgnoreCase)
        || contentType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase);

    private static string DecodePrefix(byte[] bytes) =>
        bytes.Length == 0 ? "" : System.Text.Encoding.UTF8.GetString(bytes);

    private static bool LooksLikeUnsupportedText(string text)
    {
        var value = text.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        return value.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("<html", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("<head", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("<body", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith('{')
            || value.StartsWith('[');
    }

    private static bool LooksLikeUnsupportedPayload(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return false;
        }

        var sampleLength = Math.Min(bytes.Length, 512);
        var textLike = 0;
        for (var index = 0; index < sampleLength; index++)
        {
            var value = bytes[index];
            if (value is 9 or 10 or 13 || value >= 0x20 && value != 0x7F)
            {
                textLike++;
            }
        }

        return textLike >= Math.Ceiling(sampleLength * 0.95)
            && LooksLikeUnsupportedText(DecodePrefix(bytes));
    }

    private static bool IsHlsPlaylist(string playlist) =>
        playlist.TrimStart('\uFEFF', ' ', '\t', '\r', '\n')
            .StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase);

    private static bool HasRecognizedMediaSignature(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 'F' && bytes[1] == 'L' && bytes[2] == 'V')
        {
            return true;
        }

        if (bytes.Length >= 8
            && ((bytes[4] == 'f' && bytes[5] == 't' && bytes[6] == 'y' && bytes[7] == 'p')
                || (bytes[4] == 's' && bytes[5] == 't' && bytes[6] == 'y' && bytes[7] == 'p')
                || (bytes[4] == 'm' && bytes[5] == 'o' && bytes[6] == 'o' && bytes[7] == 'f')))
        {
            return true;
        }

        if (bytes.Length >= 4
            && ((bytes[0] == 0x1A && bytes[1] == 0x45 && bytes[2] == 0xDF && bytes[3] == 0xA3)
                || (bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0x01 && bytes[3] is 0xBA or 0xB3)))
        {
            return true;
        }

        if (bytes.Length >= 12
            && bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F'
            && bytes[8] == 'A' && bytes[9] == 'V' && bytes[10] == 'I')
        {
            return true;
        }

        if (bytes.Length >= 376 && bytes[0] == 0x47 && bytes[188] == 0x47)
        {
            return true;
        }

        return bytes.Length >= 4 && bytes[0] == 'O' && bytes[1] == 'g' && bytes[2] == 'g' && bytes[3] == 'S';
    }

    private static (string? KeyUri, string? InitUri) SelectHlsDependencies(string playlist)
    {
        string? keyUri = null;
        string? initUri = null;
        foreach (var rawLine in playlist.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("#EXT-X-KEY:", StringComparison.OrdinalIgnoreCase))
            {
                keyUri = line.Contains("METHOD=NONE", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : ExtractUriAttribute(line);
                continue;
            }

            if (line.StartsWith("#EXT-X-MAP:", StringComparison.OrdinalIgnoreCase))
            {
                initUri = ExtractUriAttribute(line);
                continue;
            }

            if (line.Length > 0 && !line.StartsWith('#'))
            {
                break;
            }
        }

        return (keyUri, initUri);
    }

    private static string? ExtractUriAttribute(string line)
    {
        var match = UriAttributeRegex.Match(line);
        if (!match.Success)
        {
            return null;
        }

        var value = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
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
        || uri.AbsolutePath.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
        || uri.AbsolutePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
        || uri.AbsolutePath.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase)
        || uri.AbsolutePath.EndsWith(".webm", StringComparison.OrdinalIgnoreCase)
        || uri.AbsolutePath.EndsWith(".mov", StringComparison.OrdinalIgnoreCase)
        || uri.AbsolutePath.EndsWith(".mpeg", StringComparison.OrdinalIgnoreCase)
        || uri.AbsolutePath.EndsWith(".mpg", StringComparison.OrdinalIgnoreCase);

    private static (string Uri, double? Bps, string? Resolution)? SelectLowestBandwidthVariant(string playlist)
    {
        var lines = playlist.Split('\n');
        var variants = new List<(string Uri, double? Bps, string? Resolution)>();
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
                    variants.Add((candidate, bps, resolution));
                    break;
                }
            }
        }

        return variants.Count == 0
            ? null
            : variants.OrderBy(variant => variant.Bps ?? double.MaxValue).First();
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
