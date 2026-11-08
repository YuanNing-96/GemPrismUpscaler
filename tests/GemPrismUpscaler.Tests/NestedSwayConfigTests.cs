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

    [Theory]
    [InlineData(319, 720)]
    [InlineData(1280, 239)]
    [InlineData(16385, 720)]
    [InlineData(1280, 16385)]
    public void Build_RejectsOutOfRangeSize(int width, int height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SwayConfig.Build(width, height));
    }

    [Fact]
    public void Build_RejectsEmptyOutputName()
    {
        Assert.Throws<ArgumentException>(() => SwayConfig.Build(1280, 720, " "));
    }
}