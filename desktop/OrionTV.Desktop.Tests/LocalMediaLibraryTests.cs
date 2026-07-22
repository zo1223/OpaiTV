using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.Tests;

public sealed class LocalMediaLibraryTests
{
    [Theory]
    [InlineData("My.Show.S02E03.1080p.mkv", "My Show", 3)]
    [InlineData("庆余年 第12集.mp4", "庆余年", 12)]
    [InlineData("Series_EP07_web.mkv", "Series", 7)]
    [InlineData("Series - 08 - 1080p.mp4", "Series", 8)]
    public void Parse_extracts_common_episode_names(string fileName, string title, int episode)
    {
        var parsed = LocalMediaNaming.Parse(fileName);

        Assert.Equal(title, parsed.Title);
        Assert.Equal(episode, parsed.EpisodeNumber);
    }

    [Fact]
    public void Parse_does_not_treat_a_four_digit_movie_year_as_an_episode()
    {
        var parsed = LocalMediaNaming.Parse("Movie.2024.1080p.mkv");

        Assert.Null(parsed.EpisodeNumber);
    }

    [Fact]
    public void Parse_keeps_dots_in_a_cache_title_that_has_no_media_extension()
    {
        var parsed = LocalMediaNaming.Parse("Mr. Robot 第2集");

        Assert.Equal("Mr Robot", parsed.Title);
        Assert.Equal(2, parsed.EpisodeNumber);
    }

    [Fact]
    public void Parse_uses_the_parent_folder_for_numeric_episode_files()
    {
        var path = Path.Combine("Shows", "My Series", "[03].mkv");

        var parsed = LocalMediaNaming.Parse(path);

        Assert.Equal("My Series", parsed.Title);
        Assert.Equal(3, parsed.EpisodeNumber);
    }

    [Fact]
    public void Import_groups_sorts_and_persists_episodes_with_neighboring_poster()
    {
        var root = Path.Combine(Path.GetTempPath(), $"opaitv-local-media-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var first = Path.Combine(root, "My.Show.S01E01.mkv");
            var second = Path.Combine(root, "My.Show.S01E02.mkv");
            var poster = Path.Combine(root, "My Show.jpg");
            File.WriteAllBytes(first, [1]);
            File.WriteAllBytes(second, [2]);
            File.WriteAllBytes(poster, [3]);
            var storePath = Path.Combine(root, "library.json");

            var imported = new LocalMediaStore(storePath).Import([second, first]);

            var item = Assert.Single(imported);
            Assert.Equal("My Show", item.Title);
            Assert.Equal([1, 2], item.Episodes.Select(episode => episode.Number));
            Assert.Equal([first, second], item.Episodes.Select(episode => episode.Path));
            Assert.Equal(poster, item.Poster);

            var reloaded = Assert.Single(new LocalMediaStore(storePath).GetItems());
            Assert.Equal(item.Id, reloaded.Id);
            Assert.Equal(item.Title, reloaded.Title);
            Assert.Equal(item.Poster, reloaded.Poster);
            Assert.Equal(item.SaveTime, reloaded.SaveTime);
            Assert.Equal(item.Episodes.ToArray(), reloaded.Episodes.ToArray());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Import_merges_later_episodes_into_the_existing_series()
    {
        var root = Path.Combine(Path.GetTempPath(), $"opaitv-local-media-merge-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var first = Path.Combine(root, "Show 第1集.mp4");
            var second = Path.Combine(root, "Show 第2集.mp4");
            File.WriteAllBytes(first, [1]);
            File.WriteAllBytes(second, [2]);
            var store = new LocalMediaStore(Path.Combine(root, "library.json"));

            var original = Assert.Single(store.Import([first]));
            var updated = Assert.Single(store.Import([second]));

            Assert.Equal(original.Id, updated.Id);
            Assert.Equal(2, updated.Episodes.Count);
            Assert.Single(store.GetItems());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Updating_a_missing_history_cover_preserves_watch_order_and_local_episodes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"opaitv-history-{Guid.NewGuid():N}.json");
        try
        {
            var store = new PlayRecordStore(path);
            store.Save("local", "media-1", new PlayRecordItem(
                "Show", "本地", "", 1, 2, 12, 30, "",
                LocalMediaId: "media-1",
                LocalEpisodes: ["file:///one.mp4", "file:///two.mp4"]));
            var before = Assert.IsType<PlayRecordItem>(store.Get("local", "media-1"));

            store.UpdateCover("local", "media-1", "poster.jpg");

            var after = Assert.IsType<PlayRecordItem>(store.Get("local", "media-1"));
            Assert.Equal("poster.jpg", after.Cover);
            Assert.Equal(before.SaveTime, after.SaveTime);
            Assert.Equal(before.LocalEpisodes?.ToArray(), after.LocalEpisodes?.ToArray());
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void Existing_history_json_without_local_fields_remains_readable()
    {
        var path = Path.Combine(Path.GetTempPath(), $"opaitv-old-history-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """
                {
                  "source+1": {
                    "Title": "Old Show",
                    "SourceName": "Source",
                    "Cover": "poster.jpg",
                    "Index": 0,
                    "TotalEpisodes": 1,
                    "PlayTime": 12,
                    "TotalTime": 30,
                    "Year": "2024",
                    "SaveTime": 123
                  }
                }
                """);

            var record = Assert.IsType<PlayRecordItem>(new PlayRecordStore(path).Get("source", "1"));

            Assert.Equal("Old Show", record.Title);
            Assert.Null(record.LocalMediaId);
            Assert.Null(record.LocalEpisodes);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
