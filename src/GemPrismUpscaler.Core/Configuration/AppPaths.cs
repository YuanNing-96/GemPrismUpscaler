namespace GemPrismUpscaler.Core.Configuration;

/// <summary>
/// XDG-style locations. GEMPRISM_CONFIG points at the config file and also
/// relocates the effects directory next to it, which keeps portable/test
/// setups self-contained.
/// </summary>
public static class AppPaths
{
    public const string ConfigFileName = "config.toml";
    public const string EffectsDirectoryName = "effects";

    public static string ConfigPath
    {
        get
        {
            string? overridden = Environment.GetEnvironmentVariable("GEMPRISM_CONFIG");
            if (!string.IsNullOrWhiteSpace(overridden))
                return System.IO.Path.GetFullPath(overridden);

            string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrWhiteSpace(baseDir))
                baseDir = System.IO.Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.UserProfile), ".config");
            return System.IO.Path.Combine(baseDir, "GemPrismUpscaler", ConfigFileName);
        }
    }

    public static string ConfigDirectory => System.IO.Path.GetDirectoryName(ConfigPath) ?? ".";

    /// <summary>Effects shipped by the user, next to config.toml.</summary>
    public static string EffectsDirectory =>
        System.IO.Path.Combine(ConfigDirectory, EffectsDirectoryName);

    public static string DefaultLogPath
    {
        get
        {
            string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(baseDir))
                baseDir = System.IO.Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.UserProfile), ".local", "share");
            return System.IO.Path.Combine(baseDir, "GemPrismUpscaler", "app.log");
        }
    }

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(EffectsDirectory);
        string? logDir = System.IO.Path.GetDirectoryName(DefaultLogPath);
        if (!string.IsNullOrWhiteSpace(logDir)) Directory.CreateDirectory(logDir);
    }
}
