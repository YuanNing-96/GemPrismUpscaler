using System.Diagnostics;
using System.Text;

namespace GemPrismUpscaler.Core.Platform.Linux;

/// <summary>
/// Best-effort process identity for profile matching. Wine games often have
/// /proc/&lt;pid&gt;/exe pointing at the wine loader, so the cmdline tokens are
/// searched for *.exe as well.
/// </summary>
public static class ProcessIdentity
{
    public static IReadOnlyList<string> GetExecutableCandidates(int pid)
    {
        var candidates = new List<string>(6);
        if (pid <= 0) return candidates;

        try
        {
            string link = $"/proc/{pid}/exe";
            var target = File.ResolveLinkTarget(link, returnFinalTarget: true);
            if (target is not null)
            {
                candidates.Add(target.FullName);
                candidates.Add(Path.GetFileName(target.FullName));
            }
        }
        catch
        {
            // Permission denied / process gone: fall through to cmdline.
        }

        try
        {
            string cmdline = File.ReadAllText($"/proc/{pid}/cmdline", Encoding.UTF8);
            foreach (string token in cmdline.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    candidates.Add(token);
                    candidates.Add(Path.GetFileName(token));
                }
            }
        }
        catch
        {
            // Best effort only.
        }

        return candidates
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string? GetExecutablePath(int pid)
    {
        try
        {
            return File.ResolveLinkTarget($"/proc/{pid}/exe", returnFinalTarget: true)?.FullName;
        }
        catch
        {
            return null;
        }
    }
}
