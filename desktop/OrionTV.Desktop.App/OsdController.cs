using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace OrionTV.Desktop.App;

/// <summary>
/// 播放器 OSD 自动隐藏：鼠标移动时显示控制层并重置计时，静止数秒后淡出并隐藏光标；
/// 通过 <see cref="Pin"/> 在侧栏/频道列表展开或鼠标悬停控制栏时锁定显示。
/// </summary>
public sealed class OsdController
{
    private static readonly Duration FadeDuration = new(TimeSpan.FromMilliseconds(180));

    private readonly FrameworkElement _host;
    private readonly UIElement[] _layers;
    private readonly DispatcherTimer _timer;
    private bool _pinned;
    private bool _visible = true;

    public OsdController(FrameworkElement host, params UIElement[] layers)
    {
        _host = host;
        _layers = layers;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            if (!_pinned)
            {
                Hide();
            }
        };

        _host.MouseMove += (_, _) => Show();
        _host.MouseLeave += (_, _) =>
        {
            if (!_pinned)
            {
                Hide();
            }
        };
    }

    /// <summary>锁定/解锁显示（如侧栏打开、鼠标悬停控制栏时锁定）。</summary>
    public void Pin(bool pinned)
    {
        _pinned = pinned;
        if (pinned)
        {
            Show();
        }
        else
        {
            RestartTimer();
        }
    }

    public void Show()
    {
        if (!_visible)
        {
            _visible = true;
            foreach (var layer in _layers)
            {
                Fade(layer, 1);
            }
        }

        _host.Cursor = Cursors.Arrow;
        RestartTimer();
    }

    public void Hide()
    {
        if (!_visible)
        {
            return;
        }

        _visible = false;
        foreach (var layer in _layers)
        {
            Fade(layer, 0);
        }

        _host.Cursor = Cursors.None;
    }

    private void RestartTimer()
    {
        _timer.Stop();
        if (!_pinned)
        {
            _timer.Start();
        }
    }

    private static void Fade(UIElement layer, double to)
    {
        layer.IsHitTestVisible = to > 0;
        layer.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(to, FadeDuration));
    }
}
