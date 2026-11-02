namespace GemPrismUpscaler.Core.Diagnostics;

/// <summary>Single source for the user-visible version string.</summary>
public static class AppInfo
{
    public const string Product = "GemPrismUpscaler";

    public static string Version { get; } = Format(typeof(AppInfo).Assembly.GetName().Version);

    public static string DisplayName => $"{Product} v{Version}";

    private static string Format(Version? version)
    {
        if (version is null) return "0.0.0";
        return version.Build >= 0 ? $"{version.Major}.{version.Minor}.{version.Build}" : $"{version.Major}.{version.Minor}";
    }
}
