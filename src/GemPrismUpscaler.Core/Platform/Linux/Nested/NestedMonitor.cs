using GemPrismUpscaler.Core.Geometry;
using GemPrismUpscaler.Core.Platform.Linux.X11;

namespace GemPrismUpscaler.Core.Platform.Linux.Nested;

public sealed record NestedMonitorInfo(string Name, int X, int Y, int Width, int Height);

/// <summary>
/// Resolve the KDE/XWayland output that will host the nested presentation and
/// use its real physical mode as the fixed nested desktop size.
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

        return new NestedMonitorInfo(selected.Name, selected.X, selected.Y,
            selected.Width, selected.Height);
    }
}
