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
}
