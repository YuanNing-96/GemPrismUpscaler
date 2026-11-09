using System.Runtime.InteropServices;
using GemPrismUpscaler.Core.Diagnostics;
using GemPrismUpscaler.Core.Platform.Linux.X11;

namespace GemPrismUpscaler.Core.Platform.Linux.Nested;

/// <summary>
/// Reads and changes the ICCCM <c>WM_STATE</c> of a window on the nested
/// XWayland server.
///
/// A game may minimize one of its own windows (the Endfield launcher does this
/// as soon as the game starts). wlroots/sway sets <c>WM_STATE=IconicState</c>
/// but keeps the surface mapped on the output; the client therefore stops
/// painting while the output still contains the (now stale, usually black)
/// window. sway has no minimize concept we can mirror from its tree, so this
/// probe is the only way to tell "minimized" from "still rendering".
/// </summary>
internal sealed class NestedWindowProbe : IDisposable
{
    private const int NormalState = 1;
    private const int IconicState = 3;

    private readonly X11Display _display;
    private readonly ulong _wmStateAtom;
    private bool _disposed;

    public NestedWindowProbe(string displayName)
    {
        _display = new X11Display(displayName);
        _wmStateAtom = X11Native.XInternAtom(_display.Handle, "WM_STATE", 0);
    }

    public bool IsIconified(ulong xid) => ReadState(xid) == IconicState;

    /// <summary>
    /// Deiconifies a window the way an X11 window manager would. wlroots keeps
    /// the surface mapped while iconic, so a plain XMapWindow is a no-op and
    /// the client sees no map transition. Unmap+map forces the client to
    /// observe a real transition and repaint.
    /// </summary>
    public bool Deiconify(ulong xid)
    {
        if (xid == 0 || _wmStateAtom == 0) return false;
        try
        {
            WriteState(xid, NormalState);
            X11Native.XUnmapWindow(_display.Handle, xid);
            _display.Sync();
            Thread.Sleep(30);
            X11Native.XMapRaised(_display.Handle, xid);
            _display.Sync();
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"nested window probe: deiconify 0x{xid:x} failed: {ex.Message}");
            return false;
        }
    }

    private int? ReadState(ulong xid)
    {
        if (_disposed || xid == 0 || _wmStateAtom == 0) return null;
        int status = X11Native.XGetWindowProperty(_display.Handle, xid, _wmStateAtom,
            0, 2, 0, 0, out ulong actualType, out int actualFormat, out ulong nitems,
            out _, out IntPtr prop);
        if (status != 0 || prop == IntPtr.Zero) return null;
        try
        {
            if (actualFormat != 32 || nitems < 1) return null;
            return Marshal.ReadInt32(prop);
        }
        finally
        {
            X11Native.XFree(prop);
        }
    }

    private void WriteState(ulong xid, int state)
    {
        ulong[] values = { (ulong)state, 0 };
        X11Native.XChangeProperty(_display.Handle, xid, _wmStateAtom, _wmStateAtom,
            32, X11Native.PropModeReplace, values, 2);
        _display.Sync();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _display.Dispose();
    }
}
