using System.Diagnostics;
using System.Text;
using GemPrismUpscaler.Core.Diagnostics;

namespace GemPrismUpscaler.Services;

/// <summary>
/// KWin/Wayland can refuse a plain Activate() from a tray/DBus callback because
/// there is no xdg-activation token. KWin exposes a small scripting interface;
/// use it as a last-resort "bring my own window to the front" on KDE only.
///
/// This is deliberately guarded: non-KDE desktops and missing qdbus/gdbus are
/// treated as "unsupported" and the caller simply keeps the normal behaviour.
/// </summary>
public static class KdeWindowActivator
{
    private const string ResourceClass = "GemPrismUpscaler";

    public static bool IsKde
    {
        get
        {
            string desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? string.Empty;
            return desktop.Contains("KDE", StringComparison.OrdinalIgnoreCase) ||
                   !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("KDE_FULL_SESSION"));
        }
    }

    public static bool TryActivate()
    {
        if (!IsKde) return false;

        string scriptPath = Path.Combine(Path.GetTempPath(),
            $"gemprism-activate-{Environment.ProcessId}-{Guid.NewGuid():N}.js");

        string script = $$"""
            const wins = workspace.windowList();
            for (const w of wins) {
                if ((w.resourceClass || "") === "{{ResourceClass}}") {
                    if (w.minimized) { try { w.minimized = false; } catch (e) {} }
                    workspace.activeWindow = w;
                }
            }
            """;

        try
        {
            File.WriteAllText(scriptPath, script, new UTF8Encoding(false));
            if (!Run("qdbus6", "org.kde.KWin", "/Scripting",
                    "org.kde.kwin.Scripting.loadScript", scriptPath))
            {
                if (!Run("gdbus", "call", "--session", "--dest", "org.kde.KWin",
                        "--object-path", "/Scripting", "--method",
                        "org.kde.kwin.Scripting.loadScript", scriptPath))
                    return false;
            }

            bool started = Run("qdbus6", "org.kde.KWin", "/Scripting",
                               "org.kde.kwin.Scripting.start") ||
                           Run("gdbus", "call", "--session", "--dest", "org.kde.KWin",
                               "--object-path", "/Scripting", "--method",
                               "org.kde.kwin.Scripting.start");
            Log.Info($"kde activation fallback: script={scriptPath} started={started}");
            return started;
        }
        catch (Exception ex)
        {
            Log.Debug($"kde activation fallback failed: {ex.Message}");
            return false;
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { }
        }
    }

    private static bool Run(string fileName, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string arg in args) psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi);
            if (process is null) return false;
            var read = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(900))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return false;
            }
            _ = read.GetAwaiter().GetResult();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
