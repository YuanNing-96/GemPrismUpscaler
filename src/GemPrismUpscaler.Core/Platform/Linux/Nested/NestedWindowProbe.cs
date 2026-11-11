using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using GemPrismUpscaler.Core.Diagnostics;
using GemPrismUpscaler.Core.Platform.Linux.X11;

namespace GemPrismUpscaler.Core.Platform.Linux.Nested;

/// <summary>
/// Reads and changes the ICCCM <c>WM_STATE</c> of a window on the nested
/// XWayland server, and listens for the window manager's minimize request.
///
/// A game may minimize one of its own windows (the Endfield launcher does this
/// as soon as the game starts, 秽翼 does it on Esc). ICCCM says a client asks
/// for that by sending a <c>WM_CHANGE_STATE</c> ClientMessage with
/// <c>IconicState</c> to the root window, which is exactly what Wine does
/// (verified with an X11 event listener on the nested display). sway has no
/// minimize concept and ignores the request, so no <c>WM_STATE=IconicState</c>
/// is ever set here and the client keeps the window mapped while it stops
/// painting. Watching the ClientMessage is therefore the only reliable
/// "the game asked to be minimized" signal inside the nested session; the
/// WM_STATE accessors remain for deiconify and for clients that set the
/// property themselves.
/// </summary>
internal sealed class NestedWindowProbe : IDisposable
{
    private const int NormalState = 1;
    private const int IconicState = 3;
    private const int ClientMessageType = 33;

    private readonly X11Display _display;
    private readonly ulong _wmStateAtom;
    private readonly ulong _changeStateAtom;
    private readonly List<ulong> _minimizeRequests = new();
    private bool _disposed;

    public NestedWindowProbe(string displayName)
    {
        _display = new X11Display(displayName);
        _wmStateAtom = X11Native.XInternAtom(_display.Handle, "WM_STATE", 0);
        _changeStateAtom = X11Native.XInternAtom(_display.Handle, "WM_CHANGE_STATE", 0);
        // XIconifyWindow sends the message with SubstructureNotifyMask, so
        // listening for substructure notifications is enough to get a copy.
        // Selecting SubstructureRedirectMask instead would steal the request
        // from sway, which stays the real window manager here.
        X11Native.XSelectInput(_display.Handle, _display.Root,
            X11Native.SubstructureNotifyMask);
        _display.Flush();
        DrainEvents();
    }

    /// <summary>
    /// Read the nested XWayland pointer position. Wine's X11 driver moves its
    /// internal pointer with XWarpPointer for dialogs; this does not produce a
    /// locked-pointer hint, so the session polls this instead.
    /// </summary>
    public bool TryGetPointerPosition(out int x, out int y)
    {
        if (X11Native.XQueryPointer(_display.Handle, _display.Root, out _, out _,
                out x, out y, out _, out _, out _) == 0)
        {
            x = 0;
            y = 0;
            return false;
        }
        return true;
    }

    /// <summary>
    /// Takes the next queued <c>WM_CHANGE_STATE(IconicState)</c> request, or
    /// returns false when no client asked to be minimized since the last call.
    /// </summary>
    public bool TryTakeMinimizeRequest(out ulong xid)
    {
        DrainEvents();
        if (_minimizeRequests.Count == 0)
        {
            xid = 0;
            return false;
        }
        xid = _minimizeRequests[0];
        _minimizeRequests.RemoveAt(0);
        return true;
    }

    private void DrainEvents()
    {
        if (_disposed || _display.Handle == IntPtr.Zero) return;
        while (X11Native.XPending(_display.Handle) > 0)
        {
            X11Native.XEvent ev = default;
            X11Native.XNextEvent(_display.Handle, ref ev);
            if (ev.Type != ClientMessageType) continue;
            ref var cm = ref Unsafe.As<X11Native.XEvent, X11Native.XClientMessageEvent>(ref ev);
            if (cm.MessageType != _changeStateAtom || cm.Format != 32) continue;
            if (cm.Data0 != IconicState) continue;
            if (!_minimizeRequests.Contains(cm.Window))
                _minimizeRequests.Add(cm.Window);
        }
    }

    public bool IsIconified(ulong xid) => ReadState(xid) == IconicState;

    /// <summary>
    /// Iconifies a window the way an X11 window manager would: set
    /// <c>WM_STATE=IconicState</c> and unmap it. The client then sees exactly
    /// the transition KWin gives it, which is what makes Wine keep its own
    /// minimize state consistent (mapping without that transition makes it
    /// re-minimize itself a moment later).
    /// </summary>
    public bool Iconify(ulong xid)
    {
        if (xid == 0 || _wmStateAtom == 0) return false;
        try
        {
            // KWin's order, observed on a direct (non-nested) run: mark the
            // window hidden, unmap it, and set WM_STATE afterwards. Wine reads
            // WM_STATE while it processes the unmap, so the reverse order is
            // not equivalent.
            SetHiddenState(xid, true);
            X11Native.XUnmapWindow(_display.Handle, xid);
            _display.Sync();
            WriteState(xid, IconicState);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"nested window probe: iconify 0x{xid:x} failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Deiconifies a window the way a window manager would: set
    /// <c>WM_STATE=NormalState</c> and map it. The window was unmapped by
    /// <see cref="Iconify"/>, so this is a real map transition for the client.
    /// </summary>
    public bool Deiconify(ulong xid)
    {
        if (xid == 0 || _wmStateAtom == 0) return false;
        try
        {
            // Same order as KWin on restore: drop the hidden state, map, then
            // set WM_STATE=Normal.
            SetHiddenState(xid, false);
            X11Native.XMapRaised(_display.Handle, xid);
            _display.Sync();
            WriteState(xid, NormalState);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"nested window probe: deiconify 0x{xid:x} failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>True while the X window still exists on the nested server.</summary>
    public bool IsAlive(ulong xid)
    {
        if (_disposed || xid == 0) return false;
        try
        {
            return X11Native.XGetWindowAttributes(_display.Handle, xid,
                out _) != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Asks a (possibly iconic) X11 window to close, like a WM would.</summary>
    public bool RequestClose(ulong xid)
    {
        if (_disposed || xid == 0) return false;
        try
        {
            ulong protocols = X11Native.XInternAtom(_display.Handle, "WM_PROTOCOLS", 0);
            ulong deleteWindow = X11Native.XInternAtom(_display.Handle, "WM_DELETE_WINDOW", 0);
            if (protocols == 0 || deleteWindow == 0) return false;
            var ev = new X11Native.XClientMessageEvent
            {
                Type = ClientMessageType,
                SendEvent = 1,
                Display = _display.Handle,
                Window = xid,
                MessageType = protocols,
                Format = 32,
                Data0 = (long)deleteWindow
            };
            X11Native.XSendEvent(_display.Handle, xid, 0, 0, ref ev);
            _display.Flush();
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"nested window probe: close 0x{xid:x} failed: {ex.Message}");
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

    /// <summary>
    /// Adds or removes <c>_NET_WM_STATE_HIDDEN</c>, which is how an EWMH window
    /// manager tells the client that a window was iconified rather than hidden
    /// by itself. KWin leaves the window with only that atom while minimized.
    /// </summary>
    private void SetHiddenState(ulong xid, bool hidden)
    {
        ulong hiddenAtom = _display.AtomNetWmStateHidden;
        ulong netWmState = _display.AtomNetWmState;
        if (hiddenAtom == 0 || netWmState == 0) return;
        const ulong XaAtom = 4;
        var atoms = new List<ulong>();
        int status = X11Native.XGetWindowProperty(_display.Handle, xid, netWmState,
            0, 32, 0, XaAtom, out _, out int format, out ulong count, out _, out IntPtr prop);
        if (status == 0 && prop != IntPtr.Zero)
        {
            try
            {
                if (format == 32)
                {
                    for (ulong i = 0; i < count; i++)
                        atoms.Add((ulong)(uint)Marshal.ReadInt32(prop, (int)i * 4));
                }
            }
            finally
            {
                X11Native.XFree(prop);
            }
        }
        atoms.RemoveAll(a => a == hiddenAtom);
        if (hidden) atoms.Add(hiddenAtom);
        X11Native.XChangeProperty(_display.Handle, xid, netWmState, XaAtom, 32,
            X11Native.PropModeReplace, atoms.ToArray(), atoms.Count);
        _display.Sync();
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
