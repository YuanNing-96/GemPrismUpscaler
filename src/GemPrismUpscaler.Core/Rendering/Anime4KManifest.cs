using System.Text.Json;

namespace GemPrismUpscaler.Core.Rendering;

/// <summary>
/// Manifest emitted by tools/build_shaders.py. It describes every Anime4K
/// variant as a small pass graph so the renderer does not hardcode the S layout.
/// </summary>
internal sealed class Anime4KManifest
{
    public string GeneratedBy { get; set; } = string.Empty;
    public List<string> VariantOrder { get; set; } = new();
    public Dictionary<string, Anime4KVariant> Variants { get; set; } = new();
}

internal sealed class Anime4KVariant
{
    public string DisplayName { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public int Radius { get; set; }
    public int TextureCount { get; set; }
    public int PushConstantBytes { get; set; }
    public List<Anime4KTexture> Textures { get; set; } = new();
    public string OutputTexture { get; set; } = string.Empty;
    public List<Anime4KPass> Passes { get; set; } = new();

    /// <summary>Set for custom effects; shader names are relative to this directory.</summary>
    internal string? ShaderDirectory { get; set; }

    /// <summary>Custom effect opt-out of dirty-rect rendering.</summary>
    internal bool RequiresFullFrame { get; set; }
}

internal sealed class Anime4KTexture
{
    public string Name { get; set; } = string.Empty;
    public int Scale { get; set; } = 1;
    public bool External { get; set; }
}

internal sealed class Anime4KPass
{
    public int Index { get; set; }
    public string Desc { get; set; } = string.Empty;
    public List<string> Inputs { get; set; } = new();
    public string Output { get; set; } = string.Empty;
    public int OutputScale { get; set; } = 1;
    public string Shader { get; set; } = string.Empty;
}

internal static class Anime4KShaders
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly Lazy<Anime4KManifest> LazyManifest = new(Load);

    public static Anime4KManifest Manifest => LazyManifest.Value;

    public static Anime4KVariant For(EffectMode effect)
    {
        string key = effect.VariantKey();
        if (!Manifest.Variants.TryGetValue(key, out var variant))
            throw new InvalidOperationException($"Anime4K variant '{key}' is missing from the manifest");
        return variant;
    }

    private static Anime4KManifest Load()
    {
        string json = VulkanUtil.LoadEmbeddedText("anime4k_manifest.json");
        var manifest = JsonSerializer.Deserialize<Anime4KManifest>(json, JsonOptions)
            ?? throw new InvalidOperationException("anime4k_manifest.json is empty");
        if (manifest.Variants.Count == 0)
            throw new InvalidOperationException("anime4k_manifest.json has no variants");
        return manifest;
    }
}