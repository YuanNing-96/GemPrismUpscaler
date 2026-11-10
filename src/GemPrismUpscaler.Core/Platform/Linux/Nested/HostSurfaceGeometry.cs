namespace GemPrismUpscaler.Core.Platform.Linux.Nested;

/// <summary>
/// Converts between the two coordinate spaces of a host surface on Wayland:
///
/// <list type="bullet">
/// <item>surface-local / logical space: what the compositor configures and what
/// pointer events and <c>wp_pointer_warp_v1</c> use;</item>
/// <item>buffer / presentation space: the Vulkan swapchain extent, which the
/// session blits into and where pointer coordinates are consumed.</item>
/// </list>
///
/// They differ by the output scale (1.5 on the user's 4K monitors). On X11 the
/// two spaces are identical, so the X11 host never calls these helpers.
/// </summary>
public static class HostSurfaceGeometry
{
    public const double MinimumScale = 0.25;
    public const double MaximumScale = 8.0;

    /// <summary>
    /// A compositor scale of zero or a nonsense value (some outputs report 0
    /// before their first <c>wl_output.scale</c> event) must not divide by
    /// zero, so fall back to an unscaled surface.
    /// </summary>
    public static double Sanitize(double scale)
        => double.IsFinite(scale) && scale >= MinimumScale && scale <= MaximumScale
            ? scale
            : 1.0;

    /// <summary>Presentation pixels to surface-local units.</summary>
    public static int ToLogical(int pixels, double scale)
        => Math.Max(1, (int)Math.Round(pixels / Sanitize(scale)));

    /// <summary>Surface-local units to presentation pixels.</summary>
    public static int ToPixels(int logical, double scale)
        => Math.Max(1, (int)Math.Round(logical * Sanitize(scale)));

    /// <summary>
    /// Convert one pointer axis from surface-local to presentation pixels and
    /// keep the result inside the surface. Pointer events arrive in logical
    /// units even when the buffer is larger, so the multiplication is what
    /// keeps hover and click on the same pixel.
    /// </summary>
    public static int LocalToPixel(int local, double scale, int pixels)
    {
        int value = (int)Math.Round(local * Sanitize(scale));
        return Math.Clamp(value, 0, Math.Max(0, pixels - 1));
    }

    /// <summary>Convert one presentation pixel to the surface-local coordinate.</summary>
    public static int PixelToLocal(int pixel, double scale, int logical)
    {
        int value = (int)Math.Round(pixel / Sanitize(scale));
        return Math.Clamp(value, 0, Math.Max(0, logical - 1));
    }
}