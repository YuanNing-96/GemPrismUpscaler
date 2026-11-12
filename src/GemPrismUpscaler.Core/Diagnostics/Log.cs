using GemPrismUpscaler.Core.Configuration;

namespace GemPrismUpscaler.Core.Diagnostics;

public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3,
    Off = 4
}

public static class Log
{
    private const long MaxBytes = 5 * 1024 * 1024;
    private const int MaxFiles = 3;

    private static readonly object Gate = new();
    private static string? _path;
    private static LogLevel _level = LogLevel.Info;
    private static long _writtenBytes;
    private static string? _fallbackPath;

    public static string Path
    {
        get
        {
            if (_path is not null) return _path;
            string? env = Environment.GetEnvironmentVariable("GEMPRISM_LOG");
            _path = string.IsNullOrWhiteSpace(env)
                ? Configuration.AppPaths.DefaultLogPath
                : env;
            return _path;
        }
    }

    public static LogLevel Level
    {
        get => _level;
        set => _level = value;
    }

    /// <summary>
    /// Applies the configured path/level. GEMPRISM_LOG and GEMPRISM_LOG_LEVEL
    /// remain the highest-priority overrides for debugging.
    /// </summary>
    public static void Configure(string? path, string? level)
    {
        string? envPath = Environment.GetEnvironmentVariable("GEMPRISM_LOG");
        string resolved = !string.IsNullOrWhiteSpace(envPath)
            ? envPath
            : !string.IsNullOrWhiteSpace(path)
                ? path
                : Configuration.AppPaths.DefaultLogPath;

        lock (Gate)
        {
            _path = resolved;
            _level = ParseLevel(level);
            _writtenBytes = 0;
            try
            {
                string? dir = System.IO.Path.GetDirectoryName(resolved);
                if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
                if (File.Exists(resolved))
                    _writtenBytes = new FileInfo(resolved).Length;
                if (_writtenBytes > MaxBytes) RotateLocked();
            }
            catch
            {
                // Logging must never take down the app.
            }
        }
    }

    public static LogLevel ParseLevel(string? value)
    {
        string? env = Environment.GetEnvironmentVariable("GEMPRISM_LOG_LEVEL");
        string text = (!string.IsNullOrWhiteSpace(env) ? env : value ?? string.Empty)
            .Trim().ToLowerInvariant();
        return text switch
        {
            "debug" or "verbose" => LogLevel.Debug,
            "warn" or "warning" => LogLevel.Warn,
            "error" => LogLevel.Error,
            "off" or "none" => LogLevel.Off,
            _ => LogLevel.Info
        };
    }

    public static bool IsEnabled(LogLevel level) => level >= _level && _level != LogLevel.Off;

    public static void Write(string message) => Info(message);

    public static void Debug(string message) => Write(LogLevel.Debug, message);

    public static void Info(string message) => Write(LogLevel.Info, message);

    public static void Warn(string message) => Write(LogLevel.Warn, message);

    public static void Error(string message, Exception? exception = null)
    {
        if (!IsEnabled(LogLevel.Error)) return;
        string text = exception is null
            ? message
            : _level <= LogLevel.Debug
                ? $"{message}\n{exception}"
                : $"{message} ({exception.GetType().Name}: {exception.Message})";
        Write(LogLevel.Error, text);
    }

    public static void Error(string message, string? detail)
    {
        if (!IsEnabled(LogLevel.Error)) return;
        Write(LogLevel.Error, string.IsNullOrWhiteSpace(detail) ? message : $"{message}\n{detail}");
    }

    /// <summary>
    /// Writes a compact header at the start of a GUI/session run so a shared
    /// log contains the version, mode, session type, config and args.
    /// </summary>
    public static void WriteSessionHeader(string mode, string[] args, AppConfig? config = null)
    {
        try
        {
            Write(LogLevel.Info, "=== session ===");
            Write(LogLevel.Info, $"version={AppInfo.Version} mode={mode} pid={Environment.ProcessId} " +
                                 $"started={DateTimeOffset.Now:O}");
            Write(LogLevel.Info, $"args={string.Join(' ', args)}");
            Write(LogLevel.Info, $"session={Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "-"} " +
                                 $"desktop={Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "-"} " +
                                 $"wayland={Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") ?? "-"} " +
                                 $"display={Environment.GetEnvironmentVariable("DISPLAY") ?? "-"}");
            Write(LogLevel.Info, $"config={Configuration.AppPaths.ConfigPath} " +
                                 $"schema={config?.Schema.ToString() ?? "-"} " +
                                 $"developer={DiagnosticsRuntime.DeveloperMode} " +
                                 $"validation={DiagnosticsRuntime.ValidationLayerEnabled}");
        }
        catch
        {
            // Header is best-effort.
        }
    }

    public static void Write(LogLevel level, string message)
    {
        if (!IsEnabled(level)) return;
        try
        {
            string prefix = level switch
            {
                LogLevel.Debug => "DBG",
                LogLevel.Warn => "WRN",
                LogLevel.Error => "ERR",
                _ => "INF"
            };
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{prefix}] {message}";
            lock (Gate)
            {
                EnsureWritableLocked();
                bool written = TryAppendLocked(line);
                if (!written)
                {
                    // Path may be read-only/full; fall back to temp once.
                    _fallbackPath ??= System.IO.Path.Combine(
                        System.IO.Path.GetTempPath(), "GemPrismUpscaler-fallback.log");
                    if (!string.Equals(_path, _fallbackPath, StringComparison.Ordinal))
                    {
                        _path = _fallbackPath;
                        _writtenBytes = 0;
                        TryAppendLocked(line);
                    }
                }
            }
            Console.WriteLine(line);
        }
        catch
        {
            // Logging must never take down the upscaler.
        }
    }

    private static void EnsureWritableLocked()
    {
        string path = Path;
        string? dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
        if (_writtenBytes > 0 && _writtenBytes > MaxBytes) RotateLocked();
    }

    private static bool TryAppendLocked(string line)
    {
        try
        {
            File.AppendAllText(Path, line + Environment.NewLine);
            _writtenBytes += line.Length + Environment.NewLine.Length;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void RotateLocked()
    {
        try
        {
            string path = Path;
            string oldest = $"{path}.{MaxFiles}";
            if (File.Exists(oldest)) File.Delete(oldest);
            for (int i = MaxFiles - 1; i >= 1; i--)
            {
                string from = $"{path}.{i}";
                string to = $"{path}.{i + 1}";
                if (File.Exists(from))
                {
                    if (File.Exists(to)) File.Delete(to);
                    File.Move(from, to);
                }
            }
            if (File.Exists(path))
            {
                string first = $"{path}.1";
                if (File.Exists(first)) File.Delete(first);
                File.Move(path, first);
            }
            _writtenBytes = 0;
        }
        catch
        {
            // If rotation fails, keep appending; a full disk is handled by the
            // fallback path on the next write.
        }
    }
}
