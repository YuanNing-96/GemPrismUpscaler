using GemPrismUpscaler.Core.Diagnostics;

namespace GemPrismUpscaler.Core.Platform.Linux.X11;

public sealed class X11Hotkey : IDisposable
{
    private readonly X11Display _display;
    private readonly int _keycode;
    private readonly uint _modifiers = X11Native.Mod1Mask | X11Native.ShiftMask;
    private bool _grabbed;

    public X11Hotkey(X11Display display, ulong keysym = X11Native.XK_A)
    {
        _display = display;
        _keycode = X11Native.XKeysymToKeycode(display.Handle, keysym);
        if (_keycode == 0) return;

        int before = X11Display.LastError;
        X11Native.XGrabKey(display.Handle, _keycode, _modifiers, display.Root, 1,
            X11Native.GrabModeAsync, X11Native.GrabModeAsync);
        _display.Sync();
        _grabbed = X11Display.LastError == before;
        if (!_grabbed)
            Log.Write("XGrabKey Alt+Shift+A failed (BadAccess); hotkey disabled (UI buttons still work)");
    }

    public bool IsGrabbed => _grabbed;

    internal bool IsHotkey(in X11Native.XEvent ev) => _grabbed && ev.Type == 2 && ev.Window == _display.Root && (int)ev.Detail == _keycode;

    public void Dispose()
    {
        if (!_grabbed) return;
        _grabbed = false;
        X11Native.XUngrabKey(_display.Handle, _keycode, _modifiers, _display.Root);
        _display.Sync();
    }
}
