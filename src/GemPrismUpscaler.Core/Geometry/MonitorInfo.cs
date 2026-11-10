namespace GemPrismUpscaler.Core.Geometry;

/// <summary>
/// One XRandR monitor in X11 root coordinates. On XWayland these are physical
/// pixels, and each output's geometry already includes its scale and rotation.
/// </summary>
public readonly record struct MonitorInfo(
    string Name,
    int X,
    int Y,
    int Width,
    int Height,
    bool Primary,
    int RefreshMilliHz = 0)
{
    public RectI Bounds => new(X, Y, Width, Height);

    /// <summary>True when the X server reported a real refresh rate.</summary>
    public bool HasRefresh => RefreshMilliHz > 0;

    public override string ToString() =>
        $"{Name} {Width}x{Height}+{X}+{Y}" +
        (HasRefresh ? $"@{RefreshMilliHz / 1000.0:0.###}Hz" : string.Empty) +
        (Primary ? " (primary)" : string.Empty);
}

public static class MonitorSelector
{
    /// <summary>
    /// Picks the monitor for a source window. An explicit preferred name wins;
    /// otherwise the largest intersection, then the monitor containing the
    /// window centre, then the primary monitor. Never returns null when the
    /// list is non-empty.
    /// </summary>
    public static MonitorInfo? Select(
        IReadOnlyList<MonitorInfo> monitors,
        RectI source,
        string? preferredName = null)
    {
        if (monitors.Count == 0) return null;

        if (!string.IsNullOrWhiteSpace(preferredName))
        {
            foreach (var monitor in monitors)
            {
                if (string.Equals(monitor.Name, preferredName, StringComparison.OrdinalIgnoreCase))
                    return monitor;
            }
        }

        MonitorInfo? best = null;
        long bestArea = 0;
        foreach (var monitor in monitors)
        {
            long area = IntersectionArea(source, monitor.Bounds);
            if (area > bestArea)
            {
                bestArea = area;
                best = monitor;
            }
        }
        if (bestArea > 0) return best;

        int centerX = source.X + source.Width / 2;
        int centerY = source.Y + source.Height / 2;
        foreach (var monitor in monitors)
        {
            if (monitor.Bounds.Contains(centerX, centerY)) return monitor;
        }

        foreach (var monitor in monitors)
        {
            if (monitor.Primary) return monitor;
        }
        return monitors[0];
    }

    public static long IntersectionArea(RectI a, RectI b)
    {
        int left = Math.Max(a.X, b.X);
        int top = Math.Max(a.Y, b.Y);
        int right = Math.Min(a.Right, b.Right);
        int bottom = Math.Min(a.Bottom, b.Bottom);
        if (right <= left || bottom <= top) return 0;
        return (long)(right - left) * (bottom - top);
    }
}
