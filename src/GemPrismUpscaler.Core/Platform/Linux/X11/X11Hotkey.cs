using GemPrismUpscaler.Core.Configuration;
using GemPrismUpscaler.Core.Diagnostics;

namespace GemPrismUpscaler.Core.Platform.Linux.X11;

/// <summary>
/// Global hotkey grab on the X11 root window. A configurable binding replaces
/// the M1a hardcoded Alt+Shift+A. NumLock/CapsLock variants are grabbed too so
/// the hotkey still works when those locks are active.
/// </summary>
public sealed class X11Hotkey : IDisposable
{
    private readonly X11Display _display;
    private readonly HotkeyBinding _binding;
    private readonly uint _modifiers;
    private readonly int _keycode;
    private readonly List<uint> _grabMasks = new();
    private bool _grabbed;

    public X11Hotkey(X11Display display, HotkeyBinding? binding = null)
    {
        _display = display;
        _binding = binding ?? HotkeyBinding.Default;
        _modifiers = MapModifiers(_binding.Modifiers);
        _keycode = ResolveKeycode(display, _binding.Key);

        if (_keycode == 0)
        {
            Error = $"无法解析热键主键 '{_binding.Key}'";
            Log.Warn($"hotkey {_binding}: {Error}");
            return;
        }

        int before = X11Display.LastError;
        uint[] extras =
        [
            0,
            X11Native.LockMask,
            X11Native.Mod2Mask,
            X11Native.LockMask | X11Native.Mod2Mask
        ];

        foreach (uint extra in extras)
        {
            uint mask = _modifiers | extra;
            X11Native.XGrabKey(display.Handle, _keycode, mask, display.Root, 1,
                X11Native.GrabModeAsync, X11Native.GrabModeAsync);
            _grabMasks.Add(mask);
        }
        _display.Sync();

        if (X11Display.LastError != before)
        {
            Ungrab();
            Error = $"热键 {_binding} 已被其他程序占用（XGrabKey BadAccess）";
            Log.Warn($"hotkey {_binding}: {Error}");
            return;
        }

        _grabbed = true;
    }

    public HotkeyBinding Binding => _binding;
    public bool IsGrabbed => _grabbed;
    public string? Error { get; }

    internal bool IsHotkey(in X11Native.XEvent ev)
    {
        if (!_grabbed || ev.Type != 2 || ev.Window != _display.Root) return false;
        if ((int)ev.Detail != _keycode) return false;
        return (ev.State & X11Native.ModifierMask) == _modifiers;
    }

    private static int ResolveKeycode(X11Display display, string key)
    {
        ulong keysym = X11Native.XStringToKeysym(key);
        if (keysym == 0 && key.Length == 1)
        {
            keysym = X11Native.XStringToKeysym(char.IsUpper(key[0])
                ? key.ToLowerInvariant()
                : key.ToUpperInvariant());
        }
        return keysym == 0 ? 0 : X11Native.XKeysymToKeycode(display.Handle, keysym);
    }

    private static uint MapModifiers(HotkeyModifiers modifiers)
    {
        uint mask = 0;
        if (modifiers.HasFlag(HotkeyModifiers.Shift)) mask |= X11Native.ShiftMask;
        if (modifiers.HasFlag(HotkeyModifiers.Control)) mask |= X11Native.ControlMask;
        if (modifiers.HasFlag(HotkeyModifiers.Alt)) mask |= X11Native.Mod1Mask;
        if (modifiers.HasFlag(HotkeyModifiers.Super)) mask |= X11Native.Mod4Mask;
        return mask;
    }

    private void Ungrab()
    {
        foreach (uint mask in _grabMasks)
            X11Native.XUngrabKey(_display.Handle, _keycode, mask, _display.Root);
        _grabMasks.Clear();
        _display.Sync();
    }

    public void Dispose()
    {
        if (!_grabbed) return;
        _grabbed = false;
        Ungrab();
    }
}
