using GemPrismUpscaler.Core.Configuration;
using GemPrismUpscaler.Core.Platform.Linux;
using Xunit;

namespace GemPrismUpscaler.Tests;

public sealed class M1cProfileTests
{
    [Theory]
    [InlineData("*.exe", "game.exe", true)]
    [InlineData("*game*", "MyGame.exe", true)]
    [InlineData("game?.exe", "game1.exe", true)]
    [InlineData("game?.exe", "game10.exe", false)]
    [InlineData("testpattern", "TestPattern", true)]
    [InlineData("*.exe", "game.bin", false)]
    public void Glob_Matches(string pattern, string value, bool expected)
    {
        Assert.Equal(expected, ProfileMatcher.Matches(pattern, value));
    }

    [Fact]
    public void ClassMatch_Wins_FirstProfileInFileOrder()
    {
        var profiles = new List<ProfileConfig>
        {
            new() { Name = "first", Class = ["test*"], Effect = "anime4k-m" },
            new() { Name = "second", Class = ["testpattern"], Effect = "anime4k-l" }
        };

        var match = ProfileMatcher.Match(profiles, "testpattern", []);

        Assert.NotNull(match);
        Assert.Equal("first", match!.Profile.Name);
        Assert.Equal("class", match.Field);
    }

    [Fact]
    public void ExeCandidate_MatchesFullPathOrBasename()
    {
        var profiles = new List<ProfileConfig>
        {
            new() { Name = "wine", Exe = ["*/drive_c/game.exe"], Effect = "anime4k-l" }
        };

        var match = ProfileMatcher.Match(profiles, "game.exe",
            ["/home/user/Games/drive_c/game.exe"]);

        Assert.NotNull(match);
        Assert.Equal("wine", match!.Profile.Name);
        Assert.Equal("exe", match.Field);
    }

    [Fact]
    public void NoMatch_ReturnsNull()
    {
        var profiles = new List<ProfileConfig>
        {
            new() { Name = "only", Class = ["other"] }
        };

        Assert.Null(ProfileMatcher.Match(profiles, "testpattern", ["/tmp/game.exe"]));
    }

    [Fact]
    public void EmptyPatterns_AreIgnored()
    {
        var profiles = new List<ProfileConfig>
        {
            new() { Name = "empty" }
        };
        Assert.Null(ProfileMatcher.Match(profiles, "testpattern", []));
    }
}
