using GemPrismUpscaler.Core.Platform.Linux.Nested;
using Xunit;

namespace GemPrismUpscaler.Tests;

public class NestedSessionStateTests
{
    [Fact]
    public void EnvFile_QuotesValuesAndContainsAllPaths()
    {
        var state = new NestedSessionState(42, "/tmp/a b/wayland-1", ":2",
            "/tmp/a b/sway.sock", "token'x", 1280, 720);
        string text = state.ToEnvFile();
        Assert.Contains("GEMPRISM_NESTED_PID=42", text);
        Assert.Contains("GEMPRISM_NESTED_WAYLAND='/tmp/a b/wayland-1'", text);
        Assert.Contains("GEMPRISM_NESTED_DISPLAY=':2'", text);
        Assert.Contains("GEMPRISM_NESTED_TOKEN='token'\\''x'", text);
    }
}
