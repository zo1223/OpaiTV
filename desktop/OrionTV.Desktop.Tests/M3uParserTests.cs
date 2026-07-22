using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.Tests;

public class M3uParserTests
{
    [Fact]
    public void Parse_reads_name_group_and_logo()
    {
        const string m3u = "#EXTM3U\n#EXTINF:-1 tvg-logo=\"http://logo/a.png\" group-title=\"央视\",CCTV1\nhttp://example.com/cctv1.m3u8\n";

        var channels = M3uParser.Parse(m3u);

        Assert.Single(channels);
        Assert.Equal("CCTV1", channels[0].Name);
        Assert.Equal("央视", channels[0].Group);
        Assert.Equal("http://logo/a.png", channels[0].Logo);
        Assert.Equal("http://example.com/cctv1.m3u8", channels[0].Url);
    }

    [Fact]
    public void Parse_defaults_group_when_missing()
    {
        const string m3u = "#EXTINF:-1,Local\nhttp://example.com/x.m3u8\n";

        var channels = M3uParser.Parse(m3u);

        Assert.Single(channels);
        Assert.Equal("Local", channels[0].Name);
        Assert.Equal("Default", channels[0].Group);
    }

    [Fact]
    public void Parse_skips_lines_without_url()
    {
        const string m3u = "#EXTM3U\n#EXTINF:-1,Only Info\n";
        Assert.Empty(M3uParser.Parse(m3u));
    }

    [Fact]
    public void Parse_handles_multiple_channels_and_groups()
    {
        const string m3u =
            "#EXTINF:-1 group-title=\"A\",One\nhttp://a/1\n" +
            "#EXTINF:-1 group-title=\"B\",Two\nhttp://b/2\n";

        var channels = M3uParser.Parse(m3u);

        Assert.Equal(2, channels.Count);
        Assert.Equal("A", channels[0].Group);
        Assert.Equal("B", channels[1].Group);
    }

    [Fact]
    public void MergeChannels_preserves_primary_groups_and_appends_named_manual_sources()
    {
        var primary = new ConfiguredLiveSource("默认直播源", "https://primary/list.m3u", true);
        var extra = new ConfiguredLiveSource("BBC", "https://extra/list.m3u", false);
        var primaryChannel = new LiveChannel("p1", "CCTV1", "https://stream/cctv1", "央视");
        var extraChannel = new LiveChannel("e1", "BBC News", "https://stream/bbc", "News");

        var merged = DesktopLiveSourceSettings.MergeChannels([
            (primary, (IReadOnlyList<LiveChannel>)[primaryChannel]),
            (extra, (IReadOnlyList<LiveChannel>)[extraChannel, primaryChannel])
        ]);

        Assert.Equal(2, merged.Count);
        Assert.Same(primaryChannel, merged[0]);
        Assert.Equal("央视", merged[0].Group);
        Assert.Equal("BBC · News", merged[1].Group);
        Assert.Equal("BBC News", merged[1].Name);
    }
}
