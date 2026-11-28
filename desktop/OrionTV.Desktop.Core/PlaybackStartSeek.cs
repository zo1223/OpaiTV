namespace OrionTV.Desktop.Core;

/// <summary>
/// 起播位置（续播）是否真的落实了的判断与补 seek 策略。不依赖播放器，界面层只负责把播放器事件报进来、
/// 按返回值执行 seek。
/// <para>
/// 起播位置首先随媒体一起交给播放器（<see cref="PlaybackMediaOptions.StartTimeOption"/>）；这里是兜底：
/// 播放器报上来的真实进度说明它正在别处播放（离目标还很远）时才补 seek，按递增间隔重试有限次数，之后放弃。
/// </para>
/// <para>
/// 调用方必须只报“真实进度”：LibVLC 在设置进度的那一刻会同步回报一次“目标值”（此时 seek 还没执行，
/// 也可能随后被解复用器拒绝），那一次不能报进来，否则补 seek 会被误判为已经成功。
/// </para>
/// </summary>
public sealed class PlaybackStartSeekTracker
{
    /// <summary>进度到了“目标之前这么多毫秒以内”就算落实（关键帧对齐会让实际位置略早于目标）。</summary>
    public const long ToleranceMs = 3000;

    /// <summary>目标不超过“片长减这么多毫秒”，避免一起播就到片尾。</summary>
    public const long EndGuardMs = 1000;

    /// <summary>
    /// 进度不到这么多毫秒时不当作“正在别处播放”的证据：刚起播、或 seek 已被接受但新位置还没报上来时，
    /// 播放器报的就是 0 或接近 0（HLS 在 seek 成功后会先报一阵 0）。这时补 seek 只会打断正在进行的缓冲。
    /// </summary>
    public const long MinEvidenceMs = 300;

    /// <summary>补 seek 的最多次数。</summary>
    public const int MaxAttempts = 6;

    /// <summary>时长一直未知（无法 seek）时，从第一次报进度起最多等这么久就放弃。</summary>
    public static readonly TimeSpan UnknownLengthGiveUp = TimeSpan.FromSeconds(10);

    /// <summary>从第一次报进度起，超过这么久仍未落实就放弃，不再打扰已经在看的用户。</summary>
    public static readonly TimeSpan OverallGiveUp = TimeSpan.FromSeconds(30);

    private long _targetMs;
    private int _attempts;
    private DateTimeOffset _lastAttemptAt;
    private DateTimeOffset? _firstProgressAt;

    /// <summary>还有没落实的起播位置。</summary>
    public bool IsPending => _targetMs > 0;

    /// <summary>没落实的起播位置（毫秒）；没有时为 0。</summary>
    public long TargetMs => _targetMs;

    /// <summary>已经补过几次 seek。</summary>
    public int Attempts => _attempts;

    /// <summary>开始一次新的起播：<paramref name="targetMs"/> 不大于 0 表示从头播，没有要落实的位置。</summary>
    public void Arm(long targetMs)
    {
        _targetMs = Math.Max(0, targetMs);
        _attempts = 0;
        _lastAttemptAt = DateTimeOffset.MinValue;
        _firstProgressAt = null;
    }

    /// <summary>不再落实起播位置：停止播放、用户自己拖了进度等。</summary>
    public void Cancel() => _targetMs = 0;

    /// <summary>第 <paramref name="attemptsMade"/> 次补 seek 之后，至少隔多久才补下一次。</summary>
    public static TimeSpan RetryDelay(int attemptsMade) => attemptsMade switch
    {
        <= 1 => TimeSpan.FromMilliseconds(500),
        2 => TimeSpan.FromSeconds(1),
        3 => TimeSpan.FromSeconds(2),
        4 => TimeSpan.FromSeconds(3),
        _ => TimeSpan.FromSeconds(4),
    };

    /// <summary>已知片长时把目标夹在片尾之前；片长未知时原样返回。</summary>
    public static long EffectiveTargetMs(long targetMs, long lengthMs) => lengthMs > 0
        ? Math.Clamp(targetMs, 0, Math.Max(0, lengthMs - EndGuardMs))
        : Math.Max(0, targetMs);

    /// <summary>当前进度是否已经到了起播位置附近（或之后）。</summary>
    public static bool IsReached(long currentTimeMs, long targetMs, long lengthMs) =>
        currentTimeMs >= Math.Max(0, EffectiveTargetMs(targetMs, lengthMs) - ToleranceMs);

    /// <summary>
    /// 播放器报上来一次真实进度。已到目标附近就确认落实；进度说明它正在别处播放时，按重试间隔返回要补 seek
    /// 到的位置；进度还是 0、不到间隔、时长未知或已放弃时返回 null。
    /// </summary>
    public long? OnProgress(long currentTimeMs, long lengthMs, DateTimeOffset now)
    {
        if (!IsPending)
        {
            return null;
        }

        if (IsReached(currentTimeMs, _targetMs, lengthMs))
        {
            _targetMs = 0;
            return null;
        }

        var firstProgressAt = _firstProgressAt ??= now;
        var waited = now - firstProgressAt;
        if (waited >= OverallGiveUp || (lengthMs <= 0 && waited >= UnknownLengthGiveUp))
        {
            _targetMs = 0;
            return null;
        }

        if (currentTimeMs < MinEvidenceMs)
        {
            // 还看不出播放器在哪：先不补，等它报出真实位置。
            return null;
        }

        return NextAttempt(lengthMs, now);
    }

    private long? NextAttempt(long lengthMs, DateTimeOffset now)
    {
        if (lengthMs <= 0)
        {
            // 时长未知时播放器会直接丢弃 seek：不消耗次数，等后面的事件再试。
            return null;
        }

        if (_attempts >= MaxAttempts)
        {
            _targetMs = 0;
            return null;
        }

        if (_attempts > 0 && now - _lastAttemptAt < RetryDelay(_attempts))
        {
            return null;
        }

        _attempts++;
        _lastAttemptAt = now;
        return EffectiveTargetMs(_targetMs, lengthMs);
    }
}
