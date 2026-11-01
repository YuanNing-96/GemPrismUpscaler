namespace GemPrismUpscaler.Core.Geometry;

public enum ScalingMode
{
    Fit,
    Stretch
}

public readonly record struct RectI(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;

    public override string ToString() => $"{Width}x{Height}+{X}+{Y}";
}

public static class LayoutCalculator
{
    /// <summary>
    /// Computes where the source image is drawn on the output surface.
    /// Fit keeps aspect ratio and pillarboxes/letterboxes; Stretch fills output.
    /// </summary>
    public static RectI ComputeDestRect(int sourceWidth, int sourceHeight, int outputWidth, int outputHeight, ScalingMode mode)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || outputWidth <= 0 || outputHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceWidth), "dimensions must be positive");

        if (mode == ScalingMode.Stretch)
            return new RectI(0, 0, outputWidth, outputHeight);

        double sx = (double)outputWidth / sourceWidth;
        double sy = (double)outputHeight / sourceHeight;
        double scale = Math.Min(sx, sy);
        int w = Math.Max(1, (int)Math.Round(sourceWidth * scale));
        int h = Math.Max(1, (int)Math.Round(sourceHeight * scale));
        int x = (outputWidth - w) / 2;
        int y = (outputHeight - h) / 2;
        return new RectI(x, y, w, h);
    }
}

public readonly struct CoordinateMapper
{
    public RectI Dest { get; }
    public int SourceWidth { get; }
    public int SourceHeight { get; }

    public CoordinateMapper(RectI dest, int sourceWidth, int sourceHeight)
    {
        Dest = dest;
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
    }

    /// <summary>Maps an overlay-local point to source-window-local coordinates.</summary>
    public bool TryMap(int x, int y, out int sourceX, out int sourceY)
    {
        sourceX = 0;
        sourceY = 0;
        if (!Dest.Contains(x, y))
            return false;

        double fx = (double)(x - Dest.X) / Dest.Width;
        double fy = (double)(y - Dest.Y) / Dest.Height;
        sourceX = Math.Clamp((int)(fx * SourceWidth), 0, SourceWidth - 1);
        sourceY = Math.Clamp((int)(fy * SourceHeight), 0, SourceHeight - 1);
        return true;
    }
}