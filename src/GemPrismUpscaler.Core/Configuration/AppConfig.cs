using System.Text.Json;

namespace GemPrismUpscaler.Core.Configuration;

/// <summary>
/// User-editable configuration. Property names are PascalCase in C# and are
/// serialised as snake_case TOML keys (General.SecondInstance ->
/// [general] second_instance).
/// </summary>
public sealed class AppConfig
{
    public int Schema { get; set; } = AppConfigSchema.Current;
    public GeneralConfig General { get; set; } = new();
    public HotkeyConfig Hotkey { get; set; } = new();
    public LoggingConfig Logging { get; set; } = new();
    public DisplayConfig Display { get; set; } = new();
    public EffectConfig Effect { get; set; } = new();
    public CaptureConfig Capture { get; set; } = new();
    public TrayConfig Tray { get; set; } = new();
    public InputConfig Input { get; set; } = new();
    public DiagnosticsConfig Diagnostics { get; set; } = new();
    public LastConfig Last { get; set; } = new();

    /// <summary>TOML array of tables: [[profile]]</summary>
    public List<ProfileConfig> Profile { get; set; } = new();

    public static AppConfig CreateDefault() => new();

    /// <summary>
    /// Repairs values that a hand-edited file can get wrong. Problems are
    /// appended to <paramref name="warnings"/>; the returned config is always
    /// usable.
    /// </summary>
    public void Normalize(List<string> warnings)
    {
        if (Schema != AppConfigSchema.Current)
        {
            if (Schema > AppConfigSchema.Current)
                warnings.Add($"配置 schema={Schema} 高于本程序支持的 {AppConfigSchema.Current}，未知字段会被忽略");
            Schema = AppConfigSchema.Current;
        }

        General ??= new GeneralConfig();
        Hotkey ??= new HotkeyConfig();
        Logging ??= new LoggingConfig();
        Display ??= new DisplayConfig();
        Effect ??= new EffectConfig();
        Capture ??= new CaptureConfig();
        Tray ??= new TrayConfig();
        Input ??= new InputConfig();
        Diagnostics ??= new DiagnosticsConfig();
        Last ??= new LastConfig();
        Profile ??= new List<ProfileConfig>();

        General.SecondInstance = NormalizeChoice(General.SecondInstance,
            "notify", "activate", "notify", warnings, "general.second_instance");
        General.CloseAction = NormalizeChoice(General.CloseAction,
            "exit", "tray", "exit", warnings, "general.close_action");
        General.RefreshIntervalMs = Math.Clamp(General.RefreshIntervalMs, 250, 5000);

        Hotkey.ToggleOverlay = string.IsNullOrWhiteSpace(Hotkey.ToggleOverlay)
            ? "Alt+Shift+A"
            : Hotkey.ToggleOverlay.Trim();

        Logging.Level = NormalizeChoice(Logging.Level,
            "debug", "info", "warn", "error", "info", warnings, "logging.level");
        Logging.Path = Logging.Path?.Trim() ?? string.Empty;

        Display.Monitor = string.IsNullOrWhiteSpace(Display.Monitor)
            ? "auto"
            : Display.Monitor.Trim();

        Effect.Default = string.IsNullOrWhiteSpace(Effect.Default)
            ? "anime4k-s"
            : Effect.Default.Trim();
        Effect.Scaling = NormalizeChoice(Effect.Scaling,
            "fit", "stretch", "fit", warnings, "effect.scaling");

        Capture.Backend = NormalizeChoice(Capture.Backend,
            new[] { "auto", "x11", "wayland" }, "auto", warnings, "capture.backend");
        Capture.Mode = NormalizeChoice(Capture.Mode,
            new[] { "auto", "x11", "portal" }, "auto", warnings, "capture.mode");

        Input.Mode = NormalizeChoice(Input.Mode,
            "2d", "fps", "2d", warnings, "input.mode");
        Input.PointerMode = NormalizeChoice(Input.PointerMode,
            new[] { "auto", "passthrough", "forward" }, "auto", warnings, "input.pointer_mode");

        if (Diagnostics.DeveloperMode)
            Logging.Level = "debug";

        Last ??= new LastConfig();
        Last.Effect = Last.Effect?.Trim() ?? string.Empty;
        Last.Scaling = Last.Scaling?.Trim() ?? string.Empty;
        Last.Monitor = Last.Monitor?.Trim() ?? string.Empty;

        for (int i = 0; i < Profile.Count; i++)
        {
            var profile = Profile[i] ??= new ProfileConfig();
            if (string.IsNullOrWhiteSpace(profile.Name))
                profile.Name = $"profile-{i + 1}";
            profile.Name = profile.Name.Trim();
            profile.Class ??= new List<string>();
            profile.Exe ??= new List<string>();
            profile.Class = profile.Class
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim())
                .ToList();
            profile.Exe = profile.Exe
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim())
                .ToList();
            if (profile.Scaling is not null)
                profile.Scaling = NormalizeChoice(profile.Scaling,
                    "fit", "stretch", "fit", warnings, $"profile[{i}].scaling");
            if (profile.Monitor is not null)
                profile.Monitor = profile.Monitor.Trim();
            if (profile.Effect is not null)
                profile.Effect = profile.Effect.Trim();
            if (profile.Hotkey is not null)
                profile.Hotkey = profile.Hotkey.Trim();
        }
    }

    private static string NormalizeChoice(string? value, string allowed1, string allowed2,
        string fallback, List<string> warnings, string key)
    {
        string text = value?.Trim().ToLowerInvariant() ?? string.Empty;
        if (text == allowed1 || text == allowed2) return text;
        if (text.Length == 0) return fallback;
        warnings.Add($"{key}='{value}' 无效，已回退为 '{fallback}'");
        return fallback;
    }

    private static string NormalizeChoice(string? value, string[] allowed,
        string fallback, List<string> warnings, string key)
    {
        string text = value?.Trim().ToLowerInvariant() ?? string.Empty;
        if (Array.IndexOf(allowed, text) >= 0) return text;
        if (text.Length == 0) return fallback;
        warnings.Add($"{key}='{value}' 无效，已回退为 '{fallback}'");
        return fallback;
    }

    private static string NormalizeChoice(string? value, string allowed1, string allowed2,
        string allowed3, string allowed4, string fallback, List<string> warnings, string key)
    {
        string text = value?.Trim().ToLowerInvariant() ?? string.Empty;
        if (text == allowed1 || text == allowed2 || text == allowed3 || text == allowed4) return text;
        if (text.Length == 0) return fallback;
        warnings.Add($"{key}='{value}' 无效，已回退为 '{fallback}'");
        return fallback;
    }

    internal static JsonNamingPolicy NamingPolicy => JsonNamingPolicy.SnakeCaseLower;
}

public static class AppConfigSchema
{
    public const int Current = 1;
}

public sealed class GeneralConfig
{
    public string SecondInstance { get; set; } = "notify";
    public string CloseAction { get; set; } = "exit";
    public bool FollowFocus { get; set; } = true;
    public bool RememberLast { get; set; } = true;
    public bool AutoRefreshWindows { get; set; }
    public int RefreshIntervalMs { get; set; } = 1000;
}

public sealed class HotkeyConfig
{
    public string ToggleOverlay { get; set; } = "Alt+Shift+A";
}

public sealed class LoggingConfig
{
    public string Path { get; set; } = string.Empty;
    public string Level { get; set; } = "info";
}

public sealed class DisplayConfig
{
    public string Monitor { get; set; } = "auto";
}

public sealed class EffectConfig
{
    public string Default { get; set; } = "anime4k-s";
    public string Scaling { get; set; } = "fit";
    public bool DirtyRect { get; set; } = true;
}

public sealed class CaptureConfig
{
    /// <summary>auto | x11 | wayland</summary>
    public string Backend { get; set; } = "auto";
    /// <summary>auto | x11 | portal</summary>
    public string Mode { get; set; } = "auto";
}

public sealed class TrayConfig
{
    public bool Enabled { get; set; } = true;
}

public sealed class InputConfig
{
    /// <summary>2d | fps (M1c placeholder; M2 keeps for compatibility).</summary>
    public string Mode { get; set; } = "2d";
    /// <summary>auto | passthrough | forward (Wayland pointer handling).</summary>
    public string PointerMode { get; set; } = "auto";
}

public sealed class DiagnosticsConfig
{
    public bool DeveloperMode { get; set; }
}

public sealed class LastConfig
{
    public string Effect { get; set; } = string.Empty;
    public string Scaling { get; set; } = string.Empty;
    public string Monitor { get; set; } = string.Empty;
}

public sealed class ProfileConfig
{
    public string Name { get; set; } = string.Empty;
    public List<string> Class { get; set; } = new();
    public List<string> Exe { get; set; } = new();
    public string? Effect { get; set; }
    public string? Scaling { get; set; }
    public string? Monitor { get; set; }
    public bool? FollowFocus { get; set; }
    public bool? DirtyRect { get; set; }
    public string? Hotkey { get; set; }
}
