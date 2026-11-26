using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.Tests;

public class SearchQueryStateTests
{
    [Fact]
    public void Starts_empty()
    {
        var state = new SearchQueryState();
        Assert.Equal(string.Empty, state.Text);
        Assert.Equal(string.Empty, state.SubmittedQuery);
    }

    [Fact]
    public void Set_updates_text_and_notifies_once_per_real_change()
    {
        var state = new SearchQueryState();
        var seen = new List<string>();
        state.Changed += seen.Add;

        Assert.True(state.Set("流浪地球"));
        Assert.False(state.Set("流浪地球"));
        Assert.True(state.Set("流浪地球2"));

        Assert.Equal("流浪地球2", state.Text);
        Assert.Equal(new[] { "流浪地球", "流浪地球2" }, seen);
    }

    [Fact]
    public void Null_is_treated_as_empty_and_does_not_notify_when_already_empty()
    {
        var state = new SearchQueryState();
        var count = 0;
        state.Changed += _ => count++;

        Assert.False(state.Set(null));
        Assert.True(state.Set("abc"));
        Assert.True(state.Set(null));

        Assert.Equal(string.Empty, state.Text);
        Assert.Equal(2, count);
    }

    [Fact]
    public void Submitted_query_is_trimmed_but_text_keeps_what_was_typed()
    {
        var state = new SearchQueryState();
        state.Set("  三体 ");
        Assert.Equal("  三体 ", state.Text);
        Assert.Equal("三体", state.SubmittedQuery);

        state.Set("   ");
        Assert.Equal(string.Empty, state.SubmittedQuery);
    }

    [Fact]
    public void Two_inputs_mirroring_each_other_stay_in_sync_without_looping()
    {
        // 模拟顶栏输入框与搜索页输入框：各自“文本变了就写回状态”，状态变了就把文本设成同一个词
        var state = new SearchQueryState();
        var top = string.Empty;
        var page = string.Empty;
        var notifications = 0;
        state.Changed += text =>
        {
            notifications++;
            if (top != text)
            {
                top = text;
                state.Set(top);
            }

            if (page != text)
            {
                page = text;
                state.Set(page);
            }
        };

        top = "庆余年";
        state.Set(top);
        Assert.Equal("庆余年", page);

        page = "庆余年 第二季";
        state.Set(page);
        Assert.Equal("庆余年 第二季", top);
        Assert.Equal(2, notifications);
    }
}
