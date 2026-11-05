using System.Text.RegularExpressions;
using GemPrismUpscaler.Core.Configuration;
using GemPrismUpscaler.Core.Platform.Linux.X11;

namespace GemPrismUpscaler.Core.Platform.Linux;

public sealed record ProfileMatch(ProfileConfig Profile, string Field, string Value);

/// <summary>
/// Matches [[profile]] entries against WM_CLASS / executable candidates.
/// First profile in file order wins. Patterns use shell-style * and ?, are
/// case-insensitive, and are matched against the full value and its basename.
/// </summary>
public static class ProfileMatcher
{
    public static ProfileMatch? Match(IReadOnlyList<ProfileConfig> profiles, WindowInfo window)
    {
        var candidates = ProcessIdentity.GetExecutableCandidates(window.Pid);
        return Match(profiles, window.ClassName, candidates);
    }

    public static ProfileMatch? Match(IReadOnlyList<ProfileConfig> profiles,
        string className, IReadOnlyList<string> executableCandidates)
    {
        foreach (var profile in profiles)
        {
            foreach (string pattern in profile.Class)
            {
                if (Matches(pattern, className))
                    return new ProfileMatch(profile, "class", className);
            }

            foreach (string pattern in profile.Exe)
            {
                foreach (string candidate in executableCandidates)
                {
                    if (Matches(pattern, candidate) || Matches(pattern, Path.GetFileName(candidate)))
                        return new ProfileMatch(profile, "exe", candidate);
                }
            }
        }

        return null;
    }

    public static bool Matches(string pattern, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (string.IsNullOrWhiteSpace(pattern)) return false;

        string regex = "^" + Regex.Escape(pattern)
            .Replace("\\*", ".*", StringComparison.Ordinal)
            .Replace("\\?", ".", StringComparison.Ordinal) + "$";
        return Regex.IsMatch(value, regex,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(50));
    }
}
