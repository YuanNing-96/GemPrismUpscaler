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
    private static readonly object Gate = new();
    private static string? _path;
    private static LogLevel _level = LogLevel.Info;

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
                var dir = System.IO.Path.GetDirectoryName(Path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(Path, line + Environment.NewLine);
            }
            Console.WriteLine(line);
        }
        catch
        {
            // Logging must never take down the upscaler.
        }
    }
}
