using System.Globalization;
using System.Text.RegularExpressions;

namespace OrionTV.Desktop.Core;

/// <summary>广告识别用到的是哪一级。</summary>
public enum AdDetectionTier
{
    None,
    Cue,
    BlackSilence,
    ForeignHost,
}

public enum AdTierOutcome
{
    /// <summary>这一级给出了广告区间。</summary>
    Hit,

    /// <summary>这一级正常跑完，但没有发现广告。</summary>
    Miss,

    /// <summary>这一级没法判断（探测失败、超时、格式不支持），降到下一级。</summary>
    Error,
}

public sealed record AdTierAttempt(AdDetectionTier Tier, AdTierOutcome Outcome, string? Detail = null);

/// <summary>多级广告识别的结果：区间、命中的级别、每一级的尝试记录。</summary>
public sealed record AdDetectionResult(
    IReadOnlyList<AdRange> Ranges,
    AdDetectionTier Tier,
    IReadOnlyList<AdTierAttempt> Attempts)
{
    public static AdDetectionResult Empty(IReadOnlyList<AdTierAttempt>? attempts = null) =>
        new([], AdDetectionTier.None, attempts ?? []);
}

/// <summary>第二级（黑场 + 静音）需要的分片探测：取大小、取内容。失败返回 null。</summary>
public interface ISegmentProbe
{
    Task<long?> GetSizeAsync(string url, CancellationToken cancellationToken);

    Task<byte[]?> FetchAsync(string url, int maxBytes, CancellationToken cancellationToken);
}

/// <summary>默认探测器：HEAD 取 Content-Length，GET 取内容（超过上限放弃）。</summary>
public sealed class HttpSegmentProbe(HttpClient httpClient) : ISegmentProbe
{
    public async Task<long?> GetSizeAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, url);
        using var response = await httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return response.Content.Headers.ContentLength is { } length && length > 0 ? length : null;
    }

    public async Task<byte[]?> FetchAsync(string url, int maxBytes, CancellationToken cancellationToken)
    {
        using var response = await httpClient
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        if (response.Content.Headers.ContentLength is { } declared && declared > maxBytes)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}

/// <summary>一个 MPEG-TS 分片里视频、音频各有多少负载字节。</summary>
public sealed record TsPayloadStats(long VideoBytes, long AudioBytes, bool HasVideo, bool HasAudio);

/// <summary>
/// 最小的 MPEG-TS 解析：只读 PAT / PMT 找出视频、音频 PID，再累计各自的负载字节（不含 TS 头与适配域）。
/// 不解码画面和声音。全黑画面与数字静音压缩后几乎不占字节，所以用负载字节率近似判断黑场与静音。
/// </summary>
public static class MpegTsProbe
{
    private const int PacketSize = 188;
    private const byte SyncByte = 0x47;

    public static TsPayloadStats? Analyze(ReadOnlySpan<byte> data)
    {
        var offset = FindSync(data);
        if (offset < 0)
        {
            return null;
        }

        var pmtPids = new HashSet<int>();
        var videoPids = new HashSet<int>();
        var audioPids = new HashSet<int>();
        var bytesByPid = new Dictionary<int, long>();
        var sawPmt = false;

        for (var position = offset; position + PacketSize <= data.Length; position += PacketSize)
        {
            var packet = data.Slice(position, PacketSize);
            if (packet[0] != SyncByte)
            {
                continue;
            }

            var payloadStart = (packet[1] & 0x40) != 0;
            var pid = ((packet[1] & 0x1F) << 8) | packet[2];
            var adaptation = (packet[3] >> 4) & 0x3;
            if ((adaptation & 0x1) == 0)
            {
                continue;
            }

            var index = 4;
            if ((adaptation & 0x2) != 0)
            {
                index += 1 + packet[4];
            }

            if (index >= PacketSize)
            {
                continue;
            }

            var payload = packet[index..];
            if (pid == 0)
            {
                if (payloadStart)
                {
                    ParsePat(payload, pmtPids);
                }

                continue;
            }

            if (pmtPids.Contains(pid))
            {
                if (payloadStart && ParsePmt(payload, videoPids, audioPids))
                {
                    sawPmt = true;
                }

                continue;
            }

            if (pid == 0x1FFF)
            {
                continue;
            }

            bytesByPid[pid] = bytesByPid.GetValueOrDefault(pid) + payload.Length;
        }

        if (!sawPmt)
        {
            return null;
        }

        long video = 0;
        long audio = 0;
        foreach (var pid in videoPids)
        {
            video += bytesByPid.GetValueOrDefault(pid);
        }

        foreach (var pid in audioPids)
        {
            audio += bytesByPid.GetValueOrDefault(pid);
        }

        return new TsPayloadStats(video, audio, videoPids.Count > 0, audioPids.Count > 0);
    }

    private static int FindSync(ReadOnlySpan<byte> data)
    {
        for (var offset = 0; offset < PacketSize && offset + PacketSize <= data.Length; offset++)
        {
            if (data[offset] != SyncByte)
            {
                continue;
            }

            var second = offset + PacketSize;
            var third = second + PacketSize;
            if ((second >= data.Length || data[second] == SyncByte) && (third >= data.Length || data[third] == SyncByte))
            {
                return offset;
            }
        }

        return -1;
    }

    private static void ParsePat(ReadOnlySpan<byte> payload, HashSet<int> pmtPids)
    {
        if (payload.Length < 1)
        {
            return;
        }

        var start = 1 + payload[0];
        if (start + 8 > payload.Length || payload[start] != 0x00)
        {
            return;
        }

        var section = payload[start..];
        var sectionLength = ((section[1] & 0x0F) << 8) | section[2];
        var end = Math.Min(section.Length, 3 + sectionLength - 4);
        for (var i = 8; i + 4 <= end; i += 4)
        {
            var programNumber = (section[i] << 8) | section[i + 1];
            if (programNumber != 0)
            {
                pmtPids.Add(((section[i + 2] & 0x1F) << 8) | section[i + 3]);
            }
        }
    }

    private static bool ParsePmt(ReadOnlySpan<byte> payload, HashSet<int> videoPids, HashSet<int> audioPids)
    {
        if (payload.Length < 1)
        {
            return false;
        }

        var start = 1 + payload[0];
        if (start + 12 > payload.Length || payload[start] != 0x02)
        {
            return false;
        }

        var section = payload[start..];
        var sectionLength = ((section[1] & 0x0F) << 8) | section[2];
        var end = Math.Min(section.Length, 3 + sectionLength - 4);
        var programInfoLength = ((section[10] & 0x0F) << 8) | section[11];
        for (var i = 12 + programInfoLength; i + 5 <= end;)
        {
            var streamType = section[i];
            var pid = ((section[i + 1] & 0x1F) << 8) | section[i + 2];
            var infoLength = ((section[i + 3] & 0x0F) << 8) | section[i + 4];
            if (streamType is 0x01 or 0x02 or 0x1B or 0x24)
            {
                videoPids.Add(pid);
            }
            else if (streamType is 0x03 or 0x04 or 0x0F or 0x11 or 0x81)
            {
                audioPids.Add(pid);
            }

            i += 5 + infoLength;
        }

        return true;
    }
}

/// <summary>
/// 第二级：片头片尾的黑场 + 静音段。
/// 只看播放列表开头和结尾各 60 秒内的分片：先用大小挑出码率骤降的分片，再下载这些（很小的）分片确认
/// “视频几乎没有字节（黑场）且音频几乎没有字节（静音）”。片头最后一段黑场静音之前、片尾第一段黑场静音之后的内容
/// 视为广告块，各自最多 45 秒。阈值与手机端 services/adDetection.ts 保持一致。
/// </summary>
public static partial class BlackSilenceAdDetector
{
    public const double HeadWindowMs = 60_000;
    public const double TailWindowMs = 60_000;
    public const double MaxBlockMs = 45_000;
    public const double MinTotalMs = 300_000;
    public const int MinSegments = 6;
    public const int MaxWindowSegments = 16;
    public const double MaxMarkMs = 15_000;
    public const double CandidateMaxBytesPerSecond = 40_000;
    public const double CandidateMaxFraction = 0.25;
    public const double BlackMaxVideoBytesPerSecond = 25_000;
    public const double BlackMaxFraction = 0.2;
    public const double SilentMaxAudioBytesPerSecond = 1_500;
    public const int MaxCandidateDownloads = 8;
    public const int MaxSegmentBytes = 1_500_000;
    private const int ProbeConcurrency = 6;

    public sealed record Outcome(AdTierOutcome Result, IReadOnlyList<AdRange> Ranges, string? Detail = null);

    public sealed record Segment(int Index, double StartMs, double DurationMs, string Url)
    {
        public double EndMs => StartMs + DurationMs;
    }

    [GeneratedRegex(@"^#EXTINF:([\d.]+)")]
    private static partial Regex ExtInfRegex();

    [GeneratedRegex(@"^#EXT-X-KEY:.*METHOD=(?!NONE)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex EncryptedRegex();

    [GeneratedRegex(@"^#EXT-X-MAP:", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex InitSegmentRegex();

    public static IReadOnlyList<Segment> ParseSegments(string playlist, string baseUrl)
    {
        var segments = new List<Segment>();
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
        {
            return segments;
        }

        double elapsed = 0;
        double? pendingDuration = null;
        foreach (var rawLine in playlist.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var match = ExtInfRegex().Match(line);
            if (match.Success)
            {
                pendingDuration = double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                    ? Math.Round(seconds * 1000, MidpointRounding.AwayFromZero)
                    : 0;
                continue;
            }

            if (line.StartsWith('#') || pendingDuration is not { } duration)
            {
                continue;
            }

            pendingDuration = null;
            if (duration <= 0 || !Uri.TryCreate(baseUri, line, out var segmentUri))
            {
                continue;
            }

            segments.Add(new Segment(segments.Count, elapsed, duration, segmentUri.ToString()));
            elapsed += duration;
        }

        return segments;
    }

    public static async Task<Outcome> DetectAsync(
        string playlist,
        string baseUrl,
        ISegmentProbe probe,
        CancellationToken cancellationToken)
    {
        if (EncryptedRegex().IsMatch(playlist))
        {
            return new Outcome(AdTierOutcome.Error, [], "分片已加密，无法分析");
        }

        if (InitSegmentRegex().IsMatch(playlist))
        {
            return new Outcome(AdTierOutcome.Error, [], "不是 MPEG-TS 分片，无法分析");
        }

        var segments = ParseSegments(playlist, baseUrl);
        var total = segments.Count == 0 ? 0 : segments[^1].EndMs;
        if (segments.Count < MinSegments || total < MinTotalMs)
        {
            return new Outcome(AdTierOutcome.Miss, [], "时长太短，不做黑场静音判断");
        }

        var window = segments.Where(segment => segment.StartMs < HeadWindowMs).Take(MaxWindowSegments)
            .Concat(segments.Where(segment => segment.EndMs > total - TailWindowMs).TakeLast(MaxWindowSegments))
            .DistinctBy(segment => segment.Index)
            .OrderBy(segment => segment.Index)
            .ToList();

        // 第一步：只取大小。
        using var gate = new SemaphoreSlim(ProbeConcurrency);
        var sizes = await Task.WhenAll(window.Select(async segment =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await probe.GetSizeAsync(segment.Url, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                return null;
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);

        var sized = window
            .Select((segment, i) => (Segment: segment, Size: sizes[i]))
            .Where(entry => entry.Size is > 0)
            .Select(entry => (entry.Segment, Rate: entry.Size!.Value / (entry.Segment.DurationMs / 1000)))
            .ToList();
        if (sized.Count * 2 < window.Count || sized.Count == 0)
        {
            return new Outcome(AdTierOutcome.Error, [], "取不到分片大小");
        }

        var median = Median(sized.Select(entry => entry.Rate));
        var candidates = sized
            .Where(entry => entry.Rate <= CandidateMaxBytesPerSecond && entry.Rate <= CandidateMaxFraction * median)
            .Take(MaxCandidateDownloads)
            .ToList();
        if (candidates.Count == 0)
        {
            return new Outcome(AdTierOutcome.Miss, [], "没有码率骤降的分片");
        }

        // 第二步：下载候选分片，分别确认黑场与静音。
        var blackLimit = Math.Min(BlackMaxVideoBytesPerSecond, BlackMaxFraction * median);
        var marked = new List<Segment>();
        var analyzed = 0;
        foreach (var (segment, _) in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[]? bytes;
            try
            {
                bytes = await probe.FetchAsync(segment.Url, MaxSegmentBytes, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                bytes = null;
            }

            var stats = bytes is null ? null : MpegTsProbe.Analyze(bytes);
            if (stats is null)
            {
                continue;
            }

            analyzed++;
            var seconds = segment.DurationMs / 1000;
            var isBlack = stats.VideoBytes / seconds <= blackLimit;
            var isSilent = stats.AudioBytes / seconds <= SilentMaxAudioBytesPerSecond;
            if (isBlack && isSilent)
            {
                marked.Add(segment);
            }
        }

        if (analyzed == 0)
        {
            return new Outcome(AdTierOutcome.Error, [], "候选分片无法解析");
        }

        var ranges = BuildRanges(marked, total);
        return ranges.Count > 0
            ? new Outcome(AdTierOutcome.Hit, ranges)
            : new Outcome(AdTierOutcome.Miss, [], "片头片尾没有黑场静音段");
    }

    /// <summary>把黑场静音分片合成段，再换算成片头 / 片尾广告区间。</summary>
    public static IReadOnlyList<AdRange> BuildRanges(IReadOnlyList<Segment> marked, double totalMs)
    {
        var runs = new List<(double Start, double End)>();
        Segment? previous = null;
        foreach (var segment in marked.OrderBy(segment => segment.Index))
        {
            if (previous is not null && segment.Index == previous.Index + 1 && runs.Count > 0)
            {
                runs[^1] = (runs[^1].Start, segment.EndMs);
            }
            else
            {
                runs.Add((segment.StartMs, segment.EndMs));
            }

            previous = segment;
        }

        runs.RemoveAll(run => run.End - run.Start > MaxMarkMs);

        var ranges = new List<AdRange>();
        var head = runs.Where(run => run.End <= MaxBlockMs).Select(run => (double?)run.End).LastOrDefault();
        if (head is { } headEnd && headEnd > 0)
        {
            ranges.Add(new AdRange(0, headEnd));
        }

        var tail = runs.Where(run => run.Start >= totalMs - MaxBlockMs && run.Start > (head ?? 0))
            .Select(run => (double?)run.Start)
            .FirstOrDefault();
        if (tail is { } tailStart && tailStart < totalMs)
        {
            ranges.Add(new AdRange(tailStart, totalMs));
        }

        return ranges;
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(value => value).ToList();
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }
}
