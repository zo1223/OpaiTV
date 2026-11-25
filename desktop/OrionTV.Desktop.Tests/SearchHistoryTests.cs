using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.Tests;

public sealed class SearchHistoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "opaitv-search-history-" + Guid.NewGuid().ToString("N"));
    private readonly string _file;

    public SearchHistoryTests()
    {
        _file = Path.Combine(_directory, "search_history.json");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    [Fact]
    public void Record_counts_repeat_searches_and_orders_most_recent_first()
    {
        var store = new SearchHistoryStore(_file);
        var start = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);

        store.Record("流浪地球", start);
        store.Record("回南天", start.AddMinutes(1));
        store.Record("  流浪地球 ", start.AddMinutes(2));
        store.Record("   ", start.AddMinutes(3));

        var entries = new SearchHistoryStore(_file).GetAll();
        Assert.Equal(["流浪地球", "回南天"], entries.Select(entry => entry.Query).ToArray());
        Assert.Equal(2, entries[0].Count);
        Assert.Equal(1, entries[1].Count);
    }

    [Fact]
    public void Remove_deletes_a_single_entry_and_Clear_empties_the_history()
    {
        var store = new SearchHistoryStore(_file);
        store.Record("a");
        store.Record("b");

        Assert.True(store.Remove("A"));
        Assert.False(store.Remove("missing"));
        Assert.Equal(["b"], store.GetAll().Select(entry => entry.Query).ToArray());

        store.Clear();
        Assert.Empty(store.GetAll());
    }

    [Fact]
    public void Record_keeps_only_the_newest_entries_up_to_the_limit()
    {
        var store = new SearchHistoryStore(_file);
        var start = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);
        for (var index = 0; index < SearchHistoryStore.MaxEntries + 5; index++)
        {
            store.Record("q" + index, start.AddSeconds(index));
        }

        var entries = store.GetAll();
        Assert.Equal(SearchHistoryStore.MaxEntries, entries.Count);
        Assert.Equal("q" + (SearchHistoryStore.MaxEntries + 4), entries[0].Query);
        Assert.DoesNotContain(entries, entry => entry.Query == "q0");
    }

    [Fact]
    public void A_corrupt_history_file_is_treated_as_empty_and_can_be_written_again()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(_file, "{not json");
        var store = new SearchHistoryStore(_file);

        Assert.Empty(store.GetAll());
        store.Record("ok");
        Assert.Single(store.GetAll());
    }

    [Fact]
    public void ToTagCloud_gives_frequent_and_recent_queries_larger_fonts_within_bounds()
    {
        SearchHistoryEntry[] entries =
        [
            new("刚搜一次", 1, 3_000),
            new("常搜", 9, 2_000),
            new("很久以前搜一次", 1, 1_000),
        ];

        var tags = SearchHistoryStore.ToTagCloud(entries, minFontSize: 12, maxFontSize: 20);

        Assert.Equal(entries.Select(entry => entry.Query), tags.Select(tag => tag.Query));
        Assert.All(tags, tag => Assert.InRange(tag.FontSize, 12, 20));
        Assert.True(tags[1].FontSize > tags[0].FontSize);
        Assert.True(tags[0].FontSize > tags[2].FontSize);
        Assert.Empty(SearchHistoryStore.ToTagCloud([]));
        // 重复项也不能抛异常。
        Assert.Equal(2, SearchHistoryStore.ToTagCloud([entries[0], entries[0]]).Count);
    }
}
