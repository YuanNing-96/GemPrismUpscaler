using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using GemPrismUpscaler.Core.Diagnostics;
using GemPrismUpscaler.Core.Geometry;
using GemPrismUpscaler.Core.Platform.Linux.X11;
using GemPrismUpscaler.Core.Rendering;

namespace GemPrismUpscaler.Core.Platform.Linux.Nested;

/// <summary>
/// Single KDE-facing window for a nested game. It presents either the original
/// frame in a normal decorated window, or the upscaled frame fullscreen. The
/// private sway game itself has no KDE window.
/// </summary>
public sealed class NestedHostWindow : IHostWindow
{
    private readonly X11Display _display;
    private readonly MonitorInfo _monitor;
    private readonly ulong _fullscreenAtom;
    private readonly ulong _maximizedVertAtom;
    private readonly ulong _maximizedHorzAtom;
    private readonly ulong _fullscreenMonitorsAtom;
    private readonly int _monitorIndex;
    private readonly ulong _deleteWindowAtom;
    private readonly ulong _wmProtocolsAtom;
    private bool _disposed;
    private bool _focused;
    private bool _fullscreen;
    private bool _iconified;
    private int _originalWidth;
    private int _originalHeight;
    private int _width;
    private int _height;

    public ulong Window { get; }
    public int X { get; private set; }
    public int Y { get; private set; }
    public int Width => _width;
    public int Height => _height;
    public bool IsVisible { get; private set; }
    public bool IsFocused => _focused;
    public bool IsFullscreen => _fullscreen;
    public string MonitorName => _monitor.Name;
    public bool IsWayland => false;
    public IntPtr VulkanDisplay => IntPtr.Zero;
    public IntPtr VulkanSurface => IntPtr.Zero;

    public X11Display X11 => _display;

    /// <summary>
    /// Move the desktop pointer onto this host window at a window-local point.
    /// Used to mirror a cursor warp the game requested inside the nested
    /// compositor so the visible cursor stays in sync with the game.
    /// </summary>
    public bool WarpPointer(int x, int y)
    {
        // Only moves XWayland's shadow pointer; KWin's visible cursor is not
        // affected, so callers must not treat this as a successful warp. The
        // native Wayland host window uses wp_pointer_warp_v1 for that.
        X11Native.XWarpPointer(_display.Handle, 0, Window, 0, 0, 0, 0, x, y);
        _display.Sync();
        return false;
    }
    public IntPtr X11Window => (IntPtr)Window;

    public VulkanBlitter CreateBlitter(EffectDefinition effect, bool sourceRgb)
        => new(_display, Window, _width, _height, effect, sourceRgb);

    public event Action<OverlayPointerEvent>? Pointer;
    public event Action<OverlayKeyEvent>? Key;
    public event Action<OverlayModifiers>? Modifiers;
    public event Action<bool>? FocusChanged;
    /// <summary>
    /// The X server asked us to repaint (newly mapped, uncovered, resized).
    /// A Vulkan swapchain keeps no reusable front buffer, so the session must
    /// re-present the last frame; otherwise a static game/launcher stays black
    /// until its pixels happen to change.
    /// </summary>
    public event Action? Exposed;
    /// <summary>
    /// KWin mapped the host again after we mirrored a game-side minimize.
    /// The session uses this to deiconify the inner game window.
    /// </summary>
    public event Action? Restored;
    public event Action? Closed;
    public event Action? CloseRequested;

    public NestedHostWindow(string? outputName, string title,
        int originalWidth, int originalHeight, bool startFullscreen,
        ulong transientFor = 0, bool dialog = false)
    {
        _display = new X11Display();
        var monitors = X11MonitorEnumerator.List(_display);
        MonitorInfo monitor = default;
        if (!string.IsNullOrWhiteSpace(outputName))
        {
            monitor = monitors.FirstOrDefault(m =>
                string.Equals(m.Name, outputName, StringComparison.OrdinalIgnoreCase));
            if (monitor.Width <= 0)
            {
                Log.Warn($"nested host monitor '{outputName}' not found; using largest. " +
                         $"available={string.Join(", ", monitors.Select(m => $"{m.Name}:{m.Width}x{m.Height}"))}");
                monitor = monitors.OrderByDescending(m => (long)m.Width * m.Height).FirstOrDefault();
            }
        }
        else
        {
            monitor = monitors.FirstOrDefault(m => m.Primary);
        }
        if (monitor.Width <= 0) monitor = monitors[0];
        _monitor = monitor;
        _monitorIndex = Math.Max(0, monitors.FindIndex(m =>
            string.Equals(m.Name, monitor.Name, StringComparison.OrdinalIgnoreCase)));
        Log.Info($"nested host monitor requested='{outputName ?? "auto"}' " +
                 $"selected='{monitor.Name}' geometry={monitor.X},{monitor.Y} {monitor.Width}x{monitor.Height} " +
                 $"available={string.Join(", ", monitors.Select(m => $"{m.Name}:{m.Width}x{m.Height}"))}");

        _originalWidth = Math.Max(64, originalWidth);
        _originalHeight = Math.Max(64, originalHeight);
        // Do not pre-set _NET_WM_STATE before mapping. KWin ignores/clears a
        // fullscreen property that is written directly, and later treats the
        // client as a maximized window with decorations. Start windowed at the
        // restore size, map, then request fullscreen through EWMH.
        _fullscreen = false;
        _deleteWindowAtom = X11Native.XInternAtom(_display.Handle, "WM_DELETE_WINDOW", 0);
        _wmProtocolsAtom = X11Native.XInternAtom(_display.Handle, "WM_PROTOCOLS", 0);

        // Always create the client at its original size. KWin records this
        // geometry and restores it when _NET_WM_STATE_FULLSCREEN is removed.
        // Creating the window already fullscreen makes the restore size the
        // whole monitor, which defeats the windowed original mode.
        int w = _originalWidth;
        int h = _originalHeight;
        X = monitor.X + (monitor.Width - w) / 2;
        Y = monitor.Y + (monitor.Height - h) / 2;
        _width = w;
        _height = h;

        Window = X11Native.XCreateSimpleWindow(_display.Handle, _display.Root, X, Y,
            (uint)w, (uint)h, 0, 0, 0);
        if (Window == 0) throw new InvalidOperationException("XCreateSimpleWindow failed");
        SetDialogProperties(transientFor, dialog);
        if (_wmProtocolsAtom != 0 && _deleteWindowAtom != 0)
        {
            ulong protocol = _deleteWindowAtom;
            X11Native.XChangeProperty(_display.Handle, Window, _wmProtocolsAtom,
                X11Native.XA_ATOM, 32, X11Native.PropModeReplace, ref protocol, 1);
        }

        long mask = X11Native.KeyPressMask | X11Native.KeyReleaseMask |
                    X11Native.ButtonPressMask | X11Native.ButtonReleaseMask |
                    X11Native.PointerMotionMask | X11Native.EnterWindowMask |
                    X11Native.LeaveWindowMask | X11Native.FocusChangeMask |
                    X11Native.ExposureMask |
                    X11Native.StructureNotifyMask;
        X11Native.XSelectInput(_display.Handle, Window, mask);

        var hints = new X11Native.XWMHints { Flags = X11Native.InputHint, Input = 1 };
        X11Native.XSetWMHints(_display.Handle, Window, ref hints);
        SetTitle(title);
        _fullscreenAtom = X11Native.XInternAtom(_display.Handle, "_NET_WM_STATE_FULLSCREEN", 0);
        _maximizedVertAtom = X11Native.XInternAtom(_display.Handle,
            "_NET_WM_STATE_MAXIMIZED_VERT", 0);
        _maximizedHorzAtom = X11Native.XInternAtom(_display.Handle,
            "_NET_WM_STATE_MAXIMIZED_HORZ", 0);
        _fullscreenMonitorsAtom = X11Native.XInternAtom(_display.Handle,
            "_NET_WM_FULLSCREEN_MONITORS", 0);
        X11Native.XMapRaised(_display.Handle, Window);
        _display.Sync();
        IsVisible = true;
        for (int i = 0; i < 10; i++)
        {
            Thread.Sleep(10);
            Pump();
        }
        UpdateGeometry();
        if (startFullscreen) SetFullscreen(true);
    }

    private void SetDialogProperties(ulong transientFor, bool dialog)
    {
        // Separate X connections still address the same X server, so the
        // dialog host can be made transient for the main host. KWin then keeps
        // the exit/confirm dialog above its parent instead of letting the main
        // host raise over it.
        if (transientFor != 0)
        {
            ulong atom = X11Native.XInternAtom(_display.Handle, "WM_TRANSIENT_FOR", 0);
            if (atom != 0)
            {
                ulong value = transientFor;
                X11Native.XChangeProperty(_display.Handle, Window, atom,
                    X11Native.XA_WINDOW, 32, X11Native.PropModeReplace, ref value, 1);
            }
        }
        if (dialog)
        {
            ulong type = X11Native.XInternAtom(_display.Handle, "_NET_WM_WINDOW_TYPE_DIALOG", 0);
            if (type != 0 && _display.AtomNetWmWindowType != 0)
            {
                X11Native.XChangeProperty(_display.Handle, Window, _display.AtomNetWmWindowType,
                    X11Native.XA_ATOM, 32, X11Native.PropModeReplace, ref type, 1);
            }
        }
    }

    public void SetFullscreen(bool fullscreen)
    {
        if (_disposed || _fullscreen == fullscreen) return;
        _fullscreen = fullscreen;

        if (fullscreen)
        {
            // Pin the fullscreen request to the monitor selected for this
            // nested output. Without this KWin may fullscreen on whichever
            // monitor currently has the active window.
            if (_fullscreenMonitorsAtom != 0)
            {
                ulong[] monitorIndex = { (ulong)_monitorIndex, (ulong)_monitorIndex,
                    (ulong)_monitorIndex, (ulong)_monitorIndex };
                X11Native.XChangeProperty(_display.Handle, Window,
                    _fullscreenMonitorsAtom, X11Native.XA_CARDINAL, 32,
                    X11Native.PropModeReplace, monitorIndex, 4);
                _display.Sync();
            }

            // Clear a previous maximize first. Otherwise KWin remembers the
            // maximized restore geometry and a later fullscreen exit returns
            // to a 4K maximized window instead of the original 800x600.
            SendWmStateChange(0, _maximizedVertAtom, _maximizedHorzAtom);
            for (int attempt = 0; attempt < 3; attempt++)
            {
                SendWmStateChange(1, _fullscreenAtom, 0);
                if (WaitForWmState(fullscreen: true, noMaximized: true,
                        _monitor.Width, _monitor.Height, 220))
                    break;
                Log.Warn($"nested host fullscreen not applied yet " +
                         $"client={_width}x{_height} target={_monitor.Width}x{_monitor.Height}");
            }
        }
        else
        {
            // Do not just resize out of fullscreen: KWin still owns the
            // fullscreen/maximized state and will clamp the request to the
            // decorated work area. Clear the EWMH states, wait for KWin, then
            // apply the windowed restore geometry.
            SendWmStateChange(0, _fullscreenAtom, 0);
            SendWmStateChange(0, _maximizedVertAtom, _maximizedHorzAtom);
            WaitForWmState(fullscreen: false, noMaximized: true,
                0, 0, 300);

            X = _monitor.X + (_monitor.Width - _originalWidth) / 2;
            Y = _monitor.Y + (_monitor.Height - _originalHeight) / 2;
            X11Native.XMoveResizeWindow(_display.Handle, Window, X, Y,
                (uint)_originalWidth, (uint)_originalHeight);
            _display.Sync();
            WaitForClientSize(_originalWidth, _originalHeight, 220);
        }

        Pump();
        UpdateGeometry();
    }

    private bool WaitForWmState(bool fullscreen, bool noMaximized,
        int width, int height, int timeoutMs)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            Pump();
            UpdateGeometry();
            bool isFullscreen = HasWmState(_fullscreenAtom);
            bool isMaximized = HasWmState(_maximizedVertAtom) ||
                               HasWmState(_maximizedHorzAtom);
            bool sizeMatches = width <= 0 || (_width == width && _height == height);
            if (isFullscreen == fullscreen && (!noMaximized || !isMaximized) && sizeMatches)
                return true;
            Thread.Sleep(10);
        }
        Pump();
        UpdateGeometry();
        return HasWmState(_fullscreenAtom) == fullscreen;
    }

    private bool HasWmState(ulong atom)
    {
        if (atom == 0 || _display.AtomNetWmState == 0) return false;
        int status = X11Native.XGetWindowProperty(_display.Handle, Window,
            _display.AtomNetWmState, 0, 64, 0, X11Native.XA_ATOM,
            out ulong actualType, out int actualFormat, out ulong nitems,
            out _, out IntPtr prop);
        if (status != 0 || actualType != X11Native.XA_ATOM ||
            actualFormat != 32 || prop == IntPtr.Zero)
            return false;
        try
        {
            int count = (int)Math.Min(nitems, 64UL);
            for (int i = 0; i < count; i++)
            {
                if ((ulong)Marshal.ReadInt64(prop, i * 8) == atom) return true;
            }
            return false;
        }
        finally
        {
            X11Native.XFree(prop);
        }
    }

    private bool WaitForClientSize(int width, int height, int timeoutMs)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            Pump();
            UpdateGeometry();
            if (_width == width && _height == height) return true;
            Thread.Sleep(10);
        }
        UpdateGeometry();
        return _width == width && _height == height;
    }

    private void SendWmStateChange(int action, ulong first, ulong second)
    {
        if (first == 0) return;
        var ev = new X11Native.XClientMessageEvent
        {
            Type = X11Native.ClientMessage,
            SendEvent = 1,
            Display = _display.Handle,
            Window = Window,
            MessageType = _display.AtomNetWmState,
            Format = 32,
            Data0 = action,
            Data1 = (long)first,
            Data2 = (long)second,
            Data3 = 1
        };
        X11Native.XSendEvent(_display.Handle, _display.Root, 0,
            X11Native.SubstructureRedirectMask | X11Native.SubstructureNotifyMask, ref ev);
        _display.Flush();
    }

    public void SetOriginalSize(int width, int height)
    {
        _originalWidth = Math.Max(64, width);
        _originalHeight = Math.Max(64, height);
        if (!_fullscreen)
        {
            X = _monitor.X + (_monitor.Width - _originalWidth) / 2;
            Y = _monitor.Y + (_monitor.Height - _originalHeight) / 2;
            _width = _originalWidth;
            _height = _originalHeight;
            X11Native.XMoveResizeWindow(_display.Handle, Window, X, Y,
                (uint)_width, (uint)_height);
            _display.Sync();
            UpdateGeometry();
        }
    }

    private void UpdateGeometry()
    {
        if (_disposed) return;
        if (X11Native.XGetGeometry(_display.Handle, Window, out _, out _, out _,
                out uint w, out uint h, out _, out _) != 0)
        {
            if (w > 0) _width = (int)w;
            if (h > 0) _height = (int)h;
        }
    }

    /// <summary>
    /// Mirror a game-side minimize: ask KWin to iconify this host so it goes
    /// to the taskbar instead of showing the stale/black frame the nested
    /// client stopped rendering.
    /// </summary>
    public void Iconify()
    {
        if (_disposed) return;
        _iconified = true;
        X11Native.XIconifyWindow(_display.Handle, Window, _display.Screen);
        _display.Sync();
    }

    /// <summary>
    /// Map the host again without stealing focus. Used when the inner window
    /// un-minimizes on its own, or while we restore it in response to the user
    /// restoring the host from the taskbar.
    /// </summary>
    public void RestoreFromIconify()
    {
        if (_disposed) return;
        _iconified = false;
        X11Native.XMapRaised(_display.Handle, Window);
        IsVisible = true;
        _display.Sync();
    }

    public void Focus()
    {
        if (_disposed) return;
        X11Native.XSetInputFocus(_display.Handle, Window, X11Native.RevertToParent, 0);
        Activate();
        _display.Sync();
    }

    public void Show()
    {
        if (_disposed) return;
        if (!IsVisible)
        {
            X11Native.XMapRaised(_display.Handle, Window);
            IsVisible = true;
        }
        else
        {
            X11Native.XRaiseWindow(_display.Handle, Window);
        }
        Activate();
        _display.Sync();
    }

    /// <summary>
    /// Ask KWin to make this a real active window, not merely an X input-focus
    /// change. XSetInputFocus alone can leave a newly mapped host behind a
    /// native Wayland window that KWin considers active.
    /// </summary>
    public void Activate()
    {
        if (_disposed) return;
        var ev = new X11Native.XClientMessageEvent
        {
            Type = X11Native.ClientMessage,
            SendEvent = 1,
            Display = _display.Handle,
            Window = Window,
            MessageType = _display.AtomNetActiveWindow,
            Format = 32,
            Data0 = 1, // source indication: application
            Data1 = 0, // CurrentTime
            Data2 = 0
        };
        X11Native.XSendEvent(_display.Handle, _display.Root, 0,
            X11Native.SubstructureRedirectMask | X11Native.SubstructureNotifyMask, ref ev);
        _display.Flush();
    }

    public void Raise()
    {
        if (_disposed) return;
        X11Native.XRaiseWindow(_display.Handle, Window);
        _display.Sync();
    }

    public void SetTitle(string title)
    {
        if (_disposed || string.IsNullOrWhiteSpace(title)) return;

        // XStoreName sets WM_NAME as STRING (Latin-1), which turns Chinese
        // game titles into mojibake. KWin uses _NET_WM_NAME in UTF8_STRING.
        byte[] utf8 = Encoding.UTF8.GetBytes(title);
        if (_display.AtomNetWmName != 0 && _display.AtomUtf8String != 0)
        {
            X11Native.XChangeProperty(_display.Handle, Window, _display.AtomNetWmName,
                _display.AtomUtf8String, 8, X11Native.PropModeReplace, utf8, utf8.Length);
        }
        X11Native.XStoreName(_display.Handle, Window, title);
        _display.Sync();
    }

    public void Hide()
    {
        if (_disposed || !IsVisible) return;
        X11Native.XUnmapWindow(_display.Handle, Window);
        _display.Sync();
        IsVisible = false;
    }

    public void Pump()
    {
        if (_disposed) return;
        try
        {
            while (X11Native.XPending(_display.Handle) > 0)
            {
                var ev = default(X11Native.XEvent);
                X11Native.XNextEvent(_display.Handle, ref ev);
                switch (ev.Type)
                {
                    case X11Native.FocusIn:
                        if (!_focused) { _focused = true; FocusChanged?.Invoke(true); }
                        break;
                    case X11Native.FocusOut:
                        if (_focused) { _focused = false; FocusChanged?.Invoke(false); }
                        break;
                    case X11Native.ConfigureNotify:
                        UpdateGeometry();
                        break;
                    case X11Native.Expose:
                        Exposed?.Invoke();
                        break;
                    case X11Native.MapNotify:
                        if (_iconified)
                        {
                            _iconified = false;
                            Restored?.Invoke();
                        }
                        break;
                    case X11Native.MotionNotify:
                        Pointer?.Invoke(new OverlayPointerEvent(OverlayPointerKind.Motion, ev.X, ev.Y));
                        break;
                    case X11Native.EnterNotify:
                        Pointer?.Invoke(new OverlayPointerEvent(OverlayPointerKind.Enter, ev.X, ev.Y));
                        break;
                    case X11Native.LeaveNotify:
                        Pointer?.Invoke(new OverlayPointerEvent(OverlayPointerKind.Leave, 0, 0));
                        break;
                    case X11Native.ButtonPress:
                    case X11Native.ButtonRelease:
                    {
                        bool pressed = ev.Type == X11Native.ButtonPress;
                        int button = (int)ev.Detail;
                        OverlayPointerKind kind = button switch
                        {
                            4 => OverlayPointerKind.WheelUp,
                            5 => OverlayPointerKind.WheelDown,
                            _ => pressed ? OverlayPointerKind.ButtonPress : OverlayPointerKind.ButtonRelease
                        };
                        int mapped = button is 4 or 5 ? 0 : button switch
                        {
                            2 => 2,
                            3 => 3,
                            _ => 1
                        };
                        Log.Debug($"nested host button type={ev.Type} detail={button} " +
                                  $"mapped={mapped} at={ev.X},{ev.Y}");
                        Pointer?.Invoke(new OverlayPointerEvent(kind, ev.X, ev.Y, mapped));
                        break;
                    }
                    case X11Native.KeyPress:
                    case X11Native.KeyRelease:
                    {
                        int keycode = (int)ev.Detail - 8;
                        if (keycode > 0)
                        {
                            Modifiers?.Invoke(new OverlayModifiers((int)ev.State, 0, 0, 0));
                            Key?.Invoke(new OverlayKeyEvent(keycode,
                                ev.Type == X11Native.KeyPress ? OverlayKeyState.Pressed : OverlayKeyState.Released));
                        }
                        break;
                    }
                    case X11Native.ClientMessage:
                    {
                        // XEvent.Detail is at the offset of data.l[3], not
                        // data.l[0]. Overlay the larger client-message struct
                        // so WM_DELETE_WINDOW (data.l[0]) is read correctly.
                        var client = Unsafe.As<X11Native.XEvent,
                            X11Native.XClientMessageEvent>(ref ev);
                        if (client.MessageType == _wmProtocolsAtom &&
                            (ulong)client.Data0 == _deleteWindowAtom)
                            CloseRequested?.Invoke();
                        break;
                    }
                    case 17: // DestroyNotify
                        Closed?.Invoke();
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"nested host window pump failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        IsVisible = false;
        try
        {
            if (Window != 0) X11Native.XDestroyWindow(_display.Handle, Window);
            _display.Sync();
        }
        catch { }
        _display.Dispose();
    }
}
