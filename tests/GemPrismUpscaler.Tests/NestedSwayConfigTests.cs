using GemPrismUpscaler.Core.Platform.Linux.Nested;
using Xunit;

namespace GemPrismUpscaler.Tests;

public class NestedSwayConfigTests
{
    [Fact]
    public void Build_ContainsOutputResolutionAndXWayland()
    {
        string config = SwayConfig.Build(1024, 768);

        Assert.Contains("output HEADLESS-1 resolution 1024x768", config);
        Assert.Contains("xwayland enable", config);
        Assert.Contains("default_border none", config);
        Assert.Contains("gaps inner 0", config);
        Assert.Contains("popup_during_fullscreen smart", config);
        Assert.Contains("for_window [class=\".*\"] floating enable", config);
        Assert.Contains("for_window [app_id=\".*\"] floating enable", config);
        Assert.Contains("for_window [window_type=\"dialog\"] floating enable", config);
    }

    [Fact]
    public void Build_UsesRequestedOutputName()
    {
        string config = SwayConfig.Build(1920, 1080, "HEADLESS-2");
        Assert.Contains("output HEADLESS-2 resolution 1920x1080", config);
    }

    [Fact]
    public void Build_AppendsMonitorRefreshRate()
    {
        string config = SwayConfig.Build(1920, 1080, refreshMilliHz: 59950);

        Assert.Contains("output HEADLESS-1 resolution 1920x1080@59.95Hz", config);
    }

    [Fact]
    public void Build_FormatsWholeRefreshRateWithoutDecimals()
    {
        string config = SwayConfig.Build(3840, 2160, refreshMilliHz: 60000);

        Assert.Contains("output HEADLESS-1 resolution 3840x2160@60Hz", config);
    }

    [Fact]
    public void Mode_OmitsRefreshWhenUnknown()
    {
        Assert.Equal("1280x720", SwayConfig.Mode(1280, 720));
        Assert.Equal("1280x720@144Hz", SwayConfig.Mode(1280, 720, 144000));
    }

    [Theory]
    [InlineData(319, 720)]
    [InlineData(1280, 239)]
    [InlineData(16385, 720)]
    [InlineData(1280, 16385)]
    public void Build_RejectsOutOfRangeSize(int width, int height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SwayConfig.Build(width, height));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1_000_001)]
    public void Build_RejectsOutOfRangeRefresh(int refreshMilliHz)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SwayConfig.Build(1280, 720, refreshMilliHz: refreshMilliHz));
    }

    [Fact]
    public void Build_RejectsEmptyOutputName()
    {
        Assert.Throws<ArgumentException>(() => SwayConfig.Build(1280, 720, " "));
    }
}