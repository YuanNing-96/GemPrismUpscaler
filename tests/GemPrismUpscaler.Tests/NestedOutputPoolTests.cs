using GemPrismUpscaler.Core.Geometry;
using GemPrismUpscaler.Core.Platform.Linux.Nested;
using Xunit;

namespace GemPrismUpscaler.Tests;

public class NestedOutputPoolTests
{
    private const string Outputs = """
        [
          {
            "name": "HEADLESS-1",
            "active": true,
            "rect": {"x":0,"y":0,"width":3840,"height":2160},
            "current_mode": {"width":3840,"height":2160,"refresh":0}
          },
          {
            "name": "HEADLESS-2",
            "active": true,
            "rect": {"x":3840,"y":0,"width":3840,"height":2160},
            "current_mode": {"width":3840,"height":2160,"refresh":0}
          }
        ]
        """;

    [Fact]
    public void ParseOutputs_ReadsNameRectAndMode()
    {
        var outputs = SwayOutputPool.ParseOutputs(Outputs);

        Assert.Equal(2, outputs.Count);
        Assert.Equal("HEADLESS-1", outputs[0].Name);
        Assert.Equal(new RectI(0, 0, 3840, 2160), outputs[0].Rect);
        Assert.Equal(3840, outputs[1].Rect.X);
        Assert.True(outputs[1].Active);
    }

    [Fact]
    public void ParseOutputs_UsesModeWhenRectIsMissing()
    {
        const string json = """
            [
              {
                "name": "HEADLESS-1",
                "active": false,
                "current_mode": {"width":1920,"height":1080,"refresh":0}
              }
            ]
            """;

        var output = Assert.Single(SwayOutputPool.ParseOutputs(json));
        Assert.Equal(1920, output.Width);
        Assert.Equal(1080, output.Height);
        Assert.False(output.Active);
    }

    [Fact]
    public void ParseOutputs_IgnoresMalformedAndUnnamedEntries()
    {
        const string json = """
            [
              {"active": true, "rect": {"x":0,"y":0,"width":10,"height":10}},
              {"name": "", "rect": {"x":0,"y":0,"width":10,"height":10}},
              {"name": "HEADLESS-9"}
            ]
            """;

        var output = Assert.Single(SwayOutputPool.ParseOutputs(json));
        Assert.Equal("HEADLESS-9", output.Name);
        Assert.Equal(0, output.Width);
    }
}
