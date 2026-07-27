using OrionTV.Desktop.App;
using OrionTV.Desktop.App.Views;
using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.Tests;

public sealed class PlaybackResumePolicyTests
{
    [Theory]
    [InlineData(25_000, 15_000)]
    [InlineData(10_000, 0)]
    [InlineData(4_000, 0)]
    public void Resume_position_rewinds_ten_seconds(double savedMs, double expectedMs)
    {
        Assert.Equal(expectedMs, PlaybackResumePolicy.StartPositionMilliseconds(savedMs));
    }

    [Fact]
    public void Episode_matching_uses_saved_number_instead_of_old_array_index()
    {
        var index = PlaybackResumePolicy.FindEpisodeIndex(
            ["第1集", "第180集", "第185集"],
            episodeCount: 3,
            fallbackIndex: 179,
            savedEpisodeLabel: "第180集",
            savedEpisodeNumber: 180);

        Assert.Equal(1, index);
    }

    [Fact]
    public void Episode_matching_rejects_source_that_does_not_have_target_episode()
    {
        var index = PlaybackResumePolicy.FindEpisodeIndex(
            ["第1集", "第2集"],
            episodeCount: 2,
            fallbackIndex: 179,
            savedEpisodeLabel: "第180集",
            savedEpisodeNumber: 180);

        Assert.Equal(-1, index);
    }

    [Fact]
    public void Episode_matching_does_not_replace_missing_number_with_same_array_position()
    {
        var index = PlaybackResumePolicy.FindEpisodeIndex(
            ["第1集", "第3集", "第4集"],
            episodeCount: 3,
            fallbackIndex: 1,
            savedEpisodeLabel: "第2集",
            savedEpisodeNumber: 2);

        Assert.Equal(-1, index);
    }

    [Fact]
    public void Original_playback_line_is_selected_by_saved_line_key()
    {
        var result = new SearchResult(
            "id-1",
            "Series",
            "",
            [],
            "source",
            "Source",
            "",
            PlaybackSources:
            [
                new PlaybackSource("html", "HTML", ["https://video.example/page"]),
                new PlaybackSource("hls", "HLS", ["https://video.example/play.m3u8"]),
            ]);

        var selected = PlaybackResumePolicy.FindOriginalPlaybackLine([result], "id-1", "hls");

        Assert.NotNull(selected);
        Assert.Equal("hls", selected.PlaybackSourceKey);
        Assert.Equal("https://video.example/play.m3u8", Assert.Single(selected.Episodes));
        Assert.Null(PlaybackResumePolicy.FindOriginalPlaybackLine([result], "id-1", "missing"));
    }

    [Fact]
    public void Legacy_history_uses_saved_source_name_without_guessing_a_line()
    {
        var result = new SearchResult(
            "id-1",
            "Series",
            "",
            [],
            "source",
            "Source",
            "",
            PlaybackSources:
            [
                new PlaybackSource("0:html", "HTML", ["https://video.example/page"]),
                new PlaybackSource("1:hls", "HLS", ["https://video.example/play.m3u8"]),
            ]);

        var selected = PlaybackResumePolicy.FindOriginalPlaybackLine(
            [result],
            "id-1",
            playbackSourceKey: null,
            savedSourceName: "Source · HLS");

        Assert.NotNull(selected);
        Assert.Equal("1:hls", selected.PlaybackSourceKey);
        Assert.Null(PlaybackResumePolicy.FindOriginalPlaybackLine([result], "id-1", null));
        Assert.Null(PlaybackResumePolicy.FindOriginalPlaybackLine(
            [result],
            "id-1",
            playbackSourceKey: null,
            savedSourceName: "Source · Missing"));
    }

    [Fact]
    public void Source_switch_probe_targets_only_the_semantically_matching_episode()
    {
        var source = new SearchResult(
            "id-1",
            "Series",
            "",
            ["u1", "u180", "u185"],
            "source",
            "Source",
            "",
            PlaybackSourceKey: "hls",
            EpisodeLabels: ["第1集", "第180集", "第185集"]);
        var target = new PlaybackResumeContext(
            EpisodeIndex: 179,
            PositionMs: 120_000,
            EpisodeLabel: "第180集",
            EpisodeNumber: 180,
            HistorySource: "source",
            HistoryId: "id-1");

        var candidate = Assert.Single(PlayerSourceSwitchPolicy.SelectCandidates([source], target));

        Assert.Same(source, candidate.Source);
        Assert.Equal(1, candidate.EpisodeIndex);
        Assert.Equal(["u180"], candidate.ProbeSource.Episodes);
        Assert.Equal(["第180集"], candidate.ProbeSource.EpisodeLabels);
        Assert.Equal(
            SearchResultSources.SelectionKeyOf(source),
            SearchResultSources.SelectionKeyOf(candidate.ProbeSource));
    }

    [Fact]
    public void Source_switch_excludes_routes_without_the_target_episode()
    {
        var missing = new SearchResult(
            "missing",
            "Series",
            "",
            ["u1", "u2"],
            "missing-source",
            "Missing",
            "",
            EpisodeLabels: ["第1集", "第2集"]);
        var blank = new SearchResult(
            "blank",
            "Series",
            "",
            [""],
            "blank-source",
            "Blank",
            "",
            EpisodeLabels: ["第180集"]);
        var target = new PlaybackResumeContext(
            EpisodeIndex: 179,
            PositionMs: 120_000,
            EpisodeLabel: "第180集",
            EpisodeNumber: 180);

        Assert.Empty(PlayerSourceSwitchPolicy.SelectCandidates([missing, blank], target));
    }

    [Theory]
    [InlineData(false, 1_190_000, 1_191_000, false)]
    [InlineData(true, 65_000, 1_191_000, false)]
    [InlineData(true, 1_180_999, 1_191_000, false)]
    [InlineData(true, 1_181_000, 1_191_000, true)]
    [InlineData(true, 1_191_000, 1_191_000, true)]
    public void Auto_next_requires_confirmed_playback_near_the_real_end(
        bool playbackConfirmed,
        long currentTimeMs,
        long lengthMs,
        bool expected)
    {
        Assert.Equal(
            expected,
            PlayerEpisodeAdvancePolicy.ShouldAdvance(playbackConfirmed, currentTimeMs, lengthMs));
    }

    [Fact]
    public void History_round_trip_preserves_line_and_semantic_episode_identity()
    {
        var path = Path.Combine(Path.GetTempPath(), $"opaitv-resume-{Guid.NewGuid():N}.json");
        try
        {
            var store = new PlayRecordStore(path);
            store.Save("source", "id-1", new PlayRecordItem(
                "Series",
                "Source · HLS",
                "",
                179,
                185,
                1_234,
                2_000,
                "2026",
                PlaybackSourceKey: "hls",
                EpisodeLabel: "第180集",
                EpisodeNumber: 180));

            var record = Assert.IsType<PlayRecordItem>(store.Get("source", "id-1"));

            Assert.Equal("hls", record.PlaybackSourceKey);
            Assert.Equal("第180集", record.EpisodeLabel);
            Assert.Equal(180, record.EpisodeNumber);
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
