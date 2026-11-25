namespace OrionTV.Desktop.Core;

/// <summary>可以用键盘互相切换的三页，顺序即数字键 1 / 2 / 3。</summary>
public enum SwitchablePage
{
    Home = 1,
    Search = 2,
    Detail = 3,
}

/// <summary>参与页面切换的按键（界面层把 WPF 的 Key 映射到这里）。</summary>
public enum PageSwitchKey
{
    Left,
    Right,
    Digit1,
    Digit2,
    Digit3,
}

/// <summary>切换之后键盘焦点落在哪里。</summary>
public enum PageFocusTarget
{
    /// <summary>首页：第一张卡片。</summary>
    FirstCard,

    /// <summary>搜索页：搜索框。</summary>
    SearchBox,

    /// <summary>详情页：播放按钮（要续播的那一集，没有则第一集）。</summary>
    PlayButton,
}

/// <summary>
/// 首页 / 搜索页 / 详情页之间的键盘切换规则（纯函数，不依赖界面）：
/// 数字键 1 / 2 / 3 直达；Ctrl+←/→ 按顺序切到上一页 / 下一页，不循环。
/// 不抢普通的 ←/→：它们在卡片之间移动焦点、在输入框里移动光标。
/// </summary>
public static class PageKeyboardSwitcher
{
    public static IReadOnlyList<SwitchablePage> Order { get; } =
        [SwitchablePage.Home, SwitchablePage.Search, SwitchablePage.Detail];

    /// <param name="current">当前页；不是这三页之一（播放、直播、本地视频、设置、收藏）时传 null，一律不切换。</param>
    /// <param name="key">按下的键。</param>
    /// <param name="ctrl">是否只按着 Ctrl。</param>
    /// <param name="focusInTextInput">焦点是否在输入框之类会吃掉按键的控件里。</param>
    /// <param name="detailAvailable">本次运行是否打开过详情页（有可回去的详情）。</param>
    /// <param name="otherModifiers">是否还按着 Shift / Alt / Win；按着就不处理。</param>
    /// <returns>要切到的页；不切换时返回 null（调用方不要吞掉这次按键）。</returns>
    public static SwitchablePage? Resolve(
        SwitchablePage? current,
        PageSwitchKey key,
        bool ctrl,
        bool focusInTextInput,
        bool detailAvailable,
        bool otherModifiers = false)
    {
        if (current is not { } page || otherModifiers)
        {
            return null;
        }

        // 已经在详情页上，详情当然是可用的
        var hasDetail = detailAvailable || page == SwitchablePage.Detail;
        SwitchablePage? target;
        switch (key)
        {
            case PageSwitchKey.Digit1:
            case PageSwitchKey.Digit2:
            case PageSwitchKey.Digit3:
                // 输入框里的数字是在打字；Ctrl+数字在哪里都能切
                if (focusInTextInput && !ctrl)
                {
                    return null;
                }

                target = key switch
                {
                    PageSwitchKey.Digit1 => SwitchablePage.Home,
                    PageSwitchKey.Digit2 => SwitchablePage.Search,
                    _ => SwitchablePage.Detail,
                };
                break;

            case PageSwitchKey.Left:
            case PageSwitchKey.Right:
                // 输入框里 Ctrl+←/→ 是按词移动光标
                if (!ctrl || focusInTextInput)
                {
                    return null;
                }

                var pages = Order.Where(item => item != SwitchablePage.Detail || hasDetail).ToList();
                var index = pages.IndexOf(page) + (key == PageSwitchKey.Left ? -1 : 1);
                target = index >= 0 && index < pages.Count ? pages[index] : null;
                break;

            default:
                return null;
        }

        if (target is null || target == page)
        {
            return null;
        }

        return target == SwitchablePage.Detail && !hasDetail ? null : target;
    }

    public static PageFocusTarget FocusTargetOf(SwitchablePage page) => page switch
    {
        SwitchablePage.Search => PageFocusTarget.SearchBox,
        SwitchablePage.Detail => PageFocusTarget.PlayButton,
        _ => PageFocusTarget.FirstCard,
    };
}
