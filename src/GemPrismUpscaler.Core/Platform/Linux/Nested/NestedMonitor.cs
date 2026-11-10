using GemPrismUpscaler.Core.Diagnostics;
using GemPrismUpscaler.Core.Geometry;
using GemPrismUpscaler.Core.Platform.Linux.X11;

namespace GemPrismUpscaler.Core.Platform.Linux.Nested;

public sealed record NestedMonitorInfo(string Name, int X, int Y, int Width, int Height,
    int RefreshMilliHz = 0);

/// <summary>
/// Resolve the KDE/XWayland output that will host the nested presentation and
/// use its real physical mode (resolution and refresh) as the fixed nested
/// desktop size. The mode is a snapshot: a later hotplug change does not
/// resize the running virtual desktop.
/// </summary>
public static class NestedMonitor
{
    public static NestedMonitorInfo Resolve(string? requested)
    {
        using var display = new X11Display();
        var monitors = X11MonitorEnumerator.List(display);
        if (monitors.Count == 0)
            throw new InvalidOperationException("no XRandR monitor found");

        MonitorInfo selected = default;
        if (!string.IsNullOrWhiteSpace(requested))
        {
            selected = monitors.FirstOrDefault(m =>
                string.Equals(m.Name, requested, StringComparison.OrdinalIgnoreCase));
            if (selected.Width <= 0)
                throw new InvalidOperationException(
                    $"monitor '{requested}' not found; available: " +
                    string.Join(", ", monitors.Select(m => $"{m.Name}:{m.Width}x{m.Height}")));
        }
        else
        {
            selected = monitors.FirstOrDefault(m => m.Primary);
            if (selected.Width <= 0) selected = monitors[0];
        }

        int refresh = selected.RefreshMilliHz;
        if (refresh <= 0)
        {
            // Without a real refresh the headless output ends up at 0 Hz, and a
            // toplevel capture output (also 0 Hz) becomes the frame-pacing
            // output, which never reports presentation feedback (DXVK then
            // waits forever). 60 Hz keeps the session usable until the monitor
            // reports a proper mode.
            refresh = 60000;
            Log.Warn(
                $"nested monitor '{selected.Name}' did not report a refresh rate; " +
                "using 60Hz for the virtual output");
        }

        return new NestedMonitorInfo(selected.Name, selected.X, selected.Y,
            selected.Width, selected.Height, refresh);
    }
}