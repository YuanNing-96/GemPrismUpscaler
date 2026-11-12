using GemPrismUpscaler.Core.Configuration;
using GemPrismUpscaler.Core.Diagnostics;

namespace GemPrismUpscaler.Core.Rendering;

/// <summary>
/// Built-in Anime4K/双线性 definitions plus user effects loaded from
/// AppPaths.EffectsDirectory/*.toml. Reloading is explicit (settings/效果 tab).
/// </summary>
public static class EffectRegistry
{
    private static readonly object Gate = new();
    private static List<EffectDefinition>? _all;
    private static Dictionary<string, EffectDefinition>? _byId;

    public static IReadOnlyList<EffectDefinition> All
    {
        get
        {
            EnsureLoaded();
            return _all!;
        }
    }

    public static void Reload()
    {
        lock (Gate)
        {
            _all = null;
            _byId = null;
        }
        EnsureLoaded();
    }

    public static EffectDefinition? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        EnsureLoaded();
        return _byId!.TryGetValue(id.Trim(), out var effect) ? effect : null;
    }

    public static EffectDefinition BuiltIn(EffectMode mode)
    {
        EnsureLoaded();
        return _all!.First(e => e.Kind != EffectKind.Custom && e.Mode == mode);
    }

    public static EffectDefinition Resolve(string? id)
    {
        return Find(id) ?? BuiltIn(EffectMode.Anime4KS);
    }

    private static void EnsureLoaded()
    {
        if (_all is not null) return;
        lock (Gate)
        {
            if (_all is not null) return;
            var all = new List<EffectDefinition>();
            var byId = new Dictionary<string, EffectDefinition>(StringComparer.OrdinalIgnoreCase);

            AddBuiltIn(all, byId, new EffectDefinition
            {
                Id = "bilinear",
                DisplayName = "双线性",
                Kind = EffectKind.Bilinear,
                Mode = EffectMode.Bilinear
            });

            AddMode(all, byId, EffectMode.Anime4KS, "anime4k-s", "Anime4K S");
            AddMode(all, byId, EffectMode.Anime4KM, "anime4k-m", "Anime4K M");
            AddMode(all, byId, EffectMode.Anime4KL, "anime4k-l", "Anime4K L");
            AddMode(all, byId, EffectMode.Anime4KVL, "anime4k-vl", "Anime4K VL");

            LoadCustom(all, byId);

            _all = all;
            _byId = byId;
        }
    }

    private static void AddMode(List<EffectDefinition> all,
        Dictionary<string, EffectDefinition> byId, EffectMode mode, string id, string name)
    {
        Anime4KVariant? variant = null;
        string? unavailable = null;
        try
        {
            variant = Anime4KShaders.For(mode);
        }
        catch (Exception ex)
        {
            unavailable = ex.Message;
            Log.Warn($"effect '{id}' unavailable: {ex.Message}");
        }

        AddBuiltIn(all, byId, new EffectDefinition
        {
            Id = id,
            DisplayName = name,
            Kind = EffectKind.Anime4K,
            Mode = mode,
            Variant = variant,
            UnavailableReason = unavailable
        });
    }

    private static void AddBuiltIn(List<EffectDefinition> all,
        Dictionary<string, EffectDefinition> byId, EffectDefinition effect)
    {
        all.Add(effect);
        byId[effect.Id] = effect;
    }

    private static void LoadCustom(List<EffectDefinition> all,
        Dictionary<string, EffectDefinition> byId)
    {
        string directory = AppPaths.EffectsDirectory;
        if (!Directory.Exists(directory)) return;

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory, "*.toml", SearchOption.TopDirectoryOnly)
                .Concat(Directory.EnumerateDirectories(directory)
                    .SelectMany(sub => Directory.EnumerateFiles(sub, "*.toml", SearchOption.TopDirectoryOnly)))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log.Warn($"effect directory scan failed: {ex.Message}");
            return;
        }

        foreach (string file in files)
        {
            var effect = CustomEffectLoader.Load(file, out string? error);
            if (effect is null)
            {
                Log.Warn($"custom effect skipped ({file}): {error}");
                ErrorBus.Warning("effect", $"自定义 effect 加载失败：{Path.GetFileName(file)}", null, error);
                continue;
            }
            if (byId.ContainsKey(effect.Id))
            {
                Log.Warn($"custom effect '{effect.Id}' skipped: id already exists");
                continue;
            }
            byId[effect.Id] = effect;
            all.Add(effect);
            Log.Info($"custom effect loaded: {effect.Id} ({file})");
        }
    }
}
