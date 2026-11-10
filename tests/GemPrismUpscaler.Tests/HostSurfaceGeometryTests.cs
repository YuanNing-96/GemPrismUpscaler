using GemPrismUpscaler.Core.Platform.Linux.Nested;
using Xunit;

namespace GemPrismUpscaler.Tests;

/// <summary>
/// The native Wayland host window spans two coordinate spaces: the buffer that
/// Vulkan presents (the game's pixel size, 1:1 with the monitor) and the
/// surface-local logical size the compositor configures. A scale mistake here
/// shifts every click by a factor of the output scale, so the conversions are
/// pinned by tests.
/// </summary>
public class HostSurfaceGeometryTests
{
    [Theory]
    [InlineData(1286, 1.5, 857)]
    [InlineData(3840, 1.5, 2560)]
    [InlineData(800, 1.0, 800)]
    [InlineData(5120, 2.0, 2560)]
    [InlineData(1500, 1.25, 1200)]
    public void ToLogical_DividesByScale(int pixels, double scale, int expected)
        => Assert.Equal(expected, HostSurfaceGeometry.ToLogical(pixels, scale));

    [Theory]
    [InlineData(857, 1.5, 1286)]
    [InlineData(2560, 1.5, 3840)]
    [InlineData(800, 1.0, 800)]
    [InlineData(2560, 2.0, 5120)]
    [InlineData(1200, 1.25, 1500)]
    public void ToPixels_MultipliesByScale(int logical, double scale, int expected)
        => Assert.Equal(expected, HostSurfaceGeometry.ToPixels(logical, scale));

    [Theory]
    [InlineData(1.5)]
    [InlineData(1.25)]
    [InlineData(2.0)]
    public void RoundTrip_StaysWithinOnePixel(double scale)
    {
        foreach (int pixels in new[] { 800, 1286, 1517, 3840 })
        {
            int logical = HostSurfaceGeometry.ToLogical(pixels, scale);
            int back = HostSurfaceGeometry.ToPixels(logical, scale);
            Assert.InRange(back, pixels - 1, pixels + 1);
        }
    }

    [Fact]
    public void LocalToPixel_ClampsToTheBuffer()
    {
        Assert.Equal(0, HostSurfaceGeometry.LocalToPixel(-4, 1.5, 1286));
        Assert.Equal(1285, HostSurfaceGeometry.LocalToPixel(9999, 1.5, 1286));
        Assert.Equal(585, HostSurfaceGeometry.LocalToPixel(390, 1.5, 1286));
    }

    [Fact]
    public void PixelToLocal_ClampsToTheSurface()
    {
        Assert.Equal(0, HostSurfaceGeometry.PixelToLocal(-4, 1.5, 857));
        Assert.Equal(856, HostSurfaceGeometry.PixelToLocal(9999, 1.5, 857));
        // A warp to the last pixel of the buffer stays inside the surface.
        Assert.Equal(856, HostSurfaceGeometry.PixelToLocal(1285, 1.5, 857));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-2.0)]
    [InlineData(double.NaN)]
    [InlineData(1000.0)]
    public void Sanitize_FallsBackToUnscaled(double scale)
        => Assert.Equal(1.0, HostSurfaceGeometry.Sanitize(scale));

    [Fact]
    public void Sanitize_KeepsFractionalScales()
        => Assert.Equal(1.5, HostSurfaceGeometry.Sanitize(1.5));
}