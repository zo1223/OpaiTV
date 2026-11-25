using OrionTV.Desktop.Core;

namespace OrionTV.Desktop.Tests;

public class CardMotionTests
{
    [Fact]
    public void Tilt_AtCenter_IsFlat()
    {
        var pose = CardTilt.FromPointer(75, 105, 150, 210);

        Assert.Equal(0, pose.SkewX, 6);
        Assert.Equal(0, pose.SkewY, 6);
        Assert.Equal(0, pose.ShiftX, 6);
        Assert.Equal(0, pose.ShiftY, 6);
        Assert.Equal(0.5, pose.GlareX, 6);
        Assert.Equal(0.5, pose.GlareY, 6);
    }

    [Fact]
    public void Tilt_FollowsPointerHorizontally()
    {
        var left = CardTilt.FromPointer(0, 105, 150, 210);
        var right = CardTilt.FromPointer(150, 105, 150, 210);

        Assert.Equal(-CardTilt.MaxSkewDegrees, left.SkewY, 6);
        Assert.Equal(CardTilt.MaxSkewDegrees, right.SkewY, 6);
        Assert.Equal(-CardTilt.MaxShiftPixels, left.ShiftX, 6);
        Assert.Equal(CardTilt.MaxShiftPixels, right.ShiftX, 6);
        Assert.Equal(0, left.SkewX, 6);
        Assert.Equal(0, right.SkewX, 6);
        Assert.Equal(0, left.GlareX, 6);
        Assert.Equal(1, right.GlareX, 6);
    }

    [Fact]
    public void Tilt_FollowsPointerVertically()
    {
        var top = CardTilt.FromPointer(75, 0, 150, 210);
        var bottom = CardTilt.FromPointer(75, 210, 150, 210);

        Assert.True(top.SkewX > 0);
        Assert.True(bottom.SkewX < 0);
        Assert.Equal(-top.SkewX, bottom.SkewX, 6);
        Assert.Equal(-CardTilt.MaxShiftPixels, top.ShiftY, 6);
        Assert.Equal(CardTilt.MaxShiftPixels, bottom.ShiftY, 6);
        Assert.Equal(0, top.SkewY, 6);
    }

    [Theory]
    [InlineData(-500, -500)]
    [InlineData(9999, 9999)]
    [InlineData(-1, 300)]
    [InlineData(40, 80)]
    [InlineData(149.9, 0.1)]
    public void Tilt_StaysWithinLimits(double x, double y)
    {
        var pose = CardTilt.FromPointer(x, y, 150, 210);

        Assert.InRange(pose.SkewX, -CardTilt.MaxSkewDegrees, CardTilt.MaxSkewDegrees);
        Assert.InRange(pose.SkewY, -CardTilt.MaxSkewDegrees, CardTilt.MaxSkewDegrees);
        Assert.InRange(pose.ShiftX, -CardTilt.MaxShiftPixels, CardTilt.MaxShiftPixels);
        Assert.InRange(pose.ShiftY, -CardTilt.MaxShiftPixels, CardTilt.MaxShiftPixels);
        Assert.InRange(pose.GlareX, 0, 1);
        Assert.InRange(pose.GlareY, 0, 1);
    }

    [Fact]
    public void Tilt_OutsideCard_ClampsToEdge()
    {
        Assert.Equal(CardTilt.FromPointer(150, 210, 150, 210), CardTilt.FromPointer(400, 900, 150, 210));
        Assert.Equal(CardTilt.FromPointer(0, 0, 150, 210), CardTilt.FromPointer(-30, -30, 150, 210));
    }

    [Theory]
    [InlineData(0, 210)]
    [InlineData(150, 0)]
    [InlineData(-150, 210)]
    [InlineData(double.NaN, 210)]
    [InlineData(150, double.PositiveInfinity)]
    public void Tilt_UnusableSize_ReturnsRest(double width, double height)
    {
        Assert.Equal(CardTilt.Rest, CardTilt.FromPointer(10, 10, width, height));
    }

    [Theory]
    [InlineData(double.NaN, 10)]
    [InlineData(10, double.NaN)]
    [InlineData(double.NegativeInfinity, 10)]
    public void Tilt_UnusablePointer_ReturnsRest(double x, double y)
    {
        Assert.Equal(CardTilt.Rest, CardTilt.FromPointer(x, y, 150, 210));
    }

    [Fact]
    public void Tilt_IsSubtle()
    {
        // 效果要轻：倾斜不超过 3 度，位移和抬起只有几个像素，放大不超过 5%。
        Assert.InRange(CardTilt.MaxSkewDegrees, 0.5, 3);
        Assert.InRange(CardTilt.MaxShiftPixels, 0, 4);
        Assert.InRange(CardTilt.LiftPixels, 1, 6);
        Assert.InRange(CardTilt.HoverScale, 1.0, 1.05);
    }

    [Fact]
    public void Stagger_FirstCardStartsImmediately()
    {
        Assert.Equal(0, EntranceStagger.DelayFor(0));
    }

    [Fact]
    public void Stagger_DelayGrowsThenCaps()
    {
        Assert.Equal(EntranceStagger.StepMilliseconds, EntranceStagger.DelayFor(1));
        Assert.Equal(EntranceStagger.StepMilliseconds * 5, EntranceStagger.DelayFor(5));

        var capped = EntranceStagger.MaxSteps * EntranceStagger.StepMilliseconds;
        Assert.Equal(capped, EntranceStagger.DelayFor(EntranceStagger.MaxSteps));
        Assert.Equal(capped, EntranceStagger.DelayFor(EntranceStagger.MaxSteps + 7));
        Assert.Equal(capped, EntranceStagger.DelayFor(EntranceStagger.MaxAnimatedItems - 1));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(36)]
    [InlineData(500)]
    public void Stagger_OutsideAnimatedRange_DoesNotAnimate(int index)
    {
        Assert.Null(EntranceStagger.DelayFor(index));
    }

    [Fact]
    public void Stagger_NeverDelaysLongEnoughToFeelSlow()
    {
        // 不拖慢操作：一批卡片从开始到全部出现不超过半秒。
        Assert.True(EntranceStagger.LongestTotalMilliseconds <= 500);
        for (var i = 0; i < 1000; i++)
        {
            var delay = EntranceStagger.DelayFor(i);
            if (delay is int value)
            {
                Assert.InRange(value, 0, EntranceStagger.MaxSteps * EntranceStagger.StepMilliseconds);
            }
        }
    }
}
