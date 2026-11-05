using System.Text;
using System.Text.Json;
using Tomlyn;

namespace GemPrismUpscaler.Core.Configuration;

public sealed record ConfigLoadResult(
    AppConfig Config,
    string Path,
    bool FileExisted,
    bool RecoveredFromError,
    string? Error,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Loads and saves config.toml atomically. A broken file (TOML syntax error,
/// invalid value or unknown key) is renamed to config.toml.bad-* and a clean
/// config is written back, so the next start is not broken again.
/// </summary>
public static class ConfigStore
{
    private static readonly TomlSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = TomlIgnoreCondition.WhenWritingNull
    };

    private static readonly HashSet<string> TopLevelKeys = new(StringComparer.Ordinal)
    {
        "schema",
        // Tomlyn serialises an empty profile list as "profile = []".
        "profile"
    };

    private static readonly Dictionary<string, HashSet<string>> SectionKeys = new(StringComparer.Ordinal)
    {
        ["general"] = new(StringComparer.Ordinal)
        {
            "second_instance", "close_action", "follow_focus", "remember_last",
            "auto_refresh_windows", "refresh_interval_ms"
        },
        ["hotkey"] = new(StringComparer.Ordinal) { "toggle_overlay" },
        ["logging"] = new(StringComparer.Ordinal) { "path", "level" },
        ["display"] = new(StringComparer.Ordinal) { "monitor" },
        ["effect"] = new(StringComparer.Ordinal) { "default", "scaling", "dirty_rect" },
        ["tray"] = new(StringComparer.Ordinal) { "enabled" },
        ["input"] = new(StringComparer.Ordinal) { "mode" },
        ["diagnostics"] = new(StringComparer.Ordinal) { "developer_mode" },
        ["last"] = new(StringComparer.Ordinal) { "effect", "scaling", "monitor" },
        ["profile"] = new(StringComparer.Ordinal)
        {
            "name", "class", "exe", "effect", "scaling", "monitor",
            "follow_focus", "dirty_rect", "hotkey"
        }
    };

    public static ConfigLoadResult Load(string? path = null)
    {
        path ??= AppPaths.ConfigPath;
        var warnings = new List<string>();

        if (!File.Exists(path))
        {
            var defaults = AppConfig.CreateDefault();
            defaults.Normalize(warnings);
            return new ConfigLoadResult(defaults, path, false, false, null, warnings);
        }

        string text;
        try
        {
            text = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            var defaults = AppConfig.CreateDefault();
            defaults.Normalize(warnings);
            return new ConfigLoadResult(defaults, path, true, true,
                $"读取配置失败：{ex.Message}", warnings);
        }

        try
        {
            var config = TomlSerializer.Deserialize<AppConfig>(text, Options)
                         ?? throw new InvalidOperationException("配置内容为空");
            config.Normalize(warnings);

            foreach (string unknown in DetectUnknownKeys(text))
                warnings.Add($"未知配置键 '{unknown}' 已忽略");

            if (!HotkeyBinding.TryParse(config.Hotkey.ToggleOverlay, out var hotkey, out string? hotkeyError))
            {
                warnings.Add($"hotkey.toggle_overlay 无效（{hotkeyError}），已回退为 {HotkeyBinding.Default}");
                config.Hotkey.ToggleOverlay = HotkeyBinding.Default.ToString();
            }
            else
            {
                config.Hotkey.ToggleOverlay = hotkey.ToString();
            }

            if (warnings.Count == 0)
                return new ConfigLoadResult(config, path, true, false, null, warnings);

            // Values/keys were invalid but the file was syntactically valid:
            // preserve the good fields, correct the bad ones, and write the
            // cleaned file back after renaming the original.
            string? backup = TryMoveToBackup(path);
            string? saveError = TrySave(config, path);
            string message = backup is null
                ? $"配置包含 {warnings.Count} 个无效项，原文件无法备份" +
                  (saveError is null ? "，已写回修正后的配置" : $"；写回失败：{saveError}")
                : $"配置包含 {warnings.Count} 个无效项，原文件已备份到 {backup}" +
                  (saveError is null ? "，已写回修正后的配置" : $"；写回失败：{saveError}");
            return new ConfigLoadResult(config, path, true, true, message, warnings);
        }
        catch (Exception ex)
        {
            // Syntax error: cannot isolate the bad line, so reset everything.
            string? backup = TryMoveToBackup(path);
            var defaults = AppConfig.CreateDefault();
            defaults.Normalize(warnings);
            string? saveError = TrySave(defaults, path);
            string message = backup is null
                ? $"读取配置失败：{ex.Message}；原文件无法备份" +
                  (saveError is null ? "，已写入默认配置" : $"；写入默认配置失败：{saveError}")
                : $"读取配置失败：{ex.Message}；原文件已备份到 {backup}" +
                  (saveError is null ? "，已写入默认配置" : $"；写入默认配置失败：{saveError}");
            return new ConfigLoadResult(defaults, path, true, true, message, warnings);
        }
    }

    public static void Save(AppConfig config, string? path = null)
    {
        string? error = TrySave(config, path ?? AppPaths.ConfigPath);
        if (error is not null)
            throw new IOException(error);
    }

    private static string? TrySave(AppConfig config, string path)
    {
        try
        {
            var warnings = new List<string>();
            config.Normalize(warnings);

            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

            string text = TomlSerializer.Serialize(config, Options);
            string temp = $"{path}.tmp-{Environment.ProcessId}";
            try
            {
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write(text);
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }

                if (OperatingSystem.IsLinux())
                {
                    try { File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
                    catch { /* best effort */ }
                }

                File.Move(temp, path, overwrite: true);
                return null;
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static string? TryMoveToBackup(string path)
    {
        try
        {
            string backup = $"{path}.bad-{DateTime.Now:yyyyMMdd-HHmmss-fff}";
            File.Move(path, backup);
            return backup;
        }
        catch
        {
            try
            {
                string backup = $"{path}.bad-{DateTime.Now:yyyyMMdd-HHmmss-fff}";
                File.Copy(path, backup, overwrite: false);
                File.Delete(path);
                return backup;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Lightweight line scan for unknown sections/keys. Tomlyn ignores unknown
    /// properties, so the serializer alone cannot tell us that a typo silently
    /// dropped a setting.
    /// </summary>
    private static List<string> DetectUnknownKeys(string text)
    {
        var unknown = new List<string>();
        string section = string.Empty;

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            if (line.StartsWith("[[", StringComparison.Ordinal))
            {
                int close = line.IndexOf("]]", StringComparison.Ordinal);
                section = close > 2 ? line[2..close].Trim() : string.Empty;
                if (!SectionKeys.ContainsKey(section))
                    unknown.Add($"[{section}]");
                continue;
            }

            if (line.Length > 0 && line[0] == '[')
            {
                int close = line.IndexOf(']');
                section = close > 1 ? line[1..close].Trim() : string.Empty;
                if (!SectionKeys.ContainsKey(section))
                    unknown.Add($"[{section}]");
                continue;
            }

            int equals = line.IndexOf('=');
            if (equals <= 0) continue;
            string key = line[..equals].Trim();
            if (key.Length == 0) continue;
            if (key.Length >= 2 && key[0] == '"' && key[^1] == '"')
                key = key[1..^1];

            HashSet<string>? allowed = section.Length == 0
                ? TopLevelKeys
                : SectionKeys.GetValueOrDefault(section);
            if (allowed is null || !allowed.Contains(key))
                unknown.Add(section.Length == 0 ? key : $"{section}.{key}");
        }

        return unknown;
    }
}
