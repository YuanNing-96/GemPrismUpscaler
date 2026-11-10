using System.Globalization;

namespace GemPrismUpscaler.Core.Platform.Linux.Nested;

/// <summary>
/// Minimal sway configuration for the private nested session. The base output
/// is a headless display at the target monitor's physical resolution and
/// refresh rate; the output pool creates additional outputs for each game
/// toplevel. Windows are kept floating so a windowed game can resize itself;
/// fullscreen clients are still fullscreen through sway.
/// </summary>
public static class SwayConfig
{
    public const string DefaultOutput = "HEADLESS-1";

    /// <summary>
    /// Formats the sway mode argument. A known physical refresh rate is part of
    /// the mode so the virtual desktop paces like the monitor; 0 means the host
    /// did not report one and sway keeps its default.
    /// </summary>
    public static string Mode(int width, int height, int refreshMilliHz = 0)
    {
        if (refreshMilliHz <= 0) return $"{width}x{height}";
        string hz = (refreshMilliHz / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);
        return $"{width}x{height}@{hz}Hz";
    }

    public static string Build(int width, int height, string output = DefaultOutput,
        int refreshMilliHz = 0)
    {
        if (width is < 320 or > 16384)
            throw new ArgumentOutOfRangeException(nameof(width), "nested output width out of range");
        if (height is < 240 or > 16384)
            throw new ArgumentOutOfRangeException(nameof(height), "nested output height out of range");
        if (string.IsNullOrWhiteSpace(output))
            throw new ArgumentException("output name is required", nameof(output));
        if (refreshMilliHz is < 0 or > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(refreshMilliHz),
                "nested output refresh out of range");

        return $"""
            output {output} resolution {Mode(width, height, refreshMilliHz)}
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

    public static void Write(string path, int width, int height, string output = DefaultOutput,
        int refreshMilliHz = 0)
    {
        File.WriteAllText(path, Build(width, height, output, refreshMilliHz));
    }
}