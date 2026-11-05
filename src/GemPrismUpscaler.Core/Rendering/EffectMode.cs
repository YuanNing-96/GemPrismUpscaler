namespace GemPrismUpscaler.Core.Rendering;

public enum EffectMode
{
    /// <summary>M0a path: one vkCmdBlitImage with linear filtering.</summary>
    Bilinear = 0,

    /// <summary>Anime4K 2x CNN (S) + Lanczos-2 final composition.</summary>
    Anime4KS = 1,

    /// <summary>Anime4K 2x CNN (M) + Lanczos-2 final composition.</summary>
    Anime4KM = 2,

    /// <summary>Anime4K 2x CNN (L) + Lanczos-2 final composition.</summary>
    Anime4KL = 3,

    /// <summary>Anime4K 2x CNN (VL, experimental high-quality) + Lanczos-2.</summary>
    Anime4KVL = 4,

    /// <summary>User-provided TOML + SPIR-V pipeline.</summary>
    Custom = 5
}

public static class EffectModeExtensions
{
    public static bool IsAnime4K(this EffectMode mode) => mode != EffectMode.Bilinear;

    /// <summary>Manifest key for the shared renderer (s/m/l).</summary>
    public static string VariantKey(this EffectMode mode) => mode switch
    {
        EffectMode.Anime4KS => "s",
        EffectMode.Anime4KM => "m",
        EffectMode.Anime4KL => "l",
        EffectMode.Anime4KVL => "vl",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "bilinear has no Anime4K variant")
    };

    /// <summary>Stable token used in logs and on the command line.</summary>
    public static string CliName(this EffectMode mode) => mode switch
    {
        EffectMode.Bilinear => "bilinear",
        EffectMode.Anime4KS => "anime4k-s",
        EffectMode.Anime4KM => "anime4k-m",
        EffectMode.Anime4KL => "anime4k-l",
        EffectMode.Anime4KVL => "anime4k-vl",
        EffectMode.Custom => "custom",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
    };

    /// <summary>User-facing label for the UI and logs.</summary>
    public static string DisplayName(this EffectMode mode) => mode switch
    {
        EffectMode.Bilinear => "双线性（M0a）",
        EffectMode.Anime4KS => "Anime4K S 2× + Lanczos",
        EffectMode.Anime4KM => "Anime4K M 2× + Lanczos",
        EffectMode.Anime4KL => "Anime4K L 2× + Lanczos",
        EffectMode.Anime4KVL => "Anime4K VL 2× + Lanczos",
        EffectMode.Custom => "自定义 effect",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
    };

    /// <summary>
    /// Parses the CLI vocabulary. <c>anime4k</c> stays an alias for S so the
    /// M0b/M1a command lines continue to work.
    /// </summary>
    public static bool TryParse(string? value, out EffectMode mode)
    {
        mode = EffectMode.Anime4KS;
        if (string.IsNullOrWhiteSpace(value)) return false;

        switch (value.Trim().ToLowerInvariant())
        {
            case "bilinear":
                mode = EffectMode.Bilinear;
                return true;
            case "anime4k":
            case "anime4k-s":
            case "anime4ks":
            case "s":
                mode = EffectMode.Anime4KS;
                return true;
            case "anime4k-m":
            case "anime4km":
            case "m":
                mode = EffectMode.Anime4KM;
                return true;
            case "anime4k-l":
            case "anime4kl":
            case "l":
                mode = EffectMode.Anime4KL;
                return true;
            case "anime4k-vl":
            case "anime4kvl":
            case "vl":
                mode = EffectMode.Anime4KVL;
                return true;
            default:
                return false;
        }
    }
}