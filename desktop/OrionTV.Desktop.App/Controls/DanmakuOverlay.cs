using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.App.Controls;

/// <summary>
/// 弹幕层：盖在视频画面上，不接收鼠标。滚动弹幕从右往左飘，顶部/底部弹幕居中停留几秒。
///
/// 这里只负责“画”：每条弹幕是一个画好文字的 <see cref="DrawingVisual"/>，每帧只改它的偏移量。
/// 画什么、画在哪由 <see cref="DanmakuSession"/>（OrionTV.Desktop.Core，纯逻辑、有单元测试）按视频时间算出来；
/// 视频时间来自 <see cref="DanmakuClock"/>，由播放页把播放器的进度、暂停、拖动喂进来。
/// 没有弹幕来源（开关关闭、没匹配到、取数失败）时这一层是空的，也不挂逐帧回调。
/// </summary>
public sealed class DanmakuOverlay : FrameworkElement
{
    private const int MaxItems = 60;
    private static readonly Brush ShadowBrush = CreateFrozenBrush(Color.FromArgb(0xE6, 0, 0, 0));
    private static readonly Typeface TextTypeface = new(
        new FontFamily("Microsoft YaHei UI, Microsoft YaHei, Segoe UI"),
        FontStyles.Normal,
        FontWeights.Bold,
        FontStretches.Normal);

    private sealed class Sprite(DrawingVisual visual)
    {
        public DrawingVisual Visual { get; } = visual;

        public int SeenInFrame { get; set; }
    }

    private readonly VisualCollection _visuals;
    private readonly Dictionary<int, Sprite> _sprites = [];
    private readonly List<int> _expired = [];
    private readonly Dictionary<int, Brush> _brushes = [];
    private readonly DanmakuClock _clock = new();
    private readonly Stopwatch _wall = Stopwatch.StartNew();
    private DanmakuSession? _session;
    private string _speed = DanmakuSettingsRules.DefaultSpeed;
    private string _fontSize = DanmakuSettingsRules.DefaultFontSize;
    private double _pixelsPerDip = 1;
    private bool _rendering;
    private int _frame;

    public DanmakuOverlay()
    {
        _visuals = new VisualCollection(this);
        IsHitTestVisible = false;
        Focusable = false;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        Opacity = DanmakuSettingsRules.DefaultOpacity;
        Loaded += (_, _) => UpdateRenderingHook();
        Unloaded += (_, _) => UpdateRenderingHook();
        IsVisibleChanged += (_, _) => UpdateRenderingHook();
        SizeChanged += (_, _) => ApplyLayoutOptions();
    }

    /// <summary>当前是否有弹幕来源。</summary>
    public bool HasTrack => _session is not null;

    protected override int VisualChildrenCount => _visuals.Count;

    protected override Visual GetVisualChild(int index) => _visuals[index];

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        ClearSprites();
        ApplyLayoutOptions();
    }

    /// <summary>透明度、滚动速度、字号（设置页的取值）。</summary>
    public void ApplyStyle(double opacity, string? speed, string? fontSize)
    {
        Opacity = DanmakuSettingsRules.NormalizeOpacity(opacity);
        var nextSpeed = DanmakuSettingsRules.NormalizeSpeed(speed);
        var nextFontSize = DanmakuSettingsRules.NormalizeFontSize(fontSize);
        if (nextSpeed == _speed && nextFontSize == _fontSize)
        {
            return;
        }

        _speed = nextSpeed;
        _fontSize = nextFontSize;
        ClearSprites();
        ApplyLayoutOptions();
    }

    /// <summary>换一集视频的弹幕来源；null 表示没有弹幕（清空画面）。</summary>
    public void SetTrack(DanmakuTrack? track)
    {
        _session?.Dispose();
        _session = null;
        ClearSprites();
        if (track is not null)
        {
            _session = new DanmakuSession(track, BuildLayoutOptions(), _clock, () => _wall.ElapsedMilliseconds);
        }

        UpdateRenderingHook();
    }

    /// <summary>开始放新的一集：时间回到 0 并停住，等播放器报进度。</summary>
    public void ResetClock() => _clock.Reset();

    /// <summary>播放器在播 / 没在播（暂停、结束、出错）。暂停时弹幕停在原地。</summary>
    public void SetPlaying(bool playing) => _clock.SetPlaying(playing, _wall.ElapsedMilliseconds);

    /// <summary>播放器上报了进度（正片时间，秒）。</summary>
    public void SyncPosition(double seconds) => _clock.Sync(seconds, _wall.ElapsedMilliseconds);

    /// <summary>用户把进度拖到了 seconds（正片时间，秒）：弹幕立刻换成那个时间点该有的画面。</summary>
    public void Seek(double seconds) => _clock.Seek(seconds, _wall.ElapsedMilliseconds);

    /// <summary>按画面短边定基准字号：720p 窗口约 26，1080p 及以上 32。</summary>
    internal static double BaseFontSize(double width, double height) =>
        Math.Round(Math.Clamp(Math.Min(width, height) / 28, 16, 32));

    private static Brush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private double CurrentFontSize() =>
        Math.Round(BaseFontSize(ActualWidth, ActualHeight) * DanmakuSettingsRules.FontScale(_fontSize));

    private DanmakuLayoutOptions BuildLayoutOptions()
    {
        _pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        return new DanmakuLayoutOptions(
            ActualWidth,
            ActualHeight,
            CurrentFontSize(),
            DanmakuSettingsRules.ScrollSeconds(_speed),
            DanmakuSettingsRules.FixedSeconds,
            MeasureText,
            MaxItems);
    }

    private void ApplyLayoutOptions()
    {
        if (_session is null)
        {
            return;
        }

        ClearSprites();
        _session.SetOptions(BuildLayoutOptions());
    }

    private FormattedText CreateText(string text, double fontSize, Brush brush) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, TextTypeface, fontSize, brush, _pixelsPerDip);

    /// <summary>排布用的文字宽度（含左右各留的一点空）。</summary>
    private double MeasureText(string text, double fontSize)
    {
        try
        {
            return Math.Ceiling(CreateText(text, fontSize, Brushes.White).WidthIncludingTrailingWhitespace)
                   + Math.Ceiling(fontSize * 0.4);
        }
        catch
        {
            return DanmakuEngine.EstimateTextWidth(text, fontSize);
        }
    }

    private void UpdateRenderingHook()
    {
        var shouldRender = _session is not null && IsLoaded && IsVisible;
        if (shouldRender == _rendering)
        {
            return;
        }

        _rendering = shouldRender;
        if (shouldRender)
        {
            CompositionTarget.Rendering += OnRendering;
        }
        else
        {
            CompositionTarget.Rendering -= OnRendering;
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var session = _session;
        if (session is null || !IsVisible)
        {
            UpdateRenderingHook();
            return;
        }

        if (ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        try
        {
            RenderFrame(session);
        }
        catch
        {
            // 弹幕出任何问题都不能影响播放：这一集不再显示弹幕
            SetTrack(null);
        }
    }

    private void RenderFrame(DanmakuSession session)
    {
        var frame = ++_frame;
        var lineHeight = session.LineHeight;
        foreach (var sprite in session.Frame(_wall.ElapsedMilliseconds))
        {
            if (!_sprites.TryGetValue(sprite.Item.Id, out var existing))
            {
                existing = new Sprite(CreateVisual(sprite.Item, lineHeight));
                _sprites[sprite.Item.Id] = existing;
                _visuals.Add(existing.Visual);
            }

            existing.SeenInFrame = frame;
            // 暂停时每帧算出来的位置不变，赋同样的值不会触发重绘
            var offset = new Vector(sprite.X, sprite.Y);
            if (existing.Visual.Offset != offset)
            {
                existing.Visual.Offset = offset;
            }
        }

        if (_sprites.Count == 0)
        {
            return;
        }

        _expired.Clear();
        foreach (var (id, sprite) in _sprites)
        {
            if (sprite.SeenInFrame != frame)
            {
                _expired.Add(id);
            }
        }

        foreach (var id in _expired)
        {
            _visuals.Remove(_sprites[id].Visual);
            _sprites.Remove(id);
        }
    }

    private DrawingVisual CreateVisual(DanmakuItem item, double lineHeight)
    {
        var fontSize = CurrentFontSize();
        var visual = new DrawingVisual();
        using var context = visual.RenderOpen();
        var shadow = CreateText(item.Comment.Text, fontSize, ShadowBrush);
        var text = CreateText(item.Comment.Text, fontSize, BrushOf(item.Comment.Color));
        var x = Math.Ceiling(fontSize * 0.2);
        var y = Math.Max(0, (lineHeight - text.Height) / 2);
        // 四个方向各偏一像素画一遍黑字当描边：深色浅色画面上都看得清
        context.DrawText(shadow, new Point(x - 1, y));
        context.DrawText(shadow, new Point(x + 1, y));
        context.DrawText(shadow, new Point(x, y - 1));
        context.DrawText(shadow, new Point(x, y + 1));
        context.DrawText(text, new Point(x, y));
        return visual;
    }

    private Brush BrushOf(int color)
    {
        if (!_brushes.TryGetValue(color, out var brush))
        {
            if (_brushes.Count > 256)
            {
                _brushes.Clear();
            }

            brush = CreateFrozenBrush(Color.FromRgb((byte)(color >> 16), (byte)(color >> 8), (byte)color));
            _brushes[color] = brush;
        }

        return brush;
    }

    private void ClearSprites()
    {
        _visuals.Clear();
        _sprites.Clear();
    }
}
