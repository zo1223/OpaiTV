using OrionTV.Desktop.Core;
using Xunit;

namespace OrionTV.Desktop.Tests;

public sealed class MiniPlayerSessionTests
{
    private static MiniPlayerSession Create(
        string title = "示例剧",
        string label = "第3集",
        double startMs = 65_000) =>
        new(new MiniPlayerMedia(title, label, "https://media.example/v/3.m3u8", startMs));

    [Fact]
    public void StartsAtHandoffPositionAndShowsTitleWithEpisode()
    {
        var session = Create();

        Assert.Equal("示例剧  第3集", session.DisplayTitle);
        Assert.Equal(65_000, session.PositionMs);
        Assert.Equal(0, session.DurationMs);
        Assert.False(session.IsPlaying);
        Assert.False(session.IsCollapsed);
        Assert.False(session.IsClosed);
        Assert.False(session.HasProgressed);
        Assert.Equal("01:05 / --:--", session.ProgressText);
        Assert.Equal(0, session.ProgressFraction);
    }

    [Theory]
    [InlineData("示例剧", "", "示例剧")]
    [InlineData("示例剧", "   ", "示例剧")]
    [InlineData("", "第1集", "第1集")]
    [InlineData("  示例剧 ", " 第2集 ", "示例剧  第2集")]
    public void DisplayTitleHandlesMissingParts(string title, string label, string expected)
    {
        Assert.Equal(expected, Create(title, label).DisplayTitle);
    }

    [Fact]
    public void ProgressReportsUpdatePositionDurationAndText()
    {
        var session = Create();
        var changes = 0;
        session.Changed += () => changes++;

        session.ReportProgress(90_000, 600_000);

        Assert.True(session.HasProgressed);
        Assert.Equal(90_000, session.PositionMs);
        Assert.Equal(600_000, session.DurationMs);
        Assert.Equal(0.15, session.ProgressFraction, 6);
        Assert.Equal("01:30 / 10:00", session.ProgressText);
        Assert.Equal(1, changes);

        // 相同的上报不重复触发重画
        session.ReportProgress(90_000, 600_000);
        Assert.Equal(1, changes);
    }

    [Theory]
    [InlineData(-5_000, 600_000, 0, 0)]
    [InlineData(700_000, 600_000, 600_000, 1)]
    [InlineData(double.NaN, 600_000, 0, 0)]
    [InlineData(30_000, 0, 30_000, 0)]
    [InlineData(30_000, -1, 30_000, 0)]
    public void ProgressIsClamped(double position, double duration, double expectedPosition, double expectedFraction)
    {
        var session = Create();

        session.ReportProgress(position, duration);

        Assert.Equal(expectedPosition, session.PositionMs);
        Assert.Equal(expectedFraction, session.ProgressFraction);
    }

    [Fact]
    public void ResumePositionUsesHandoffStartUntilPlayerReportsProgress()
    {
        var session = Create(startMs: 120_000);
        Assert.Equal(120_000, session.ResumePositionMs);

        session.ReportProgress(125_500, 900_000);
        Assert.Equal(125_500, session.ResumePositionMs);

        // 播放器报回 0（例如重新缓冲从头开始）也如实交回，不再用交接起点
        session.ReportProgress(0, 900_000);
        Assert.Equal(0, session.ResumePositionMs);
    }

    [Fact]
    public void NegativeOrInvalidStartPositionIsTreatedAsZero()
    {
        Assert.Equal(0, Create(startMs: -10).ResumePositionMs);
        Assert.Equal(0, Create(startMs: double.NaN).ResumePositionMs);
        Assert.Equal(0, Create(startMs: double.NaN).PositionMs);
    }

    [Fact]
    public void PlayPauseToggleAndReportedState()
    {
        var session = Create();
        var changes = 0;
        session.Changed += () => changes++;

        session.ReportPlaying(true);
        Assert.True(session.IsPlaying);
        Assert.Equal(1, changes);

        session.ReportPlaying(true);
        Assert.Equal(1, changes);

        Assert.False(session.TogglePlayPause());
        Assert.False(session.IsPlaying);
        Assert.True(session.TogglePlayPause());
        Assert.True(session.IsPlaying);
        Assert.Equal(3, changes);
    }

    [Fact]
    public void CollapseAndExpand()
    {
        var session = Create();

        Assert.True(session.ToggleCollapsed());
        Assert.True(session.IsCollapsed);
        Assert.False(session.ToggleCollapsed());
        Assert.False(session.IsCollapsed);
    }

    [Fact]
    public void CollapsingDoesNotChangePlaybackOrProgress()
    {
        var session = Create();
        session.ReportPlaying(true);
        session.ReportProgress(10_000, 100_000);

        session.ToggleCollapsed();

        Assert.True(session.IsPlaying);
        Assert.Equal(10_000, session.PositionMs);
        Assert.Equal(0.1, session.ProgressFraction, 6);
    }

    [Fact]
    public void SeekTargetMapsFractionToDuration()
    {
        var session = Create();
        Assert.Null(session.SeekTargetMs(0.5));

        session.ReportProgress(10_000, 200_000);

        Assert.Equal(100_000, session.SeekTargetMs(0.5));
        Assert.Equal(0, session.SeekTargetMs(-1));
        Assert.Equal(200_000, session.SeekTargetMs(3));
        Assert.Null(session.SeekTargetMs(double.NaN));
    }

    [Fact]
    public void CloseFreezesStateAndIgnoresLaterInput()
    {
        var session = Create();
        session.ReportPlaying(true);
        session.ReportProgress(42_000, 100_000);
        var changes = 0;
        session.Changed += () => changes++;

        session.Close();

        Assert.True(session.IsClosed);
        Assert.False(session.IsPlaying);
        Assert.Equal(1, changes);

        session.ReportProgress(80_000, 100_000);
        session.ReportPlaying(true);
        Assert.False(session.TogglePlayPause());
        session.ToggleCollapsed();
        session.Close();

        Assert.Equal(42_000, session.PositionMs);
        Assert.Equal(42_000, session.ResumePositionMs);
        Assert.False(session.IsPlaying);
        Assert.False(session.IsCollapsed);
        Assert.Null(session.SeekTargetMs(0.5));
        Assert.Equal(1, changes);
    }

    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(-1, "00:00")]
    [InlineData(59_999, "00:59")]
    [InlineData(61_000, "01:01")]
    [InlineData(3_600_000, "1:00:00")]
    [InlineData(3_725_000, "1:02:05")]
    public void FormatsTime(double milliseconds, string expected)
    {
        Assert.Equal(expected, MiniPlayerSession.FormatTime(milliseconds));
    }

    [Theory]
    [InlineData("https://media.example/a.m3u8", true)]
    [InlineData("http://127.0.0.1:5123/vod/abc/playlist.m3u8", true)]
    [InlineData("  https://media.example/a.mp4 ", true)]
    [InlineData("C:\\Videos\\a.mp4", false)]
    [InlineData("file:///C:/Videos/a.mp4", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("not a url", false)]
    public void OnlyOnlineAddressesCanBeHandedOff(string? url, bool expected)
    {
        Assert.Equal(expected, MiniPlayerSession.CanHandOff(url));
    }
}
