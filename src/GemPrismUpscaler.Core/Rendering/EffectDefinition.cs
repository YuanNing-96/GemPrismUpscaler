namespace GemPrismUpscaler.Core.Rendering;

public enum EffectKind
{
    Bilinear,
    Anime4K,
    Custom
}

/// <summary>
/// A user-visible effect. Built-ins wrap the embedded Anime4K manifest; custom
/// effects wrap a TOML pass graph plus external SPIR-V files.
/// </summary>
public sealed class EffectDefinition
{
    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public EffectKind Kind { get; init; }
    public EffectMode Mode { get; init; }
    public int Radius => Variant?.Radius ?? 0;
    public bool RequiresFullFrame => Variant?.RequiresFullFrame ?? false;
    public bool IsBilinear => Kind == EffectKind.Bilinear;
    public bool IsCompute => Kind != EffectKind.Bilinear;
    public bool IsBuiltIn => Kind != EffectKind.Custom;
    public bool IsAvailable => Kind == EffectKind.Bilinear || Variant is not null;
    public string? UnavailableReason { get; init; }

    internal Anime4KVariant? Variant { get; init; }

    public override string ToString() => DisplayName;
}
