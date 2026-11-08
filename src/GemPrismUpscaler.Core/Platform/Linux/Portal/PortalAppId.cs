using GemPrismUpscaler.Core.Diagnostics;

namespace GemPrismUpscaler.Core.Platform.Linux.Portal;

/// <summary>
/// Application id used for xdg-desktop-portal. Unsandboxed processes have no
/// app id by default, and GlobalShortcuts rejects calls without one. The
/// org.freedesktop.host.portal.Registry interface associates our D-Bus
/// connection with an id, but it must match the basename of a .desktop file,
/// so a minimal user-local desktop entry is installed on first use.
/// </summary>
public static class PortalAppId
{
    public const string DefaultValue = "org.gemprism.GemPrismUpscaler";

    public static string Value =>
        Environment.GetEnvironmentVariable("GEMPRISM_PORTAL_APP_ID") is { Length: > 0 } custom
            ? custom
            : DefaultValue;

    public static string DesktopFilePath
    {
        get
        {
            string dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") ??
                              Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                  ".local", "share");
            return Path.Combine(dataHome, "applications", $"{Value}.desktop");
        }
    }

    public static void EnsureDesktopFile()
    {
        try
        {
            string path = DesktopFilePath;
            string exec = Environment.ProcessPath ?? "GemPrismUpscaler";
            string content = $"""
                [Desktop Entry]
                Type=Application
                Name=GemPrismUpscaler
                Comment=GemPrism upscaling overlay
                Exec={exec}
                Icon=applications-games
                Terminal=false
                Categories=Utility;Game;
                """;
            if (File.Exists(path) &&
                string.Equals(File.ReadAllText(path), content, StringComparison.Ordinal))
                return;

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            Log.Info($"portal app id desktop entry written: {path}");
        }
        catch (Exception ex)
        {
            Log.Warn($"portal app id desktop entry failed: {ex.Message}");
        }
    }
}
