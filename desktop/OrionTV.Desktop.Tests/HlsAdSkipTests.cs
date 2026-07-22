using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.Tests;

public class HlsAdSkipTests
{
    [Fact]
    public void ParseAdRanges_detects_single_cue_block()
    {
        const string playlist =
            "#EXTM3U\n" +
            "#EXTINF:10.0,\n" + "s0.ts\n" +
            "#EXTINF:10.0,\n" + "s1.ts\n" +
            "#EXT-X-CUE-OUT:DURATION=5.0\n" +
            "#EXTINF:5.0,\n" + "ad.ts\n" +
            "#EXT-X-CUE-IN\n" +
            "#EXTINF:10.0,\n" + "s2.ts\n";

        var ranges = HlsAdSkip.ParseAdRanges(playlist);

        Assert.Single(ranges);
        Assert.Equal(20000, ranges[0].StartMs);
        Assert.Equal(25000, ranges[0].EndMs);
    }

    [Fact]
    public void ParseAdRanges_detects_multiple_blocks()
    {
        const string playlist =
            "#EXTINF:30,\n" + "a.ts\n" +
            "#EXT-X-CUE-OUT:DURATION=15\n" +
            "#EXTINF:15,\n" + "ad1.ts\n" +
            "#EXT-X-CUE-IN\n" +
            "#EXTINF:60,\n" + "b.ts\n" +
            "#EXT-X-CUE-OUT\n" +
            "#EXTINF:10,\n" + "ad2.ts\n" +
            "#EXT-X-CUE-IN\n";

        var ranges = HlsAdSkip.ParseAdRanges(playlist);

        Assert.Equal(2, ranges.Count);
        Assert.Equal((30000, 45000), (ranges[0].StartMs, ranges[0].EndMs));
        Assert.Equal((105000, 115000), (ranges[1].StartMs, ranges[1].EndMs));
    }

    [Fact]
    public void ParseAdRanges_returns_empty_without_markers()
    {
        const string playlist = "#EXTM3U\n#EXTINF:10,\ns0.ts\n#EXTINF:10,\ns1.ts\n";
        Assert.Empty(HlsAdSkip.ParseAdRanges(playlist));
    }

    [Fact]
    public void ParseAdRanges_returns_empty_for_master_playlist()
    {
        const string master =
            "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=1280x720\n720p.m3u8\n";
        Assert.Empty(HlsAdSkip.ParseAdRanges(master));
    }

    [Fact]
    public void ParseAdRanges_ignores_unclosed_cue_out()
    {
        const string playlist = "#EXTINF:10,\ns0.ts\n#EXT-X-CUE-OUT:DURATION=5\n#EXTINF:5,\nad.ts\n";
        // 没有 CUE-IN 收尾，不应产生区间
        Assert.Empty(HlsAdSkip.ParseAdRanges(playlist));
    }

    [Fact]
    public void ForeignHost_detects_minority_block_from_other_cdn()
    {
        const string playlist =
            "#EXTINF:10,\n" + "https://cdn-a.example/0.ts\n" +
            "#EXTINF:10,\n" + "https://cdn-a.example/1.ts\n" +
            "#EXTINF:5,\n" + "https://ad-cdn.example/ad0.ts\n" +
            "#EXTINF:5,\n" + "https://ad-cdn.example/ad1.ts\n" +
            "#EXTINF:10,\n" + "https://cdn-a.example/2.ts\n";

        var ranges = HlsAdSkip.ParseForeignHostAdRanges(playlist, "https://cdn-a.example/x.m3u8");

        Assert.Single(ranges);
        Assert.Equal((20000, 30000), (ranges[0].StartMs, ranges[0].EndMs));
    }

    [Fact]
    public void ForeignHost_returns_empty_for_single_host()
    {
        const string playlist =
            "#EXTINF:10,\n" + "https://cdn-a.example/0.ts\n" +
            "#EXTINF:10,\n" + "https://cdn-a.example/1.ts\n";
        Assert.Empty(HlsAdSkip.ParseForeignHostAdRanges(playlist, "https://cdn-a.example/x.m3u8"));
    }

    [Fact]
    public void ForeignHost_resolves_relative_segments_to_base_host()
    {
        // 相对段路径解析到同一 base host，应视为单 host，不产生广告区间。
        const string playlist =
            "#EXTINF:10,\n" + "0.ts\n" +
            "#EXTINF:10,\n" + "1.ts\n";
        Assert.Empty(HlsAdSkip.ParseForeignHostAdRanges(playlist, "https://cdn-a.example/path/x.m3u8"));
    }

    [Fact]
    public void ForeignHost_bails_out_when_no_clear_majority()
    {
        // 异源占比过高（>50%）时无法可靠区分，放弃跳过。
        const string playlist =
            "#EXTINF:10,\n" + "https://cdn-a.example/0.ts\n" +
            "#EXTINF:10,\n" + "https://cdn-b.example/1.ts\n" +
            "#EXTINF:5,\n" + "https://cdn-c.example/2.ts\n";
        Assert.Empty(HlsAdSkip.ParseForeignHostAdRanges(playlist, "https://cdn-a.example/x.m3u8"));
    }
}
