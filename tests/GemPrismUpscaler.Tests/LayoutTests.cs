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

public class MonitorLayoutTests
{
    [Fact]
    public void SixteenByNineFitsPortraitMonitorWithLetterbox()
    {
        var r = GemPrismUpscaler.Core.Geometry.LayoutCalculator.ComputeDestRect(
            1280, 720, 1800, 2880, GemPrismUpscaler.Core.Geometry.ScalingMode.Fit);
        Assert.Equal(1800, r.Width);
        Assert.Equal(1012, r.Height);
        Assert.Equal(0, r.X);
        Assert.Equal(934, r.Y);
    }

    [Fact]
    public void FourByThreeFitsLandscapeMonitorWithPillarbox()
    {
        var r = GemPrismUpscaler.Core.Geometry.LayoutCalculator.ComputeDestRect(
            1024, 768, 3840, 2160, GemPrismUpscaler.Core.Geometry.ScalingMode.Fit);
        Assert.Equal(new GemPrismUpscaler.Core.Geometry.RectI(480, 0, 2880, 2160), r);
    }
}
