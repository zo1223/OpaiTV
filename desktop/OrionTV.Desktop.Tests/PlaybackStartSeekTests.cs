using System.Globalization;
using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.Tests;

/// <summary>续播起播位置：随媒体交给播放器的选项，以及“没落实就补 seek”的判断。</summary>
public sealed class PlaybackStartSeekTests
{
    private const long Length = 2_700_000; // 45 分钟
    private const long Target = 1_200_000; // 20 分钟
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(double seconds) => T0 + TimeSpan.FromSeconds(seconds);

    [Theory]
    [InlineData(0, null)]
    [InlineData(-5, null)]
    [InlineData(1_000, ":start-time=1")]
    [InlineData(90_500, ":start-time=90.5")]
    [InlineData(1_234_567, ":start-time=1234.567")]
    public void Start_time_option_is_in_seconds_and_absent_when_playing_from_the_beginning(long startMs, string? expected)
    {
        Assert.Equal(expected, PlaybackMediaOptions.StartTimeOption(startMs));
    }

    [Fact]
    public void Start_time_option_does_not_depend_on_the_system_number_format()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            // 小数点是逗号的系统区域：选项里仍然必须是点，否则播放器解析不出来
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal(":start-time=90.5", PlaybackMediaOptions.StartTimeOption(90_500));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Playing_from_the_beginning_has_nothing_to_apply()
    {
        var tracker = new PlaybackStartSeekTracker();
        tracker.Arm(0);

        Assert.False(tracker.IsPending);
        Assert.Null(tracker.OnProgress(5_000, Length, T0));
    }

    [Fact]
    public void Player_that_started_at_the_resume_position_is_confirmed_without_any_seek()
    {
        var tracker = new PlaybackStartSeekTracker();
        tracker.Arm(Target);

        // 刚起播、新位置还没报上来时播放器报 0：不能当成“在从头播”去补 seek
        Assert.Null(tracker.OnProgress(0, Length, At(0)));
        Assert.Null(tracker.OnProgress(0, Length, At(1)));
        Assert.True(tracker.IsPending);

        // 关键帧对齐后实际位置略早于目标
        Assert.Null(tracker.OnProgress(Target - 2_000, Length, At(2)));
        Assert.False(tracker.IsPending);
        Assert.Equal(0, tracker.Attempts);
    }

    [Fact]
    public void Player_that_is_playing_from_the_beginning_gets_a_seek_and_retries_until_it_lands()
    {
        var tracker = new PlaybackStartSeekTracker();
        tracker.Arm(Target);

        Assert.Null(tracker.OnProgress(0, Length, At(0)));
        Assert.Null(tracker.OnProgress(250, Length, At(0.3)));

        // 进度在走、离目标很远：补第一次
        Assert.Equal(Target, tracker.OnProgress(600, Length, At(0.6)));
        Assert.Equal(1, tracker.Attempts);

        // seek 被播放器丢弃（仍在从头播）：不到间隔不重复补，到了间隔再补
        Assert.Null(tracker.OnProgress(900, Length, At(0.9)));
        Assert.Equal(Target, tracker.OnProgress(1_200, Length, At(1.2)));
        Assert.Equal(2, tracker.Attempts);

        // 这次被接受：先报一阵 0，再报到目标附近
        Assert.Null(tracker.OnProgress(0, Length, At(1.5)));
        Assert.True(tracker.IsPending);
        Assert.Null(tracker.OnProgress(Target - 500, Length, At(3)));
        Assert.False(tracker.IsPending);
        Assert.Equal(2, tracker.Attempts);
    }

    [Fact]
    public void Retry_intervals_grow_and_attempts_are_limited()
    {
        var tracker = new PlaybackStartSeekTracker();
        tracker.Arm(Target);

        var seeks = 0;
        for (var second = 0; second < 24; second += 4)
        {
            if (tracker.OnProgress(1_000 + second * 1_000, Length, At(second)) is not null)
            {
                seeks++;
            }
        }

        Assert.Equal(PlaybackStartSeekTracker.MaxAttempts, seeks);
        Assert.True(tracker.IsPending);

        // 次数用完：放弃，不再打扰已经在看的用户
        Assert.Null(tracker.OnProgress(25_000, Length, At(24)));
        Assert.False(tracker.IsPending);
        Assert.True(PlaybackStartSeekTracker.RetryDelay(1) < PlaybackStartSeekTracker.RetryDelay(3));
        Assert.True(PlaybackStartSeekTracker.RetryDelay(3) < PlaybackStartSeekTracker.RetryDelay(5));
    }

    [Fact]
    public void Unknown_length_does_not_consume_attempts_and_gives_up_after_a_while()
    {
        var tracker = new PlaybackStartSeekTracker();
        tracker.Arm(Target);

        Assert.Null(tracker.OnProgress(2_000, 0, At(0)));
        Assert.Null(tracker.OnProgress(6_000, 0, At(4)));
        Assert.Equal(0, tracker.Attempts);
        Assert.True(tracker.IsPending);

        // 时长后来知道了：照常补
        Assert.Equal(Target, tracker.OnProgress(7_000, Length, At(5)));

        tracker.Arm(Target);
        Assert.Null(tracker.OnProgress(2_000, 0, At(0)));
        Assert.Null(tracker.OnProgress(12_000, 0, At(10)));
        Assert.False(tracker.IsPending);
    }

    [Fact]
    public void Gives_up_after_the_overall_time_limit()
    {
        var tracker = new PlaybackStartSeekTracker();
        tracker.Arm(Target);

        Assert.Equal(Target, tracker.OnProgress(1_000, Length, At(0)));
        Assert.Null(tracker.OnProgress(31_000, Length, At(30)));
        Assert.False(tracker.IsPending);
    }

    [Fact]
    public void Target_beyond_the_end_is_clamped_before_the_end()
    {
        var tracker = new PlaybackStartSeekTracker();
        tracker.Arm(Length + 60_000);

        Assert.Equal(Length - PlaybackStartSeekTracker.EndGuardMs, tracker.OnProgress(1_000, Length, At(0)));
        Assert.True(PlaybackStartSeekTracker.IsReached(Length - 3_500, Length + 60_000, Length));
        Assert.False(PlaybackStartSeekTracker.IsReached(Length - 10_000, Length + 60_000, Length));
    }

    [Fact]
    public void Cancel_and_rearm_reset_the_state()
    {
        var tracker = new PlaybackStartSeekTracker();
        tracker.Arm(Target);
        Assert.Equal(Target, tracker.OnProgress(1_000, Length, At(0)));

        tracker.Cancel();
        Assert.False(tracker.IsPending);
        Assert.Null(tracker.OnProgress(2_000, Length, At(5)));

        tracker.Arm(Target);
        Assert.True(tracker.IsPending);
        Assert.Equal(0, tracker.Attempts);
        Assert.Equal(Target, tracker.OnProgress(1_000, Length, At(6)));
    }
}
