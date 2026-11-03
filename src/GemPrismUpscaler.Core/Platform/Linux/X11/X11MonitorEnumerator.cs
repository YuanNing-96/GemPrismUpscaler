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
        try
        {
            monitorsPtr = X11Native.XRRGetMonitors(display.Handle, display.Root, 1, out int count);
            if (monitorsPtr != IntPtr.Zero && count > 0)
            {
                int size = Marshal.SizeOf<X11Native.XRRMonitorInfo>();
                for (int i = 0; i < count; i++)
                {
                    var info = Marshal.PtrToStructure<X11Native.XRRMonitorInfo>(
                        IntPtr.Add(monitorsPtr, i * size));
                    if (info.Width <= 0 || info.Height <= 0) continue;
                    string name = GetAtomName(display, info.Name) ?? $"Monitor-{i}";
                    result.Add(new MonitorInfo(name, info.X, info.Y, info.Width, info.Height, info.Primary != 0));
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
        }

        if (result.Count == 0)
        {
            result.Add(new MonitorInfo("default", 0, 0, display.Width, display.Height, true));
        }
        return result;
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
