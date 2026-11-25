namespace OrionTV.Desktop.Core;

// 弹幕排布与时间对齐(纯逻辑,不依赖界面)。
// 与手机版 services/danmaku/engine.ts 逐条对应,改规则时两边一起改。
//
// 所有位置都是"视频时间"的函数:时钟停(暂停/缓冲)弹幕就停;时间跳变(拖动进度)时
// 按新时间重建画面上的弹幕,包括已经飞到一半的。

// ---------- 时钟 ----------

/// <summary>
/// 播放器每隔几百毫秒才上报一次进度,这里在两次上报之间按播放速度推算当前视频时间。
/// 暂停、缓冲卡住时时间不走。所有"墙上时间"都由调用方传入(毫秒,单调递增即可)。
/// </summary>
public sealed class DanmakuClock
{
    /// <summary>位置上报与推算值相差超过这么多秒才重新对表,避免上报抖动造成弹幕来回跳。</summary>
    public const double ResyncSeconds = 0.4;

    /// <summary>这么久没有新的进度上报,就认为播放器卡住了(缓冲),时钟停在原地等它。</summary>
    public const long StallMs = 1500;

    /// <summary>播放中连续两次上报同一个位置、且间隔超过这么久,也算卡住。</summary>
    public const long RepeatMs = 250;

    /// <summary>卡住时时钟回拨超过这么多秒才算"画面上弹幕的位置变了"。</summary>
    public const double VisibleSeconds = 0.05;

    /// <summary>主动拖动进度后这么久之内,离目标太远的进度上报当作拖动前的旧上报丢掉(仅桌面版)。</summary>
    public const long SeekGuardMs = 1000;

    /// <summary>拖动后的上报离目标不超过这么多秒才算数(仅桌面版)。</summary>
    public const double SeekGuardSeconds = 2;

    private double _baseSeconds;
    private long _baseWallMs;
    private bool _playing;
    private double _rate = 1;
    private bool _stalled;
    private double _lastReportSeconds = -1;
    private long _lastReportWallMs;
    private double _seekTargetSeconds;
    private long _seekGuardUntilMs = long.MinValue;

    public bool IsPlaying => _playing;

    public double PlaybackRate => _rate;

    /// <summary>此刻的视频时间(秒)。</summary>
    public double Now(long wallMs)
    {
        if (!_playing || _stalled)
        {
            return _baseSeconds;
        }

        // 上报断了就只再往前推 StallMs
        var until = Math.Min(wallMs, Math.Max(_baseWallMs, _lastReportWallMs + StallMs));
        return _baseSeconds + Math.Max(0, until - _baseWallMs) / 1000d * _rate;
    }

    /// <summary>时间此刻是否在走(在播、没有卡住、进度上报也没断)。</summary>
    public bool IsRunningAt(long wallMs) =>
        _playing && !_stalled && wallMs < Math.Max(_baseWallMs, _lastReportWallMs + StallMs);

    /// <summary>返回状态是否变了。</summary>
    public bool SetPlaying(bool playing, long wallMs)
    {
        if (playing == _playing)
        {
            return false;
        }

        Rebase(Now(wallMs), wallMs);
        _playing = playing;
        _stalled = false;
        _lastReportWallMs = wallMs;
        return true;
    }

    /// <summary>返回倍速是否变了。</summary>
    public bool SetRate(double rate, long wallMs)
    {
        var next = double.IsFinite(rate) && rate > 0 ? rate : 1;
        if (next == _rate)
        {
            return false;
        }

        Rebase(Now(wallMs), wallMs);
        _rate = next;
        return true;
    }

    /// <summary>播放器上报了进度。返回是否重新对了表(此时画面上弹幕的位置会跳一下)。</summary>
    public bool Sync(double positionSeconds, long wallMs)
    {
        if (!double.IsFinite(positionSeconds))
        {
            return false;
        }

        var position = Math.Max(0, positionSeconds);
        if (wallMs < _seekGuardUntilMs && Math.Abs(position - _seekTargetSeconds) > SeekGuardSeconds)
        {
            // 刚拖过进度,这还是拖动之前的旧位置
            return false;
        }

        _seekGuardUntilMs = long.MinValue;
        var current = Now(wallMs);
        var repeated = position == _lastReportSeconds && wallMs - _lastReportWallMs >= RepeatMs;
        _lastReportSeconds = position;
        _lastReportWallMs = wallMs;

        if (_playing && repeated)
        {
            // 说是在播,进度却没动:卡住了,时钟停在上报的位置
            var wasStalled = _stalled;
            _stalled = true;
            Rebase(position, wallMs);
            return !wasStalled || Math.Abs(position - current) > VisibleSeconds;
        }

        if (_stalled)
        {
            _stalled = false;
            Rebase(position, wallMs);
            return true;
        }

        if (Math.Abs(position - current) <= ResyncSeconds)
        {
            return false;
        }

        Rebase(position, wallMs);
        return true;
    }

    /// <summary>
    /// 用户主动把进度拖到了 positionSeconds(仅桌面版:LibVLC 暂停时不上报进度,拖动后要立刻对上)。
    /// 时钟立即跳到目标并停住,等播放器报出拖动后的新位置再继续走。
    /// </summary>
    public void Seek(double positionSeconds, long wallMs)
    {
        if (!double.IsFinite(positionSeconds))
        {
            return;
        }

        var position = Math.Max(0, positionSeconds);
        Rebase(position, wallMs);
        _stalled = _playing;
        _lastReportSeconds = -1;
        _lastReportWallMs = wallMs;
        _seekTargetSeconds = position;
        _seekGuardUntilMs = wallMs + SeekGuardMs;
    }

    public void Reset()
    {
        _baseSeconds = 0;
        _baseWallMs = 0;
        _playing = false;
        _rate = 1;
        _stalled = false;
        _lastReportSeconds = -1;
        _lastReportWallMs = 0;
        _seekTargetSeconds = 0;
        _seekGuardUntilMs = long.MinValue;
    }

    private void Rebase(double seconds, long wallMs)
    {
        _baseSeconds = seconds;
        _baseWallMs = wallMs;
    }
}

/// <summary>
/// 播放器时间 → 正片时间。在线源的 HLS 里可能插了广告段(见 <see cref="HlsAdSkip"/>),
/// 弹幕是按没有广告的正片对的时间,所以要把当前位置之前的广告时长扣掉;正落在广告里时停在广告开头。
/// </summary>
public static class DanmakuTimeMapping
{
    public static double ContentSeconds(double positionMs, IReadOnlyList<AdRange>? adRanges)
    {
        if (!double.IsFinite(positionMs))
        {
            return 0;
        }

        var position = Math.Max(0, positionMs);
        double ads = 0;
        if (adRanges is not null)
        {
            foreach (var range in adRanges)
            {
                if (range is null
                    || !double.IsFinite(range.StartMs)
                    || !double.IsFinite(range.EndMs)
                    || range.EndMs <= range.StartMs
                    || range.StartMs >= position)
                {
                    continue;
                }

                ads += Math.Min(position, range.EndMs) - Math.Max(0, range.StartMs);
            }
        }

        return Math.Max(0, position - ads) / 1000d;
    }
}

// ---------- 分片缓存 ----------

/// <summary>
/// 按播放位置懒加载弹幕分片,并按时间区间查询已加载的弹幕。分片加载失败只是该段没有弹幕。
/// 加载在后台完成,所以这个类是线程安全的;加载完的分片由 <see cref="DrainLoaded"/> 交给使用方。
/// </summary>
public sealed class DanmakuTimeline : IDisposable
{
    public const long RetryMs = 20000;
    public const int MaxAttempts = 3;

    /// <summary>离下一个分片还有这么多秒时提前取。</summary>
    public const double LookaheadSeconds = 10;

    /// <summary>
    /// 当前位置离所在分片开头不到这么多秒时，上一片也要取：拖到分片开头附近时，
    /// 上一片末尾的弹幕还在画面上飞。要不小于最慢的滚动时长。
    /// </summary>
    public const double LookbehindSeconds = 12;

    private sealed class SegmentState
    {
        public IReadOnlyList<DanmakuComment>? Comments;
        public bool Loading;
        public int Attempts;
        public long LastAttemptMs;
    }

    private readonly DanmakuTrack _track;
    private readonly Func<long> _wallNow;
    private readonly object _sync = new();
    private readonly Dictionary<int, SegmentState> _segments = [];
    private readonly List<int> _loaded = [];
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    /// <param name="track">弹幕来源</param>
    /// <param name="wallNow">墙上时间(毫秒),只用于限制失败重试的间隔</param>
    public DanmakuTimeline(DanmakuTrack track, Func<long>? wallNow = null)
    {
        _track = track;
        _wallNow = wallNow ?? (static () => Environment.TickCount64);
    }

    /// <summary>确保 seconds 所在分片(以及即将播到的下一片、弹幕还没飞完的上一片)已在加载。</summary>
    public void Ensure(double seconds)
    {
        var index = IndexOf(seconds);
        Load(index);
        if (_track.SegmentSeconds > 0)
        {
            var previous = IndexOf(Math.Max(0, seconds - LookbehindSeconds));
            if (previous != index)
            {
                Load(previous);
            }

            var next = IndexOf(seconds + LookaheadSeconds);
            if (next != index)
            {
                Load(next);
            }
        }
    }

    /// <summary>分片覆盖的时间范围 [From, To)(秒);整集只有一个分片时到正无穷。</summary>
    public (double From, double To) RangeOf(int index)
    {
        var span = _track.SegmentSeconds;
        return span > 0 ? (index * span, (index + 1) * span) : (0, double.PositiveInfinity);
    }

    /// <summary>时间在 (fromExclusive, toInclusive] 内的已加载弹幕,按时间升序。</summary>
    public IReadOnlyList<DanmakuComment> Between(double fromExclusive, double toInclusive)
    {
        if (!(toInclusive > fromExclusive))
        {
            return [];
        }

        var result = new List<DanmakuComment>();
        var first = IndexOf(Math.Max(0, fromExclusive));
        var last = IndexOf(Math.Max(0, toInclusive));
        lock (_sync)
        {
            for (var index = first; index <= last; index++)
            {
                if (!_segments.TryGetValue(index, out var state) || state.Comments is not { } comments)
                {
                    continue;
                }

                for (var i = UpperBound(comments, fromExclusive); i < comments.Count; i++)
                {
                    if (comments[i].Time > toInclusive)
                    {
                        break;
                    }

                    result.Add(comments[i]);
                }
            }
        }

        return result;
    }

    /// <summary>取走上次调用之后加载完成的分片下标(没有时返回空)。</summary>
    public IReadOnlyList<int> DrainLoaded()
    {
        lock (_sync)
        {
            if (_loaded.Count == 0)
            {
                return [];
            }

            var result = _loaded.ToArray();
            _loaded.Clear();
            return result;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _segments.Clear();
            _loaded.Clear();
        }

        try
        {
            _cts.Cancel();
        }
        catch
        {
            // 取消回调里的异常与弹幕无关
        }

        _cts.Dispose();
    }

    private int IndexOf(double seconds)
    {
        var span = _track.SegmentSeconds;
        if (!(span > 0))
        {
            return 0;
        }

        var index = Math.Floor(seconds / span);
        return index >= int.MaxValue ? int.MaxValue : (int)Math.Max(0, index);
    }

    private void Load(int index)
    {
        SegmentState state;
        CancellationToken token;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            if (!_segments.TryGetValue(index, out state!))
            {
                state = new SegmentState();
                _segments[index] = state;
            }

            if (state.Comments is not null || state.Loading || state.Attempts >= MaxAttempts)
            {
                return;
            }

            var nowMs = _wallNow();
            if (state.Attempts > 0 && nowMs - state.LastAttemptMs < RetryMs)
            {
                return;
            }

            state.Loading = true;
            state.Attempts++;
            state.LastAttemptMs = nowMs;
            token = _cts.Token;
        }

        _ = LoadCoreAsync(index, state, token);
    }

    private async Task LoadCoreAsync(int index, SegmentState state, CancellationToken token)
    {
        try
        {
            var comments = await _track.LoadSegmentAsync(index, token).ConfigureAwait(false);
            // 稳定排序:同一时间的弹幕保持原顺序
            IReadOnlyList<DanmakuComment> sorted = (comments ?? []).OrderBy(comment => comment.Time).ToList();
            lock (_sync)
            {
                state.Loading = false;
                if (_disposed)
                {
                    return;
                }

                state.Comments = sorted;
                _loaded.Add(index);
            }
        }
        catch
        {
            lock (_sync)
            {
                state.Loading = false;
            }
        }
    }

    /// <summary>第一个 Time &gt; value 的下标。</summary>
    private static int UpperBound(IReadOnlyList<DanmakuComment> comments, double value)
    {
        var low = 0;
        var high = comments.Count;
        while (low < high)
        {
            var mid = (low + high) >> 1;
            if (comments[mid].Time > value)
            {
                high = mid;
            }
            else
            {
                low = mid + 1;
            }
        }

        return low;
    }
}

// ---------- 排布 ----------

/// <summary>排布参数。</summary>
/// <param name="Width">画面宽</param>
/// <param name="Height">画面高</param>
/// <param name="FontSize">字号</param>
/// <param name="ScrollSeconds">滚动弹幕横穿画面的秒数</param>
/// <param name="FixedSeconds">顶部/底部弹幕停留的秒数</param>
/// <param name="Measure">文字宽度(文字, 字号) → 宽;不给时按字符估算</param>
/// <param name="MaxItems">画面上同时最多几条</param>
public sealed record DanmakuLayoutOptions(
    double Width,
    double Height,
    double FontSize,
    double ScrollSeconds,
    double FixedSeconds,
    Func<string, double, double>? Measure = null,
    int MaxItems = DanmakuEngine.DefaultMaxItems);

/// <summary>画面上的一条弹幕。</summary>
public sealed class DanmakuItem
{
    internal DanmakuItem(int id, DanmakuComment comment, int lane, double y, double width, double start, double end)
    {
        Id = id;
        Comment = comment;
        Lane = lane;
        Y = y;
        Width = width;
        Start = start;
        End = end;
    }

    public int Id { get; }

    public DanmakuComment Comment { get; }

    public DanmakuMode Mode => Comment.Mode;

    public int Lane { get; }

    /// <summary>文字顶边的纵坐标。</summary>
    public double Y { get; }

    public double Width { get; }

    /// <summary>出现/消失的视频时间(秒)。</summary>
    public double Start { get; }

    public double End { get; }
}

/// <summary>把弹幕排到画面上:滚动弹幕分行、不重叠、不追尾;顶部/底部弹幕各占一行停留几秒。</summary>
public sealed class DanmakuEngine
{
    public const int DefaultMaxItems = 60;

    private const double LineHeightRatio = 1.35;

    /// <summary>滚动弹幕占画面上方这么大的比例。</summary>
    private const double ScrollAreaRatio = 0.45;

    /// <summary>滚动弹幕最多几行。</summary>
    private const int MaxScrollLanes = 12;

    /// <summary>顶部/底部弹幕各自最多占的行数比例。</summary>
    private const double FixedAreaRatio = 0.3;

    /// <summary>底部弹幕离画面底边留出的比例(避开进度条和字幕)。</summary>
    private const double BottomMarginRatio = 0.1;

    /// <summary>同一行前后两条弹幕的最小间隔(字号的倍数)。</summary>
    private const double LaneGapRatio = 1;

    /// <summary>时间向前跳超过这么多秒按拖动处理。</summary>
    private const double JumpForwardSeconds = 1.5;

    /// <summary>时间往回退超过这么多秒按拖动处理;更小的回退只当时间没动。</summary>
    private const double JumpBackwardSeconds = 1;

    private const int MaxTextLength = 80;

    private readonly Func<double, double, IReadOnlyList<DanmakuComment>> _source;
    private DanmakuLayoutOptions _options;
    private List<DanmakuItem> _active = [];
    private DanmakuItem?[] _scrollLanes = [];
    private double[] _topLanes = [];
    private double[] _bottomLanes = [];
    private double? _lastTime;

    /// <summary>已经排到的视频时间(lastTime 加上提前量)。</summary>
    private double _placedUntil;
    private int _nextId = 1;

    /// <param name="source">取时间在 (fromExclusive, toInclusive] 内的弹幕,按时间升序</param>
    /// <param name="options">排布参数</param>
    public DanmakuEngine(Func<double, double, IReadOnlyList<DanmakuComment>> source, DanmakuLayoutOptions options)
    {
        _source = source;
        _options = options;
        ResetLanes();
    }

    public IReadOnlyList<DanmakuItem> Items => _active;

    public double LineHeight => Math.Ceiling(_options.FontSize * LineHeightRatio);

    /// <summary>按字符估算文字宽度:中日韩等全角字符算一个字宽,其余算 0.58 个。</summary>
    public static double EstimateTextWidth(string text, double fontSize)
    {
        double units = 0;
        foreach (var c in text)
        {
            units += c > 0x2e7f ? 1 : 0.58;
        }

        return Math.Ceiling(units * fontSize) + Math.Ceiling(fontSize * 0.4);
    }

    /// <summary>画面尺寸或样式变了:清空,下次 Advance 时按当前时间重建。</summary>
    public void SetOptions(DanmakuLayoutOptions options)
    {
        _options = options;
        Invalidate();
    }

    /// <summary>清空画面;下次 Advance 时按当时的时间重建。</summary>
    public void Invalidate()
    {
        _active = [];
        _lastTime = null;
        _placedUntil = 0;
        ResetLanes();
    }

    /// <summary>
    /// 把画面重建到视频时间 time:清掉现有弹幕,把此刻应该还在画面上的弹幕按各自出现时间重新排一遍。
    /// lookahead &gt; 0 时把接下来这么多秒内要出现的也先排好(它们的横坐标还在画面右边之外)。
    /// </summary>
    public void Seek(double time, double lookahead = 0)
    {
        _active = [];
        ResetLanes();
        var window = Math.Max(_options.ScrollSeconds, _options.FixedSeconds);
        foreach (var comment in _source(time - window, time + lookahead))
        {
            Place(comment);
        }

        _active.RemoveAll(item => !(item.End > time));
        _lastTime = time;
        _placedUntil = time + lookahead;
    }

    /// <summary>
    /// 推进到视频时间 time。返回画面上的弹幕集合是否变了。
    /// 时间跳变(拖动进度、换集)时自动重建。
    /// </summary>
    public bool Advance(double time, double lookahead = 0)
    {
        if (_options.Width <= 0 || _options.Height <= 0)
        {
            return false;
        }

        if (_lastTime is not { } lastTime
            || time < lastTime - JumpBackwardSeconds
            || time > lastTime + JumpForwardSeconds)
        {
            var hadItems = _active.Count > 0;
            Seek(time, lookahead);
            return hadItems || _active.Count > 0;
        }

        if (time <= lastTime)
        {
            return false;
        }

        var changed = false;
        var until = time + lookahead;
        if (until > _placedUntil)
        {
            foreach (var comment in _source(_placedUntil, until))
            {
                if (Place(comment))
                {
                    changed = true;
                }
            }

            _placedUntil = until;
        }

        var removed = _active.RemoveAll(item => !(item.End > time));
        _lastTime = time;
        return changed || removed > 0;
    }

    /// <summary>
    /// 时间在 [from, to) 的一段弹幕刚加载完。如果这段时间已经排过(加载比播放慢,或刚拖过进度),
    /// 清空画面,下次 Advance 时把它们补排进去;还没播到的不用管。
    /// </summary>
    public void NotifyLoaded(double from, double to)
    {
        if (_lastTime is not { } lastTime)
        {
            return;
        }

        var window = Math.Max(_options.ScrollSeconds, _options.FixedSeconds);
        if (from < _placedUntil && to > lastTime - window)
        {
            Invalidate();
        }
    }

    /// <summary>视频时间 time 时这条弹幕是否该显示(提前排好的弹幕在出现时间之前不显示)。</summary>
    public bool IsVisibleAt(DanmakuItem item, double time) => item.Start <= time && time < item.End;

    /// <summary>弹幕在视频时间 time 的横坐标(文字左边)。</summary>
    public double XAt(DanmakuItem item, double time)
    {
        if (item.Mode != DanmakuMode.Scroll)
        {
            return DanmakuJs.Round((_options.Width - item.Width) / 2);
        }

        var progress = (time - item.Start) / (item.End - item.Start);
        return _options.Width - progress * (_options.Width + item.Width);
    }

    private void ResetLanes()
    {
        var lineHeight = Math.Ceiling(_options.FontSize * LineHeightRatio);
        int LanesIn(double ratio)
        {
            if (!(lineHeight > 0))
            {
                return 1;
            }

            var lanes = Math.Floor(_options.Height * ratio / lineHeight);
            return double.IsFinite(lanes) ? (int)Math.Clamp(lanes, 1, 1000) : 1;
        }

        _scrollLanes = new DanmakuItem?[Math.Min(MaxScrollLanes, LanesIn(ScrollAreaRatio))];
        _topLanes = new double[LanesIn(FixedAreaRatio)];
        _bottomLanes = new double[LanesIn(FixedAreaRatio)];
        Array.Fill(_topLanes, double.NegativeInfinity);
        Array.Fill(_bottomLanes, double.NegativeInfinity);
    }

    /// <summary>给弹幕找一行;没有空行或画面已满就丢弃(弹幕太密时自然限流)。</summary>
    private bool Place(DanmakuComment comment)
    {
        var screenWidth = _options.Width;
        var fontSize = _options.FontSize;
        var scrollSeconds = _options.ScrollSeconds;
        var fixedSeconds = _options.FixedSeconds;
        if (_active.Count >= _options.MaxItems)
        {
            // 已经消失的不占名额
            _active.RemoveAll(item => !(item.End > comment.Time));
            if (_active.Count >= _options.MaxItems)
            {
                return false;
            }
        }

        var text = comment.Text.Length > MaxTextLength ? comment.Text[..MaxTextLength] : comment.Text;
        var width = (_options.Measure ?? EstimateTextWidth)(text, fontSize);
        var lineHeight = LineHeight;
        var start = comment.Time;
        var shown = ReferenceEquals(text, comment.Text) ? comment : comment with { Text = text };

        if (comment.Mode == DanmakuMode.Scroll)
        {
            var speed = (screenWidth + width) / scrollSeconds;
            var gap = fontSize * LaneGapRatio;
            for (var lane = 0; lane < _scrollLanes.Length; lane++)
            {
                var last = _scrollLanes[lane];
                if (last is not null)
                {
                    var lastSpeed = (screenWidth + last.Width) / scrollSeconds;
                    // 前一条的尾巴已经完全进入画面并留出间隔,且新弹幕在前一条飞出之前追不上它
                    var tailEntered = last.Start + (last.Width + gap) / lastSpeed <= start;
                    var noCatchUp = start + screenWidth / speed >= last.End;
                    if (!tailEntered || !noCatchUp)
                    {
                        continue;
                    }
                }

                var item = new DanmakuItem(_nextId++, shown, lane, lane * lineHeight, width, start, start + scrollSeconds);
                _scrollLanes[lane] = item;
                _active.Add(item);
                return true;
            }

            return false;
        }

        var lanes = comment.Mode == DanmakuMode.Top ? _topLanes : _bottomLanes;
        for (var lane = 0; lane < lanes.Length; lane++)
        {
            if (lanes[lane] > start)
            {
                continue;
            }

            lanes[lane] = start + fixedSeconds;
            var y = comment.Mode == DanmakuMode.Top
                ? lane * lineHeight
                : Math.Max(0, DanmakuJs.Round(_options.Height * (1 - BottomMarginRatio)) - (lane + 1) * lineHeight);
            _active.Add(new DanmakuItem(_nextId++, shown, lane, y, width, start, start + fixedSeconds));
            return true;
        }

        return false;
    }
}

// ---------- 一集视频的弹幕画面 ----------

/// <summary>某一帧里一条弹幕的位置。</summary>
public readonly record struct DanmakuSprite(DanmakuItem Item, double X, double Y);

/// <summary>
/// 把时钟、分片缓存和排布串起来:渲染层每帧调一次 <see cref="Frame"/>,拿到此刻该画的弹幕和位置。
/// 时钟由播放器驱动(可以先于弹幕匹配完成就开始走),所以由外面传进来。只在一个线程(界面线程)上使用。
/// </summary>
public sealed class DanmakuSession : IDisposable
{
    private readonly DanmakuClock _clock;
    private readonly DanmakuTimeline _timeline;
    private readonly DanmakuEngine _engine;
    private readonly List<DanmakuSprite> _sprites = [];

    public DanmakuSession(DanmakuTrack track, DanmakuLayoutOptions options, DanmakuClock clock, Func<long>? wallNow = null)
    {
        Track = track;
        _clock = clock;
        _timeline = new DanmakuTimeline(track, wallNow);
        _engine = new DanmakuEngine(_timeline.Between, options);
    }

    public DanmakuTrack Track { get; }

    public double LineHeight => _engine.LineHeight;

    /// <summary>画面尺寸或样式变了:下一帧按当前时间重排。</summary>
    public void SetOptions(DanmakuLayoutOptions options) => _engine.SetOptions(options);

    /// <summary>
    /// 墙上时间 wallMs 这一帧该画的弹幕(返回的列表下一次调用时会被改写)。
    /// 顺带按播放位置加载弹幕分片;迟到的分片加载完后把已经错过的弹幕补排进来。
    /// </summary>
    public IReadOnlyList<DanmakuSprite> Frame(long wallMs)
    {
        var time = _clock.Now(wallMs);
        _timeline.Ensure(time);
        foreach (var index in _timeline.DrainLoaded())
        {
            var (from, to) = _timeline.RangeOf(index);
            _engine.NotifyLoaded(from, to);
        }

        _engine.Advance(time);
        _sprites.Clear();
        foreach (var item in _engine.Items)
        {
            if (_engine.IsVisibleAt(item, time))
            {
                _sprites.Add(new DanmakuSprite(item, _engine.XAt(item, time), item.Y));
            }
        }

        return _sprites;
    }

    public void Dispose() => _timeline.Dispose();
}
