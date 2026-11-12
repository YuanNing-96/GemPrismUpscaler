using GemPrismUpscaler.Core.Platform.Linux.Nested;
using Xunit;

namespace GemPrismUpscaler.Tests;

public class NestedCommandLineTests
{
    [Fact]
    public void SplitsPlainArguments()
    {
        Assert.Equal(new[] { "env", "A=1", "lutris", "lutris:rungameid/3" },
            NestedCommandLine.Split("env A=1 lutris lutris:rungameid/3"));
    }

    [Fact]
    public void KeepsQuotedPathsTogether()
    {
        Assert.Equal(new[] { "/home/user/My Games/launcher", "--flag" },
            NestedCommandLine.Split("\"/home/user/My Games/launcher\" --flag"));
        Assert.Equal(new[] { "/home/user/My Games/launcher" },
            NestedCommandLine.Split("'/home/user/My Games/launcher'"));
    }

    [Fact]
    public void HandlesEscapedQuotesAndSpaces()
    {
        Assert.Equal(new[] { "a b", "c\"d" },
            NestedCommandLine.Split("a\\ b \"c\\\"d\""));
    }

    [Fact]
    public void RejectsUnclosedQuote()
    {
        Assert.Throws<FormatException>(() => NestedCommandLine.Split("\"unterminated"));
    }
}