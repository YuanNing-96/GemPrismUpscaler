using System.Diagnostics;
using GemPrismUpscaler.Core.Diagnostics;

namespace GemPrismUpscaler.Core.Platform.Linux.Nested;

/// <summary>
/// Development probe for <see cref="ToplevelCapture"/>: runs inside a nested
/// session, finds the first game toplevel, captures frames and writes the last
/// one as PPM. Used to validate the ext-image-copy-capture path without the
/// GUI or the Vulkan blitter.
/// </summary>
public static class NestedCaptureProbe
{
    public static int Run(NestedSession session, string? appId, string? title,
        string outPath, int frames)
    {
        if (string.IsNullOrWhiteSpace(session.SwaySocket))
        {
            Console.Error.WriteLine("probe: session has no sway socket");
            return 1;
        }

        var ipc = SwayIpc.Connect(session.SwaySocket);
        using var tracker = new NestedWindowTracker(ipc, SwayConfig.DefaultOutput, session.Token,
            session.CommandPid, null, ownsIpc: true);

        var wait = Stopwatch.StartNew();
        while (wait.ElapsedMilliseconds < 20000)
        {
            tracker.Poll();
            if (tracker.AllWindows.Count > 0) break;
            Thread.Sleep(200);
        }
        if (tracker.AllWindows.Count == 0)
        {
            Console.Error.WriteLine("probe: no game toplevel appeared");
            return 1;
        }

        foreach (var window in tracker.AllWindows)
            Console.WriteLine($"probe window id={window.Id} app_id='{window.AppId}' " +
                              $"title='{window.Name}' rect={window.Rect} " +
                              $"requested={window.RequestedWidth}x{window.RequestedHeight} " +
                              $"fullscreen={window.FullscreenMode}");

        NestedWindowInfo? target = tracker.AllWindows.FirstOrDefault(w =>
            (string.IsNullOrEmpty(appId) || string.Equals(w.AppId, appId, StringComparison.Ordinal)) &&
            (string.IsNullOrEmpty(title) || string.Equals(w.Name, title, StringComparison.Ordinal)));
        target ??= tracker.AllWindows.FirstOrDefault(w => w.IsIndependentWindow)
                   ?? tracker.AllWindows[0];

        Console.WriteLine($"probe target id={target.Id} app_id='{target.AppId}' title='{target.Name}'");
        using var capture = new ToplevelCapture(session.WaylandDisplay!, target.AppId, target.Name);
        Console.WriteLine($"probe capture {capture.Width}x{capture.Height} stride={capture.Stride} " +
                          $"format-source bpp={capture.SourceIsRgb}");

        var clock = Stopwatch.StartNew();
        int ok = 0;
        int timeouts = 0;
        bool wrote = false;
        for (int i = 0; i < frames; i++)
        {
            bool got = false;
            var frameWait = Stopwatch.StartNew();
            while (frameWait.ElapsedMilliseconds < 2000)
            {
                if (capture.TryCapture(out _, 0)) { got = true; break; }
                Thread.Sleep(1);
            }
            if (!got)
            {
                timeouts++;
                if (ok > 0 || timeouts > 2) break;
                continue;
            }
            ok++;
            if (i % 30 == 0)
                Console.WriteLine($"probe frame {i} ok={ok} elapsed={clock.ElapsedMilliseconds}ms");
        }
        double seconds = clock.Elapsed.TotalSeconds;
        Console.WriteLine($"probe captured={ok} timeouts={timeouts} in {seconds:F2}s " +
                          $"fps={(seconds > 0 ? ok / seconds : 0):F1}");

        if (ok > 0)
        {
            wrote = WritePpm(outPath, capture.Pixels, capture.Width, capture.Height, capture.Stride);
            Console.WriteLine(wrote ? $"probe wrote {outPath}" : "probe PPM write failed");
        }
        return ok > 0 && wrote ? 0 : 1;
    }

    private static bool WritePpm(string path, ReadOnlySpan<byte> bgra, int width, int height,
        int stride)
    {
        try
        {
            using var file = File.Create(path);
            using var writer = new BinaryWriter(file);
            writer.Write(System.Text.Encoding.ASCII.GetBytes($"P6\n{width} {height}\n255\n"));
            for (int y = 0; y < height; y++)
            {
                var row = bgra.Slice(y * stride, stride);
                for (int x = 0; x < width; x++)
                {
                    int o = x * 4;
                    writer.Write(row[o + 2]);
                    writer.Write(row[o + 1]);
                    writer.Write(row[o]);
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"probe: PPM write failed: {ex.Message}");
            return false;
        }
    }
}