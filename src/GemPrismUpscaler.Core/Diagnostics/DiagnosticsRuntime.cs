using GemPrismUpscaler.Core.Configuration;

namespace GemPrismUpscaler.Core.Diagnostics;

/// <summary>
/// Process-wide switches derived from config + environment. Kept separate from
/// Log so the renderer can consult them without depending on the UI.
/// </summary>
public static class DiagnosticsRuntime
{
    public static bool DeveloperMode { get; private set; }
    public static bool ValidationLayerEnabled { get; private set; }
    public static bool ValidationOverridden { get; private set; }

    public static void Configure(AppConfig config)
    {
        string? devEnv = Environment.GetEnvironmentVariable("GEMPRISM_DEV");
        DeveloperMode = config.Diagnostics.DeveloperMode ||
                        devEnv is "1" or "true" or "yes" or "on";

        string? validationEnv = Environment.GetEnvironmentVariable("GEMPRISM_VALIDATION");
        if (validationEnv is not null)
        {
            ValidationOverridden = true;
            ValidationLayerEnabled = validationEnv is "1" or "true" or "yes" or "on";
        }
        else
        {
            ValidationOverridden = false;
            ValidationLayerEnabled = DeveloperMode || IsDebugBuild;
        }
    }

    public static string Describe()
        => $"developer={DeveloperMode} validation={ValidationLayerEnabled}" +
           (ValidationOverridden ? " (env)" : string.Empty);

    private static bool IsDebugBuild
    {
        get
        {
#if DEBUG
            return true;
#else
            return false;
#endif
        }
    }
}
