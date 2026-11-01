namespace GemPrismUpscaler.Core.Diagnostics;

public static class Log
{
    private static readonly object Gate = new();
    private static string? _path;

    public static string Path
    {
        get
        {
            if (_path is not null) return _path;
            var env = Environment.GetEnvironmentVariable("GEMPRISM_LOG");
            _path = string.IsNullOrWhiteSpace(env)
                ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GemPrismUpscaler", "app.log")
                : env;
            return _path;
        }
    }

    public static void Write(string message)
    {
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}";
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