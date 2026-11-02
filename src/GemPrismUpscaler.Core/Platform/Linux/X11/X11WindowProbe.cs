using System.Runtime.InteropServices;

namespace GemPrismUpscaler.Core.Platform.Linux.X11;

/// <summary>
/// Cheap X11 window state queries used by the engine loop: is the source still
/// alive, is it viewable (not minimized), and does its process own the input focus.
/// </summary>
public static class X11WindowProbe
{
    public readonly record struct WindowState(bool Exists, bool Viewable, bool Hidden)
    {
        public static WindowState Gone => new(false, false, false);
    }

    public static WindowState GetState(X11Display display, ulong window)
    {
        if (X11Native.XGetWindowAttributes(display.Handle, window, out var attrs) == 0)
            return WindowState.Gone;

        bool viewable = attrs.MapState == X11Native.IsViewable;
        bool hidden = viewable && HasHiddenState(display, window);
        return new WindowState(true, viewable, hidden);
    }

    /// <summary>
    /// True when the input focus is the window itself or one of its descendants.
    /// XWayland reports the focused X11 window; native Wayland focus yields
    /// PointerRoot/None, which counts as "not focused".
    /// </summary>
    public static bool IsFocused(X11Display display, ulong window)
    {
        if (X11Native.XGetInputFocus(display.Handle, out ulong focus, out _) == 0)
            return false;
        if (focus == window) return true;
        if (focus is 0 or 1) return false;

        // Walk up from the focused window to the root; the source may be an ancestor.
        for (int depth = 0; depth < 32; depth++)
        {
            if (focus == 0 || focus == display.Root) return false;
            if (X11Native.XQueryTree(display.Handle, focus, out _, out ulong parent, out _, out _) == 0)
                return false;
            if (parent == window) return true;
            if (parent == 0 || parent == focus) return false;
            focus = parent;
        }
        return false;
    }

    /// <summary>
    /// Asks the window manager to activate (focus) the window via EWMH
    /// _NET_ACTIVE_WINDOW. XSetInputFocus alone is often ignored by KWin when the
    /// request comes from a background client (focus stealing prevention).
    /// </summary>
    public static void Activate(X11Display display, ulong window)
    {
        if (display.AtomNetActiveWindow == 0) return;
        var ev = new X11Native.XClientMessageEvent
        {
            Type = X11Native.ClientMessage,
            SendEvent = 1,
            Display = display.Handle,
            Window = window,
            MessageType = display.AtomNetActiveWindow,
            Format = 32,
            // 2 = pager/tool source indication, which bypasses focus-stealing
            // prevention; the overlay is a user-invoked tool, not a background app.
            Data0 = 2,
            Data1 = 0,
            Data2 = 0
        };
        X11Native.XSendEvent(display.Handle, display.Root, 0,
            X11Native.SubstructureRedirectMask | X11Native.SubstructureNotifyMask, ref ev);
        display.Flush();
    }

    private static bool HasHiddenState(X11Display display, ulong window)
    {
        if (display.AtomNetWmStateHidden == 0) return false;
        if (X11Native.XGetWindowProperty(display.Handle, window, display.AtomNetWmState, 0, 16, 0, 0,
                out _, out int format, out ulong nitems, out _, out IntPtr prop) != 0)
            return false;
        try
        {
            if (format != 32 || prop == IntPtr.Zero) return false;
            for (int i = 0; i < (int)nitems; i++)
            {
                long atom = Marshal.ReadInt64(prop, i * 8);
                if ((ulong)atom == display.AtomNetWmStateHidden) return true;
            }
            return false;
        }
        finally
        {
            if (prop != IntPtr.Zero) X11Native.XFree(prop);
        }
    }
}
