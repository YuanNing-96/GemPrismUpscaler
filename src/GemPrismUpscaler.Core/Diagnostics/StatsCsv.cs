using GemPrismUpscaler.Core.Engine;

namespace GemPrismUpscaler.Core.Diagnostics;

/// <summary>
/// Optional CSV sink for the once-per-second metrics report. Enabled only when
/// GEMPRISM_STATS is set; a missing/unwritable path must never affect rendering.
/// </summary>
internal static class StatsCsv
{
    private static readonly object Gate = new();
    private static bool _headerChecked;

    public static void TryWrite(FrameReport report)
    {
        string? path = Environment.GetEnvironmentVariable("GEMPRISM_STATS");
        if (string.IsNullOrWhiteSpace(path)) return;

        lock (Gate)
        {
            try
            {
                string? directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                if (!_headerChecked)
                {
                    if (!File.Exists(path) || new FileInfo(path).Length == 0)
                        File.WriteAllText(path, FrameReport.CsvHeader + Environment.NewLine);
                    _headerChecked = true;
                }
                File.AppendAllText(path, report.ToCsvLine() + Environment.NewLine);
            }
            catch
            {
                // Metrics must never take down the upscaler.
            }
        }
    }
}