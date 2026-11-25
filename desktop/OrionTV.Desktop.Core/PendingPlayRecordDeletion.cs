namespace OrionTV.Desktop.Core;

/// <summary>一条等待撤销窗口结束的“接着看”删除。</summary>
/// <param name="Title">提示条里显示的片名。</param>
/// <param name="Records">被删除卡片归并的全部观看记录（同一部片在不同源的记录一起删）。</param>
public sealed record PendingPlayRecordDeletionInfo(string Title, IReadOnlyList<PlayRecordHistoryEntry> Records);

/// <summary>
/// “接着看”可撤销删除（与手机端行为一致）：
/// 删除后条目立即从列表隐藏，但观看记录文件不动；撤销窗口（5 秒）内撤销则原样恢复，
/// 超时后才真正从 <see cref="PlayRecordStore"/> 删除。一次只保留一条待撤销的删除：
/// 窗口内再删另一条，前一条立即真正删除。待删期间该记录被重新播放更新过（SaveTime 变新）则不删。
/// </summary>
public sealed class PendingPlayRecordDeletion : IDisposable
{
    /// <summary>撤销窗口，两端一致。</summary>
    public static readonly TimeSpan UndoWindow = TimeSpan.FromSeconds(5);

    private readonly object _lock = new();
    private readonly PlayRecordStore _store;
    private readonly Func<TimeSpan, Action, IDisposable> _schedule;
    private PendingPlayRecordDeletionInfo? _pending;
    private IDisposable? _timer;
    private int _generation;

    /// <param name="schedule">
    /// 定时器工厂：在给定时长后调用回调，返回值 Dispose 即取消。默认用线程池定时器；
    /// 界面层传入 UI 线程定时器，测试传入手动触发的假定时器。
    /// </param>
    public PendingPlayRecordDeletion(PlayRecordStore store, Func<TimeSpan, Action, IDisposable>? schedule = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _schedule = schedule ?? ScheduleOnThreadPool;
    }

    /// <summary>待撤销项出现、被撤销或被真正删除之后触发（可能不在 UI 线程）。</summary>
    public event Action? Changed;

    /// <summary>当前等待撤销的删除；没有时为 null。</summary>
    public PendingPlayRecordDeletionInfo? Pending
    {
        get
        {
            lock (_lock)
            {
                return _pending;
            }
        }
    }

    /// <summary>该记录是否正在等待撤销（列表据此隐藏它）。</summary>
    public bool IsPending(string source, string id)
    {
        lock (_lock)
        {
            return _pending is not null
                && _pending.Records.Any(entry =>
                    string.Equals(entry.Source, source, StringComparison.Ordinal)
                    && string.Equals(entry.Id, id, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// 删除一张“接着看”卡片：连同归并到这张卡片的其它源记录一起进入待撤销状态。
    /// 已有待撤销项时先把它真正删除。记录不存在时返回 false。
    /// </summary>
    public bool Delete(string source, string id, string? title = null)
    {
        var records = _store.GetMediaGroup(source, id);
        if (records.Count == 0)
        {
            return false;
        }

        PendingPlayRecordDeletionInfo? previous;
        lock (_lock)
        {
            previous = TakePending();
            var displayTitle = !string.IsNullOrWhiteSpace(title)
                ? title
                : records.Select(entry => entry.Record.Title).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
            _pending = new PendingPlayRecordDeletionInfo(displayTitle, records);
            var generation = ++_generation;
            _timer = _schedule(UndoWindow, () => OnTimerElapsed(generation));
        }

        Commit(previous);
        Changed?.Invoke();
        return true;
    }

    /// <summary>撤销窗口内撤销：条目恢复显示，存储从未被动过。没有待撤销项时返回 false。</summary>
    public bool Undo()
    {
        lock (_lock)
        {
            if (TakePending() is null)
            {
                return false;
            }
        }

        Changed?.Invoke();
        return true;
    }

    /// <summary>立即真正删除待撤销项（应用退出、清空历史前调用）。</summary>
    public void Flush()
    {
        PendingPlayRecordDeletionInfo? pending;
        lock (_lock)
        {
            pending = TakePending();
        }

        if (pending is not null)
        {
            Commit(pending);
            Changed?.Invoke();
        }
    }

    public void Dispose() => Flush();

    private void OnTimerElapsed(int generation)
    {
        PendingPlayRecordDeletionInfo? pending;
        lock (_lock)
        {
            if (generation != _generation || _pending is null)
            {
                // 已被撤销、提前提交，或已被新的删除取代。
                return;
            }

            pending = TakePending();
        }

        Commit(pending);
        Changed?.Invoke();
    }

    /// <summary>取出并清空待撤销项，同时取消定时器。调用方必须持有 <see cref="_lock"/>。</summary>
    private PendingPlayRecordDeletionInfo? TakePending()
    {
        var pending = _pending;
        _pending = null;
        _generation++;
        _timer?.Dispose();
        _timer = null;
        return pending;
    }

    private void Commit(PendingPlayRecordDeletionInfo? pending)
    {
        if (pending is not null)
        {
            _store.RemoveUnlessUpdated(pending.Records);
        }
    }

    private static IDisposable ScheduleOnThreadPool(TimeSpan delay, Action callback) =>
        new Timer(_ => callback(), null, delay, Timeout.InfiniteTimeSpan);
}
