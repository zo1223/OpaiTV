using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.App.Controls;

public partial class VideoCardControl : UserControl
{
    public static readonly DependencyProperty ItemProperty =
        DependencyProperty.Register(nameof(Item), typeof(CatalogItem), typeof(VideoCardControl),
            new PropertyMetadata(null, OnItemChanged));

    public static readonly RoutedEvent ClickEvent =
        EventManager.RegisterRoutedEvent(nameof(Click), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(VideoCardControl));

    // 海报在布局里的实际宽度（控件宽 150 减去左右各 6 的外边距），与 XAML 里 CardBorder 的 Width 一致。
    private const double PosterWidth = 138;

    // 悬浮动效的时长（毫秒）：进入 / 跟随鼠标 / 离开。
    private const int HoverInMs = 140;
    private const int FollowMs = 90;
    private const int HoverOutMs = 180;

    private bool _hovering;
    private CardTiltPose _pose = CardTilt.Rest;

    public VideoCardControl()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 播放一次入场动画（淡入 + 上移到位）。由网格在卡片首次出现时调用；
    /// 动画结束后回到属性本身的值，不影响之后的悬浮效果。
    /// </summary>
    public void PlayEntrance(int delayMs) =>
        Motion.FadeRiseIn(Root, EntranceShift, EntranceStagger.DurationMilliseconds, delayMs);

    public event RoutedEventHandler Click
    {
        add => AddHandler(ClickEvent, value);
        remove => RemoveHandler(ClickEvent, value);
    }

    public CatalogItem? Item
    {
        get => (CatalogItem?)GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    private static void OnItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((VideoCardControl)d).Render();
    }

    private void Render()
    {
        var item = Item;
        if (item is null)
        {
            return;
        }

        ImageLoader.SetSourceUrl(PosterImage, item.Poster);
        TitleText.Text = item.Title;

        var playabilityLabel = CatalogPlayabilityBadge.GetLabel(
            item.PlayabilityStatus,
            item.PlayableSourceCount);
        if (!string.IsNullOrWhiteSpace(playabilityLabel))
        {
            SourceText.Text = playabilityLabel;
            SourceBadge.Background = item.PlayabilityStatus switch
            {
                CatalogPlayabilityStatus.Playable =>
                    new SolidColorBrush(Color.FromArgb(0xCC, 0x00, 0xA8, 0x62)),
                CatalogPlayabilityStatus.Available or CatalogPlayabilityStatus.Checking =>
                    new SolidColorBrush(Color.FromArgb(0xCC, 0x34, 0x3A, 0x3C)),
                _ => new SolidColorBrush(Color.FromArgb(0xCC, 0x7F, 0x1D, 0x1D)),
            };
            SourceBadge.Visibility = Visibility.Visible;
        }
        else if (!string.IsNullOrWhiteSpace(item.SourceName)
            && item.Source != "douban"
            && item.CandidateSources is null
            && item.PlayabilityStatus != CatalogPlayabilityStatus.Checking)
        {
            SourceText.Text = item.SourceName;
            SourceBadge.Background = new SolidColorBrush(Color.FromArgb(0xB3, 0x00, 0x00, 0x00));
            SourceBadge.Visibility = Visibility.Visible;
        }
        else
        {
            SourceBadge.Visibility = Visibility.Collapsed;
        }

        if (!string.IsNullOrWhiteSpace(item.Rate))
        {
            MetaText.Text = $"★ {item.Rate}";
            MetaText.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x00));
            MetaBadge.Visibility = Visibility.Visible;
        }
        else if (!string.IsNullOrWhiteSpace(item.Year))
        {
            MetaText.Text = item.Year;
            MetaText.Foreground = Brushes.White;
            MetaBadge.Visibility = Visibility.Visible;
        }
        else
        {
            MetaBadge.Visibility = Visibility.Collapsed;
        }

        var progress = item.Progress ?? 0;
        var isContinueWatching = progress > 0 && progress < 1;
        if (isContinueWatching)
        {
            ProgressTrack.Visibility = Visibility.Visible;
            // 进度条画在海报边框（2px）以内
            ProgressBar.Width = (PosterWidth - 4) * progress;
            if (item.EpisodeIndex is int episode)
            {
                ContinueText.Text = $"第{episode + 1}集 已观看 {(int)Math.Round(progress * 100)}%";
                ContinueText.Visibility = Visibility.Visible;
            }
        }
        else
        {
            ProgressTrack.Visibility = Visibility.Collapsed;
            ContinueText.Visibility = Visibility.Collapsed;
        }

        // “为你推荐”的推荐理由：显示在标题下方
        if (!string.IsNullOrWhiteSpace(item.Reason))
        {
            ContinueText.Text = item.Reason;
            ContinueText.ToolTip = item.Reason;
            ContinueText.Visibility = Visibility.Visible;
        }
    }

    private void OnMouseEnter(object sender, MouseEventArgs e)
    {
        CardBorder.BorderBrush = (Brush)FindResource("PrimaryBrush");
        _hovering = true;

        // 悬浮抬起：放大 + 上移 + 高光。只动渲染变换与透明度，不触发布局。
        Motion.EaseTo(CardScale, ScaleTransform.ScaleXProperty, CardTilt.HoverScale, HoverInMs);
        Motion.EaseTo(CardScale, ScaleTransform.ScaleYProperty, CardTilt.HoverScale, HoverInMs);
        Motion.EaseTo(CardLift, TranslateTransform.YProperty, -CardTilt.LiftPixels, HoverInMs);
        Motion.EaseTo(GlareLayer, OpacityProperty, 1, HoverInMs);
        FollowPointer(e);
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_hovering)
        {
            FollowPointer(e);
        }
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        CardBorder.BorderBrush = Brushes.Transparent;
        _hovering = false;

        Motion.EaseTo(CardScale, ScaleTransform.ScaleXProperty, 1, HoverOutMs);
        Motion.EaseTo(CardScale, ScaleTransform.ScaleYProperty, 1, HoverOutMs);
        Motion.EaseTo(CardLift, TranslateTransform.YProperty, 0, HoverOutMs);
        Motion.EaseTo(GlareLayer, OpacityProperty, 0, HoverOutMs);
        ApplyPose(CardTilt.Rest, HoverOutMs);
    }

    /// <summary>按鼠标在海报上的位置倾斜。系统关闭了窗口动画时不倾斜，只保留抬起。</summary>
    private void FollowPointer(MouseEventArgs e)
    {
        if (!Motion.Enabled)
        {
            return;
        }

        // 相对未做悬浮变换的容器取坐标，避免卡片自身的位移反过来影响计算。
        var position = e.GetPosition(Root);
        ApplyPose(
            CardTilt.FromPointer(position.X, position.Y, CardBorder.ActualWidth, CardBorder.ActualHeight),
            FollowMs);
    }

    private void ApplyPose(CardTiltPose pose, int durationMs)
    {
        // 鼠标移动不足以改变姿态时不重复起动画。
        if (IsSamePose(pose, _pose))
        {
            return;
        }

        _pose = pose;
        Motion.EaseTo(CardSkew, SkewTransform.AngleXProperty, pose.SkewX, durationMs);
        Motion.EaseTo(CardSkew, SkewTransform.AngleYProperty, pose.SkewY, durationMs);
        Motion.EaseTo(CardShift, TranslateTransform.XProperty, pose.ShiftX, durationMs);
        Motion.EaseTo(CardShift, TranslateTransform.YProperty, pose.ShiftY, durationMs);

        var glare = new Point(pose.GlareX, pose.GlareY);
        GlareBrush.Center = glare;
        GlareBrush.GradientOrigin = glare;
    }

    private static bool IsSamePose(CardTiltPose a, CardTiltPose b) =>
        Math.Abs(a.GlareX - b.GlareX) < 0.01 && Math.Abs(a.GlareY - b.GlareY) < 0.01;

    private void OnClick(object sender, RoutedEventArgs e)
    {
        if (Item is not null)
        {
            RaiseEvent(new RoutedEventArgs(ClickEvent, this));
        }
    }
}
