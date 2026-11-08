namespace GemPrismUpscaler.Core.Platform.Linux.Nested;

/// <summary>
/// Minimal sway configuration for the private nested session. The base output
/// is a headless display at the target monitor's physical resolution; the
/// output pool creates additional outputs for each game toplevel. Windows are
/// kept floating so a windowed game can resize itself; fullscreen clients are
/// still fullscreen through sway.
/// </summary>
public static class SwayConfig
{
    public const string DefaultOutput = "HEADLESS-1";

    public static string Build(int width, int height, string output = DefaultOutput)
    {
        if (width is < 320 or > 16384)
            throw new ArgumentOutOfRangeException(nameof(width), "nested output width out of range");
        if (height is < 240 or > 16384)
            throw new ArgumentOutOfRangeException(nameof(height), "nested output height out of range");
        if (string.IsNullOrWhiteSpace(output))
            throw new ArgumentException("output name is required", nameof(output));

        return $"""
            output {output} resolution {width}x{height}
            xwayland enable
            default_border none
            default_floating_border none
            gaps inner 0
            gaps outer 0
            popup_during_fullscreen smart
            for_window [class=".*"] floating enable
            for_window [app_id=".*"] floating enable
            for_window [window_type="dialog"] floating enable
            """;
    }

    public static void Write(string path, int width, int height, string output = DefaultOutput)
    {
        File.WriteAllText(path, Build(width, height, output));
    }
}