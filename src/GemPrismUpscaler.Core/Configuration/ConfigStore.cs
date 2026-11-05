using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
/// Loads and saves config.toml atomically. A broken file is backed up and the
/// app continues with defaults; it must never prevent startup.
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

        try
        {
            string text = File.ReadAllText(path, Encoding.UTF8);
            var config = TomlSerializer.Deserialize<AppConfig>(text, Options)
                         ?? throw new InvalidOperationException("配置内容为空");
            config.Normalize(warnings);

            if (!HotkeyBinding.TryParse(config.Hotkey.ToggleOverlay, out var hotkey, out string? hotkeyError))
            {
                warnings.Add($"hotkey.toggle_overlay 无效（{hotkeyError}），已回退为 {HotkeyBinding.Default}");
                config.Hotkey.ToggleOverlay = HotkeyBinding.Default.ToString();
            }
            else
            {
                config.Hotkey.ToggleOverlay = hotkey.ToString();
            }

            return new ConfigLoadResult(config, path, true, false, null, warnings);
        }
        catch (Exception ex)
        {
            string? backup = TryBackup(path);
            string message = backup is null
                ? $"读取配置失败：{ex.Message}"
                : $"读取配置失败：{ex.Message}；原文件已备份到 {backup}";
            var defaults = AppConfig.CreateDefault();
            defaults.Normalize(warnings);
            return new ConfigLoadResult(defaults, path, true, true, message, warnings);
        }
    }

    public static void Save(AppConfig config, string? path = null)
    {
        path ??= AppPaths.ConfigPath;
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
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    private static string? TryBackup(string path)
    {
        try
        {
            string backup = $"{path}.bad-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Copy(path, backup, overwrite: false);
            return backup;
        }
        catch
        {
            return null;
        }
    }
}
