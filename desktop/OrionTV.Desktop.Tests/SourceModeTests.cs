using System.Text.Json;
using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.Tests;

public class SourceModeTests
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    private static SearchResult Result(string title, string? typeName = null, IReadOnlyList<PlaybackSource>? lines = null, string source = "s1") =>
        new("1", title, "", lines?.FirstOrDefault()?.Episodes ?? [], source, source, "2024", TypeName: typeName, PlaybackSources: lines);

    [Theory]
    [InlineData("stable", SourceMode.Stable)]
    [InlineData("media", SourceMode.Media)]
    [InlineData("full", SourceMode.Full)]
    [InlineData("off", SourceMode.Off)]
    [InlineData(" OFF ", SourceMode.Off)]
    [InlineData("", SourceMode.Full)]
    [InlineData(null, SourceMode.Full)]
    [InlineData("whatever", SourceMode.Full)]
    public void Parse_UnknownOrMissingIsFull(string? value, SourceMode expected)
    {
        Assert.Equal(expected, SourceModes.Parse(value));
    }

    [Fact]
    public void ToValue_RoundTripsAllModes()
    {
        foreach (var mode in SourceModes.All)
        {
            Assert.Equal(mode, SourceModes.Parse(SourceModes.ToValue(mode)));
        }

        Assert.Equal(4, SourceModes.All.Count);
    }

    [Fact]
    public void ModeOf_DefaultsToFullAndWithIsImmediate()
    {
        Assert.Equal(SourceMode.Full, SourceModes.ModeOf(null, "a"));
        var modes = SourceModes.With(null, "a", SourceMode.Off);
        Assert.Equal(SourceMode.Off, SourceModes.ModeOf(modes, "a"));
        Assert.Equal(SourceMode.Full, SourceModes.ModeOf(modes, "b"));

        var back = SourceModes.With(modes, "a", SourceMode.Full);
        Assert.Empty(back);
        // 原字典不被修改
        Assert.Equal(SourceMode.Off, SourceModes.ModeOf(modes, "a"));
    }

    [Fact]
    public void ActiveSources_DropsOffSources()
    {
        var sources = new[]
        {
            new VideoSource("a", "A", "https://a.example/api"),
            new VideoSource("b", "B", "https://b.example/api"),
        };
        var modes = SourceModes.With(null, "b", SourceMode.Off);

        var active = SourceModes.ActiveSources(sources, key => SourceModes.ModeOf(modes, key));

        Assert.Equal(new[] { "a" }, active.Select(source => source.Key).ToArray());
        Assert.Same(sources, SourceModes.ActiveSources(sources, null));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("动作片", true)]
    [InlineData("国产剧", true)]
    [InlineData("日本动漫", true)]
    [InlineData("大陆综艺", true)]
    [InlineData("纪录片", true)]
    [InlineData("短剧", false)]
    [InlineData("电影解说", false)]
    [InlineData("体育赛事", false)]
    [InlineData("MV", false)]
    [InlineData("新闻资讯", false)]
    [InlineData("其它", false)]
    public void IsMediaCategory_ExcludeWordsWinThenIncludeWords(string? category, bool expected)
    {
        Assert.Equal(expected, SourceModes.IsMediaCategory(category));
    }

    [Fact]
    public void Apply_MediaKeepsOnlyMediaCategories()
    {
        var results = new[] { Result("甲", "动作片"), Result("乙", "短剧"), Result("丙", null), Result("丁", "体育") };

        var kept = SourceModes.Apply(SourceMode.Media, results);

        Assert.Equal(new[] { "甲", "丙" }, kept.Select(item => item.Title).ToArray());
        Assert.Same(results, SourceModes.Apply(SourceMode.Full, results));
        Assert.Empty(SourceModes.Apply(SourceMode.Off, results));
    }

    [Fact]
    public void PickStableLine_PrefersHlsThenEpisodeCountThenFirst()
    {
        var mp4 = new PlaybackSource("mp4", "直链", ["https://v.example/1.mp4", "https://v.example/2.mp4", "https://v.example/3.mp4"]);
        var hlsShort = new PlaybackSource("m1", "线路1", ["https://v.example/1.m3u8"]);
        var hlsLong = new PlaybackSource("m2", "线路2", ["https://v.example/1.m3u8", "https://v.example/2.m3u8"]);
        var hlsLongTwin = new PlaybackSource("m3", "线路3", ["https://v.example/a.m3u8", "https://v.example/b.m3u8"]);

        Assert.Equal("m2", SourceModes.PickStableLine([mp4, hlsShort, hlsLong, hlsLongTwin])!.Key);
        Assert.Equal("m1", SourceModes.PickStableLine([mp4, hlsShort])!.Key);
        Assert.Null(SourceModes.PickStableLine([]));
    }

    [Fact]
    public void Apply_StableKeepsOneLinePerResult()
    {
        var mp4 = new PlaybackSource("mp4", "直链", ["https://v.example/1.mp4"]);
        var hls = new PlaybackSource("hls", "线路", ["https://v.example/1.m3u8", "https://v.example/2.m3u8"]);
        var results = new[] { Result("甲", lines: [mp4, hls]), Result("乙") };

        var kept = SourceModes.Apply(SourceMode.Stable, results);

        Assert.Equal(2, kept.Count);
        Assert.Equal(new[] { "hls" }, kept[0].PlaybackSources!.Select(line => line.Key).ToArray());
        Assert.Equal(hls.Episodes, kept[0].Episodes);
        Assert.Single(SearchResultSources.Expand(kept[0]));
        Assert.Same(results[1], kept[1]);
    }

    [Fact]
    public void Settings_RoundTripSourceModesAndStayCompatibleWithOldFiles()
    {
        var settings = new DesktopSettings(SourceModes: SourceModes.With(SourceModes.With(null, "a", SourceMode.Stable), "b", SourceMode.Off));
        var restored = JsonSerializer.Deserialize<DesktopSettings>(JsonSerializer.Serialize(settings, Options), Options)!;

        Assert.Equal(SourceMode.Stable, SourceModes.ModeOf(restored.SourceModes, "a"));
        Assert.Equal(SourceMode.Off, SourceModes.ModeOf(restored.SourceModes, "b"));
        Assert.Equal(SourceMode.Full, SourceModes.ModeOf(restored.SourceModes, "c"));

        // 旧设置文件没有这两个字段：模式全部是全量，推荐默认开
        var legacy = DesktopSettingsMigration.Apply(
            JsonSerializer.Deserialize<DesktopSettings>("""{"SubscriptionUrl":"https://subscription.example/c.json","Volume":80}""", Options)!);
        Assert.Null(legacy.SourceModes);
        Assert.True(legacy.PersonalizedRecommendations);
        Assert.Equal(SourceMode.Full, SourceModes.ModeOf(legacy.SourceModes, "a"));
        Assert.Equal(80, legacy.Volume);
    }

    [Fact]
    public void Normalize_DropsInvalidAndDefaultEntries()
    {
        var raw = new Dictionary<string, string> { ["a"] = "OFF", ["b"] = "full", ["c"] = "bogus", ["d"] = "media" };

        var normalized = SourceModes.Normalize(raw)!;

        Assert.Equal(2, normalized.Count);
        Assert.Equal("off", normalized["a"]);
        Assert.Equal("media", normalized["d"]);
        Assert.Same(normalized, SourceModes.Normalize(normalized));
    }
}
