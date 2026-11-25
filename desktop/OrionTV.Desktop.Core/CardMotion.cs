namespace OrionTV.Desktop.Core;

/// <summary>
/// 卡片悬浮时的姿态。倾斜用斜切角（度）近似，位移单位是像素，高光中心是 0..1 的相对坐标。
/// </summary>
public readonly record struct CardTiltPose(
    double SkewX,
    double SkewY,
    double ShiftX,
    double ShiftY,
    double GlareX,
    double GlareY);

/// <summary>
/// 卡片“悬浮抬起 + 跟随鼠标轻微倾斜”的纯计算，界面层只负责把结果写到变换上。
/// WPF 的 2D 元素没有透视变换，这里用斜切 + 朝鼠标方向的小位移来近似倾斜，幅度刻意很小。
/// </summary>
public static class CardTilt
{
    /// <summary>斜切角上限（度）。</summary>
    public const double MaxSkewDegrees = 2.5;

    /// <summary>朝鼠标方向的位移上限（像素）。</summary>
    public const double MaxShiftPixels = 3;

    /// <summary>悬浮时抬起的高度（像素，向上）。</summary>
    public const double LiftPixels = 4;

    /// <summary>悬浮时的放大比例（与改动前的悬停放大一致）。</summary>
    public const double HoverScale = 1.04;

    /// <summary>没有悬浮时的姿态：不倾斜、不位移，高光在正中。</summary>
    public static CardTiltPose Rest => new(0, 0, 0, 0, 0.5, 0.5);

    /// <summary>
    /// 由鼠标在卡片内的位置算出姿态。(x, y) 以卡片左上角为原点；
    /// 鼠标在正中时不倾斜，越靠边倾斜越大，超出卡片范围按边缘算。
    /// 尺寸或坐标不可用（0、负数、NaN、无穷）时返回 <see cref="Rest"/>。
    /// </summary>
    public static CardTiltPose FromPointer(double x, double y, double width, double height)
    {
        if (!IsUsableSize(width) || !IsUsableSize(height) || !double.IsFinite(x) || !double.IsFinite(y))
        {
            return Rest;
        }

        var relativeX = Math.Clamp(x / width, 0, 1);
        var relativeY = Math.Clamp(y / height, 0, 1);
        var offsetX = relativeX * 2 - 1;
        var offsetY = relativeY * 2 - 1;

        return new CardTiltPose(
            SkewX: -offsetY * MaxSkewDegrees * 0.6,
            SkewY: offsetX * MaxSkewDegrees,
            ShiftX: offsetX * MaxShiftPixels,
            ShiftY: offsetY * MaxShiftPixels,
            GlareX: relativeX,
            GlareY: relativeY);
    }

    private static bool IsUsableSize(double value) => double.IsFinite(value) && value > 0;
}

/// <summary>
/// 列表出现时卡片入场动画的错峰规则：按卡片在本批里的序号排延迟，延迟封顶，
/// 超过一屏左右的卡片不播动画，避免长列表同时跑几百个动画。
/// </summary>
public static class EntranceStagger
{
    /// <summary>相邻两张卡片的延迟差（毫秒）。</summary>
    public const int StepMilliseconds = 18;

    /// <summary>延迟最多排到第几档，之后的卡片与这一档同时出现。</summary>
    public const int MaxSteps = 12;

    /// <summary>一批里最多有多少张卡片播放入场动画，其余直接出现。</summary>
    public const int MaxAnimatedItems = 36;

    /// <summary>单张卡片入场动画的时长（毫秒）。</summary>
    public const int DurationMilliseconds = 200;

    /// <summary>
    /// 这张卡片的入场延迟（毫秒）；返回 null 表示不播动画、直接出现。
    /// </summary>
    public static int? DelayFor(int indexInBatch)
    {
        if (indexInBatch < 0 || indexInBatch >= MaxAnimatedItems)
        {
            return null;
        }

        return Math.Min(indexInBatch, MaxSteps) * StepMilliseconds;
    }

    /// <summary>一批卡片从第一张开始到最后一张播完的最长耗时（毫秒）。</summary>
    public static int LongestTotalMilliseconds => MaxSteps * StepMilliseconds + DurationMilliseconds;
}
