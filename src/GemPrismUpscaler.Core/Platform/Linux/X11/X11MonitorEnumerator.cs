using System.Runtime.InteropServices;
using GemPrismUpscaler.Core.Diagnostics;
using GemPrismUpscaler.Core.Geometry;

namespace GemPrismUpscaler.Core.Platform.Linux.X11;

/// <summary>
/// XRandR 1.5 monitor enumeration. XWayland exposes one monitor per configured
/// output with physical-pixel geometry, so the same rectangles can be used for
/// the overlay window, swapchain, layout and input mapping.
/// </summary>
public static class X11MonitorEnumerator
{
    public static List<MonitorInfo> List(X11Display display)
    {
        var result = new List<MonitorInfo>();
        IntPtr monitorsPtr = IntPtr.Zero;
        IntPtr resourcesPtr = IntPtr.Zero;
        try
        {
            monitorsPtr = X11Native.XRRGetMonitors(display.Handle, display.Root, 1, out int count);
            resourcesPtr = X11Native.XRRGetScreenResourcesCurrent(display.Handle, display.Root);
            if (monitorsPtr != IntPtr.Zero && count > 0)
            {
                int size = Marshal.SizeOf<X11Native.XRRMonitorInfo>();
                for (int i = 0; i < count; i++)
                {
                    var info = Marshal.PtrToStructure<X11Native.XRRMonitorInfo>(
                        IntPtr.Add(monitorsPtr, i * size));
                    if (info.Width <= 0 || info.Height <= 0) continue;
                    string name = GetAtomName(display, info.Name) ?? $"Monitor-{i}";
                    int refresh = FindRefreshMilliHz(display, resourcesPtr,
                        info.X, info.Y, info.Width, info.Height);
                    result.Add(new MonitorInfo(name, info.X, info.Y, info.Width, info.Height,
                        info.Primary != 0, refresh));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write($"XRandR monitor enumeration failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (monitorsPtr != IntPtr.Zero)
            {
                try { X11Native.XRRFreeMonitors(monitorsPtr); } catch { }
            }
            if (resourcesPtr != IntPtr.Zero)
            {
                try { X11Native.XRRFreeScreenResources(resourcesPtr); } catch { }
            }
        }

        if (result.Count == 0)
        {
            result.Add(new MonitorInfo("default", 0, 0, display.Width, display.Height, true));
        }
        return result;
    }

    /// <summary>
    /// Current mode refresh in milli-Hz for the CRTC covering the monitor
    /// rectangle. Returns 0 when the X server cannot map rectangle to mode.
    /// </summary>
    private static int FindRefreshMilliHz(X11Display display, IntPtr resourcesPtr,
        int x, int y, int width, int height)
    {
        if (resourcesPtr == IntPtr.Zero) return 0;
        var resources = Marshal.PtrToStructure<X11Native.XRRScreenResources>(resourcesPtr);
        if (resources.NCrtc <= 0 || resources.Crtcs == IntPtr.Zero) return 0;

        int crtcSize = Marshal.SizeOf<ulong>();
        for (int i = 0; i < resources.NCrtc; i++)
        {
            ulong crtc = (ulong)Marshal.ReadInt64(resources.Crtcs, i * crtcSize);
            if (crtc == 0) continue;
            IntPtr infoPtr = IntPtr.Zero;
            try
            {
                infoPtr = X11Native.XRRGetCrtcInfo(display.Handle, resourcesPtr, crtc);
                if (infoPtr == IntPtr.Zero) continue;
                var info = Marshal.PtrToStructure<X11Native.XRRCrtcInfo>(infoPtr);
                // XRandR reports a rotated CRTC with swapped width/height.
                bool sameOrientation = info.X == x && info.Y == y &&
                    info.Width == (uint)width && info.Height == (uint)height;
                bool rotated = info.X == x && info.Y == y &&
                    info.Width == (uint)height && info.Height == (uint)width;
                if (!sameOrientation && !rotated) continue;
                if (info.Mode == 0) return 0;
                return FindModeRefreshMilliHz(resourcesPtr, info.Mode);
            }
            finally
            {
                if (infoPtr != IntPtr.Zero)
                {
                    try { X11Native.XRRFreeCrtcInfo(infoPtr); } catch { }
                }
            }
        }
        return 0;
    }

    private static int FindModeRefreshMilliHz(IntPtr resourcesPtr, ulong modeId)
    {
        var resources = Marshal.PtrToStructure<X11Native.XRRScreenResources>(resourcesPtr);
        if (resources.NMode <= 0 || resources.Modes == IntPtr.Zero) return 0;
        int modeSize = Marshal.SizeOf<X11Native.XRRModeInfo>();
        for (int i = 0; i < resources.NMode; i++)
        {
            var mode = Marshal.PtrToStructure<X11Native.XRRModeInfo>(
                IntPtr.Add(resources.Modes, i * modeSize));
            if (mode.Id != modeId) continue;
            if (mode.HTotal == 0 || mode.VTotal == 0 || mode.DotClock == 0) return 0;
            // dotClock is in Hz; hTotal*vTotal is the pixel clock divisor.
            double hz = (double)mode.DotClock / ((double)mode.HTotal * mode.VTotal);
            if (hz is < 1 or > 2000) return 0;
            return (int)Math.Round(hz * 1000.0);
        }
        return 0;
    }

    private static string? GetAtomName(X11Display display, ulong atom)
    {
        if (atom == 0) return null;
        IntPtr ptr = IntPtr.Zero;
        try
        {
            ptr = X11Native.XGetAtomName(display.Handle, atom);
            return ptr == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(ptr);
        }
        finally
        {
            if (ptr != IntPtr.Zero) X11Native.XFree(ptr);
        }
    }
}
