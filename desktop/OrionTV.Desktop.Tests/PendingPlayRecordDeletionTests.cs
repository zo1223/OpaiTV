using System.Text.Json;
using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.Tests;

/// <summary>“接着看”可撤销删除：删除先隐藏，撤销窗口内可恢复，超时后才真正删除。</summary>
public sealed class PendingPlayRecordDeletionTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"opaitv-pending-delete-{Guid.NewGuid():N}.json");
    private readonly ManualScheduler _scheduler = new();

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
    public void Undo_window_is_five_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), PendingPlayRecordDeletion.UndoWindow);

        var (_, deletion) = Create(("source-a+1", Record("影片甲", 100)));
        deletion.Delete("source-a", "1");

        Assert.Equal(TimeSpan.FromSeconds(5), Assert.Single(_scheduler.Active).Delay);
    }

    [Fact]
    public void Delete_hides_the_record_immediately_but_leaves_the_store_untouched()
    {
        var (store, deletion) = Create(
            ("source-a+1", Record("影片甲", 100)),
            ("source-b+2", Record("影片乙", 200)));
        var changes = 0;
        deletion.Changed += () => changes++;

        Assert.True(deletion.Delete("source-a", "1", "影片甲"));

        Assert.Equal("影片甲", deletion.Pending?.Title);
        Assert.True(deletion.IsPending("source-a", "1"));
        Assert.False(deletion.IsPending("source-b", "2"));
        Assert.Equal(1, changes);
        // 列表里已经看不到，但文件里还在
        var visible = Assert.Single(store.GetLatestByMedia(isExcluded: deletion.IsPending));
        Assert.Equal("2", visible.Id);
        Assert.NotNull(store.Get("source-a", "1"));
    }

    [Fact]
    public void Undo_inside_the_window_restores_the_record_and_cancels_the_timer()
    {
        var (store, deletion) = Create(("source-a+1", Record("影片甲", 100, playTime: 321)));
        deletion.Delete("source-a", "1");

        Assert.True(deletion.Undo());

        Assert.Null(deletion.Pending);
        Assert.False(deletion.IsPending("source-a", "1"));
        Assert.Empty(_scheduler.Active);
        var restored = Assert.Single(store.GetLatestByMedia(isExcluded: deletion.IsPending));
        Assert.Equal(321, restored.Record.PlayTime);
        Assert.Equal(100, restored.Record.SaveTime);

        // 已经取消的定时器即使触发也不能再删
        _scheduler.FireAll(includeCancelled: true);
        Assert.NotNull(store.Get("source-a", "1"));
    }

    [Fact]
    public void Timeout_removes_the_record_for_real()
    {
        var (store, deletion) = Create(
            ("source-a+1", Record("影片甲", 100)),
            ("source-b+2", Record("影片乙", 200)));
        var changes = 0;
        deletion.Delete("source-a", "1");
        deletion.Changed += () => changes++;

        _scheduler.FireAll();

        Assert.Null(deletion.Pending);
        Assert.Null(store.Get("source-a", "1"));
        Assert.NotNull(store.Get("source-b", "2"));
        Assert.Equal(1, changes);
        // 超时之后再点撤销无效
        Assert.False(deletion.Undo());
        Assert.Null(store.Get("source-a", "1"));
    }

    [Fact]
    public void Deleting_a_second_record_commits_the_first_one_immediately()
    {
        var (store, deletion) = Create(
            ("source-a+1", Record("影片甲", 100)),
            ("source-b+2", Record("影片乙", 200)));

        deletion.Delete("source-a", "1");
        deletion.Delete("source-b", "2");

        Assert.Null(store.Get("source-a", "1"));
        Assert.NotNull(store.Get("source-b", "2"));
        Assert.Equal("影片乙", deletion.Pending?.Title);
        Assert.Single(_scheduler.Active);

        // 撤销只恢复第二条
        Assert.True(deletion.Undo());
        Assert.Null(store.Get("source-a", "1"));
        Assert.NotNull(store.Get("source-b", "2"));
    }

    [Fact]
    public void A_stale_timer_from_an_undone_delete_does_not_commit_a_later_delete()
    {
        var (store, deletion) = Create(("source-a+1", Record("影片甲", 100)));
        deletion.Delete("source-a", "1");
        var staleTimer = Assert.Single(_scheduler.Active);
        deletion.Undo();
        deletion.Delete("source-a", "1");

        staleTimer.Callback();

        Assert.NotNull(deletion.Pending);
        Assert.NotNull(store.Get("source-a", "1"));
    }

    [Fact]
    public void Deleting_a_card_removes_every_source_record_merged_into_it()
    {
        // 同一部片在两个源各有一条记录，首页归并成一张卡片
        var (store, deletion) = Create(
            ("source-a+old", Record("示例 影片", 100)),
            ("source-b+new", Record("示例影片", 200)),
            ("source-c+other", Record("别的片", 150)));
        Assert.Equal(2, store.GetLatestByMedia().Count);

        deletion.Delete("source-b", "new");

        Assert.Equal(2, deletion.Pending?.Records.Count);
        Assert.True(deletion.IsPending("source-a", "old"));
        Assert.Equal("other", Assert.Single(store.GetLatestByMedia(isExcluded: deletion.IsPending)).Id);

        _scheduler.FireAll();

        // 旧源的记录不会在删除后重新冒出来
        Assert.Equal("other", Assert.Single(store.GetLatestByMedia()).Id);
        Assert.Null(store.Get("source-a", "old"));
    }

    [Fact]
    public void A_record_replayed_during_the_undo_window_is_kept()
    {
        var (store, deletion) = Create(("source-a+1", Record("影片甲", 100)));
        deletion.Delete("source-a", "1");

        // 撤销窗口内又播放了它：Save 会写入新的 SaveTime
        store.Save("source-a", "1", Record("影片甲", 0, playTime: 900));
        _scheduler.FireAll();

        Assert.Null(deletion.Pending);
        Assert.Equal(900, store.Get("source-a", "1")?.PlayTime);
    }

    [Fact]
    public void Flush_commits_the_pending_delete_right_away()
    {
        var (store, deletion) = Create(("source-a+1", Record("影片甲", 100)));
        deletion.Delete("source-a", "1");

        deletion.Flush();

        Assert.Null(deletion.Pending);
        Assert.Empty(_scheduler.Active);
        Assert.Null(store.Get("source-a", "1"));

        // 没有待撤销项时 Flush 不做任何事
        deletion.Flush();
        Assert.Empty(store.GetAll());
    }

    [Fact]
    public void Deleting_a_missing_record_does_nothing()
    {
        var (store, deletion) = Create(("source-a+1", Record("影片甲", 100)));
        var changes = 0;
        deletion.Changed += () => changes++;

        Assert.False(deletion.Delete("source-a", "missing"));

        Assert.Null(deletion.Pending);
        Assert.Equal(0, changes);
        Assert.Empty(_scheduler.Active);
        Assert.Single(store.GetAll());
    }

    [Fact]
    public void Default_scheduler_commits_after_the_window_without_a_ui_thread()
    {
        // 不注入定时器时走线程池定时器；这里只确认它能创建并能被撤销取消。
        var store = CreateStore(("source-a+1", Record("影片甲", 100)));
        using var deletion = new PendingPlayRecordDeletion(store);

        Assert.True(deletion.Delete("source-a", "1"));
        Assert.True(deletion.Undo());

        Assert.NotNull(store.Get("source-a", "1"));
    }

    private (PlayRecordStore Store, PendingPlayRecordDeletion Deletion) Create(
        params (string Key, PlayRecordItem Record)[] records)
    {
        var store = CreateStore(records);
        return (store, new PendingPlayRecordDeletion(store, _scheduler.Schedule));
    }

    private PlayRecordStore CreateStore(params (string Key, PlayRecordItem Record)[] records)
    {
        File.WriteAllText(_path, JsonSerializer.Serialize(records.ToDictionary(item => item.Key, item => item.Record)));
        return new PlayRecordStore(_path);
    }

    private static PlayRecordItem Record(string title, long saveTime, double playTime = 60) =>
        new(
            Title: title,
            SourceName: "源",
            Cover: "",
            Index: 0,
            TotalEpisodes: 10,
            PlayTime: playTime,
            TotalTime: 1_000,
            Year: "",
            SaveTime: saveTime);

    /// <summary>手动触发的假定时器。</summary>
    private sealed class ManualScheduler
    {
        private readonly List<Entry> _entries = [];

        public IReadOnlyList<Entry> Active => _entries.Where(entry => !entry.Cancelled).ToList();

        public IDisposable Schedule(TimeSpan delay, Action callback)
        {
            var entry = new Entry(delay, callback);
            _entries.Add(entry);
            return entry;
        }

        public void FireAll(bool includeCancelled = false)
        {
            foreach (var entry in _entries.ToList())
            {
                if (includeCancelled || !entry.Cancelled)
                {
                    entry.Callback();
                }
            }
        }

        public sealed class Entry(TimeSpan delay, Action callback) : IDisposable
        {
            public TimeSpan Delay { get; } = delay;
            public Action Callback { get; } = callback;
            public bool Cancelled { get; private set; }

            public void Dispose() => Cancelled = true;
        }
    }
}
