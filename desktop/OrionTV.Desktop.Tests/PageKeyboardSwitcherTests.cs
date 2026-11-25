using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.Tests;

public class PageKeyboardSwitcherTests
{
    private static SwitchablePage? Press(
        SwitchablePage? current,
        PageSwitchKey key,
        bool ctrl = false,
        bool inTextInput = false,
        bool detailAvailable = true,
        bool otherModifiers = false) =>
        PageKeyboardSwitcher.Resolve(current, key, ctrl, inTextInput, detailAvailable, otherModifiers);

    [Fact]
    public void Order_IsHomeSearchDetail()
    {
        Assert.Equal(
            new[] { SwitchablePage.Home, SwitchablePage.Search, SwitchablePage.Detail },
            PageKeyboardSwitcher.Order);
    }

    [Theory]
    [InlineData(SwitchablePage.Home, PageSwitchKey.Digit2, SwitchablePage.Search)]
    [InlineData(SwitchablePage.Home, PageSwitchKey.Digit3, SwitchablePage.Detail)]
    [InlineData(SwitchablePage.Search, PageSwitchKey.Digit1, SwitchablePage.Home)]
    [InlineData(SwitchablePage.Search, PageSwitchKey.Digit3, SwitchablePage.Detail)]
    [InlineData(SwitchablePage.Detail, PageSwitchKey.Digit1, SwitchablePage.Home)]
    [InlineData(SwitchablePage.Detail, PageSwitchKey.Digit2, SwitchablePage.Search)]
    public void Digits_SwitchDirectly(SwitchablePage current, PageSwitchKey key, SwitchablePage expected)
    {
        Assert.Equal(expected, Press(current, key));
        Assert.Equal(expected, Press(current, key, ctrl: true));
    }

    [Theory]
    [InlineData(SwitchablePage.Home, PageSwitchKey.Digit1)]
    [InlineData(SwitchablePage.Search, PageSwitchKey.Digit2)]
    [InlineData(SwitchablePage.Detail, PageSwitchKey.Digit3)]
    public void Digit_ForThePageYouAreOn_DoesNothing(SwitchablePage current, PageSwitchKey key)
    {
        Assert.Null(Press(current, key));
        Assert.Null(Press(current, key, ctrl: true));
    }

    [Theory]
    [InlineData(PageSwitchKey.Digit1)]
    [InlineData(PageSwitchKey.Digit3)]
    public void Digits_WhileTyping_AreLeftToTheTextBox(PageSwitchKey key)
    {
        Assert.Null(Press(SwitchablePage.Search, key, inTextInput: true));
    }

    [Fact]
    public void CtrlDigits_SwitchEvenFromATextBox()
    {
        Assert.Equal(SwitchablePage.Home, Press(SwitchablePage.Search, PageSwitchKey.Digit1, ctrl: true, inTextInput: true));
        Assert.Equal(SwitchablePage.Detail, Press(SwitchablePage.Search, PageSwitchKey.Digit3, ctrl: true, inTextInput: true));
    }

    [Fact]
    public void Detail_IsUnreachableUntilOneHasBeenOpened()
    {
        Assert.Null(Press(SwitchablePage.Home, PageSwitchKey.Digit3, detailAvailable: false));
        Assert.Null(Press(SwitchablePage.Search, PageSwitchKey.Digit3, ctrl: true, detailAvailable: false));
        Assert.Null(Press(SwitchablePage.Search, PageSwitchKey.Right, ctrl: true, detailAvailable: false));
        // 首页 → 搜索不受影响
        Assert.Equal(SwitchablePage.Search, Press(SwitchablePage.Home, PageSwitchKey.Right, ctrl: true, detailAvailable: false));
    }

    [Fact]
    public void CtrlArrows_StepThroughPagesWithoutWrapping()
    {
        Assert.Equal(SwitchablePage.Search, Press(SwitchablePage.Home, PageSwitchKey.Right, ctrl: true));
        Assert.Equal(SwitchablePage.Detail, Press(SwitchablePage.Search, PageSwitchKey.Right, ctrl: true));
        Assert.Null(Press(SwitchablePage.Detail, PageSwitchKey.Right, ctrl: true));

        Assert.Equal(SwitchablePage.Search, Press(SwitchablePage.Detail, PageSwitchKey.Left, ctrl: true));
        Assert.Equal(SwitchablePage.Home, Press(SwitchablePage.Search, PageSwitchKey.Left, ctrl: true));
        Assert.Null(Press(SwitchablePage.Home, PageSwitchKey.Left, ctrl: true));
    }

    [Fact]
    public void OnTheDetailPage_ArrowsWorkEvenIfTheAvailabilityFlagIsStale()
    {
        Assert.Equal(SwitchablePage.Search, Press(SwitchablePage.Detail, PageSwitchKey.Left, ctrl: true, detailAvailable: false));
        Assert.Equal(SwitchablePage.Home, Press(SwitchablePage.Detail, PageSwitchKey.Digit1, detailAvailable: false));
    }

    [Theory]
    [InlineData(PageSwitchKey.Left)]
    [InlineData(PageSwitchKey.Right)]
    public void PlainArrows_AreNeverTaken(PageSwitchKey key)
    {
        // 普通方向键留给卡片之间移动焦点、输入框里移动光标
        Assert.Null(Press(SwitchablePage.Home, key));
        Assert.Null(Press(SwitchablePage.Search, key));
        Assert.Null(Press(SwitchablePage.Detail, key));
        Assert.Null(Press(SwitchablePage.Search, key, inTextInput: true));
    }

    [Theory]
    [InlineData(PageSwitchKey.Left)]
    [InlineData(PageSwitchKey.Right)]
    public void CtrlArrows_InATextBox_StayWordJumps(PageSwitchKey key)
    {
        Assert.Null(Press(SwitchablePage.Search, key, ctrl: true, inTextInput: true));
    }

    [Theory]
    [InlineData(PageSwitchKey.Digit1, false)]
    [InlineData(PageSwitchKey.Digit2, true)]
    [InlineData(PageSwitchKey.Digit3, false)]
    [InlineData(PageSwitchKey.Left, true)]
    [InlineData(PageSwitchKey.Right, true)]
    public void OtherPages_NeverSwitch(PageSwitchKey key, bool ctrl)
    {
        // 播放、直播、本地视频、设置、收藏：当前页传 null
        Assert.Null(Press(null, key, ctrl));
    }

    [Theory]
    [InlineData(PageSwitchKey.Digit2, false)]
    [InlineData(PageSwitchKey.Digit2, true)]
    [InlineData(PageSwitchKey.Right, true)]
    public void ShiftAltOrWin_DisableSwitching(PageSwitchKey key, bool ctrl)
    {
        Assert.Null(Press(SwitchablePage.Home, key, ctrl, otherModifiers: true));
    }

    [Fact]
    public void FocusTarget_FollowsThePage()
    {
        Assert.Equal(PageFocusTarget.FirstCard, PageKeyboardSwitcher.FocusTargetOf(SwitchablePage.Home));
        Assert.Equal(PageFocusTarget.SearchBox, PageKeyboardSwitcher.FocusTargetOf(SwitchablePage.Search));
        Assert.Equal(PageFocusTarget.PlayButton, PageKeyboardSwitcher.FocusTargetOf(SwitchablePage.Detail));
    }
}
