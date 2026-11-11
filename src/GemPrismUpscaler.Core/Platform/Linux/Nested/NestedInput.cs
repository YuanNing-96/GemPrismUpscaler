using GemPrismUpscaler.Core.Diagnostics;
using GemPrismUpscaler.Core.Geometry;
using GemPrismUpscaler.Core.Platform.Linux.X11;
using NWayland;
using NWayland.Protocols.Wayland;
using NWayland.Protocols.Wlr.WlrVirtualPointerUnstableV1;

namespace GemPrismUpscaler.Core.Platform.Linux.Nested;

/// <summary>
/// Injects logical pointer events into the nested compositor. XWayland targets
/// use XTest inside the private XWayland (which also produces XInput2 raw
/// events); native Wayland targets use wlr-virtual-pointer.
/// </summary>
public sealed class NestedInput : IDisposable
{
    private static readonly bool TraceInput =
        Environment.GetEnvironmentVariable("GEMPRISM_TRACE_INPUT") == "1";

    private readonly X11Display? _x11;
    private readonly WlDisplay? _wayland;
    private readonly ZwlrVirtualPointerV1? _virtualPointer;
    private readonly VirtualKeyboard? _vkeyboard;
    private CoordinateMapper? _mapper;
    private int _cropX;
    private int _cropY;
    private int _outputOriginX;
    private int _outputOriginY;
    private int _outputWidth;
    private int _outputHeight;
    private int _layoutWidth;
    private int _layoutHeight;
    private bool _xwayland = true;
    private bool _warnedNoPointer;
    private bool _havePointerPosition;
    private int _lastPointerX;
    private int _lastPointerY;
    private bool _disposed;

    public NestedInput(string? x11Display, string? waylandDisplay)
    {
        if (!string.IsNullOrWhiteSpace(x11Display))
        {
            try { _x11 = new X11Display(x11Display); }
            catch (Exception ex) { Log.Warn($"nested input: X11 unavailable: {ex.Message}"); }
        }

        if (!string.IsNullOrWhiteSpace(waylandDisplay))
        {
            try
            {
                _wayland = WlDisplay.Connect(waylandDisplay, new WlDisplayOptions());
                uint seatName = 0, managerName = 0, seatVersion = 0, managerVersion = 0;
                var registryListener = new RegistryListener();
                registryListener.GlobalAdded = (name, iface, version) =>
                {
                    if (iface == "wl_seat") { seatName = name; seatVersion = version; }
                    if (iface == "zwlr_virtual_pointer_manager_v1") { managerName = name; managerVersion = version; }
                };
                var registry = _wayland.GetRegistry(registryListener, null);
                _wayland.Roundtrip();
                if (seatName != 0 && managerName != 0)
                {
                    var seat = WlSeat.Bind(registry, seatName, Math.Min(seatVersion, 7),
                        new SeatListener(), null);
                    var manager = ZwlrVirtualPointerManagerV1.Bind(registry, managerName,
                        Math.Min(managerVersion, 2), new ManagerListener(), null);
                    _wayland.Roundtrip();
                    _virtualPointer = manager.CreateVirtualPointer(seat, new PointerListener(), null);
                    _wayland.Roundtrip();
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"nested input: virtual pointer unavailable: {ex.Message}");
            }

            _vkeyboard = VirtualKeyboard.TryStart(waylandDisplay);
        }
    }

    public void SetKeymap(byte[] data)
    {
        // Native Wayland keymap is forwarded to the vkeyboard helper in the
        // keyboard step; XWayland derives its keymap from XTest keycodes.
        _ = data;
    }

    public void SetMapping(RectI dest, int sourceWidth, int sourceHeight)
    {
        SetMapping(dest, new RectI(0, 0, sourceWidth, sourceHeight), sourceWidth, sourceHeight);
    }

    /// <summary>
    /// Map host-window coordinates into the cropped game-window rect inside one
    /// of several virtual outputs. <paramref name="crop"/> is relative to the
    /// output origin; injected coordinates are converted to sway's global
    /// layout coordinates by adding <paramref name="outputOrigin"/>.
    /// </summary>
    public void SetMapping(RectI dest, RectI crop, int outputWidth, int outputHeight)
    {
        SetMapping(dest, crop, outputWidth, outputHeight, 0, 0, outputWidth, outputHeight);
    }

    public void SetMapping(RectI dest, RectI crop, int outputWidth, int outputHeight,
        int outputOriginX, int outputOriginY, int layoutWidth, int layoutHeight)
    {
        _mapper = new CoordinateMapper(dest, Math.Max(1, crop.Width), Math.Max(1, crop.Height));
        _cropX = crop.X;
        _cropY = crop.Y;
        _outputOriginX = outputOriginX;
        _outputOriginY = outputOriginY;
        _outputWidth = Math.Max(1, outputWidth);
        _outputHeight = Math.Max(1, outputHeight);
        _layoutWidth = Math.Max(1, layoutWidth);
        _layoutHeight = Math.Max(1, layoutHeight);
    }

    public void SetTarget(bool xwayland) => _xwayland = xwayland;

    public bool HasPointerReference => _havePointerPosition;

    public (int X, int Y) PointerReference => (_lastPointerX, _lastPointerY);

    /// <summary>
    /// Rebase the relative-motion reference after the compositor moved the
    /// pointer on its own (client cursor hint). The next host motion then
    /// produces deltas relative to the new position instead of the stale one.
    /// </summary>
    public void SetPointerReference(int x, int y)
    {
        _lastPointerX = x;
        _lastPointerY = y;
        _havePointerPosition = true;
    }

    public void HandlePointer(OverlayPointerEvent e)
    {
        if (_mapper is null || _disposed) return;
        if (e.Kind is OverlayPointerKind.Leave)
        {
            _havePointerPosition = false;
            return;
        }
        if (!_mapper.Value.TryMap(e.X, e.Y, out int x, out int y))
        {
            if (TraceInput)
                Log.Info($"nested input trace: host={e.X},{e.Y} outside dest; dropped");
            _havePointerPosition = false;
            return;
        }
        int outputX = _outputOriginX + _cropX + x;
        int outputY = _outputOriginY + _cropY + y;
        if (TraceInput)
            Log.Info($"nested input trace: host={e.X},{e.Y} kind={e.Kind} " +
                     $"crop={_cropX},{_cropY} output={outputX},{outputY} " +
                     $"xwayland={_xwayland} vp={_virtualPointer is not null} " +
                     $"vk={_vkeyboard is not null}");

        switch (e.Kind)
        {
            case OverlayPointerKind.Motion:
                SendPointerPosition(outputX, outputY, forceAbsolute: false);
                break;
            case OverlayPointerKind.Enter:
                SendPointerPosition(outputX, outputY, forceAbsolute: true);
                break;
            case OverlayPointerKind.ButtonPress:
                SendPointerPosition(outputX, outputY, forceAbsolute: true);
                SendButton(e.Button, pressed: true);
                break;
            case OverlayPointerKind.ButtonRelease:
                SendPointerPosition(outputX, outputY, forceAbsolute: true);
                SendButton(e.Button, pressed: false);
                break;
            case OverlayPointerKind.WheelUp:
                SendPointerPosition(outputX, outputY, forceAbsolute: false);
                SendWheel(-1);
                break;
            case OverlayPointerKind.WheelDown:
                SendPointerPosition(outputX, outputY, forceAbsolute: false);
                SendWheel(1);
                break;
            case OverlayPointerKind.Axis:
                SendAxis(e.Button / 1000.0);
                break;
        }
    }

    public void HandleKey(int evdevKeyCode, bool pressed)
    {
        if (_disposed) return;

        // XTest is the reliable path for XWayland clients: the virtual
        // keyboard's Wayland-level key events do not necessarily reach the
        // XWayland keyboard focus. Native Wayland windows use the helper.
        if (_xwayland && _x11 is not null)
        {
            // XTest takes X11 keycodes; Wayland/evdev keycodes are 8 lower.
            XTestNative.XTestFakeKeyEvent(_x11.Handle, (uint)(evdevKeyCode + 8), pressed ? 1 : 0, 0);
            _x11.Sync();
            return;
        }

        _vkeyboard?.Key(evdevKeyCode, pressed);
    }

    public void HandleModifiers(OverlayModifiers modifiers)
    {
        if (_disposed || _xwayland) return;
        _vkeyboard?.Modifiers(modifiers);
    }

    /// <summary>
    /// Native Wayland games often request a persistent pointer lock. A locked
    /// pointer ignores absolute motion, but the compositor still reports
    /// relative deltas, so position the pointer once and then follow the host
    /// mouse with relative motion. XWayland keeps using XWarpPointer.
    /// </summary>
    private void SendPointerPosition(int x, int y, bool forceAbsolute)
    {
        if (_xwayland && _x11 is not null)
        {
            X11Native.XWarpPointer(_x11.Handle, 0, _x11.Root, 0, 0, 0, 0, x, y);
            _x11.Sync();
            // Remember where we put the nested pointer: the session polls
            // XQueryPointer to detect a game-initiated warp by comparing
            // against this reference.
            _lastPointerX = x;
            _lastPointerY = y;
            _havePointerPosition = true;
            return;
        }

        if (_virtualPointer is null)
        {
            if (!_warnedNoPointer)
            {
                _warnedNoPointer = true;
                Log.Warn("nested input: virtual pointer missing; pointer events are dropped");
            }
            return;
        }

        uint time = (uint)Environment.TickCount64;
        // The game can move the compositor's pointer without telling us (Wine
        // does that when a modal dialog opens). The cursor-sync loop rebases
        // the relative reference through SetPointerReference as soon as sway
        // reports the warp, so the periodic absolute re-anchor this used to do
        // is gone: it fought the reference instead of following it.
        if (forceAbsolute || !_havePointerPosition)
        {
            if (TraceInput)
                Log.Info($"nested input trace: ABSOLUTE pos={x},{y} " +
                         $"force={forceAbsolute} have={_havePointerPosition} " +
                         $"extents={Math.Max(_outputWidth, _layoutWidth)}x" +
                         $"{Math.Max(_outputHeight, _layoutHeight)}");
            // wlr-virtual-pointer absolute motion uses the compositor's global
            // layout extents, not the local output size, once several outputs
            // exist side by side.
            _virtualPointer.MotionAbsolute(time, (uint)Math.Max(0, x), (uint)Math.Max(0, y),
                (uint)Math.Max(_outputWidth, _layoutWidth),
                (uint)Math.Max(_outputHeight, _layoutHeight));
        }
        else
        {
            double dx = x - _lastPointerX;
            double dy = y - _lastPointerY;
            if (TraceInput)
                Log.Info($"nested input trace: RELATIVE dx={dx} dy={dy} " +
                         $"from={_lastPointerX},{_lastPointerY} to={x},{y}");
            if (dx != 0 || dy != 0)
                _virtualPointer.Motion(time, new WlFixed(dx), new WlFixed(dy));
        }

        _lastPointerX = x;
        _lastPointerY = y;
        _havePointerPosition = true;
        _virtualPointer.Frame();
        _wayland?.Flush();
    }

    private void SendButton(int button, bool pressed)
    {
        Log.Debug($"nested input: button={button} pressed={pressed} xwayland={_xwayland}");
        if (_xwayland && _x11 is not null)
        {
            XTestNative.XTestFakeButtonEvent(_x11.Handle, (uint)Math.Max(1, button), pressed ? 1 : 0, 0);
            _x11.Sync();
            return;
        }

        if (_virtualPointer is not null)
        {
            uint evdev = button switch
            {
                1 => 0x110,
                2 => 0x112,
                3 => 0x111,
                8 => 0x113,
                9 => 0x114,
                _ => 0x110
            };
            _virtualPointer.Button((uint)Environment.TickCount64, evdev,
                pressed ? WlPointer.ButtonStateEnum.Pressed : WlPointer.ButtonStateEnum.Released);
            _virtualPointer.Frame();
            _wayland?.Flush();
        }
    }

    private void SendWheel(int direction)
    {
        if (_xwayland && _x11 is not null)
        {
            uint button = direction < 0 ? 4u : 5u;
            XTestNative.XTestFakeButtonEvent(_x11.Handle, button, 1, 0);
            XTestNative.XTestFakeButtonEvent(_x11.Handle, button, 0, 0);
            _x11.Sync();
            return;
        }

        if (_virtualPointer is not null)
        {
            uint time = (uint)Environment.TickCount64;
            _virtualPointer.AxisSource(WlPointer.AxisSourceEnum.Wheel);
            _virtualPointer.AxisDiscrete(time, WlPointer.AxisEnum.VerticalScroll,
                new WlFixed(direction), direction);
            _virtualPointer.Frame();
            _wayland?.Flush();
        }
    }

    private void SendAxis(double value)
    {
        if (_xwayland)
        {
            SendWheel(value < 0 ? -1 : 1);
            return;
        }

        if (_virtualPointer is not null)
        {
            uint time = (uint)Environment.TickCount64;
            _virtualPointer.AxisSource(WlPointer.AxisSourceEnum.Wheel);
            _virtualPointer.Axis(time, WlPointer.AxisEnum.VerticalScroll, new WlFixed(value));
            _virtualPointer.Frame();
            _wayland?.Flush();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _vkeyboard?.Dispose(); } catch { }
        try { _virtualPointer?.Destroy(); } catch { }
        try { _wayland?.Dispose(); } catch { }
        try { _x11?.Dispose(); } catch { }
    }

    private sealed class RegistryListener : WlRegistry.Listener
    {
        public Action<uint, string, uint>? GlobalAdded;
        protected override void Global(WlRegistry s, uint name, string iface, uint version)
            => GlobalAdded?.Invoke(name, iface, version);
    }

    private sealed class SeatListener : WlSeat.Listener { }
    private sealed class ManagerListener : ZwlrVirtualPointerManagerV1.Listener { }
    private sealed class PointerListener : ZwlrVirtualPointerV1.Listener { }
}
