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
    public const string WindowAppId = "GemPrismUpscaler";

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
            string exec = Environment.ProcessPath ?? WindowAppId;
            string icon = InstallIcon() ? "gemprism" : "applications-games";
            string common = $"""
                Type=Application
                Name=GemPrismUpscaler
                Name[zh_CN]=宝石棱镜
                Comment=GemPrism upscaling overlay
                Exec="{EscapeExec(exec)}"
                Icon={icon}
                Terminal=false
                Categories=Utility;Game;
                StartupNotify=true
                StartupWMClass={WindowAppId}
                """;
            common += "\n";

            // Portal Registry requires a desktop file whose basename equals the
            // portal app id. Keep it hidden from the launcher.
            string portal = $"""
                [Desktop Entry]
                {common}NoDisplay=true
                """;
            // Avalonia's Window app_id is GemPrismUpscaler. A NoDisplay entry with
            // that basename lets KDE resolve the taskbar/titlebar icon without
            // adding a second launcher item.
            string window = $"""
                [Desktop Entry]
                {common}
                """;

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            WriteIfChanged(path, portal);
            WriteIfChanged(Path.Combine(Path.GetDirectoryName(path)!, $"{WindowAppId}.desktop"), window);
            Log.Info($"portal app id desktop entry written: {path} icon={icon}");
        }
        catch (Exception ex)
        {
            Log.Warn($"portal app id desktop file failed: {ex.Message}");
        }
    }

    private static void WriteIfChanged(string path, string content)
    {
        if (File.Exists(path) &&
            string.Equals(File.ReadAllText(path), content, StringComparison.Ordinal))
            return;
        File.WriteAllText(path, content);
    }

    /// <summary>
    /// Install the same icon used by the tray into the user's hicolor theme.
    /// KDE resolves a Wayland window's taskbar icon through its app_id and a
    /// desktop entry, not through wl_surface metadata; a stable icon name keeps
    /// the panel and the nested host windows consistent.
    /// </summary>
    private static bool InstallIcon()
    {
        string dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") ??
                          Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                              ".local", "share");
        string iconDir = Path.Combine(dataHome, "icons", "hicolor", "64x64", "apps");

        string[] candidates =
        {
            Path.Combine(AppContext.BaseDirectory, "GemPrismUpscaler.png"),
            Path.Combine(AppContext.BaseDirectory, "gemprism.png"),
            Path.Combine(Environment.CurrentDirectory, "src", "GemPrismUpscaler", "Assets", "gemprism.png")
        };
        string? source = candidates.FirstOrDefault(File.Exists);
        if (source is null) return false;

        try
        {
            Directory.CreateDirectory(iconDir);
            byte[] bytes = File.ReadAllBytes(source);
            // Plasma's task manager may look up the window app_id directly as
            // an icon name, not only the desktop entry's Icon key.
            foreach (string name in new[]
                     {
                         "gemprism.png",
                         $"{WindowAppId}.png",
                         $"{DefaultValue}.png"
                     })
            {
                string target = Path.Combine(iconDir, name);
                if (!File.Exists(target) || !File.ReadAllBytes(target).SequenceEqual(bytes))
                    File.WriteAllBytes(target, bytes);
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"portal app icon install failed: {ex.Message}");
            return false;
        }
    }

    private static string EscapeExec(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}