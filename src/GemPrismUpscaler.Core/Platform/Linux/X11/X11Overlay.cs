namespace GemPrismUpscaler.Core.Platform.Linux.X11;

public sealed class X11Overlay : IDisposable
{
    private readonly X11Display _display;
    private bool _disposed;
    private ulong _blocker;
    private static readonly bool BlockDirectScanout = string.Equals(
        Environment.GetEnvironmentVariable("GEMPRISM_BLOCK_DIRECT_SCANOUT"), "1",
        StringComparison.OrdinalIgnoreCase);

    public ulong Window { get; }
    public int X { get; }
    public int Y { get; }
    public int Width { get; }
    public int Height { get; }
    public bool IsVisible { get; private set; }

    public X11Overlay(X11Display display, int x, int y, int width, int height)
    {
        _display = display;
        X = x;
        Y = y;
        Width = width;
        Height = height;

        Window = X11Native.XCreateSimpleWindow(display.Handle, display.Root, x, y, (uint)width, (uint)height,
            0, 0, 0);
        if (Window == 0) throw new InvalidOperationException("XCreateSimpleWindow failed");

        var attrs = new X11Native.XSetWindowAttributes { OverrideRedirect = 1 };
        X11Native.XChangeWindowAttributes(display.Handle, Window, X11Native.CWOverrideRedirect, ref attrs);

        long mask = X11Native.ButtonPressMask | X11Native.ButtonReleaseMask | X11Native.PointerMotionMask |
                    X11Native.EnterWindowMask | X11Native.LeaveWindowMask;
        X11Native.XSelectInput(display.Handle, Window, mask);

        var hints = new X11Native.XWMHints
        {
            Flags = X11Native.InputHint,
            Input = 0
        };
        X11Native.XSetWMHints(display.Handle, Window, ref hints);
        X11Native.XStoreName(display.Handle, Window, "GemPrismOverlay");

        if (BlockDirectScanout)
        {
            // A tiny overlapping window prevents KWin from promoting the
            // fullscreen overlay to direct scanout, which can show stale or
            // torn buffers on some XWayland setups.
            _blocker = X11Native.XCreateSimpleWindow(display.Handle, display.Root,
                x + width - 2, y, 2, 2, 0, 0, 0xFF00FF);
            var blockerAttrs = new X11Native.XSetWindowAttributes { OverrideRedirect = 1 };
            X11Native.XChangeWindowAttributes(display.Handle, _blocker,
                X11Native.CWOverrideRedirect, ref blockerAttrs);
            var blockerHints = new X11Native.XWMHints { Flags = X11Native.InputHint, Input = 0 };
            X11Native.XSetWMHints(display.Handle, _blocker, ref blockerHints);
        }

        X11Native.XMapRaised(display.Handle, Window);
        if (_blocker != 0) X11Native.XMapRaised(display.Handle, _blocker);
        display.Sync();
        IsVisible = true;
    }

    public void Show()
    {
        if (_disposed || IsVisible) return;
        // Do not map the window with the previous swapchain image: that looks
        // like a past frame flashing back after a focus/popup hide. Paint the
        // black background first; the engine renders a fresh frame next.
        X11Native.XClearWindow(_display.Handle, Window);
        X11Native.XMapRaised(_display.Handle, Window);
        if (_blocker != 0) X11Native.XMapRaised(_display.Handle, _blocker);
        _display.Sync();
        IsVisible = true;
    }

    public void Hide()
    {
        if (_disposed || !IsVisible) return;
        if (_blocker != 0) X11Native.XUnmapWindow(_display.Handle, _blocker);
        X11Native.XUnmapWindow(_display.Handle, Window);
        _display.Sync();
        IsVisible = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        IsVisible = false;
        if (_blocker != 0) X11Native.XDestroyWindow(_display.Handle, _blocker);
        if (Window != 0)
            X11Native.XDestroyWindow(_display.Handle, Window);
        _display.Sync();
    }
}