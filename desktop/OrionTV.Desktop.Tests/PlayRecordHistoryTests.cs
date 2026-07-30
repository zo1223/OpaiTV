using System.Text.Json;
using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.Tests;

public sealed class PlayRecordHistoryTests
{
    [Fact]
    public void Online_sources_for_the_same_media_use_the_latest_resume_record()
    {
        var history = ReadHistory(new Dictionary<string, PlayRecordItem>
        {
            ["source-a+old-id"] = Record(
                title: "示例 影片",
                cover: "https://a.example/poster.jpg",
                index: 1,
                playTime: 120,
                saveTime: 100),
            ["source-b+latest-id"] = Record(
                title: "示例影片",
                cover: "https://b.example/another-poster.jpg",
                index: 5,
                playTime: 640,
                saveTime: 200,
                playbackSourceKey: "hls-b"),
        });

        var item = Assert.Single(history);
        Assert.Equal("source-b", item.Source);
        Assert.Equal("latest-id", item.Id);
        Assert.Equal(5, item.Record.Index);
        Assert.Equal(640, item.Record.PlayTime);
        Assert.Equal("hls-b", item.Record.PlaybackSourceKey);
    }

    [Fact]
    public void Latest_record_can_reuse_an_older_poster_without_losing_its_resume_target()
    {
        var history = ReadHistory(new Dictionary<string, PlayRecordItem>
        {
            ["source-a+old-id"] = Record(
                title: "示例影片",
                cover: "https://img.example/poster.jpg",
                index: 1,
                playTime: 120,
                saveTime: 100),
            ["source-b+latest-id"] = Record(
                title: "示例影片",
                cover: "",
                index: 3,
                playTime: 360,
                saveTime: 200),
        });

        var item = Assert.Single(history);
        Assert.Equal("source-b", item.Source);
        Assert.Equal("latest-id", item.Id);
        Assert.Equal("https://img.example/poster.jpg", item.Record.Cover);
        Assert.Equal(3, item.Record.Index);
        Assert.Equal(360, item.Record.PlayTime);
    }

    [Fact]
    public void Different_media_and_different_local_items_remain_separate()
    {
        var history = ReadHistory(new Dictionary<string, PlayRecordItem>
        {
            ["source-a+movie-a"] = Record("影片甲", "same-poster.jpg", 0, 10, 300),
            ["source-b+movie-b"] = Record("影片乙", "same-poster.jpg", 0, 20, 200),
            ["local+one"] = Record("本地剧集", "local-poster.jpg", 0, 30, 100),
            ["local+two"] = Record("本地剧集", "local-poster.jpg", 0, 40, 400),
        });

        Assert.Equal(4, history.Count);
        Assert.Equal("two", history[0].Id);
        Assert.Equal("movie-a", history[1].Id);
    }

    private static IReadOnlyList<PlayRecordHistoryEntry> ReadHistory(
        IReadOnlyDictionary<string, PlayRecordItem> records)
    {
        var path = Path.Combine(Path.GetTempPath(), $"opaitv-history-grouping-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(records));
            return new PlayRecordStore(path).GetLatestByMedia();
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static PlayRecordItem Record(
        string title,
        string cover,
        int index,
        double playTime,
        long saveTime,
        string? playbackSourceKey = null) =>
        new(
            Title: title,
            SourceName: "源",
            Cover: cover,
            Index: index,
            TotalEpisodes: 10,
            PlayTime: playTime,
            TotalTime: 1_000,
            Year: "",
            SaveTime: saveTime,
            PlaybackSourceKey: playbackSourceKey);
}
