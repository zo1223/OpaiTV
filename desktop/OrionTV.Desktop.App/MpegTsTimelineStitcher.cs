namespace OrionTV.Desktop.App;

/// <summary>
/// 把按顺序合并的 MPEG-TS 分片改写到同一条连续时间轴上。
/// <para>
/// HLS 允许分片之间时间戳重新起算：带 #EXT-X-DISCONTINUITY 的插播/换片源、逐片独立切片的源都是这样。
/// 这种分片原样拼成单个 merged.ts 后，播放器按“文件末尾时间戳 − 文件开头时间戳”估算总时长，
/// 只能得到最后一段连续区间的长度（例如 17 秒），进度条和拖动都不对，但内容其实可以一直播下去。
/// </para>
/// <para>
/// 做法：逐片比较参考流（优先视频）的首个 DTS 与上一片的结尾；不连续时给整片的 PTS/DTS/PCR 加同一个偏移，
/// 让它紧接上一片。只改时间戳字段，包长、负载和分片长度都不变；本来就连续的源一个字节都不会改。
/// </para>
/// </summary>
internal sealed class MpegTsTimelineStitcher
{
    /// <summary>超过这个大小的分片不读进内存分析，按原样流式拷贝。</summary>
    internal const long MaxSegmentBytes = 256L * 1024 * 1024;

    private const long TimestampModulo = 1L << 33;
    private const long TimestampMask = TimestampModulo - 1;
    private const long TicksPerSecond = 90_000;
    // 只有一帧、估不出帧间隔时按 25fps 处理。
    private const long DefaultFrameInterval = 3_600;
    private const long MaxFrameInterval = TicksPerSecond;
    // 下一片的起点落在“上一片最后一帧之后、1 秒以内”才算连续；倒退或跳得更远都按不连续接续。
    private const long ContinuitySlack = TicksPerSecond;
    private const int PacketLength = 188;
    private const int FramingProbeBytes = 64 * 1024;
    private const int FramingProbePackets = 4;
    private static readonly int[] PacketSizes = [188, 192, 204];

    /// <summary>短于这个长度的数据凑不满探测所需的包数，无法判断是不是 MPEG-TS。</summary>
    internal const int MinProbeBytes = ((FramingProbePackets - 1) * PacketLength) + 1;

    private bool _hasPrevious;
    private long _offset;
    private long _previousLastDts;
    private long _previousInterval;

    /// <summary>已经接到时间轴上的分片数，用来核对是否与 merged.ts 的已合并分片数一致。</summary>
    internal int SegmentCount { get; private set; }

    /// <summary>遇到的时间戳断点数（只用于诊断日志）。</summary>
    internal int Discontinuities { get; private set; }

    /// <summary>
    /// 分析一个分片并在原地改写时间戳，使其接在前面所有分片之后。返回这一片加上的偏移（90kHz，模 2^33）。
    /// 不是 MPEG-TS 或找不到时间戳的分片原样保留。
    /// </summary>
    internal long Restamp(Span<byte> segment)
    {
        SegmentCount++;
        if (!TryAnalyze(segment, out var timeline))
        {
            return 0;
        }

        if (_hasPrevious)
        {
            var position = (timeline.FirstDts + _offset) & TimestampMask;
            var gap = SignedDelta(position, _previousLastDts);
            if (gap <= 0 || gap > _previousInterval + ContinuitySlack)
            {
                _offset = (_previousLastDts + _previousInterval - timeline.FirstDts) & TimestampMask;
                Discontinuities++;
            }
        }

        _previousLastDts = (timeline.LastDts + _offset) & TimestampMask;
        _previousInterval = timeline.Interval;
        _hasPrevious = true;
        if (_offset != 0)
        {
            Walk(segment, _offset, null);
        }

        return _offset;
    }

    /// <summary>分片太大、没有读进内存分析时调用：只推进计数，后面的分片沿用当前偏移。</summary>
    internal void Skip()
    {
        SegmentCount++;
        _hasPrevious = false;
    }

    /// <summary>
    /// 续传时恢复状态：<paramref name="lastMergedSegment"/> 是 merged.ts 里最后一个已合并分片（已经在输出时间轴上）。
    /// </summary>
    internal void Resume(int segmentCount, Span<byte> lastMergedSegment)
    {
        SegmentCount = Math.Max(0, segmentCount);
        _offset = 0;
        _hasPrevious = false;
        if (SegmentCount > 0 && TryAnalyze(lastMergedSegment, out var timeline))
        {
            _previousLastDts = timeline.LastDts;
            _previousInterval = timeline.Interval;
            _hasPrevious = true;
        }
    }

    /// <summary>
    /// 在探测窗口内找到连续若干个按包长（188/192/204）对齐的同步字节 0x47。
    /// 允许分片头部带少量伪装数据（如假图片头）。
    /// </summary>
    internal static bool TryFindFraming(ReadOnlySpan<byte> data, out int start, out int packetSize)
    {
        var probe = data[..Math.Min(data.Length, FramingProbeBytes)];
        foreach (var size in PacketSizes)
        {
            var lastStart = probe.Length - ((FramingProbePackets - 1) * size) - 1;
            for (var candidate = 0; candidate <= lastStart; candidate++)
            {
                var packets = 0;
                while (packets < FramingProbePackets && probe[candidate + (packets * size)] == 0x47)
                {
                    packets++;
                }

                if (packets == FramingProbePackets)
                {
                    start = candidate;
                    packetSize = size;
                    return true;
                }
            }
        }

        start = 0;
        packetSize = 0;
        return false;
    }

    private static bool TryAnalyze(Span<byte> segment, out SegmentTimeline timeline)
    {
        timeline = default;
        var streams = new Dictionary<int, StreamStats>();
        Walk(segment, 0, streams);

        StreamStats? reference = null;
        foreach (var stream in streams.Values)
        {
            if (reference is null
                || (stream.IsVideo && !reference.IsVideo)
                || (stream.IsVideo == reference.IsVideo
                    && (stream.Count > reference.Count
                        || (stream.Count == reference.Count && stream.Pid < reference.Pid))))
            {
                reference = stream;
            }
        }

        if (reference is null)
        {
            return false;
        }

        var interval = DefaultFrameInterval;
        if (reference.Count > 1)
        {
            var span = SignedDelta(reference.LastDts, reference.FirstDts);
            if (span > 0)
            {
                interval = Math.Clamp(span / (reference.Count - 1), 1, MaxFrameInterval);
            }
        }

        timeline = new SegmentTimeline(reference.FirstDts, reference.LastDts, interval);
        return true;
    }

    /// <summary>
    /// 逐包扫描。<paramref name="streams"/> 不为空时只统计各 PES 流的 DTS；为空时把 <paramref name="offset"/> 加到 PCR/PTS/DTS 上。
    /// </summary>
    private static void Walk(Span<byte> data, long offset, Dictionary<int, StreamStats>? streams)
    {
        if (!TryFindFraming(data, out var position, out var packetSize))
        {
            return;
        }

        while (position + PacketLength <= data.Length)
        {
            if (data[position] != 0x47)
            {
                position = FindNextSync(data, position + 1, packetSize);
                if (position < 0)
                {
                    return;
                }

                continue;
            }

            ProcessPacket(data.Slice(position, PacketLength), offset, streams);
            position += packetSize;
        }
    }

    private static int FindNextSync(ReadOnlySpan<byte> data, int from, int packetSize)
    {
        // 失步后要求连续两个同步字节，避免把负载里的 0x47 当成包头。
        for (var candidate = from; candidate + packetSize < data.Length; candidate++)
        {
            if (data[candidate] == 0x47 && data[candidate + packetSize] == 0x47)
            {
                return candidate;
            }
        }

        return -1;
    }

    private static void ProcessPacket(Span<byte> packet, long offset, Dictionary<int, StreamStats>? streams)
    {
        var header1 = packet[1];
        var header3 = packet[3];
        var pid = ((header1 & 0x1F) << 8) | packet[2];
        // 传输错误、空包、仍被加扰的包都无法可靠解析。
        if ((header1 & 0x80) != 0 || pid == 0x1FFF || (header3 & 0xC0) != 0)
        {
            return;
        }

        var payloadStart = 4;
        if ((header3 & 0x20) != 0)
        {
            int adaptationLength = packet[4];
            if (adaptationLength > PacketLength - 5)
            {
                return;
            }

            payloadStart = 5 + adaptationLength;
            if (streams is null && adaptationLength > 0)
            {
                var flags = packet[5];
                var cursor = 6;
                if ((flags & 0x10) != 0)
                {
                    if (cursor + 6 > payloadStart)
                    {
                        return;
                    }

                    ShiftClockReference(packet.Slice(cursor, 6), offset);
                    cursor += 6;
                }

                if ((flags & 0x08) != 0 && cursor + 6 <= payloadStart)
                {
                    ShiftClockReference(packet.Slice(cursor, 6), offset);
                }
            }
        }

        // 只有带负载、且是 PES 起始包（PSI/SI 占用 0x00-0x1F）才有 PTS/DTS。
        if ((header3 & 0x10) == 0 || (header1 & 0x40) == 0 || pid < 0x20 || payloadStart + 14 > PacketLength)
        {
            return;
        }

        var pes = packet[payloadStart..];
        if (pes[0] != 0 || pes[1] != 0 || pes[2] != 1 || !HasPesTimestampHeader(pes[3]) || (pes[6] & 0xC0) != 0x80)
        {
            return;
        }

        var timestampFlags = pes[7] >> 6;
        int headerLength = pes[8];
        if (timestampFlags == 2)
        {
            if (headerLength < 5 || !TryReadTimestamp(pes.Slice(9, 5), out var pts))
            {
                return;
            }

            if (streams is null)
            {
                WriteTimestamp(pes.Slice(9, 5), pts + offset);
            }
            else
            {
                Record(streams, pid, pes[3], pts);
            }
        }
        else if (timestampFlags == 3)
        {
            if (headerLength < 10
                || pes.Length < 19
                || !TryReadTimestamp(pes.Slice(9, 5), out var pts)
                || !TryReadTimestamp(pes.Slice(14, 5), out var dts))
            {
                return;
            }

            if (streams is null)
            {
                WriteTimestamp(pes.Slice(9, 5), pts + offset);
                WriteTimestamp(pes.Slice(14, 5), dts + offset);
            }
            else
            {
                Record(streams, pid, pes[3], dts);
            }
        }
    }

    private static void Record(Dictionary<int, StreamStats> streams, int pid, byte streamId, long dts)
    {
        if (!streams.TryGetValue(pid, out var stream))
        {
            stream = new StreamStats(pid, streamId is >= 0xE0 and <= 0xEF, dts);
            streams[pid] = stream;
        }

        stream.LastDts = dts;
        stream.Count++;
    }

    /// <summary>带可选 PES 头（可能含 PTS/DTS）的 stream_id；节目流映射、填充、ECM/EMM 等没有。</summary>
    private static bool HasPesTimestampHeader(byte streamId) =>
        streamId >= 0xBD && streamId is not (0xBE or 0xBF or 0xF0 or 0xF1 or 0xF2 or 0xF8 or 0xFF);

    private static bool TryReadTimestamp(ReadOnlySpan<byte> field, out long value)
    {
        value = 0;
        // 三个标记位必须为 1，否则不是合法的时间戳字段。
        if ((field[0] & 1) == 0 || (field[2] & 1) == 0 || (field[4] & 1) == 0)
        {
            return false;
        }

        value = ((long)(field[0] & 0x0E) << 29)
            | ((long)field[1] << 22)
            | ((long)(field[2] & 0xFE) << 14)
            | ((long)field[3] << 7)
            | ((long)field[4] >> 1);
        return true;
    }

    private static void WriteTimestamp(Span<byte> field, long value)
    {
        value &= TimestampMask;
        field[0] = (byte)((field[0] & 0xF0) | (int)((value >> 29) & 0x0E) | 1);
        field[1] = (byte)(value >> 22);
        field[2] = (byte)(((value >> 14) & 0xFE) | 1);
        field[3] = (byte)(value >> 7);
        field[4] = (byte)(((value << 1) & 0xFE) | 1);
    }

    /// <summary>PCR/OPCR：33 位基准（90kHz）+ 6 位保留 + 9 位扩展；只平移基准。</summary>
    private static void ShiftClockReference(Span<byte> field, long offset)
    {
        var value = ((long)field[0] << 25)
            | ((long)field[1] << 17)
            | ((long)field[2] << 9)
            | ((long)field[3] << 1)
            | ((long)field[4] >> 7);
        value = (value + offset) & TimestampMask;
        field[0] = (byte)(value >> 25);
        field[1] = (byte)(value >> 17);
        field[2] = (byte)(value >> 9);
        field[3] = (byte)(value >> 1);
        field[4] = (byte)((field[4] & 0x7F) | (int)((value & 1) << 7));
    }

    /// <summary>两个 33 位时间戳的有符号差，正确处理回绕。</summary>
    private static long SignedDelta(long later, long earlier)
    {
        var delta = (later - earlier) & TimestampMask;
        return delta >= TimestampModulo / 2 ? delta - TimestampModulo : delta;
    }

    private readonly record struct SegmentTimeline(long FirstDts, long LastDts, long Interval);

    private sealed class StreamStats(int pid, bool isVideo, long firstDts)
    {
        internal int Pid { get; } = pid;
        internal bool IsVideo { get; } = isVideo;
        internal long FirstDts { get; } = firstDts;
        internal long LastDts { get; set; } = firstDts;
        internal int Count { get; set; }
    }
}
