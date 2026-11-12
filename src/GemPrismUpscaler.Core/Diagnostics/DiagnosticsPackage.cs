using System.IO.Compression;
using System.Text;
using GemPrismUpscaler.Core.Configuration;
using GemPrismUpscaler.Core.Platform.Linux.Nested;
using GemPrismUpscaler.Core.Platform.Linux.X11;

namespace GemPrismUpscaler.Core.Diagnostics;

/// <summary>
/// Builds a user-shareable diagnostics zip. Paths, the user name and the host
/// name are redacted; window titles/game names in the raw log may remain and
/// the generated README asks the user to review the file before sharing.
/// </summary>
public static class DiagnosticsPackage
{
    public static string Create(string? outputDirectory = null)
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string work = Path.Combine(Path.GetTempPath(), $"gemprism-diagnostics-{stamp}-{Environment.ProcessId}");
        Directory.CreateDirectory(work);

        try
        {
            WriteEnvironment(Path.Combine(work, "environment.txt"));
            WriteConfig(Path.Combine(work, "config.toml"));
            WriteLog(Path.Combine(work, "app.log"));
            WriteErrors(Path.Combine(work, "recent-errors.txt"));
            WriteNestedState(Path.Combine(work, "nested.txt"));
            File.WriteAllText(Path.Combine(work, "README.txt"), Readme(), new UTF8Encoding(false));

            string dir = outputDirectory ?? Path.Combine(AppPaths.ConfigDirectory, "diagnostics");
            Directory.CreateDirectory(dir);
            string zip = Path.Combine(dir, $"gemprism-diagnostics-{stamp}.zip");
            if (File.Exists(zip)) File.Delete(zip);
            ZipFile.CreateFromDirectory(work, zip, CompressionLevel.Fastest, includeBaseDirectory: false);
            return zip;
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { }
        }
    }

    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string user = Environment.UserName;
        string host = Environment.MachineName;
        return text
            .Replace(home, "$HOME", StringComparison.OrdinalIgnoreCase)
            .Replace(user, "$USER", StringComparison.OrdinalIgnoreCase)
            .Replace(host, "$HOST", StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteEnvironment(string path)
    {
        var text = new StringBuilder();
        text.AppendLine($"GemPrismUpscaler {AppInfo.DisplayName}");
        text.AppendLine($"generated={DateTimeOffset.Now:O}");
        text.AppendLine($"os={Environment.OSVersion}");
        text.AppendLine($"runtime={Environment.Version}");
        text.AppendLine($"pid={Environment.ProcessId}");
        text.AppendLine($"cwd={Redact(Environment.CurrentDirectory)}");
        text.AppendLine($"session_type={Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "-"}");
        text.AppendLine($"desktop={Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "-"}");
        text.AppendLine($"wayland={Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") ?? "-"}");
        text.AppendLine($"display={Environment.GetEnvironmentVariable("DISPLAY") ?? "-"}");
        text.AppendLine($"runtime_dir={Redact(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? "-")}");
        text.AppendLine($"config={Redact(AppPaths.ConfigPath)}");
        text.AppendLine($"log={Redact(Log.Path)}");
        text.AppendLine($"developer={DiagnosticsRuntime.DeveloperMode} validation={DiagnosticsRuntime.ValidationLayerEnabled}");

        var config = ConfigStore.Load();
        text.AppendLine($"config_schema={config.Config.Schema} recovered={config.RecoveredFromError} " +
                        $"warnings={config.Warnings.Count}");
        foreach (string warning in config.Warnings)
            text.AppendLine($"config_warning={Redact(warning)}");

        text.AppendLine();
        text.AppendLine("[environment]");
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            string key = (string)entry.Key;
            if (!key.StartsWith("GEMPRISM_", StringComparison.Ordinal) &&
                key is not ("XDG_SESSION_TYPE" or "XDG_CURRENT_DESKTOP" or "WAYLAND_DISPLAY" or "DISPLAY" or "XDG_RUNTIME_DIR"))
                continue;
            text.AppendLine($"{key}={Redact(entry.Value?.ToString() ?? string.Empty)}");
        }

        text.AppendLine();
        text.AppendLine("[monitors]");
        try
        {
            using var display = new X11Display();
            foreach (var monitor in X11MonitorEnumerator.List(display))
                text.AppendLine($"{monitor.Name} {monitor.Width}x{monitor.Height} " +
                                $"+{monitor.X}+{monitor.Y} primary={monitor.Primary} " +
                                $"refresh={monitor.RefreshMilliHz}mHz");
        }
        catch (Exception ex)
        {
            text.AppendLine($"monitor probe failed: {ex.GetType().Name}: {ex.Message}");
        }

        File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
    }

    private static void WriteConfig(string path)
    {
        try
        {
            string source = File.Exists(AppPaths.ConfigPath)
                ? File.ReadAllText(AppPaths.ConfigPath)
                : "# config.toml does not exist; defaults are in use\n";
            File.WriteAllText(path, Redact(source), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            File.WriteAllText(path, $"# failed to read config: {ex.Message}\n", new UTF8Encoding(false));
        }
    }

    private static void WriteLog(string path)
    {
        try
        {
            if (!File.Exists(Log.Path))
            {
                File.WriteAllText(path, "# no log file yet\n", new UTF8Encoding(false));
                return;
            }
            // Keep the package small; the tail is what matters for a failure.
            var info = new FileInfo(Log.Path);
            const long max = 5 * 1024 * 1024;
            string text;
            if (info.Length > max)
            {
                using var stream = new FileStream(Log.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                stream.Seek(-max, SeekOrigin.End);
                using var reader = new StreamReader(stream);
                text = reader.ReadToEnd();
            }
            else
            {
                text = File.ReadAllText(Log.Path);
            }
            File.WriteAllText(path, Redact(text), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            File.WriteAllText(path, $"# failed to read log: {ex.Message}\n", new UTF8Encoding(false));
        }
    }

    private static void WriteErrors(string path)
    {
        var text = new StringBuilder();
        foreach (var error in ErrorBus.Recent)
        {
            text.AppendLine($"{error.Time:O} {error.Severity} {error.Kind} {error.Source}: " +
                            $"{Redact(error.Message)}");
            if (!string.IsNullOrWhiteSpace(error.Detail))
                text.AppendLine($"  {Redact(error.Detail)}");
        }
        if (text.Length == 0) text.AppendLine("(no recent errors)");
        File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
    }

    private static void WriteNestedState(string path)
    {
        var text = new StringBuilder();
        try
        {
            string state = NestedSessionState.DefaultPath;
            text.AppendLine($"state_file={Redact(state)} exists={File.Exists(state)}");
            if (File.Exists(state))
                text.AppendLine(Redact(File.ReadAllText(state)));
        }
        catch (Exception ex)
        {
            text.AppendLine($"nested state read failed: {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            string tmp = Path.GetTempPath();
            text.AppendLine();
            text.AppendLine("[stale nested directories]");
            foreach (string dir in Directory.GetDirectories(tmp, "gemprism-nested-*"))
                text.AppendLine(Redact(dir));
        }
        catch (Exception ex)
        {
            text.AppendLine($"stale scan failed: {ex.Message}");
        }
        File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
    }

    private static string Readme()
        => """
           GemPrismUpscaler diagnostics
           ============================

           This zip contains:
           - environment.txt: version, session, config path, monitors, relevant env vars.
           - config.toml: the user config, with home directory/user/host redacted.
           - app.log: recent application log (tail, max ~5 MB), redacted for paths/users.
           - recent-errors.txt: recent user-visible errors.
           - nested.txt: nested session state and stale runtime directories.

           Review app.log and config.toml before sharing: window titles/game names
           may still appear in the raw log even though paths/users are redacted.
           """;
}
