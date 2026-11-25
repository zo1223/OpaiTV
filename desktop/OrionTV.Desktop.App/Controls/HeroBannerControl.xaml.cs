using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.App.Controls;

/// <summary>
/// 首页大横幅：轮播展示几条推荐内容（选片在 Core 的 <see cref="HeroBanner"/>，这里只负责显示与轮播）。
/// 每 6 秒换一张，淡出再淡入；鼠标停在横幅上、横幅不可见或已卸载时停表；只有一条时不轮播；没有条目时整块不显示。
/// </summary>
public partial class HeroBannerControl : UserControl
{
    public const double BannerHeight = 260;
    private const string PlayLabel = "▶  播放";
    private static readonly TimeSpan RotateInterval = TimeSpan.FromSeconds(6);
    private static readonly Duration FadeOutDuration = new(TimeSpan.FromMilliseconds(110));
    private static readonly Duration FadeInDuration = new(TimeSpan.FromMilliseconds(160));
    private static readonly Brush InactiveDotBrush = CreateFrozenBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));

    private readonly DispatcherTimer _timer;
    private List<CatalogItem> _items = new();
    private CatalogItem? _rendered;
    private int _index;
    private int _fadeGeneration;
    private bool _hovering;
    private bool _preparing;
    private bool _suppressed;

    public HeroBannerControl()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = RotateInterval };
        _timer.Tick += Timer_Tick;
        Loaded += (_, _) => UpdateTimer();
        Unloaded += (_, _) => _timer.Stop();
        IsVisibleChanged += (_, _) => UpdateTimer();
    }

    /// <summary>点了“播放”：直接起播当前显示的这一条。</summary>
    public event Action<CatalogItem>? PlayRequested;

    /// <summary>点了“详情”。</summary>
    public event Action<CatalogItem>? DetailRequested;

    /// <summary>给条目找一段简介（只查本机已有的数据）；没有时横幅只显示一行元信息。</summary>
    public Func<CatalogItem, string?>? DescriptionProvider { get; set; }

    public int Count => _items.Count;

    /// <summary>临时收起横幅（网格正在显示居中的提示文字、且放不下两者时）。条目与当前下标保留。</summary>
    public bool Suppressed
    {
        get => _suppressed;
        set
        {
            if (_suppressed == value)
            {
                return;
            }

            _suppressed = value;
            UpdateVisibility();
        }
    }

    /// <summary>
    /// 换一批条目。内容没变就什么都不做；原来显示的那条还在新列表里就停在它上面（切分类时不回到第一张）。
    /// </summary>
    public void SetItems(IReadOnlyList<CatalogItem> items)
    {
        var unchanged = items.Count == _items.Count
            && items.Zip(_items).All(pair =>
                HeroBanner.KeyOf(pair.First) == HeroBanner.KeyOf(pair.Second) && pair.First.Poster == pair.Second.Poster);
        var previous = _rendered;
        _items = items.ToList();
        if (unchanged)
        {
            return;
        }

        _index = HeroBanner.IndexAfterUpdate(_items, previous, _index);
        BuildDots();
        var current = _index < _items.Count ? _items[_index] : null;
        if (current is null || previous is null || HeroBanner.KeyOf(current) != HeroBanner.KeyOf(previous))
        {
            ShowIndex(_index, animate: false);
        }
        else
        {
            _rendered = current;
        }

        UpdateVisibility();
        UpdateTimer();
    }

    /// <summary>起播准备中：按钮显示“正在准备…”并禁用，防止重复点击；期间不自动换下一张。</summary>
    public void SetPreparing(bool preparing)
    {
        _preparing = preparing;
        PlayButton.IsEnabled = !preparing;
        PlayButton.Content = preparing ? "正在准备…" : PlayLabel;
    }

    private static Brush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        if (_preparing || _items.Count <= 1)
        {
            return;
        }

        ShowIndex(HeroBanner.Step(_index, _items.Count, 1), animate: true);
    }

    private void ShowIndex(int index, bool animate)
    {
        var generation = ++_fadeGeneration;
        if (_items.Count == 0)
        {
            _index = 0;
            _rendered = null;
            return;
        }

        _index = Math.Clamp(index, 0, _items.Count - 1);
        UpdateDots();
        if (!animate || !IsVisible)
        {
            SlideLayer.BeginAnimation(OpacityProperty, null);
            SlideLayer.Opacity = 1;
            Render();
            return;
        }

        // 淡出旧的一张 → 换内容 → 淡入，合计约 270ms
        var fadeOut = new DoubleAnimation(0, FadeOutDuration);
        fadeOut.Completed += (_, _) =>
        {
            if (generation != _fadeGeneration)
            {
                return;
            }

            Render();
            SlideLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(1, FadeInDuration));
        };
        SlideLayer.BeginAnimation(OpacityProperty, fadeOut);
    }

    private void Render()
    {
        var item = _index >= 0 && _index < _items.Count ? _items[_index] : null;
        _rendered = item;
        if (item is null)
        {
            return;
        }

        TitleText.Text = item.Title;
        ImageLoader.SetSourceUrl(PosterImage, item.Poster);

        var meta = HeroBanner.MetaLine(item);
        MetaText.Text = meta;
        MetaText.Visibility = meta.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        string? description = null;
        try
        {
            description = DescriptionProvider?.Invoke(item);
        }
        catch
        {
            // 简介只是锦上添花，取不到就只显示元信息
        }

        DescText.Text = description ?? "";
        DescText.Visibility = string.IsNullOrWhiteSpace(description) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void BuildDots()
    {
        Dots.Children.Clear();
        var multiple = _items.Count > 1;
        PrevButton.Visibility = multiple ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Visibility = multiple ? Visibility.Visible : Visibility.Collapsed;
        if (!multiple)
        {
            return;
        }

        for (var i = 0; i < _items.Count; i++)
        {
            var dot = new Border
            {
                Height = 6,
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(3, 0, 3, 0),
                Cursor = Cursors.Hand,
                Tag = i,
            };
            dot.MouseLeftButtonUp += Dot_MouseLeftButtonUp;
            Dots.Children.Add(dot);
        }

        UpdateDots();
    }

    private void UpdateDots()
    {
        foreach (var dot in Dots.Children.OfType<Border>())
        {
            var active = dot.Tag is int index && index == _index;
            dot.Width = active ? 20 : 8;
            dot.Background = active ? Brushes.White : InactiveDotBrush;
        }
    }

    private void UpdateVisibility() =>
        Visibility = _items.Count > 0 && !_suppressed ? Visibility.Visible : Visibility.Collapsed;

    private void UpdateTimer()
    {
        var run = IsLoaded && IsVisible && _items.Count > 1 && !_hovering;
        if (!run)
        {
            _timer.Stop();
        }
        else if (!_timer.IsEnabled)
        {
            _timer.Start();
        }
    }

    /// <summary>手动切换后重新计时，避免刚点完就被自动轮播换走。</summary>
    private void ShowManually(int index)
    {
        if (_preparing || _items.Count <= 1)
        {
            return;
        }

        ShowIndex(index, animate: true);
        _timer.Stop();
        UpdateTimer();
    }

    private void Dot_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border { Tag: int index })
        {
            ShowManually(index);
            e.Handled = true;
        }
    }

    private void PrevButton_Click(object sender, RoutedEventArgs e) =>
        ShowManually(HeroBanner.Step(_index, _items.Count, -1));

    private void NextButton_Click(object sender, RoutedEventArgs e) =>
        ShowManually(HeroBanner.Step(_index, _items.Count, 1));

    private void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_preparing && _rendered is { } item)
        {
            PlayRequested?.Invoke(item);
        }
    }

    private void DetailButton_Click(object sender, RoutedEventArgs e)
    {
        if (_rendered is { } item)
        {
            DetailRequested?.Invoke(item);
        }
    }

    private void Root_MouseEnter(object sender, MouseEventArgs e)
    {
        _hovering = true;
        UpdateTimer();
    }

    private void Root_MouseLeave(object sender, MouseEventArgs e)
    {
        _hovering = false;
        UpdateTimer();
    }

    // Border 的 ClipToBounds 只按矩形裁剪：这里按圆角裁，海报背景才不会露出直角
    private void Root_SizeChanged(object sender, SizeChangedEventArgs e) =>
        Root.Clip = new RectangleGeometry(new Rect(0, 0, Root.ActualWidth, Root.ActualHeight), 28, 28);
}
