using OrionTV.Desktop.App;

namespace OrionTV.Desktop.Tests;

public sealed class MpegTsTimelineStitcherTests
{
    private const long Second = TsFixture.Second;
    private const long Wrap = 1L << 33;

    [Fact]
    public void ContinuousSegmentsAreLeftByteIdentical()
    {
        var segments = Enumerable.Range(0, 4)
            .Select(index => TsFixture.Segment(TsFixture.FirstTimestamp + (index * 6 * Second), seed: index))
            .ToArray();
        var stitcher = new MpegTsTimelineStitcher();

        foreach (var segment in segments)
        {
            var copy = segment.ToArray();
            Assert.Equal(0, stitcher.Restamp(copy));
            Assert.Equal(segment, copy);
        }

        Assert.Equal(4, stitcher.SegmentCount);
    }

    [Fact]
    public void SegmentsWhoseTimestampsRestartAreStitchedIntoOneTimeline()
    {
        // 逐片独立切片的源：每个分片都从同一个时间戳重新起算。
        var restarting = Enumerable.Range(0, 5)
            .Select(index => TsFixture.Segment(TsFixture.FirstTimestamp, seed: index))
            .ToArray();
        var continuous = Enumerable.Range(0, 5)
            .Select(index => TsFixture.Segment(TsFixture.FirstTimestamp + (index * 6 * Second), seed: index))
            .ToArray();

        // 复现：原样拼接后，播放器按首尾时间戳只能算出一个分片的时长。
        Assert.Equal(6.0, TsFixture.ProbedDurationSeconds(TsFixture.Concat(restarting)), 3);

        var stitched = TsFixture.Stitch(restarting);

        Assert.Equal(TsFixture.Concat(continuous), stitched);
        Assert.Equal(30.0, TsFixture.ProbedDurationSeconds(stitched), 3);
        TsFixture.AssertMonotonic(stitched);
    }

    [Fact]
    public void SplicedContentWithUnrelatedTimestampsIsStitchedInBothDirections()
    {
        // 正片 12 秒 → 插播（时间戳倒退到很小的值）→ 正片继续（时间戳向前跳回原时间轴）→ 片尾 17 秒重新起算。
        var source = new[]
        {
            TsFixture.Segment(100 * Second, seed: 1),
            TsFixture.Segment(106 * Second, seed: 2),
            TsFixture.Segment(3 * Second, seed: 3),
            TsFixture.Segment(112 * Second, seed: 4),
            TsFixture.Segment(TsFixture.FirstTimestamp, frames: 425, seed: 5),
        };
        var expected = new[]
        {
            TsFixture.Segment(100 * Second, seed: 1),
            TsFixture.Segment(106 * Second, seed: 2),
            TsFixture.Segment(112 * Second, seed: 3),
            TsFixture.Segment(118 * Second, seed: 4),
            TsFixture.Segment(124 * Second, frames: 425, seed: 5),
        };

        // 复现用户看到的现象：总时长只剩最后一段连续区间。
        Assert.Equal(17.0, TsFixture.ProbedDurationSeconds(TsFixture.Concat(source), sinceFileStart: false), 3);

        var stitched = TsFixture.Stitch(source);

        Assert.Equal(TsFixture.Concat(expected), stitched);
        Assert.Equal(41.0, TsFixture.ProbedDurationSeconds(stitched), 3);
        TsFixture.AssertMonotonic(stitched);
    }

    [Fact]
    public void TimestampWrapAroundIsNotMistakenForADiscontinuity()
    {
        // 33 位时间戳约 26.5 小时回绕一次：跨回绕点的连续分片不能被改写。
        var beforeWrap = TsFixture.Segment(Wrap - (6 * Second), seed: 1);
        var afterWrap = TsFixture.Segment(0, seed: 2);
        var stitcher = new MpegTsTimelineStitcher();
        var first = beforeWrap.ToArray();
        var second = afterWrap.ToArray();

        Assert.Equal(0, stitcher.Restamp(first));
        Assert.Equal(0, stitcher.Restamp(second));

        Assert.Equal(beforeWrap, first);
        Assert.Equal(afterWrap, second);
    }

    [Fact]
    public void RestartedSegmentIsStitchedAcrossTheWrapPoint()
    {
        var source = new[]
        {
            TsFixture.Segment(Wrap - (3 * Second), seed: 1),
            TsFixture.Segment(50 * Second, seed: 2),
        };
        var expected = new[]
        {
            TsFixture.Segment(Wrap - (3 * Second), seed: 1),
            TsFixture.Segment(3 * Second, seed: 2),
        };

        Assert.Equal(TsFixture.Concat(expected), TsFixture.Stitch(source));
    }

    [Theory]
    [InlineData(188, 0)]
    [InlineData(188, 37)]
    [InlineData(192, 0)]
    [InlineData(204, 16)]
    public void DisguisedHeadersAndOtherPacketSizesAreStillStitched(int packetSize, int junkBytes)
    {
        var source = new[]
        {
            TsFixture.Segment(TsFixture.FirstTimestamp, seed: 1, packetSize: packetSize, junkBytes: junkBytes),
            TsFixture.Segment(TsFixture.FirstTimestamp, seed: 2, packetSize: packetSize, junkBytes: junkBytes),
        };
        var expected = new[]
        {
            TsFixture.Segment(TsFixture.FirstTimestamp, seed: 1, packetSize: packetSize, junkBytes: junkBytes),
            TsFixture.Segment(TsFixture.FirstTimestamp + (6 * Second), seed: 2, packetSize: packetSize, junkBytes: junkBytes),
        };

        Assert.Equal(TsFixture.Concat(expected), TsFixture.Stitch(source));
    }

    [Fact]
    public void AudioOnlySegmentsAreStitchedByTheirAudioTimestamps()
    {
        var source = new[]
        {
            TsFixture.Segment(TsFixture.FirstTimestamp, seed: 1, video: false),
            TsFixture.Segment(TsFixture.FirstTimestamp, seed: 2, video: false),
        };
        var expected = new[]
        {
            TsFixture.Segment(TsFixture.FirstTimestamp, seed: 1, video: false),
            TsFixture.Segment(TsFixture.FirstTimestamp + (6 * Second), seed: 2, video: false),
        };

        Assert.Equal(TsFixture.Concat(expected), TsFixture.Stitch(source));
    }

    [Fact]
    public void ResumeContinuesFromTheLastMergedSegment()
    {
        var source = Enumerable.Range(0, 3)
            .Select(index => TsFixture.Segment(TsFixture.FirstTimestamp, seed: index))
            .ToArray();
        var first = new MpegTsTimelineStitcher();
        var merged = source.Take(2).Select(segment => segment.ToArray()).ToArray();
        foreach (var segment in merged)
        {
            first.Restamp(segment);
        }

        // 新进程续传：只有 merged.ts 里最后一个已合并分片可用。
        var resumed = new MpegTsTimelineStitcher();
        resumed.Resume(2, merged[1]);
        var third = source[2].ToArray();
        resumed.Restamp(third);

        Assert.Equal(3, resumed.SegmentCount);
        Assert.Equal(TsFixture.Segment(TsFixture.FirstTimestamp + (12 * Second), seed: 2), third);
    }

    [Fact]
    public void SegmentsWithoutReadableTimestampsAreNeverModified()
    {
        var random = new Random(20261122);
        var stitcher = new MpegTsTimelineStitcher();
        stitcher.Restamp(TsFixture.Segment(TsFixture.FirstTimestamp, seed: 1));

        // 不是 TS（错误页、fMP4、随机数据）。
        var noise = new byte[4096];
        random.NextBytes(noise);
        var html = "<html><body>403 Forbidden</body></html>"u8.ToArray();
        // 仍被加扰的 TS：时间戳不可读。
        var scrambled = TsFixture.Segment(TsFixture.FirstTimestamp, seed: 2, scrambled: true);
        foreach (var body in new[] { noise, html, Array.Empty<byte>(), scrambled })
        {
            var copy = body.ToArray();
            stitcher.Restamp(copy);
            Assert.Equal(body, copy);
        }
    }

    [Fact]
    public void MalformedTransportPacketsNeverThrow()
    {
        // 包头同步字节正确、内容随机：解析必须只做边界内访问。
        var random = new Random(17);
        var stitcher = new MpegTsTimelineStitcher();
        stitcher.Restamp(TsFixture.Segment(TsFixture.FirstTimestamp, seed: 1));
        for (var round = 0; round < 200; round++)
        {
            var body = new byte[188 * 64];
            random.NextBytes(body);
            for (var offset = 0; offset < body.Length; offset += 188)
            {
                body[offset] = 0x47;
                if (round % 2 == 0)
                {
                    // 一半的轮次伪造 PES 起始码，逼近时间戳解析路径。
                    body[offset + 1] = (byte)(0x40 | (body[offset + 1] & 0x1F));
                    body[offset + 3] = (byte)(0x10 | (body[offset + 3] & 0x0F));
                    body[offset + 4] = 0;
                    body[offset + 5] = 0;
                    body[offset + 6] = 1;
                    body[offset + 7] = 0xE0;
                }
            }

            stitcher.Restamp(body.AsSpan(0, body.Length - (round % 188)));
        }

        Assert.Equal(201, stitcher.SegmentCount);
    }
}

/// <summary>
/// 测试用 MPEG-TS 分片生成器与独立的时间戳读取器。
/// 每帧 3 个包：视频 PES 起始包（PCR + PTS + DTS）、视频续包、音频 PES 起始包（仅 PTS）。
/// 同一 seed 的两个分片除时间戳字段外逐字节相同，因此“接续后的结果”可以直接与“按连续时间戳生成的分片”比较。
/// </summary>
internal static class TsFixture
{
    internal const long Second = 90_000;
    internal const long FrameInterval = 3_600;
    /// <summary>ffmpeg 切片的常见起始时间戳（1.4 秒）。</summary>
    internal const long FirstTimestamp = 126_000;
    private const long Mask = (1L << 33) - 1;
    private const int VideoPid = 0x100;
    private const int AudioPid = 0x101;

    internal static byte[] Segment(
        long startDts,
        int frames = 150,
        int seed = 0,
        bool video = true,
        int packetSize = 188,
        int junkBytes = 0,
        bool scrambled = false)
    {
        using var output = new MemoryStream();
        // 伪装头：防盗链源常在分片前面垫一段假图片头。
        output.Write(Enumerable.Repeat((byte)0x89, junkBytes).ToArray());

        void Emit(byte[] packet)
        {
            if (scrambled)
            {
                packet[3] |= 0x80;
            }

            // 192/204 字节包：188 字节 TS 包之外的附加字节。
            output.Write(Enumerable.Repeat((byte)0xAA, packetSize - 188).ToArray());
            output.Write(packet);
        }

        Emit(Psi(0x0000, [0x00, 0x00, 0xB0, 0x0D, 0x00, 0x01, 0xC1, 0x00, 0x00, 0x00, 0x01, 0xF0, 0x00]));
        Emit(Psi(0x1000, [0x00, 0x02, 0xB0, 0x17, 0x00, 0x01, 0xC1, 0x00, 0x00, 0xE1, 0x00, 0xF0, 0x00]));
        for (var frame = 0; frame < frames; frame++)
        {
            var dts = startDts + (frame * FrameInterval);
            if (video)
            {
                Emit(VideoStart(dts, frame, seed));
                Emit(VideoContinuation(frame, seed));
            }

            Emit(AudioStart(dts, frame, seed));
        }

        return output.ToArray();
    }

    internal static byte[] Concat(IEnumerable<byte[]> segments) =>
        segments.SelectMany(segment => segment).ToArray();

    internal static byte[] Stitch(IEnumerable<byte[]> segments)
    {
        var stitcher = new MpegTsTimelineStitcher();
        return Concat(segments.Select(segment =>
        {
            var copy = segment.ToArray();
            stitcher.Restamp(copy);
            return copy;
        }));
    }

    /// <summary>
    /// 模拟播放器对 TS 文件的时长估算：文件末尾的时间戳 − 文件开头的时间戳（再加一帧）。
    /// <paramref name="sinceFileStart"/> 为 false 时取“最后一段连续区间”的长度，对应时间戳在中途重新起算的文件。
    /// </summary>
    internal static double ProbedDurationSeconds(byte[] data, bool sinceFileStart = true)
    {
        var stamps = ReadPresentationTimes(data);
        var last = stamps[^1];
        var first = stamps[0];
        if (!sinceFileStart)
        {
            for (var index = stamps.Count - 1; index > 0; index--)
            {
                if (stamps[index] < stamps[index - 1])
                {
                    first = stamps[index];
                    break;
                }
            }
        }

        return (last - first + FrameInterval) / (double)Second;
    }

    internal static void AssertMonotonic(byte[] data)
    {
        var stamps = ReadPresentationTimes(data);
        for (var index = 1; index < stamps.Count; index++)
        {
            Assert.True(
                stamps[index] == stamps[index - 1] + FrameInterval,
                $"timestamp {index}: {stamps[index - 1]} -> {stamps[index]}");
        }
    }

    /// <summary>独立于被测代码的读取器：按生成器的固定布局取出参考流每个 PES 的 PTS。</summary>
    private static List<long> ReadPresentationTimes(byte[] data)
    {
        var all = new List<(int Pid, long Pts)>();
        for (var offset = 0; offset + 188 <= data.Length; offset += 188)
        {
            Assert.Equal(0x47, data[offset]);
            var pid = ((data[offset + 1] & 0x1F) << 8) | data[offset + 2];
            var start = (data[offset + 1] & 0x40) != 0;
            if (!start || pid is not (VideoPid or AudioPid))
            {
                continue;
            }

            var pes = offset + 4 + (((data[offset + 3] & 0x20) != 0) ? 1 + data[offset + 4] : 0);
            var field = data.AsSpan(pes + 9, 5);
            var pts = ((long)((field[0] >> 1) & 0x07) << 30)
                | ((long)field[1] << 22)
                | ((long)(field[2] >> 1) << 15)
                | ((long)field[3] << 7)
                | ((long)field[4] >> 1);
            all.Add((pid, pts));
        }

        var reference = all.Any(stamp => stamp.Pid == VideoPid) ? VideoPid : AudioPid;
        return all.Where(stamp => stamp.Pid == reference).Select(stamp => stamp.Pts).ToList();
    }

    private static byte[] Psi(int pid, byte[] section)
    {
        var packet = NewPacket(pid, payloadUnitStart: true, adaptation: false, counter: 0);
        section.CopyTo(packet, 4);
        return packet;
    }

    private static byte[] VideoStart(long dts, int frame, int seed)
    {
        var packet = NewPacket(VideoPid, payloadUnitStart: true, adaptation: true, counter: frame * 2);
        packet[4] = 7;
        packet[5] = 0x10;
        WriteClockReference(packet.AsSpan(6, 6), dts - 9_000);
        var pes = packet.AsSpan(12);
        WritePesHeader(pes, 0xE0, withDts: true);
        WriteTimestamp(pes.Slice(9, 5), 0x3, dts + (2 * FrameInterval));
        WriteTimestamp(pes.Slice(14, 5), 0x1, dts);
        FillPayload(pes[19..], frame, seed);
        return packet;
    }

    private static byte[] VideoContinuation(int frame, int seed)
    {
        var packet = NewPacket(VideoPid, payloadUnitStart: false, adaptation: false, counter: (frame * 2) + 1);
        var payload = packet.AsSpan(4);
        FillPayload(payload, frame, seed + 1);
        // 负载里出现同步字节和 PES 起始码都不能被当成包头/时间戳。
        new byte[] { 0x47, 0x00, 0x00, 0x01, 0xE0, 0x00, 0x00, 0x80, 0xC0, 0x0A, 0x31, 0x00, 0x01, 0x00, 0x01 }
            .CopyTo(payload[8..]);
        return packet;
    }

    private static byte[] AudioStart(long pts, int frame, int seed)
    {
        var packet = NewPacket(AudioPid, payloadUnitStart: true, adaptation: false, counter: frame);
        var pes = packet.AsSpan(4);
        WritePesHeader(pes, 0xC0, withDts: false);
        WriteTimestamp(pes.Slice(9, 5), 0x2, pts);
        FillPayload(pes[14..], frame, seed + 2);
        return packet;
    }

    private static byte[] NewPacket(int pid, bool payloadUnitStart, bool adaptation, int counter)
    {
        var packet = new byte[188];
        Array.Fill(packet, (byte)0xFF);
        packet[0] = 0x47;
        packet[1] = (byte)((payloadUnitStart ? 0x40 : 0x00) | ((pid >> 8) & 0x1F));
        packet[2] = (byte)pid;
        packet[3] = (byte)((adaptation ? 0x30 : 0x10) | (counter & 0x0F));
        return packet;
    }

    private static void WritePesHeader(Span<byte> pes, byte streamId, bool withDts)
    {
        pes[0] = 0;
        pes[1] = 0;
        pes[2] = 1;
        pes[3] = streamId;
        pes[4] = 0;
        pes[5] = 0;
        pes[6] = 0x80;
        pes[7] = (byte)(withDts ? 0xC0 : 0x80);
        pes[8] = (byte)(withDts ? 10 : 5);
    }

    private static void WriteTimestamp(Span<byte> field, int prefix, long value)
    {
        value &= Mask;
        field[0] = (byte)((prefix << 4) | (int)((value >> 30) & 0x07) << 1 | 1);
        field[1] = (byte)(value >> 22);
        field[2] = (byte)((int)((value >> 15) & 0x7F) << 1 | 1);
        field[3] = (byte)(value >> 7);
        field[4] = (byte)((int)(value & 0x7F) << 1 | 1);
    }

    private static void WriteClockReference(Span<byte> field, long value)
    {
        value &= Mask;
        field[0] = (byte)(value >> 25);
        field[1] = (byte)(value >> 17);
        field[2] = (byte)(value >> 9);
        field[3] = (byte)(value >> 1);
        field[4] = (byte)(((value & 1) << 7) | 0x7E);
        field[5] = 0x2A;
    }

    private static void FillPayload(Span<byte> payload, int frame, int seed)
    {
        for (var index = 0; index < payload.Length; index++)
        {
            payload[index] = (byte)((frame * 31) + (seed * 17) + index);
        }
    }
}
