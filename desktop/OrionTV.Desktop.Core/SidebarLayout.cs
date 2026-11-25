namespace OrionTV.Desktop.Core;

/// <summary>主窗口侧栏的三种外观状态。</summary>
public enum SidebarMode
{
    Expanded,
    Compact,
    Hidden,
}

/// <summary>侧栏折叠的纯规则（切换顺序、宽度、状态读写的文本形式）。只是外观状态，不属于设置数据。</summary>
public static class SidebarLayout
{
    public const double ExpandedWidth = 222;
    public const double CompactWidth = 84;

    /// <summary>开关按一下：展开 → 仅图标 → 隐藏 → 展开。</summary>
    public static SidebarMode Next(SidebarMode mode) => mode switch
    {
        SidebarMode.Expanded => SidebarMode.Compact,
        SidebarMode.Compact => SidebarMode.Hidden,
        _ => SidebarMode.Expanded,
    };

    public static double Width(SidebarMode mode) => mode switch
    {
        SidebarMode.Expanded => ExpandedWidth,
        SidebarMode.Compact => CompactWidth,
        _ => 0,
    };

    /// <summary>读不懂的内容（空、损坏、数字、未知名字）一律回到展开，保证导航入口默认可见。</summary>
    public static SidebarMode Parse(string? text)
    {
        var value = text?.Trim();
        return value switch
        {
            nameof(SidebarMode.Compact) => SidebarMode.Compact,
            nameof(SidebarMode.Hidden) => SidebarMode.Hidden,
            _ => SidebarMode.Expanded,
        };
    }

    public static string ToggleToolTip(SidebarMode mode) => mode switch
    {
        SidebarMode.Expanded => "收起侧栏（仅图标）",
        SidebarMode.Compact => "隐藏侧栏",
        _ => "展开侧栏",
    };
}
