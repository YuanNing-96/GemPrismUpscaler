namespace GemPrismUpscaler.Core.Platform.Linux.X11;

public sealed class X11Overlay : IDisposable
{
    private readonly X11Display _display;
    private bool _disposed;

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

        X11Native.XMapRaised(display.Handle, Window);
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
        _display.Sync();
        IsVisible = true;
    }

    public void Hide()
    {
        if (_disposed || !IsVisible) return;
        X11Native.XUnmapWindow(_display.Handle, Window);
        _display.Sync();
        IsVisible = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        IsVisible = false;
        if (Window != 0)
            X11Native.XDestroyWindow(_display.Handle, Window);
        _display.Sync();
    }
}