using GemPrismUpscaler.Core.Platform.Linux.Nested;
using Xunit;

namespace GemPrismUpscaler.Tests;

public class NestedDecorationShimTests
{
    [Fact]
    public void ResolveDecorationShimPath_PrefersSiblingOfSway()
    {
        string dir = Path.Combine(Path.GetTempPath(),
            "gp-shim-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string sway = Path.Combine(dir, "sway");
            string shim = Path.Combine(dir, "xdg-decoration-v2-shim.so");
            File.WriteAllText(sway, string.Empty);
            File.WriteAllText(shim, string.Empty);

            Assert.Equal(shim, NestedSession.ResolveDecorationShimPath(sway));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ResolveDecorationShimPath_IgnoresBareCommandName()
    {
        // A bare "sway" is resolved through PATH; it must not be treated as a
        // directory-relative path. The test output has no runtime/shim.
        Assert.Null(NestedSession.ResolveDecorationShimPath("sway"));
    }
}