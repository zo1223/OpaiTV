using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace OrionTV.Desktop.App;

/// <summary>
/// 界面轻量动效的公共入口：页面切换、卡片入场、海报淡入。
/// 所有动画都只动 Opacity / RenderTransform，不触发布局；结束后回到属性本身的值（FillBehavior.Stop），
/// 所以动画即使没跑起来，元素也是完整可见的。系统关闭了窗口动画时全部跳过。
/// </summary>
public static class Motion
{
    private static readonly IEasingFunction EaseOut = CreateEaseOut();

    /// <summary>系统“在窗口内显示动画”关闭时不播任何动效。</summary>
    public static bool Enabled
    {
        get
        {
            try
            {
                return SystemParameters.ClientAreaAnimation;
            }
            catch
            {
                return true;
            }
        }
    }

    /// <summary>
    /// 淡入并从下方轻微上移到位。<paramref name="delayMs"/> 期间保持透明并停在起点。
    /// </summary>
    public static void FadeRiseIn(UIElement element, TranslateTransform? shift, int durationMs, int delayMs = 0, double rise = 10)
    {
        if (!Enabled || durationMs <= 0)
        {
            return;
        }

        var delay = TimeSpan.FromMilliseconds(Math.Max(0, delayMs));
        var total = delay + TimeSpan.FromMilliseconds(durationMs);

        element.BeginAnimation(UIElement.OpacityProperty, HoldThenEase(0, 1, delay, total));
        shift?.BeginAnimation(TranslateTransform.YProperty, HoldThenEase(rise, 0, delay, total));
    }

    /// <summary>只淡入（海报图加载完成时用）。</summary>
    public static void FadeIn(UIElement element, int durationMs)
    {
        if (!Enabled || durationMs <= 0)
        {
            return;
        }

        element.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(durationMs))
            {
                EasingFunction = EaseOut,
                FillBehavior = FillBehavior.Stop,
            });
    }

    /// <summary>从左侧屏外滑入到位（侧栏被把手拉出时用）。动效关闭时不动，本来就在原位。</summary>
    public static void SlideInFromLeft(TranslateTransform shift, double distance, int durationMs)
    {
        if (!Enabled || durationMs <= 0 || distance <= 0)
        {
            return;
        }

        shift.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation(-distance, 0, TimeSpan.FromMilliseconds(durationMs))
            {
                EasingFunction = EaseOut,
                FillBehavior = FillBehavior.Stop,
            });
    }

    /// <summary>把一个 double 属性缓动到目标值并停在那里（悬浮姿态用）。</summary>
    public static void EaseTo(IAnimatable target, DependencyProperty property, double to, int durationMs)
    {
        if (!Enabled || durationMs <= 0)
        {
            target.BeginAnimation(property, null);
            if (target is DependencyObject element)
            {
                element.SetValue(property, to);
            }

            return;
        }

        target.BeginAnimation(
            property,
            new DoubleAnimation(to, TimeSpan.FromMilliseconds(durationMs)) { EasingFunction = EaseOut },
            HandoffBehavior.SnapshotAndReplace);
    }

    private static DoubleAnimationUsingKeyFrames HoldThenEase(double from, double to, TimeSpan delay, TimeSpan total)
    {
        var animation = new DoubleAnimationUsingKeyFrames
        {
            Duration = total,
            FillBehavior = FillBehavior.Stop,
        };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        if (delay > TimeSpan.Zero)
        {
            animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(delay)));
        }

        animation.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(total), EaseOut));
        return animation;
    }

    private static IEasingFunction CreateEaseOut()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ease.Freeze();
        return ease;
    }
}
