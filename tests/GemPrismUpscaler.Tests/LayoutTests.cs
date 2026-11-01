using GemPrismUpscaler.Core.Geometry;
using Xunit;

namespace GemPrismUpscaler.Tests;

public class LayoutTests
{
    [Fact]
    public void SixteenByNineFitFillsScreen()
    {
        var r = LayoutCalculator.ComputeDestRect(1280, 720, 3840, 2160, ScalingMode.Fit);
        Assert.Equal(new RectI(0, 0, 3840, 2160), r);
    }

    [Fact]
    public void FourByThreeFitPillarboxes()
    {
        var r = LayoutCalculator.ComputeDestRect(1024, 768, 3840, 2160, ScalingMode.Fit);
        Assert.Equal(2880, r.Width);
        Assert.Equal(2160, r.Height);
        Assert.Equal(480, r.X);
        Assert.Equal(0, r.Y);
    }

    [Fact]
    public void StretchIgnoresAspect()
    {
        var r = LayoutCalculator.ComputeDestRect(1024, 768, 3840, 2160, ScalingMode.Stretch);
        Assert.Equal(new RectI(0, 0, 3840, 2160), r);
    }

    [Fact]
    public void InvalidDimensionsThrow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LayoutCalculator.ComputeDestRect(0, 720, 3840, 2160, ScalingMode.Fit));
    }
}
