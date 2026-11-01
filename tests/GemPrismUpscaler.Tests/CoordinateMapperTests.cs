using GemPrismUpscaler.Core.Geometry;
using Xunit;

namespace GemPrismUpscaler.Tests;

public class CoordinateMapperTests
{
    [Fact]
    public void CenterMapsToSourceCenter()
    {
        var m = new CoordinateMapper(new RectI(0, 0, 3840, 2160), 1280, 720);
        Assert.True(m.TryMap(1920, 1080, out int sx, out int sy));
        Assert.Equal(640, sx);
        Assert.Equal(360, sy);
    }

    [Fact]
    public void PillarboxAreaIsIgnored()
    {
        var m = new CoordinateMapper(new RectI(480, 0, 2880, 2160), 1024, 768);
        Assert.False(m.TryMap(100, 100, out _, out _));
        Assert.True(m.TryMap(480, 0, out int sx, out int sy));
        Assert.Equal(0, sx);
        Assert.Equal(0, sy);
        Assert.True(m.TryMap(3359, 2159, out sx, out sy));
        Assert.Equal(1023, sx);
        Assert.Equal(767, sy);
    }

    [Fact]
    public void CornersClampInsideSource()
    {
        var m = new CoordinateMapper(new RectI(0, 0, 3840, 2160), 1280, 720);
        Assert.True(m.TryMap(3839, 2159, out int sx, out int sy));
        Assert.Equal(1279, sx);
        Assert.Equal(719, sy);
    }
}
