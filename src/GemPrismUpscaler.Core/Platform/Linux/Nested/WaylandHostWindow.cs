using System.Runtime.InteropServices;
using GemPrismUpscaler.Core.Diagnostics;
using GemPrismUpscaler.Core.Platform.Linux;
using GemPrismUpscaler.Core.Rendering;
using Microsoft.Win32.SafeHandles;
using NWayland;
using NWayland.Protocols.CursorShapeV1;
using NWayland.Protocols.FractionalScaleV1;
using NWayland.Protocols.PointerWarpV1;
using NWayland.Protocols.Viewporter;
using NWayland.Protocols.Wayland;
using NWayland.Protocols.XdgDecorationUnstableV1;
using NWayland.Protocols.XdgShell;
using Silk.NET.Vulkan;

namespace GemPrismUpscaler.Core.Platform.Linux.Nested;

/// <summary>
/// Native Wayland (xdg_toplevel) host window for a nested game. Unlike the
/// X11/XWayland backend this client owns the <c>wl_surface</c> itself, so it
/// can use <c>wp_pointer_warp_v1</c> to move KWin's visible cursor to the
/// position the game asked for.
///
/// <para>Size model: <see cref="Width"/>/<see cref="Height"/> are presentation
/// pixels (the Vulkan swapchain extent). The compositor configures the window
/// in logical pixels; on a fractionally scaled output the presentation size is
/// <c>logical * scale</c> and a <c>wp_viewport</c> destination maps the buffer
/// back onto the logical size. The surface-local coordinate space (pointer
/// events, warp targets) is therefore the logical size, and events are
/// multiplied by the scale before they reach the session.</para>
///
/// All methods must run on the session render thread; <see cref="WarpPointer"/>
/// is the exception and only queues a request that the next <see cref="Pump"/>
/// executes, because the wl_display must not be touched from the cursor-sync
/// thread.
/// </summary>
public sealed class WaylandHostWindow : IHostWindow
{
    private const string AppId = "gemprism-upscaler";

    private WlDisplay _display = null!;
    private WlRegistry _registry = null!;
    private WlCompositor? _compositor;
    private XdgWmBase? _wmBase;
    private ZxdgDecorationManagerV1? _decorationManager;
    private ZxdgToplevelDecorationV1? _decoration;
    private WpViewporter? _viewporter;
    private WpViewport? _viewport;
    private WpFractionalScaleManagerV1? _fractionalManager;
    private WpFractionalScaleV1? _fractional;
    private WpPointerWarpV1? _warp;
    private WlSeat? _seat;
    private WlPointer? _pointer;
    private WlKeyboard? _keyboard;
    private WlSurface? _surface;
    private XdgSurface? _xdgSurface;
    private XdgToplevel? _toplevel;

    private readonly List<OutputEntry> _outputs = new();
    private readonly List<GlobalInfo> _globals = new();
    private OutputEntry? _output;

    private readonly RegistryListener _registryListener = new();
    private readonly SurfaceListener _surfaceListener = new();
    private readonly XdgSurfaceListener _xdgSurfaceListener = new();
    private readonly ToplevelListener _toplevelListener = new();
    private readonly WmBaseListener _wmBaseListener = new();
    private readonly SeatListener _seatListener = new();
    private readonly PointerListener _pointerListener;
    private readonly KeyboardListener _keyboardListener;
    private readonly FractionalScaleListener _fractionalScaleListener = new();
    private readonly DecorationListener _decorationListener = new();

    private double _scale = 1.0;
    private int _logicalWidth = 1;
    private int _logicalHeight = 1;
    private int _width = 64;
    private int _height = 64;
    private int _requestedWidth;
    private int _requestedHeight;

    private bool _configured;
    private bool _fullscreen;
    private bool _activated;
    private bool _iconified;
    private bool _visible;
    private bool _disposed;
    private bool _pumpFailed;
    private bool _firstConfigureLogged;

    private uint _pointerEnterSerial;
    private bool _pointerOnSurface;
    private bool _notchInFrame;
    private int _lastPointerX;
    private int _lastPointerY;

    private readonly object _warpGate = new();
    private bool _warpPending;
    private int _warpX;
    private int _warpY;
    private long _lastWarpWarnAt;

    private WpCursorShapeManagerV1? _cursorShapeManager;
    private WpCursorShapeDeviceV1? _cursorShapeDevice;
    private readonly object _cursorShapeGate = new();
    private string _cursorShape = "default";
    private string _pendingCursorShape = "default";
    private bool _cursorShapePending;

    private bool _pendingFullscreen;
    private bool _pendingActivated;
    private int _pendingWidth;
    private int _pendingHeight;

    public bool IsVisible => _visible;
    public bool IsFocused => _activated;
    public bool IsFullscreen => _fullscreen;
    public bool IsWayland => true;
    // xdg-shell has no "resize to WxH" request; the session recreates the
    // toplevel when the game window changes size.
    public bool CanResize => false;
    // A Wayland surface accepts a buffer of any size; wp_viewport maps it onto
    // the current logical window. That lets the session build the next
    // presentation size before KWin sends the fullscreen/unfullscreen configure.
    public bool CanPrepareSize => true;
    public int Width => _width;
    public int Height => _height;
    public string MonitorName => _output?.Name ?? string.Empty;
    public IntPtr VulkanDisplay => _display?.Handle ?? IntPtr.Zero;
    public IntPtr VulkanSurface => _surface?.Handle ?? IntPtr.Zero;

    public event Action<OverlayPointerEvent>? Pointer;
    public event Action<OverlayKeyEvent>? Key;
    public event Action<OverlayModifiers>? Modifiers;
    public event Action<bool>? FocusChanged;
    public event Action? Exposed;
    public event Action? Restored;
    public event Action? Closed;
    public event Action? CloseRequested;

    public WaylandHostWindow(string? outputName, string title, int originalWidth,
        int originalHeight, bool startFullscreen, WaylandHostWindow? parent, bool dialog)
    {
        _pointerListener = new PointerListener(this);
        _keyboardListener = new KeyboardListener(this);

        _registryListener.GlobalAdded = (name, iface, version) =>
            _globals.Add(new GlobalInfo(name, iface, version));
        _wmBaseListener.Pinged = OnPing;
        _xdgSurfaceListener.Configured = OnXdgSurfaceConfigure;
        _toplevelListener.Configured = OnToplevelConfigure;
        _toplevelListener.CloseRequested = () => CloseRequested?.Invoke();
        _seatListener.CapabilitiesChanged = OnSeatCapabilities;
        _fractionalScaleListener.ScaleChanged = scale => OnPreferredScale(scale / 120.0);

        _display = WlDisplay.Connect(null, new WlDisplayOptions());
        _registry = _display.GetRegistry(_registryListener, null);
        _display.Roundtrip();

        BindGlobals();
        if (_compositor is null) throw new InvalidOperationException("wl_compositor missing");
        if (_wmBase is null) throw new InvalidOperationException("xdg_wm_base missing");

        BindOutputs();
        _display.Roundtrip();
        SelectOutput(outputName);
        _scale = HostSurfaceGeometry.Sanitize(_output?.Scale ?? 1.0);

        CreateSurface(title, parent);

        _width = Math.Max(64, originalWidth);
        _height = Math.Max(64, originalHeight);
        _requestedWidth = _width;
        _requestedHeight = _height;
        _logicalWidth = ToLogical(_width);
        _logicalHeight = ToLogical(_height);
        ApplyViewport();
        ApplyWindowSizeLimits();

        _surface!.Commit();
        _display.Flush();
        PumpUntil(() => _configured, 1200);
        if (!_configured)
            Log.Warn("wayland host: no initial configure; continuing with the requested size");
        _visible = true;

        Log.Info($"wayland host: buffer={_width}x{_height} logical={_logicalWidth}x{_logicalHeight} " +
                 $"scale={_scale:0.###} output='{MonitorName}' fullscreen={startFullscreen} " +
                 $"warp={_warp is not null} viewport={_viewport is not null} " +
                 $"decor={(_decoration is not null ? "server" : "none")} dialog={dialog}");

        if (startFullscreen) SetFullscreen(true);
    }

    private void CreateSurface(string title, WaylandHostWindow? parent)
    {
        _surface = _compositor!.CreateSurface(_surfaceListener, null);
        _surface.SetBufferScale(1);
        if (_viewporter is not null)
            _viewport = _viewporter.GetViewport(_surface, new EmptyViewportListener(), null);
        if (_fractionalManager is not null)
            _fractional = _fractionalManager.GetFractionalScale(_surface,
                _fractionalScaleListener, null);

        _xdgSurface = _wmBase!.GetXdgSurface(_surface, _xdgSurfaceListener, null);
        _toplevel = _xdgSurface.GetToplevel(_toplevelListener, null);
        _toplevel.SetTitle(title);
        _toplevel.SetAppId(AppId);
        if (_decorationManager is not null)
        {
            _decoration = _decorationManager.GetToplevelDecoration(_toplevel,
                _decorationListener, null);
            // KWin draws the titlebar and frame; the client renders only game
            // pixels. Without this request a compositor may wait for a client
            // side decoration and the window looks undecorated.
            _decoration.SetMode(ZxdgToplevelDecorationV1.ModeEnum.ServerSide);
        }
        if (parent?._toplevel is not null)
            _toplevel.SetParent(parent._toplevel);
    }

    private void BindGlobals()
    {
        foreach (var g in _globals)
        {
            try
            {
                switch (g.Interface)
                {
                    case "wl_compositor" when _compositor is null:
                        _compositor = WlCompositor.Bind(_registry, g.Name, Math.Min(g.Version, 4),
                            new EmptyCompositorListener(), null);
                        break;
                    case "xdg_wm_base" when _wmBase is null:
                        _wmBase = XdgWmBase.Bind(_registry, g.Name, Math.Min(g.Version, 6),
                            _wmBaseListener, null);
                        break;
                    case "zxdg_decoration_manager_v1" when _decorationManager is null:
                        _decorationManager = ZxdgDecorationManagerV1.Bind(_registry, g.Name, 1,
                            new EmptyDecorationManagerListener(), null);
                        break;
                    case "wp_viewporter" when _viewporter is null:
                        _viewporter = WpViewporter.Bind(_registry, g.Name, 1,
                            new EmptyViewporterListener(), null);
                        break;
                    case "wp_fractional_scale_manager_v1" when _fractionalManager is null:
                        _fractionalManager = WpFractionalScaleManagerV1.Bind(_registry, g.Name, 1,
                            new EmptyFractionalScaleManagerListener(), null);
                        break;
                    case "wp_pointer_warp_v1" when _warp is null:
                        _warp = WpPointerWarpV1.Bind(_registry, g.Name, 1,
                            new EmptyWarpListener(), null);
                        break;
                    case "wp_cursor_shape_manager_v1" when _cursorShapeManager is null:
                        _cursorShapeManager = WpCursorShapeManagerV1.Bind(_registry, g.Name,
                            Math.Min(g.Version, 2), new EmptyCursorShapeManagerListener(), null);
                        break;
                    case "wl_seat" when _seat is null:
                        _seat = WlSeat.Bind(_registry, g.Name, Math.Min(g.Version, 7),
                            _seatListener, null);
                        break;
                    case "wl_output":
                        _outputs.Add(new OutputEntry(g.Name, g.Version));
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"wayland host bind {g.Interface} v{g.Version} failed: {ex.Message}");
            }
        }
    }

    private void BindOutputs()
    {
        foreach (var entry in _outputs)
        {
            try
            {
                entry.Output = WlOutput.Bind(_registry, entry.GlobalName, Math.Min(entry.Version, 4),
                    entry.Listener, null);
            }
            catch (Exception ex)
            {
                Log.Warn($"wayland host output bind failed: {ex.Message}");
            }
        }
    }

    private void SelectOutput(string? preferred)
    {
        OutputEntry? selected = null;
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            selected = _outputs.FirstOrDefault(o =>
                string.Equals(o.Name, preferred, StringComparison.OrdinalIgnoreCase));
            if (selected is null)
            {
                Log.Warn($"wayland host monitor '{preferred}' not found; " +
                         $"available={string.Join(", ", _outputs.Select(o => o.Name))}");
            }
        }
        selected ??= _outputs.FirstOrDefault(o => o.PhysicalWidth > 0);
        selected ??= _outputs.FirstOrDefault();
        _output = selected;
    }

    private void OnPreferredScale(double scale)
    {
        if (scale <= 0 || Math.Abs(scale - _scale) < 0.001) return;
        Log.Info($"wayland host fractional scale {_scale:0.###} -> {scale:0.###}");
        _scale = HostSurfaceGeometry.Sanitize(scale);
        if (!_configured)
        {
            // No compositor size yet: keep the requested pixel size and only
            // reinterpret it in logical units so the window maps 1:1.
            _logicalWidth = ToLogical(_width);
            _logicalHeight = ToLogical(_height);
            ApplyViewport();
            try { _surface?.Commit(); _display.Flush(); } catch { }
        }
    }

    private int ToLogical(int pixels) => HostSurfaceGeometry.ToLogical(pixels, _scale);
    private int ToPixels(int logical) => HostSurfaceGeometry.ToPixels(logical, _scale);

    private void ApplyViewport()
    {
        if (_surface is null) return;
        if (_viewport is not null)
        {
            _viewport.SetDestination(_logicalWidth, _logicalHeight);
        }
        else
        {
            // Without viewporter fall back to an integer buffer scale. The
            // surface-local size stays logicalWidth because the compositor
            // divides the buffer by that factor.
            int integer = Math.Max(1, (int)Math.Round(_scale));
            _surface.SetBufferScale(integer);
        }
    }

    private void OnPing(uint serial)
    {
        try
        {
            _wmBase?.Pong(serial);
            _display.Flush();
        }
        catch (Exception ex)
        {
            Log.Warn($"wayland host pong failed: {ex.Message}");
        }
    }

    private void OnToplevelConfigure(int width, int height, byte[] states)
    {
        // xdg_toplevel.configure carries the state enum array as raw u32
        // little-endian bytes in the generated binding.
        uint[] values = MemoryMarshal.Cast<byte, uint>(states).ToArray();
        bool Has(XdgToplevel.StateEnum value)
        {
            foreach (uint v in values) if (v == (uint)value) return true;
            return false;
        }

        _pendingWidth = width;
        _pendingHeight = height;
        _pendingFullscreen = Has(XdgToplevel.StateEnum.Fullscreen);
        _pendingActivated = Has(XdgToplevel.StateEnum.Activated);
    }

    private void OnXdgSurfaceConfigure(uint serial)
    {
        try
        {
            _xdgSurface?.AckConfigure(serial);
            ApplyConfigure();
            _surface?.Commit();
            _display.Flush();
        }
        catch (Exception ex)
        {
            Log.Warn($"wayland host configure failed: {ex.Message}");
        }
    }

    private void ApplyConfigure()
    {
        bool fullscreen = _pendingFullscreen;
        bool activated = _pendingActivated;
        bool stateChanged = fullscreen != _fullscreen || activated != _activated;

        _fullscreen = fullscreen;
        _configured = true;

        if (_pendingWidth > 0 && _pendingHeight > 0)
        {
            _logicalWidth = _pendingWidth;
            _logicalHeight = _pendingHeight;
        }
        else if (_logicalWidth <= 0)
        {
            _logicalWidth = ToLogical(_width);
            _logicalHeight = ToLogical(_height);
        }

        int bufferWidth = ToPixels(_logicalWidth);
        int bufferHeight = ToPixels(_logicalHeight);
        bool sizeChanged = bufferWidth != _width || bufferHeight != _height;
        _width = bufferWidth;
        _height = bufferHeight;
        ApplyViewport();

        if (!_firstConfigureLogged)
        {
            _firstConfigureLogged = true;
            Log.Info($"wayland host first configure logical={_logicalWidth}x{_logicalHeight} " +
                     $"buffer={_width}x{_height} fullscreen={_fullscreen} scale={_scale:0.###}");
        }
        else if (sizeChanged)
        {
            Log.Info($"wayland host reconfigured logical={_logicalWidth}x{_logicalHeight} " +
                     $"buffer={_width}x{_height} fullscreen={_fullscreen}");
        }

        if (activated != _activated)
        {
            _activated = activated;
            if (activated && _iconified)
            {
                _iconified = false;
                Restored?.Invoke();
            }
            FocusChanged?.Invoke(activated);
        }

        if (stateChanged || sizeChanged)
            Exposed?.Invoke();
    }

    public void SetFullscreen(bool fullscreen)
    {
        if (_disposed || _toplevel is null) return;
        if (_fullscreen == fullscreen) return;
        try
        {
            if (fullscreen)
            {
                // A fixed min=max size must not clamp the fullscreen configure.
                ClearWindowSizeLimits();
                // Pin the request to the monitor selected for this nested
                // output; without an explicit output the compositor may pick
                // whichever monitor currently has the active window.
                _toplevel.SetFullscreen(_output?.Output);
            }
            else
            {
                _toplevel.UnsetFullscreen();
            }
            _display.Flush();
        }
        catch (Exception ex)
        {
            Log.Warn($"wayland host fullscreen={fullscreen} failed: {ex.Message}");
            return;
        }

        PumpUntil(() => _fullscreen == fullscreen, 800);
        if (_fullscreen != fullscreen)
            Log.Warn($"wayland host fullscreen={fullscreen} not applied yet " +
                     $"(buffer={_width}x{_height})");

        if (!fullscreen)
        {
            if (_requestedWidth > 0 && _requestedHeight > 0 &&
                (Math.Abs(_requestedWidth - _width) > 2 ||
                 Math.Abs(_requestedHeight - _height) > 2))
                ApplyRequestedSize();
            else
                ApplyWindowSizeLimits();
        }
    }

    public void SetOriginalSize(int width, int height)
    {
        if (_disposed) return;
        _requestedWidth = Math.Max(64, width);
        _requestedHeight = Math.Max(64, height);
        if (_fullscreen) return;
        if (Math.Abs(_requestedWidth - _width) <= 2 && Math.Abs(_requestedHeight - _height) <= 2)
            return;
        ApplyRequestedSize();
    }

    /// <summary>
    /// xdg_toplevel has no "resize to" request. A min size equal to the max
    /// size is the portable way to ask the compositor for one exact size; the
    /// constraints are dropped again once applied so the window stays
    /// resizable.
    /// </summary>
    private void ApplyRequestedSize()
    {
        if (_toplevel is null || _fullscreen) return;
        int logicalWidth = ToLogical(_requestedWidth);
        int logicalHeight = ToLogical(_requestedHeight);
        try
        {
            _toplevel.SetMinSize(logicalWidth, logicalHeight);
            _toplevel.SetMaxSize(logicalWidth, logicalHeight);
            _display.Flush();
            PumpUntil(() => Math.Abs(_width - _requestedWidth) <= 2 &&
                            Math.Abs(_height - _requestedHeight) <= 2, 320);
            _toplevel.SetMinSize(0, 0);
            _toplevel.SetMaxSize(0, 0);
            _display.Flush();
        }
        catch (Exception ex)
        {
            Log.Warn($"wayland host resize {_requestedWidth}x{_requestedHeight} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Keep the window at exactly one size: KWin then hides the maximize button
    /// and the resize handles. The size follows the game window; the session
    /// rebuilds the host when the game changes size, so the user never needs to
    /// drag it.
    /// </summary>
    private void ApplyWindowSizeLimits()
    {
        if (_toplevel is null || _fullscreen || _logicalWidth <= 0 || _logicalHeight <= 0) return;
        try
        {
            _toplevel.SetMinSize(_logicalWidth, _logicalHeight);
            _toplevel.SetMaxSize(_logicalWidth, _logicalHeight);
            _display.Flush();
        }
        catch (Exception ex)
        {
            Log.Warn($"wayland host size limits failed: {ex.Message}");
        }
    }

    private void ClearWindowSizeLimits()
    {
        if (_toplevel is null) return;
        try
        {
            _toplevel.SetMinSize(0, 0);
            _toplevel.SetMaxSize(0, 0);
            _display.Flush();
        }
        catch (Exception ex)
        {
            Log.Warn($"wayland host size limit reset failed: {ex.Message}");
        }
    }

    private void OnSeatCapabilities(WlSeat.CapabilityEnum capabilities)
    {
        bool hasPointer = (capabilities & WlSeat.CapabilityEnum.Pointer) != 0;
        if (!hasPointer)
        {
            _cursorShapeDevice?.Dispose();
            _cursorShapeDevice = null;
            _pointer?.Dispose();
            _pointer = null;
            _pointerOnSurface = false;
        }
        else if (_pointer is null && _seat is not null)
        {
            try { _pointer = _seat.GetPointer(_pointerListener, null); }
            catch (Exception ex) { Log.Warn($"wayland host pointer unavailable: {ex.Message}"); }
        }
        if (_pointer is not null && _cursorShapeDevice is null && _cursorShapeManager is not null)
        {
            try
            {
                _cursorShapeDevice = _cursorShapeManager.GetPointer(_pointer,
                    new EmptyCursorShapeDeviceListener(), null);
            }
            catch (Exception ex)
            {
                Log.Warn($"wayland host cursor shape unavailable: {ex.Message}");
            }
        }

        bool hasKeyboard = (capabilities & WlSeat.CapabilityEnum.Keyboard) != 0;
        if (!hasKeyboard)
        {
            _keyboard?.Dispose();
            _keyboard = null;
        }
        else if (_keyboard is null && _seat is not null)
        {
            try { _keyboard = _seat.GetKeyboard(_keyboardListener, null); }
            catch (Exception ex) { Log.Warn($"wayland host keyboard unavailable: {ex.Message}"); }
        }
    }

    private void OnPointerEnter(uint serial, int logicalX, int logicalY)
    {
        _pointerEnterSerial = serial;
        _pointerOnSurface = true;
        ApplyCursorShape();
        _lastPointerX = ToPixelsX(logicalX);
        _lastPointerY = ToPixelsY(logicalY);
        Pointer?.Invoke(new OverlayPointerEvent(OverlayPointerKind.Enter,
            _lastPointerX, _lastPointerY));
    }

    private void OnPointerLeave()
    {
        _pointerOnSurface = false;
        Pointer?.Invoke(new OverlayPointerEvent(OverlayPointerKind.Leave, 0, 0));
    }

    private void OnPointerMotion(int logicalX, int logicalY)
    {
        _lastPointerX = ToPixelsX(logicalX);
        _lastPointerY = ToPixelsY(logicalY);
        Pointer?.Invoke(new OverlayPointerEvent(OverlayPointerKind.Motion,
            _lastPointerX, _lastPointerY));
    }

    private void OnPointerButton(int button, bool pressed)
        => Pointer?.Invoke(new OverlayPointerEvent(
            pressed ? OverlayPointerKind.ButtonPress : OverlayPointerKind.ButtonRelease,
            _lastPointerX, _lastPointerY, button));

    private void OnPointerAxis(double value)
    {
        if (_notchInFrame) return;
        Pointer?.Invoke(new OverlayPointerEvent(OverlayPointerKind.Axis,
            _lastPointerX, _lastPointerY,
            (int)Math.Clamp(Math.Round(value * 1000), -1_000_000, 1_000_000)));
    }

    private void OnPointerNotch(int direction)
    {
        _notchInFrame = true;
        Pointer?.Invoke(new OverlayPointerEvent(
            direction > 0 ? OverlayPointerKind.WheelDown : OverlayPointerKind.WheelUp,
            _lastPointerX, _lastPointerY));
    }

    private void OnPointerFrame() => _notchInFrame = false;

    private int ToPixelsX(int logical) => HostSurfaceGeometry.LocalToPixel(logical, _scale, _width);
    private int ToPixelsY(int logical) => HostSurfaceGeometry.LocalToPixel(logical, _scale, _height);

    public void Show()
    {
        if (_disposed) return;
        _visible = true;
        Exposed?.Invoke();
    }

    public void Hide()
    {
        // A Wayland toplevel cannot be unmapped without destroying its
        // surface, which loses the compositor-side window state (fullscreen,
        // decoration, placement). Nothing in the nested flow needs Hide yet.
        if (_disposed || !_visible) return;
        Log.Warn("wayland host: Hide() is not supported on a native Wayland window");
    }

    public void Focus() { }

    public void Activate()
    {
        // Wayland has no focus-stealing request. The compositor focuses the
        // window on user interaction; xdg_activation would need a token from
        // a real user action.
    }

    public void Raise()
    {
        // No stacking request in xdg-shell: the compositor decides.
    }

    public void Iconify()
    {
        if (_disposed || _toplevel is null || _iconified) return;
        _iconified = true;
        try
        {
            // xdg_toplevel has no unminimize request, so a host minimized from
            // the taskbar can only come back through the compositor.
            _toplevel.SetMinimized();
            _display.Flush();
        }
        catch (Exception ex)
        {
            Log.Warn($"wayland host minimize failed: {ex.Message}");
        }
    }

    public void RestoreFromIconify()
    {
        if (_disposed || !_iconified) return;
        Log.Warn("wayland host: cannot un-minimize a native Wayland window from the client; " +
                 "restore it from the taskbar");
    }

    public void SetTitle(string title)
    {
        if (_disposed || _toplevel is null || string.IsNullOrWhiteSpace(title)) return;
        try
        {
            _toplevel.SetTitle(title);
            _display.Flush();
        }
        catch (Exception ex)
        {
            Log.Warn($"wayland host title failed: {ex.Message}");
        }
    }

    public bool WarpPointer(int x, int y)
    {
        if (_disposed || _warp is null || _pointer is null || _surface is null) return false;
        if (_pointerEnterSerial == 0) return false;
        lock (_warpGate)
        {
            _warpPending = true;
            _warpX = x;
            _warpY = y;
        }
        return true;
    }

    /// <summary>
    /// Forward the game's cursor shape name (wlr/cursor-shape naming) to KWin.
    /// Empty means the client used an image cursor, which we cannot forward
    /// yet; fall back to the theme default.
    /// </summary>
    public void SetCursorShape(string shape)
    {
        string normalized = string.IsNullOrWhiteSpace(shape) ? "default" : shape;
        lock (_cursorShapeGate)
        {
            _pendingCursorShape = normalized;
            _cursorShapePending = true;
        }
    }

    private void ProcessPendingCursorShape()
    {
        string shape;
        lock (_cursorShapeGate)
        {
            if (!_cursorShapePending) return;
            _cursorShapePending = false;
            shape = _pendingCursorShape;
        }
        if (shape == _cursorShape) return;
        _cursorShape = shape;
        ApplyCursorShape();
    }

    private void ApplyCursorShape()
    {
        if (_cursorShapeDevice is null || _pointerEnterSerial == 0) return;
        try
        {
            _cursorShapeDevice.SetShape(_pointerEnterSerial, ParseCursorShape(_cursorShape));
            _display.Flush();
        }
        catch (Exception ex)
        {
            Log.Warn($"wayland host cursor shape failed: {ex.Message}");
        }
    }

    private static WpCursorShapeDeviceV1.ShapeEnum ParseCursorShape(string name) => name switch
    {
        "context-menu" => WpCursorShapeDeviceV1.ShapeEnum.ContextMenu,
        "help" => WpCursorShapeDeviceV1.ShapeEnum.Help,
        "pointer" => WpCursorShapeDeviceV1.ShapeEnum.Pointer,
        "progress" => WpCursorShapeDeviceV1.ShapeEnum.Progress,
        "wait" => WpCursorShapeDeviceV1.ShapeEnum.Wait,
        "cell" => WpCursorShapeDeviceV1.ShapeEnum.Cell,
        "crosshair" => WpCursorShapeDeviceV1.ShapeEnum.Crosshair,
        "text" => WpCursorShapeDeviceV1.ShapeEnum.Text,
        "vertical-text" => WpCursorShapeDeviceV1.ShapeEnum.VerticalText,
        "alias" => WpCursorShapeDeviceV1.ShapeEnum.Alias,
        "copy" => WpCursorShapeDeviceV1.ShapeEnum.Copy,
        "move" => WpCursorShapeDeviceV1.ShapeEnum.Move,
        "no-drop" => WpCursorShapeDeviceV1.ShapeEnum.NoDrop,
        "not-allowed" => WpCursorShapeDeviceV1.ShapeEnum.NotAllowed,
        "grab" => WpCursorShapeDeviceV1.ShapeEnum.Grab,
        "grabbing" => WpCursorShapeDeviceV1.ShapeEnum.Grabbing,
        "e-resize" => WpCursorShapeDeviceV1.ShapeEnum.EResize,
        "n-resize" => WpCursorShapeDeviceV1.ShapeEnum.NResize,
        "ne-resize" => WpCursorShapeDeviceV1.ShapeEnum.NeResize,
        "nw-resize" => WpCursorShapeDeviceV1.ShapeEnum.NwResize,
        "s-resize" => WpCursorShapeDeviceV1.ShapeEnum.SResize,
        "se-resize" => WpCursorShapeDeviceV1.ShapeEnum.SeResize,
        "sw-resize" => WpCursorShapeDeviceV1.ShapeEnum.SwResize,
        "w-resize" => WpCursorShapeDeviceV1.ShapeEnum.WResize,
        "ew-resize" => WpCursorShapeDeviceV1.ShapeEnum.EwResize,
        "ns-resize" => WpCursorShapeDeviceV1.ShapeEnum.NsResize,
        "nesw-resize" => WpCursorShapeDeviceV1.ShapeEnum.NeswResize,
        "nwse-resize" => WpCursorShapeDeviceV1.ShapeEnum.NwseResize,
        "col-resize" => WpCursorShapeDeviceV1.ShapeEnum.ColResize,
        "row-resize" => WpCursorShapeDeviceV1.ShapeEnum.RowResize,
        "all-scroll" => WpCursorShapeDeviceV1.ShapeEnum.AllScroll,
        "zoom-in" => WpCursorShapeDeviceV1.ShapeEnum.ZoomIn,
        "zoom-out" => WpCursorShapeDeviceV1.ShapeEnum.ZoomOut,
        "dnd-ask" => WpCursorShapeDeviceV1.ShapeEnum.DndAsk,
        "all-resize" => WpCursorShapeDeviceV1.ShapeEnum.AllResize,
        _ => WpCursorShapeDeviceV1.ShapeEnum.Default,
    };

    private void ProcessPendingState()
    {
        ProcessPendingWarp();
        ProcessPendingCursorShape();
    }

    private void ProcessPendingWarp()
    {
        if (_warp is null || _pointer is null || _surface is null) return;
        int x, y;
        lock (_warpGate)
        {
            if (!_warpPending) return;
            _warpPending = false;
            x = _warpX;
            y = _warpY;
        }

        // The compositor ignores a warp whose serial does not belong to the
        // pointer's current enter, so a stale serial is harmless. Do not gate
        // this on our own enter/leave bookkeeping: KWin sometimes reports a
        // leave right after a warp while the pointer stays on the window.
        if (_pointerEnterSerial == 0)
        {
            long stale = Environment.TickCount64;
            if (stale - _lastWarpWarnAt > 2000)
            {
                _lastWarpWarnAt = stale;
                Log.Info("wayland host warp skipped: no pointer enter serial yet");
            }
            return;
        }

        int logicalX = HostSurfaceGeometry.PixelToLocal(x, _scale, _logicalWidth);
        int logicalY = HostSurfaceGeometry.PixelToLocal(y, _scale, _logicalHeight);
        if (!_pointerOnSurface)
        {
            // Every pointer event so far left the surface (the user is over
            // another window). The warp is still sent: the compositor ignores
            // it, and our own leave bookkeeping has been wrong after a warp.
            Log.Debug("wayland host warp sent while the pointer is outside the window");
        }
        try
        {
            _warp.WarpPointer(_surface, _pointer, new WlFixed(logicalX), new WlFixed(logicalY),
                _pointerEnterSerial);
            _display.Flush();
            Log.Debug($"wayland host warp buffer={x},{y} logical={logicalX},{logicalY} " +
                      $"serial={_pointerEnterSerial}");
        }
        catch (Exception ex)
        {
            Log.Warn($"wayland host warp failed: {ex.Message}");
        }
    }

    public void Pump() => PumpOnce(0);

    private void PumpOnce(int timeoutMs)
    {
        if (_disposed) return;
        try
        {
            if (_display.PrepareRead() < 0)
            {
                _display.DispatchPending();
                ProcessPendingState();
                return;
            }

            int fd = _display.GetFd();
            var pfds = new[] { new PollFd { Fd = fd, Events = PollIn, Revents = 0 } };
            int rc = poll(pfds, 1, timeoutMs);
            if (rc <= 0 || (pfds[0].Revents & PollIn) == 0)
            {
                _display.CancelRead();
                _display.DispatchPending();
                ProcessPendingState();
                return;
            }

            if (_display.ReadEvents() < 0)
            {
                _display.CancelRead();
                _display.DispatchPending();
                ProcessPendingState();
                return;
            }

            _display.DispatchPending();
            _display.Flush();
            ProcessPendingState();
        }
        catch (Exception ex)
        {
            if (!_pumpFailed)
            {
                _pumpFailed = true;
                Log.Warn($"wayland host pump failed: {ex.GetType().Name}: {ex.Message}");
                Closed?.Invoke();
            }
        }
    }

    private void PumpUntil(Func<bool> condition, int timeoutMs)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (!_disposed && Environment.TickCount64 < deadline)
        {
            PumpOnce(5);
            if (condition()) return;
        }
    }

    public VulkanBlitter CreateBlitter(EffectDefinition effect, bool sourceRgb)
        => CreateBlitter(effect, sourceRgb, _width, _height);

    public VulkanBlitter CreateBlitter(EffectDefinition effect, bool sourceRgb, int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        var blitter = new VulkanBlitter(_display.Handle, _surface!.Handle, width, height,
            effect, sourceRgb, preferredPresentMode: PresentModeKHR.MailboxKhr);
        if (width != _width || height != _height)
        {
            // The window keeps its current logical size (and viewport
            // destination); only the presentation buffer grows/shrinks. KWin
            // scales it until the configure for the new window size arrives.
            _width = width;
            _height = height;
            ApplyViewport();
        }
        return blitter;
    }

    // ---------------------------------------------------------------- listeners

    private sealed class RegistryListener : WlRegistry.Listener
    {
        public Action<uint, string, uint>? GlobalAdded;

        protected override void Global(WlRegistry s, uint name, string @interface, uint version)
            => GlobalAdded?.Invoke(name, @interface, version);

        protected override void GlobalRemove(WlRegistry s, uint name) { }
    }

    private sealed class SurfaceListener : WlSurface.Listener { }

    private sealed class EmptyCompositorListener : WlCompositor.Listener { }
    private sealed class EmptyViewporterListener : WpViewporter.Listener { }
    private sealed class EmptyViewportListener : WpViewport.Listener { }
    private sealed class EmptyFractionalScaleManagerListener : WpFractionalScaleManagerV1.Listener { }
    private sealed class EmptyDecorationManagerListener : ZxdgDecorationManagerV1.Listener { }
    private sealed class EmptyWarpListener : WpPointerWarpV1.Listener { }
    private sealed class EmptyCursorShapeManagerListener : WpCursorShapeManagerV1.Listener { }
    private sealed class EmptyCursorShapeDeviceListener : WpCursorShapeDeviceV1.Listener { }

    private sealed class WmBaseListener : XdgWmBase.Listener
    {
        public Action<uint>? Pinged;
        protected override void Ping(XdgWmBase s, uint serial) => Pinged?.Invoke(serial);
    }

    private sealed class XdgSurfaceListener : XdgSurface.Listener
    {
        public Action<uint>? Configured;
        protected override void Configure(XdgSurface s, uint serial) => Configured?.Invoke(serial);
    }

    private sealed class ToplevelListener : XdgToplevel.Listener
    {
        public Action<int, int, byte[]>? Configured;
        public Action? CloseRequested;

        protected override void Configure(XdgToplevel s, int width, int height,
            ReadOnlySpan<byte> states)
            => Configured?.Invoke(width, height, states.ToArray());

        protected override void Close(XdgToplevel s) => CloseRequested?.Invoke();
    }

    private sealed class DecorationListener : ZxdgToplevelDecorationV1.Listener
    {
        protected override void Configure(ZxdgToplevelDecorationV1 s,
            ZxdgToplevelDecorationV1.ModeEnum mode) { }
    }

    private sealed class FractionalScaleListener : WpFractionalScaleV1.Listener
    {
        public Action<uint>? ScaleChanged;
        protected override void PreferredScale(WpFractionalScaleV1 s, uint scale)
            => ScaleChanged?.Invoke(scale);
    }

    private sealed class SeatListener : WlSeat.Listener
    {
        public Action<WlSeat.CapabilityEnum>? CapabilitiesChanged;
        protected override void Capabilities(WlSeat s, WlSeat.CapabilityEnum capabilities)
            => CapabilitiesChanged?.Invoke(capabilities);
    }

    private sealed class PointerListener : WlPointer.Listener
    {
        private readonly WaylandHostWindow _window;
        public PointerListener(WaylandHostWindow window) => _window = window;

        protected override void Enter(WlPointer s, uint serial, WlSurface? surface,
            WlFixed surfaceX, WlFixed surfaceY)
            => _window.OnPointerEnter(serial, (int)Math.Round((double)surfaceX),
                (int)Math.Round((double)surfaceY));

        protected override void Leave(WlPointer s, uint serial, WlSurface? surface)
            => _window.OnPointerLeave();

        protected override void Motion(WlPointer s, uint time, WlFixed surfaceX, WlFixed surfaceY)
            => _window.OnPointerMotion((int)Math.Round((double)surfaceX),
                (int)Math.Round((double)surfaceY));

        protected override void Button(WlPointer s, uint serial, uint time, uint button,
            WlPointer.ButtonStateEnum state)
            => _window.OnPointerButton(MapButton(button),
                state == WlPointer.ButtonStateEnum.Pressed);

        protected override void Axis(WlPointer s, uint time, WlPointer.AxisEnum axis, WlFixed value)
        {
            if (axis != WlPointer.AxisEnum.VerticalScroll) return;
            _window.OnPointerAxis((double)value);
        }

        protected override void AxisDiscrete(WlPointer s, WlPointer.AxisEnum axis, int discrete)
        {
            if (axis != WlPointer.AxisEnum.VerticalScroll) return;
            _window.OnPointerNotch(discrete);
        }

        protected override void AxisValue120(WlPointer s, WlPointer.AxisEnum axis, int value120)
        {
            if (axis != WlPointer.AxisEnum.VerticalScroll) return;
            // One immediate notch is enough for games; the value is kept in
            // 120ths of a wheel step so a high resolution wheel still scrolls.
            if (value120 != 0) _window.OnPointerNotch(value120);
        }

        protected override void Frame(WlPointer s) => _window.OnPointerFrame();

        private static int MapButton(uint evdev) => evdev switch
        {
            0x110 => 1, // BTN_LEFT
            0x112 => 2, // BTN_MIDDLE
            0x111 => 3, // BTN_RIGHT
            0x113 => 8, // BTN_SIDE
            0x114 => 9, // BTN_EXTRA
            _ => 1
        };
    }

    private sealed class KeyboardListener : WlKeyboard.Listener
    {
        private readonly WaylandHostWindow _window;
        public KeyboardListener(WaylandHostWindow window) => _window = window;

        protected override void Keymap(WlKeyboard s, WlKeyboard.KeymapFormatEnum format,
            WaylandFd fd, uint size)
        {
            // Keys are forwarded as evdev codes, so the compositor keymap is
            // not needed; the fd still has to be consumed and closed.
            try
            {
                int rawFd = fd.Consume();
                using var handle = new SafeFileHandle((IntPtr)rawFd, ownsHandle: true);
            }
            catch { }
        }

        protected override void Key(WlKeyboard s, uint serial, uint time, uint key,
            WlKeyboard.KeyStateEnum state)
        {
            var value = state switch
            {
                WlKeyboard.KeyStateEnum.Pressed => OverlayKeyState.Pressed,
                WlKeyboard.KeyStateEnum.Repeated => OverlayKeyState.Repeated,
                _ => OverlayKeyState.Released
            };
            _window.Key?.Invoke(new OverlayKeyEvent((int)key, value));
        }

        protected override void Modifiers(WlKeyboard s, uint serial, uint depressed,
            uint latched, uint locked, uint group)
            => _window.Modifiers?.Invoke(new OverlayModifiers(
                (int)depressed, (int)latched, (int)locked, (int)group));
    }

    private sealed class OutputListener : WlOutput.Listener
    {
        private readonly OutputEntry _entry;
        public OutputListener(OutputEntry entry) => _entry = entry;

        protected override void Name(WlOutput s, string name)
        {
            if (string.IsNullOrWhiteSpace(_entry.Name)) _entry.Name = name;
        }

        protected override void Scale(WlOutput s, int factor)
            => _entry.Scale = Math.Max(1, factor);

        protected override void Mode(WlOutput s, WlOutput.ModeEnum flags, int width, int height,
            int refresh)
        {
            if ((flags & WlOutput.ModeEnum.Current) == 0) return;
            _entry.PhysicalWidth = width;
            _entry.PhysicalHeight = height;
        }
    }

    private sealed class OutputEntry
    {
        public uint GlobalName { get; }
        public uint Version { get; }
        public WlOutput? Output { get; set; }
        public string Name { get; set; } = string.Empty;
        public int PhysicalWidth { get; set; }
        public int PhysicalHeight { get; set; }
        public double Scale { get; set; } = 1.0;
        public OutputListener Listener { get; }

        public OutputEntry(uint globalName, uint version)
        {
            GlobalName = globalName;
            Version = version;
            Listener = new OutputListener(this);
        }
    }

    private readonly record struct GlobalInfo(uint Name, string Interface, uint Version);

    private struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    private const short PollIn = 1;

    [DllImport("libc", SetLastError = true)]
    private static extern int poll([In, Out] PollFd[] fds, nuint nfds, int timeout);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _visible = false;
        try
        {
            _pointer?.Dispose();
            _pointer = null;
            _keyboard?.Dispose();
            _keyboard = null;
            if (_seat?.IsReleaseAvailable == true) _seat.Release();
            _seat = null;

            _warp?.Destroy();
            _warp = null;
            _decoration?.Destroy();
            _decoration = null;
            _toplevel?.Destroy();
            _toplevel = null;
            _xdgSurface?.Destroy();
            _xdgSurface = null;
            _viewport?.Destroy();
            _viewport = null;
            _fractional?.Destroy();
            _fractional = null;
            _surface?.Destroy();
            _surface = null;

            foreach (var output in _outputs)
            {
                if (output.Output?.IsReleaseAvailable == true) output.Output.Release();
                output.Output = null;
            }
            _outputs.Clear();

            _fractionalManager?.Destroy();
            _fractionalManager = null;
            _decorationManager?.Destroy();
            _decorationManager = null;
            _wmBase?.Destroy();
            _wmBase = null;
            _viewporter?.Destroy();
            _viewporter = null;
            if (_compositor?.IsReleaseAvailable == true) _compositor.Release();
            _compositor = null;
            _registry.Dispose();
            _display.Flush();
            _display.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn($"wayland host dispose: {ex.Message}");
        }
    }
}