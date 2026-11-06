using GemPrismUpscaler.Core.Configuration;
using GemPrismUpscaler.Core.Geometry;
using GemPrismUpscaler.Core.Engine;
using GemPrismUpscaler.Core.Platform.Linux.Wayland;
using NWayland.Protocols.Wayland;
using Xunit;

namespace GemPrismUpscaler.Tests;

public class M2aWaylandGeometryTests
{
    [Fact]
    public void FractionalScale_UsesLogicalAndPhysicalSize()
    {
        double scale = WaylandGeometry.ComputeScale(2560, 1440, 3840, 2160,
            WlOutput.TransformEnum.Normal);
        Assert.Equal(1.5, scale, 3);
    }

    [Fact]
    public void IntegerScale_IsOne()
    {
        double scale = WaylandGeometry.ComputeScale(1200, 1920, 1920, 1200,
            WlOutput.TransformEnum.k_270);
        Assert.Equal(1.0, scale, 3);
    }

    [Fact]
    public void RotatedOutput_DoesNotUseWidthRatio()
    {
        // A 1920x1200 panel rotated 270 degrees has logical 1200x1920.
        double scale = WaylandGeometry.ComputeScale(1200, 1920, 1920, 1200,
            WlOutput.TransformEnum.k_270);
        Assert.Equal(1.0, scale, 3);
    }

    [Fact]
    public void ScaleTwo_IsTwo()
    {
        double scale = WaylandGeometry.ComputeScale(1920, 1080, 3840, 2160,
            WlOutput.TransformEnum.Normal, 2);
        Assert.Equal(2.0, scale, 3);
    }

    [Fact]
    public void MissingSizes_FallBackToIntegerScale()
    {
        double scale = WaylandGeometry.ComputeScale(0, 0, 0, 0,
            WlOutput.TransformEnum.Normal, 2);
        Assert.Equal(2.0, scale, 3);
    }

    [Fact]
    public void PhysicalToLogical_ConvertsFourKOverOnePointFive()
    {
        var (x, y, w, h) = WaylandGeometry.PhysicalToLogical(
            0, 0, 3840, 2160, 1.5, 2560, 1440);
        Assert.Equal(0, x);
        Assert.Equal(0, y);
        Assert.Equal(2560, w);
        Assert.Equal(1440, h);
    }

    [Fact]
    public void PhysicalToLogical_ClampsContentRect()
    {
        var (x, y, w, h) = WaylandGeometry.PhysicalToLogical(
            100, 100, 4000, 3000, 1.5, 2560, 1440);
        Assert.Equal(67, x);
        Assert.Equal(67, y);
        Assert.True(x + w <= 2560);
        Assert.True(y + h <= 1440);
    }
}

public class M2aConfigTests
{
    [Fact]
    public void CaptureAndPointerMode_Defaults()
    {
        var config = AppConfig.CreateDefault();
        var warnings = new List<string>();
        config.Normalize(warnings);

        Assert.Equal("auto", config.Capture.Backend);
        Assert.Equal("auto", config.Capture.Mode);
        Assert.Equal("auto", config.Input.PointerMode);
        Assert.Empty(warnings);
    }

    [Fact]
    public void CaptureAndPointerMode_InvalidValuesFallBack()
    {
        var config = AppConfig.CreateDefault();
        config.Capture.Backend = "bogus";
        config.Capture.Mode = "bogus";
        config.Input.PointerMode = "bogus";
        var warnings = new List<string>();
        config.Normalize(warnings);

        Assert.Equal("auto", config.Capture.Backend);
        Assert.Equal("auto", config.Capture.Mode);
        Assert.Equal("auto", config.Input.PointerMode);
        Assert.Contains(warnings, w => w.Contains("capture.backend"));
        Assert.Contains(warnings, w => w.Contains("capture.mode"));
        Assert.Contains(warnings, w => w.Contains("input.pointer_mode"));
    }

    [Fact]
    public void PortalSourceAlreadyOutputSize_Disables2x()
    {
        Assert.True(PortalUpscaleSession.SourceAlreadyAtOutputSize(3840, 2160, 3840, 2160));
        Assert.True(PortalUpscaleSession.SourceAlreadyAtOutputSize(3840, 1600, 3840, 2160));
        Assert.False(PortalUpscaleSession.SourceAlreadyAtOutputSize(1280, 720, 3840, 2160));
        Assert.False(PortalUpscaleSession.SourceAlreadyAtOutputSize(1920, 1080, 3840, 2160));
    }

    [Fact]
    public void FourByThreeSource_ContentRectIsPillarboxed()
    {
        var rect = LayoutCalculator.ComputeDestRect(1024, 768, 3840, 2160, ScalingMode.Fit);
        Assert.Equal(new RectI(480, 0, 2880, 2160), rect);
    }
}
