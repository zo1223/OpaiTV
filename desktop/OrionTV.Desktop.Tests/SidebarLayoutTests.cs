using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.Tests;

public class SidebarLayoutTests
{
    [Fact]
    public void Toggle_cycles_expanded_compact_hidden_and_back()
    {
        Assert.Equal(SidebarMode.Compact, SidebarLayout.Next(SidebarMode.Expanded));
        Assert.Equal(SidebarMode.Hidden, SidebarLayout.Next(SidebarMode.Compact));
        Assert.Equal(SidebarMode.Expanded, SidebarLayout.Next(SidebarMode.Hidden));
    }

    [Fact]
    public void Widths_shrink_with_each_step_and_hidden_takes_no_space()
    {
        Assert.Equal(222, SidebarLayout.Width(SidebarMode.Expanded));
        Assert.Equal(84, SidebarLayout.Width(SidebarMode.Compact));
        Assert.Equal(0, SidebarLayout.Width(SidebarMode.Hidden));
    }

    [Theory]
    [InlineData("Compact", SidebarMode.Compact)]
    [InlineData(" Hidden\n", SidebarMode.Hidden)]
    [InlineData("Expanded", SidebarMode.Expanded)]
    public void Parse_round_trips_saved_state(string text, SidebarMode expected)
    {
        Assert.Equal(expected, SidebarLayout.Parse(text));
        Assert.Equal(expected, SidebarLayout.Parse(expected.ToString()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2")]
    [InlineData("hidden")]
    [InlineData("garbage")]
    public void Unreadable_state_falls_back_to_expanded(string? text)
    {
        Assert.Equal(SidebarMode.Expanded, SidebarLayout.Parse(text));
    }

    [Fact]
    public void Every_mode_has_a_distinct_toggle_tooltip()
    {
        var tips = Enum.GetValues<SidebarMode>().Select(SidebarLayout.ToggleToolTip).ToList();
        Assert.Equal(tips.Count, tips.Distinct().Count());
        Assert.All(tips, tip => Assert.False(string.IsNullOrWhiteSpace(tip)));
    }

    [Fact]
    public void Pull_handle_shows_only_when_hidden()
    {
        Assert.True(SidebarLayout.ShowsPullHandle(SidebarMode.Hidden));
        Assert.False(SidebarLayout.ShowsPullHandle(SidebarMode.Expanded));
        Assert.False(SidebarLayout.ShowsPullHandle(SidebarMode.Compact));
    }

    [Theory]
    [InlineData(SidebarMode.Expanded, SidebarMode.Expanded)]
    [InlineData(SidebarMode.Compact, SidebarMode.Compact)]
    public void Pull_handle_restores_the_mode_before_hidden(SidebarMode before, SidebarMode expected)
    {
        Assert.Equal(expected, SidebarLayout.RestoreFromHidden(before));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(SidebarMode.Hidden)]
    public void Pull_handle_never_restores_to_hidden_when_previous_mode_is_unknown(SidebarMode? before)
    {
        // 启动时读到的就是隐藏档：取切换顺序里隐藏的前一档
        var restored = SidebarLayout.RestoreFromHidden(before);
        Assert.Equal(SidebarMode.Compact, restored);
        Assert.Equal(SidebarMode.Hidden, SidebarLayout.Next(restored));
    }

    [Fact]
    public void Toggle_into_hidden_then_pull_handle_returns_to_the_previous_mode()
    {
        // 三横线：展开 → 仅图标 → 隐藏；把手：回到隐藏前的仅图标档
        var mode = SidebarMode.Expanded;
        SidebarMode? beforeHidden = null;
        for (var i = 0; i < 2; i++)
        {
            var next = SidebarLayout.Next(mode);
            if (next == SidebarMode.Hidden)
            {
                beforeHidden = mode;
            }

            mode = next;
        }

        Assert.Equal(SidebarMode.Hidden, mode);
        Assert.Equal(SidebarMode.Compact, SidebarLayout.RestoreFromHidden(beforeHidden));
    }
}
