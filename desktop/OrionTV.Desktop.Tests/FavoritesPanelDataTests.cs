using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.Tests;

/// <summary>收藏侧滑面板的数据层：与收藏页同一份数据、同一套成人过滤；面板里删除就是 FavoriteStore.Remove。</summary>
public sealed class FavoritesPanelDataTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"opaitv-favorites-panel-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        foreach (var file in new[] { _path, _path + ".tmp" })
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    [Fact]
    public void Entries_split_the_storage_key_and_are_ordered_newest_first()
    {
        var store = new FavoriteStore(_path);
        store.Save("source-a", "1", Favorite("先收藏的"));
        Thread.Sleep(5);
        // id 里带加号时只按第一个加号拆
        store.Save("source-b", "2+x", Favorite("后收藏的"));

        var entries = store.GetVisibleEntries(_ => false);

        Assert.Equal(2, entries.Count);
        Assert.Equal(("source-b", "2+x", "后收藏的"), (entries[0].Source, entries[0].Id, entries[0].Item.Title));
        Assert.Equal(("source-a", "1", "先收藏的"), (entries[1].Source, entries[1].Id, entries[1].Item.Title));
    }

    [Fact]
    public void Entries_apply_the_same_adult_filters_as_the_favorites_page()
    {
        var store = new FavoriteStore(_path);
        store.Save("normal", "1", Favorite("普通片", category: "剧情片"));
        store.Save("adult-source", "2", Favorite("成人源的片"));
        store.Save("normal", "3", Favorite("成人分类的片", category: "伦理片"));
        Func<string, bool> isSourceHidden = source => source == "adult-source";
        Func<string?, bool> isCategoryHidden = category => AdultCategories.IsAdult(category);

        var entries = store.GetVisibleEntries(isSourceHidden, isCategoryHidden);
        var page = store.GetVisible(isSourceHidden, isCategoryHidden);

        Assert.Equal("普通片", Assert.Single(entries).Item.Title);
        Assert.Equal(page.Keys.OrderBy(key => key), entries.Select(entry => $"{entry.Source}+{entry.Id}").OrderBy(key => key));
        // 过滤只是不显示，收藏本身还在
        Assert.Equal(3, store.GetAll().Count);
        Assert.Equal(3, store.GetVisibleEntries(_ => false).Count);
    }

    [Fact]
    public void Removing_an_entry_from_the_panel_removes_only_that_favorite()
    {
        var store = new FavoriteStore(_path);
        store.Save("source-a", "1", Favorite("甲"));
        store.Save("source-b", "2", Favorite("乙"));
        var target = store.GetVisibleEntries(_ => false).Single(entry => entry.Item.Title == "甲");

        store.Remove(target.Source, target.Id);

        Assert.Equal("乙", Assert.Single(store.GetVisibleEntries(_ => false)).Item.Title);
        Assert.False(store.IsFavorited("source-a", "1"));
        Assert.True(store.IsFavorited("source-b", "2"));
    }

    [Fact]
    public void Empty_store_gives_an_empty_panel()
    {
        Assert.Empty(new FavoriteStore(_path).GetVisibleEntries(_ => false));
    }

    private static FavoriteItem Favorite(string title, string? category = null) =>
        new(Cover: "", Title: title, SourceName: "源", TotalEpisodes: 1, SearchTitle: title, Year: "2024", Category: category);
}
