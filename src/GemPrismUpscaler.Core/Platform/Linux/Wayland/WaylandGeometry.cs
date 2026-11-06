using NWayland.Protocols.Wayland;

namespace GemPrismUpscaler.Core.Platform.Linux.Wayland;

/// <summary>
/// Pure geometry helpers for Wayland outputs. Kept separate from the Wayland
/// client so scale/rotation maths can be unit tested without a compositor.
/// </summary>
public static class WaylandGeometry
{
    /// <summary>
    /// Compute the output scale from logical and physical pixel sizes. On
    /// 90/270-degree outputs logical width maps to physical height, so the
    /// ratio must be computed per axis and averaged.
    /// </summary>
    public static double ComputeScale(int logicalWidth, int logicalHeight,
        int physicalWidth, int physicalHeight,
        WlOutput.TransformEnum transform, int integerScaleFallback = 1)
    {
        if (logicalWidth <= 0 || logicalHeight <= 0 ||
            physicalWidth <= 0 || physicalHeight <= 0)
            return Math.Max(1, integerScaleFallback);

        bool rotated = transform is WlOutput.TransformEnum.k_90 or
            WlOutput.TransformEnum.k_270 or WlOutput.TransformEnum.Flipped90 or
            WlOutput.TransformEnum.Flipped270;

        double sx = rotated
            ? (double)physicalHeight / logicalWidth
            : (double)physicalWidth / logicalWidth;
        double sy = rotated
            ? (double)physicalWidth / logicalHeight
            : (double)physicalHeight / logicalHeight;

        if (sx <= 0 || sy <= 0) return Math.Max(1, integerScaleFallback);
        return (sx + sy) / 2.0;
    }

    /// <summary>Convert a physical-pixel rectangle to layer-surface logical coordinates.</summary>
    public static (int X, int Y, int Width, int Height) PhysicalToLogical(
        int x, int y, int width, int height, double scale,
        int logicalWidth, int logicalHeight)
    {
        if (scale <= 0) scale = 1;
        int lx = (int)Math.Round(x / scale);
        int ly = (int)Math.Round(y / scale);
        int lw = Math.Max(1, (int)Math.Round(width / scale));
        int lh = Math.Max(1, (int)Math.Round(height / scale));
        lx = Math.Clamp(lx, 0, Math.Max(0, logicalWidth - 1));
        ly = Math.Clamp(ly, 0, Math.Max(0, logicalHeight - 1));
        lw = Math.Min(lw, logicalWidth - lx);
        lh = Math.Min(lh, logicalHeight - ly);
        return (lx, ly, lw, lh);
    }
}
