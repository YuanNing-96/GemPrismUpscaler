using System.Text;
using GemPrismUpscaler.Core.Configuration;
using GemPrismUpscaler.Core.Diagnostics;

namespace GemPrismUpscaler.Core.Platform.Linux.Portal;

/// <summary>
/// Stores the single-use ScreenCast restore token. The token is rotated after
/// every successful Start; a stale token is harmless (the portal prompts), so
/// a simple file is sufficient.
/// </summary>
public static class PortalTokenStore
{
    public static string Path => System.IO.Path.Combine(AppPaths.ConfigDirectory, "portal.token");

    public static string? Load()
    {
        try
        {
            if (!File.Exists(Path)) return null;
            string token = File.ReadAllText(Path, Encoding.UTF8).Trim().TrimStart('\uFEFF');
            return string.IsNullOrWhiteSpace(token) ? null : token;
        }
        catch (Exception ex)
        {
            Log.Warn($"portal token read failed: {ex.Message}");
            return null;
        }
    }

    public static void Save(string token)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.ConfigDirectory);
            string temp = Path + ".tmp";
            File.WriteAllText(temp, token, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temp, Path, overwrite: true);
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex)
        {
            Log.Warn($"portal token write failed: {ex.Message}");
        }
    }
}
