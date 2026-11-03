using GemPrismUpscaler.Core.Geometry;
using GemPrismUpscaler.Core.Platform.Linux;
using Xunit;

namespace GemPrismUpscaler.Tests;

public class MonitorSelectorTests
{
    private static readonly MonitorInfo Hdmi = new("HDMI-A-1", 1800, 489, 3840, 2160, true);
    private static readonly MonitorInfo Dp = new("DP-1", 0, 0, 1800, 2880, false);

    [Fact]
    public void SelectsMonitorWithLargestIntersection()
    {
        var monitors = new[] { Hdmi, Dp };
        var selected = MonitorSelector.Select(monitors, new RectI(2000, 600, 1280, 720));
        Assert.Equal("HDMI-A-1", selected!.Value.Name);
    }

    [Fact]
    public void SelectsPortraitMonitorForWindowOnLeft()
    {
        var monitors = new[] { Hdmi, Dp };
        var selected = MonitorSelector.Select(monitors, new RectI(100, 100, 1280, 720));
        Assert.Equal("DP-1", selected!.Value.Name);
    }

    [Fact]
    public void PreferredMonitorWins()
    {
        var monitors = new[] { Hdmi, Dp };
        var selected = MonitorSelector.Select(monitors, new RectI(2000, 600, 1280, 720), "DP-1");
        Assert.Equal("DP-1", selected!.Value.Name);
    }

    [Fact]
    public void FallsBackToPrimaryWhenWindowIsOffscreen()
    {
        var monitors = new[] { Dp, Hdmi };
        var selected = MonitorSelector.Select(monitors, new RectI(-500, -500, 100, 100));
        Assert.Equal("HDMI-A-1", selected!.Value.Name);
    }

    [Fact]
    public void ReturnsNullForEmptyMonitorList()
    {
        Assert.Null(MonitorSelector.Select(Array.Empty<MonitorInfo>(), new RectI(0, 0, 10, 10)));
    }
}
