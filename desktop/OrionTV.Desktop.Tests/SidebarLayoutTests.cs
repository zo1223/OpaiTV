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

    [Fact]
    public void Pull_handle_restores_to_expanded_with_labels()
    {
        // 把手拉出来的是带文字的完整栏目，不是仅图标档，更不是隐藏档
        var restored = SidebarLayout.RestoreFromHidden();
        Assert.Equal(SidebarMode.Expanded, restored);
        Assert.Equal(SidebarLayout.ExpandedWidth, SidebarLayout.Width(restored));
        Assert.False(SidebarLayout.ShowsPullHandle(restored));
    }

    [Fact]
    public void Pull_handle_and_toggle_agree_when_leaving_hidden()
    {
        // 从隐藏档出来只有两个入口：把手、三横线。两者都回到展开档
        Assert.Equal(SidebarLayout.Next(SidebarMode.Hidden), SidebarLayout.RestoreFromHidden());
    }

    [Fact]
    public void Toggle_into_hidden_then_pull_handle_returns_to_expanded_not_compact()
    {
        // 三横线：展开 → 仅图标 → 隐藏（隐藏前是仅图标档）；把手：仍然回到展开档
        var mode = SidebarMode.Expanded;
        mode = SidebarLayout.Next(mode);
        Assert.Equal(SidebarMode.Compact, mode);
        mode = SidebarLayout.Next(mode);
        Assert.Equal(SidebarMode.Hidden, mode);

        Assert.Equal(SidebarMode.Expanded, SidebarLayout.RestoreFromHidden());
    }
}
