using System.Runtime.InteropServices;
using GemPrismUpscaler.Core.Diagnostics;
using GemPrismUpscaler.Core.Geometry;
using NWayland;
using NWayland.Protocols.FractionalScaleV1;
using NWayland.Protocols.Viewporter;
using NWayland.Protocols.Wayland;
using NWayland.Protocols.Wlr.WlrLayerShellUnstableV1;
using NWayland.Protocols.XdgOutputUnstableV1;

namespace GemPrismUpscaler.Core.Platform.Linux.Wayland;

public sealed record WaylandOutputInfo(
    string Name,
    int LogicalX,
    int LogicalY,
    int LogicalWidth,
    int LogicalHeight,
    int PhysicalWidth,
    int PhysicalHeight,
    double Scale)
{
    public override string ToString() =>
        $"{Name} logical={LogicalWidth}x{LogicalHeight}+{LogicalX}+{LogicalY} " +
        $"physical={PhysicalWidth}x{PhysicalHeight} scale={Scale:0.###}";
}

/// <summary>
/// Native Wayland presentation surface using wlr-layer-shell. One instance
/// owns one output's overlay. Pointer events are raised in physical pixels so
/// the engine can reuse the X11 CoordinateMapper/XSendEvent path.
///
/// All methods must run on the engine/render thread. The class is not
/// thread-safe by design; UI/tray code only posts requests through UpscaleSession.
/// </summary>
public sealed class WaylandOverlay : IOverlay
{
    private const string LayerNamespace = "gemprism-overlay";

    private WlDisplay _display = null!;
    private WlRegistry _registry = null!;
    private WlCompositor? _compositor;
    private ZwlrLayerShellV1? _layerShell;
    private WpViewporter? _viewporter;
    private WpViewport? _viewport;
    private WpFractionalScaleManagerV1? _fractionalScaleManager;
    private WpFractionalScaleV1? _fractionalScale;
    private ZxdgOutputManagerV1? _xdgOutputManager;
    private WlSeat? _seat;
    private WlPointer? _pointer;
    private WlSurface? _surface;
    private ZwlrLayerSurfaceV1? _layerSurface;
    private WlRegion? _inputRegion;

    private readonly List<OutputEntry> _outputs = new();
    private OutputEntry? _output;
    private readonly List<GlobalInfo> _globals = new();
    private readonly RegistryListener _registryListener = new();
    private readonly SurfaceListener _surfaceListener = new();
    private readonly LayerSurfaceListener _layerSurfaceListener = new();
    private readonly SeatListener _seatListener = new();
    private readonly PointerListener _pointerListener = new();
    private readonly FractionalScaleListener _fractionalScaleListener = new();

    private bool _configured;
    private bool _visible = true;
    private bool _pointerForwarding;
    private bool _disposed;
    private bool _selectedMissing;
    private bool _outputsChanged;
    private bool _closed;
    private int _logicalWidth;
    private int _logicalHeight;
    private int _physicalWidth;
    private int _physicalHeight;
    private double _scale = 1.0;
    private RectI? _pendingInputRegion;
    private double _wheelAccumulator;
    private int _lastX;
    private int _lastY;

    public bool IsVisible => _visible;
    public int Width => _physicalWidth;
    public int Height => _physicalHeight;
    public double Scale => _scale;
    public string OutputName => _output?.Name ?? string.Empty;
    public bool IsWayland => true;
    public IntPtr X11Window => IntPtr.Zero;
    public IntPtr VulkanDisplay => _display.Handle;
    public IntPtr VulkanSurface => _surface?.Handle ?? IntPtr.Zero;

    public event Action<OverlayPointerEvent>? Pointer;

    public bool IsConfigured => _configured;
    public bool SelectedOutputMissing => _selectedMissing;
    public bool OutputsChanged => _outputsChanged;
    public bool IsClosed => _closed;

    public IReadOnlyList<WaylandOutputInfo> Outputs =>
        _outputs.Select(o => new WaylandOutputInfo(o.Name, o.LogicalX, o.LogicalY,
            o.LogicalWidth, o.LogicalHeight, o.PhysicalWidth, o.PhysicalHeight, o.Scale)).ToList();

    private WaylandOverlay() { }

    /// <summary>
    /// Connect to the compositor, bind globals and create a layer surface on
    /// <paramref name="preferredOutput"/> (or the first output). Returns null
    /// when Wayland is unavailable or layer-shell is missing.
    /// </summary>
    public static WaylandOverlay? TryCreate(string? preferredOutput, bool forwardPointer)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
            return null;

        var overlay = new WaylandOverlay();
        try
        {
            overlay.Initialize(preferredOutput, forwardPointer);
            return overlay;
        }
        catch (Exception ex)
        {
            Log.Warn($"wayland overlay unavailable: {ex.GetType().Name}: {ex.Message}");
            overlay.Dispose();
            return null;
        }
    }

    private void Initialize(string? preferredOutput, bool forwardPointer)
    {
        _registryListener.GlobalAdded = (name, iface, version) =>
            _globals.Add(new GlobalInfo(name, iface, version));
        _registryListener.GlobalRemoved = OnGlobalRemoved;
        _layerSurfaceListener.Configured = OnConfigure;
        _layerSurfaceListener.ClosedEvent = () => _closed = true;
        _seatListener.CapabilitiesChanged = OnSeatCapabilities;
        _pointerListener.Event = OnPointerEvent;
        _fractionalScaleListener.ScaleChanged = scale => _fractionalScaleScale = scale / 120.0;

        _display = WlDisplay.Connect(null, new WlDisplayOptions());
        _registry = _display.GetRegistry(_registryListener, null);
        _display.Roundtrip();

        BindGlobals();
        if (_compositor is null) throw new InvalidOperationException("wl_compositor missing");
        if (_layerShell is null) throw new InvalidOperationException("zwlr_layer_shell_v1 missing");
        if (_viewporter is null) throw new InvalidOperationException("wp_viewporter missing");
        if (_outputs.Count == 0) throw new InvalidOperationException("no wl_output");

        BindOutputs();
        _display.Roundtrip();

        SelectOutput(preferredOutput);
        CreateLayerSurface();
        SetPointerForwarding(forwardPointer);

        // Layer-shell configure normally arrives on the next roundtrip. Use a
        // bounded pump loop so a broken compositor cannot hang startup.
        var deadline = Environment.TickCount64 + 5000;
        while (!_configured && Environment.TickCount64 < deadline)
            Pump(20);
        if (!_configured) throw new TimeoutException("layer-shell configure timeout");
        if (_physicalWidth <= 0 || _physicalHeight <= 0)
            throw new InvalidOperationException("layer-shell configured with empty size");

        if (_pendingInputRegion is { } pending)
            SetInputRegion(pending);
        else
            SetInputRegion(null);
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
                    case "zwlr_layer_shell_v1" when _layerShell is null:
                        _layerShell = ZwlrLayerShellV1.Bind(_registry, g.Name, Math.Min(g.Version, 4),
                            new EmptyLayerShellListener(), null);
                        break;
                    case "wp_viewporter" when _viewporter is null:
                        _viewporter = WpViewporter.Bind(_registry, g.Name, 1,
                            new EmptyViewporterListener(), null);
                        break;
                    case "wp_fractional_scale_manager_v1" when _fractionalScaleManager is null:
                        _fractionalScaleManager = WpFractionalScaleManagerV1.Bind(_registry, g.Name, 1,
                            new EmptyFractionalScaleManagerListener(), null);
                        break;
                    case "zxdg_output_manager_v1" when _xdgOutputManager is null:
                        _xdgOutputManager = ZxdgOutputManagerV1.Bind(_registry, g.Name, Math.Min(g.Version, 3),
                            new EmptyXdgOutputManagerListener(), null);
                        break;
                    case "wl_seat" when _seat is null:
                        _seat = WlSeat.Bind(_registry, g.Name, Math.Min(g.Version, 7), _seatListener, null);
                        break;
                    case "wl_output":
                        _outputs.Add(new OutputEntry(g.Name, g.Version));
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"wayland bind {g.Interface} v{g.Version} failed: {ex.Message}");
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
                if (_xdgOutputManager is not null)
                {
                    entry.XdgOutput = _xdgOutputManager.GetXdgOutput(entry.Output, entry.XdgListener, null);
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"wayland output bind failed: {ex.Message}");
            }
        }
    }

    private void SelectOutput(string? preferredOutput)
    {
        OutputEntry? selected = null;
        if (!string.IsNullOrWhiteSpace(preferredOutput))
        {
            selected = _outputs.FirstOrDefault(o =>
                string.Equals(o.Name, preferredOutput, StringComparison.OrdinalIgnoreCase));
        }
        selected ??= _outputs.FirstOrDefault(o => o.PhysicalWidth > 0 && o.PhysicalHeight > 0);
        selected ??= _outputs[0];
        _output = selected;
        _scale = selected.Scale;
    }

    private void CreateLayerSurface()
    {
        if (_compositor is null || _layerShell is null || _output?.Output is null)
            throw new InvalidOperationException("missing compositor/layer-shell/output");

        _surface = _compositor.CreateSurface(_surfaceListener, null);
        _viewport = _viewporter!.GetViewport(_surface, new EmptyViewportListener(), null);
        if (_fractionalScaleManager is not null)
            _fractionalScale = _fractionalScaleManager.GetFractionalScale(_surface, _fractionalScaleListener, null);

        _layerSurface = _layerShell.GetLayerSurface(_surface, _output.Output,
            ZwlrLayerShellV1.LayerEnum.Overlay, LayerNamespace, _layerSurfaceListener, null);
        _layerSurface.SetAnchor(ZwlrLayerSurfaceV1.AnchorEnum.Top |
                                ZwlrLayerSurfaceV1.AnchorEnum.Bottom |
                                ZwlrLayerSurfaceV1.AnchorEnum.Left |
                                ZwlrLayerSurfaceV1.AnchorEnum.Right);
        _layerSurface.SetExclusiveZone(-1);
        _layerSurface.SetKeyboardInteractivity(ZwlrLayerSurfaceV1.KeyboardInteractivityEnum.None);
        _layerSurface.SetSize(0, 0);
        _surface.SetBufferScale(1);
        _surface.Commit();
        _display.Flush();
    }

    private void OnConfigure(uint serial, uint width, uint height)
    {
        _layerSurface?.AckConfigure(serial);

        var output = _output;
        int scaleInt = output?.ScaleInt ?? 1;
        double scale = _fractionalScaleScale > 0 ? _fractionalScaleScale : (output?.Scale ?? Math.Max(1, scaleInt));

        int logicalW = width > 0 ? (int)width : (output?.LogicalWidth ?? 0);
        int logicalH = height > 0 ? (int)height : (output?.LogicalHeight ?? 0);
        if (logicalW <= 0 || logicalH <= 0)
            throw new InvalidOperationException("layer-shell configure has no usable size");

        // Some compositors report the configure size in physical pixels. Use
        // the output's logical/physical sizes to disambiguate.
        int physicalW;
        int physicalH;
        if (output is not null && output.PhysicalWidth > 0 &&
            Math.Abs(logicalW - output.PhysicalWidth) <= 2)
        {
            physicalW = logicalW;
            physicalH = logicalH;
            logicalW = (int)Math.Round(physicalW / scale);
            logicalH = (int)Math.Round(physicalH / scale);
        }
        else
        {
            physicalW = (int)Math.Round(logicalW * scale);
            physicalH = (int)Math.Round(logicalH * scale);
        }

        _scale = scale;
        _logicalWidth = Math.Max(1, logicalW);
        _logicalHeight = Math.Max(1, logicalH);
        _physicalWidth = Math.Max(1, physicalW);
        _physicalHeight = Math.Max(1, physicalH);
        _configured = true;

        try
        {
            _viewport?.SetDestination(_logicalWidth, _logicalHeight);
            _surface?.Commit();
            _display.Flush();
        }
        catch (Exception ex)
        {
            Log.Warn($"wayland layer configure apply failed: {ex.Message}");
        }

        Log.Info($"wayland overlay configured output='{OutputName}' " +
                 $"logical={_logicalWidth}x{_logicalHeight} physical={_physicalWidth}x{_physicalHeight} scale={_scale:0.###}");
    }

    private void OnGlobalRemoved(uint name)
    {
        var entry = _outputs.FirstOrDefault(o => o.GlobalName == name);
        if (entry is null) return;
        _outputsChanged = true;
        bool selected = ReferenceEquals(entry, _output);
        try
        {
            entry.XdgOutput?.Destroy();
            entry.Output?.Release();
        }
        catch (Exception ex)
        {
            Log.Warn($"wayland output remove: {ex.Message}");
        }
        _outputs.Remove(entry);
        if (selected)
        {
            _selectedMissing = true;
            Log.Warn($"wayland selected output '{entry.Name}' removed");
        }
    }

    private void OnSeatCapabilities(WlSeat.CapabilityEnum capabilities)
    {
        bool hasPointer = (capabilities & WlSeat.CapabilityEnum.Pointer) != 0;
        if (!hasPointer)
        {
            _pointer?.Dispose();
            _pointer = null;
            return;
        }

        if (!_pointerForwarding || _pointer is not null) return;
        try
        {
            _pointer = _seat?.GetPointer(_pointerListener, null);
        }
        catch (Exception ex)
        {
            Log.Warn($"wayland pointer unavailable: {ex.Message}");
        }
    }

    private void OnPointerEvent(OverlayPointerEvent e)
    {
        switch (e.Kind)
        {
            case OverlayPointerKind.Motion:
            case OverlayPointerKind.Enter:
                _lastX = (int)Math.Round(e.X * _scale);
                _lastY = (int)Math.Round(e.Y * _scale);
                Pointer?.Invoke(new OverlayPointerEvent(e.Kind, _lastX, _lastY));
                break;
            case OverlayPointerKind.Leave:
                Pointer?.Invoke(e);
                break;
            case OverlayPointerKind.Axis:
                _wheelAccumulator += e.Button / 1000.0;
                while (_wheelAccumulator >= 10.0)
                {
                    _wheelAccumulator -= 10.0;
                    Pointer?.Invoke(new OverlayPointerEvent(OverlayPointerKind.WheelDown, _lastX, _lastY));
                }
                while (_wheelAccumulator <= -10.0)
                {
                    _wheelAccumulator += 10.0;
                    Pointer?.Invoke(new OverlayPointerEvent(OverlayPointerKind.WheelUp, _lastX, _lastY));
                }
                break;
            default:
                Pointer?.Invoke(e with { X = _lastX, Y = _lastY });
                break;
        }
    }

    public void SetInputRegion(RectI? physicalRect)
    {
        if (_surface is null || !_configured)
        {
            _pendingInputRegion = physicalRect;
            return;
        }

        try
        {
            _inputRegion?.Destroy();
            _inputRegion = _compositor!.CreateRegion(new EmptyRegionListener(), null);
            if (physicalRect is { } rect && rect.Width > 0 && rect.Height > 0)
            {
                var (x, y, w, h) = WaylandGeometry.PhysicalToLogical(
                    rect.X, rect.Y, rect.Width, rect.Height, _scale,
                    _logicalWidth, _logicalHeight);
                _inputRegion.Add(x, y, w, h);
            }
            _surface.SetInputRegion(_inputRegion);
            _surface.Commit();
            _display.Flush();
        }
        catch (Exception ex)
        {
            Log.Warn($"wayland input region failed: {ex.Message}");
        }
    }

    public void SetPointerForwarding(bool enabled)
    {
        _pointerForwarding = enabled;
        if (!enabled)
        {
            _pointer?.Dispose();
            _pointer = null;
            return;
        }

        if (_seat is null) return;
        try
        {
            _pointer ??= _seat.GetPointer(_pointerListener, null);
        }
        catch (Exception ex)
        {
            Log.Warn($"wayland pointer unavailable: {ex.Message}");
        }
    }

    public void Show()
    {
        if (_disposed || _visible) return;
        _visible = true;
        try
        {
            _surface?.Commit();
            _display.Flush();
        }
        catch (Exception ex)
        {
            Log.Warn($"wayland overlay show failed: {ex.Message}");
        }
    }

    public void Hide()
    {
        if (_disposed || !_visible) return;
        _visible = false;
        try
        {
            _surface?.Attach(null, 0, 0);
            _surface?.Commit();
            _display.Flush();
        }
        catch (Exception ex)
        {
            Log.Warn($"wayland overlay hide failed: {ex.Message}");
        }
    }

    public void Pump(int timeoutMs)
    {
        if (_disposed) return;
        try
        {
            if (_display.PrepareRead() < 0)
            {
                _display.DispatchPending();
                return;
            }

            int fd = _display.GetFd();
            var pfds = new[] { new PollFd { Fd = fd, Events = PollIn, Revents = 0 } };
            int rc = poll(pfds, 1, timeoutMs);
            if (rc <= 0 || (pfds[0].Revents & PollIn) == 0)
            {
                _display.CancelRead();
                _display.DispatchPending();
                return;
            }

            if (_display.ReadEvents() < 0)
            {
                _display.CancelRead();
                _display.DispatchPending();
                return;
            }

            _display.DispatchPending();
            _display.Flush();
        }
        catch (Exception ex)
        {
            Log.Warn($"wayland pump failed: {ex.Message}");
        }
    }

    private double _fractionalScaleScale;

    private sealed class FractionalScaleListener : WpFractionalScaleV1.Listener
    {
        public Action<uint>? ScaleChanged;
        protected override void PreferredScale(WpFractionalScaleV1 eventSender, uint scale)
            => ScaleChanged?.Invoke(scale);
    }

    private sealed class RegistryListener : WlRegistry.Listener
    {
        public Action<uint, string, uint>? GlobalAdded;
        public Action<uint>? GlobalRemoved;

        protected override void Global(WlRegistry eventSender, uint name, string @interface, uint version)
            => GlobalAdded?.Invoke(name, @interface, version);

        protected override void GlobalRemove(WlRegistry eventSender, uint name)
            => GlobalRemoved?.Invoke(name);
    }

    private sealed class SurfaceListener : WlSurface.Listener { }

    private sealed class LayerSurfaceListener : ZwlrLayerSurfaceV1.Listener
    {
        public Action<uint, uint, uint>? Configured;
        public Action? ClosedEvent;

        protected override void Configure(ZwlrLayerSurfaceV1 eventSender, uint serial, uint width, uint height)
            => Configured?.Invoke(serial, width, height);

        protected override void Closed(ZwlrLayerSurfaceV1 eventSender) => ClosedEvent?.Invoke();
    }

    private sealed class SeatListener : WlSeat.Listener
    {
        public Action<WlSeat.CapabilityEnum>? CapabilitiesChanged;

        protected override void Capabilities(WlSeat eventSender, WlSeat.CapabilityEnum capabilities)
            => CapabilitiesChanged?.Invoke(capabilities);
    }

    private sealed class PointerListener : WlPointer.Listener
    {
        public Action<OverlayPointerEvent>? Event;

        protected override void Enter(WlPointer eventSender, uint serial, WlSurface? surface, WlFixed surfaceX, WlFixed surfaceY)
            => Event?.Invoke(new OverlayPointerEvent(OverlayPointerKind.Enter, (int)(double)surfaceX, (int)(double)surfaceY));

        protected override void Leave(WlPointer eventSender, uint serial, WlSurface? surface)
            => Event?.Invoke(new OverlayPointerEvent(OverlayPointerKind.Leave, 0, 0));

        protected override void Motion(WlPointer eventSender, uint time, WlFixed surfaceX, WlFixed surfaceY)
            => Event?.Invoke(new OverlayPointerEvent(OverlayPointerKind.Motion, (int)(double)surfaceX, (int)(double)surfaceY));

        protected override void Button(WlPointer eventSender, uint serial, uint time, uint button, WlPointer.ButtonStateEnum state)
        {
            bool pressed = state == WlPointer.ButtonStateEnum.Pressed;
            Event?.Invoke(new OverlayPointerEvent(
                pressed ? OverlayPointerKind.ButtonPress : OverlayPointerKind.ButtonRelease,
                0, 0, MapButton(button)));
        }

        protected override void Axis(WlPointer eventSender, uint time, WlPointer.AxisEnum axis, WlFixed value)
        {
            if (axis != WlPointer.AxisEnum.VerticalScroll) return;
            int scaled = (int)Math.Clamp(Math.Round((double)value * 1000), -1_000_000, 1_000_000);
            Event?.Invoke(new OverlayPointerEvent(OverlayPointerKind.Axis, 0, 0, scaled));
        }

        protected override void AxisDiscrete(WlPointer eventSender, WlPointer.AxisEnum axis, int discrete)
        {
            if (axis != WlPointer.AxisEnum.VerticalScroll) return;
            Event?.Invoke(new OverlayPointerEvent(
                discrete > 0 ? OverlayPointerKind.WheelDown : OverlayPointerKind.WheelUp, 0, 0));
        }

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

    private sealed class OutputEntry
    {
        public uint GlobalName { get; }
        public uint Version { get; }
        public WlOutput? Output { get; set; }
        public ZxdgOutputV1? XdgOutput { get; set; }
        public string Name { get; set; } = string.Empty;
        public int LogicalX { get; set; }
        public int LogicalY { get; set; }
        public int LogicalWidth { get; set; }
        public int LogicalHeight { get; set; }
        public int PhysicalWidth { get; set; }
        public int PhysicalHeight { get; set; }
        public int ScaleInt { get; set; } = 1;
        public double Scale { get; set; } = 1.0;
        public WlOutput.TransformEnum Transform { get; set; } = WlOutput.TransformEnum.Normal;
        public bool ModeSeen { get; set; }
        public OutputListener Listener { get; }
        public XdgOutputListener XdgListener { get; }

        public OutputEntry(uint globalName, uint version)
        {
            GlobalName = globalName;
            Version = version;
            Listener = new OutputListener(this);
            XdgListener = new XdgOutputListener(this);
        }

        public void RecomputeScale()
        {
            Scale = WaylandGeometry.ComputeScale(LogicalWidth, LogicalHeight,
                PhysicalWidth, PhysicalHeight, Transform, ScaleInt);
        }
    }

    private sealed class OutputListener : WlOutput.Listener
    {
        private readonly OutputEntry _entry;
        public OutputListener(OutputEntry entry) => _entry = entry;

        protected override void Geometry(WlOutput eventSender, int x, int y, int physicalWidth,
            int physicalHeight, WlOutput.SubpixelEnum subpixel, string make, string model,
            WlOutput.TransformEnum transform)
        {
            // Geometry carries physical mm/position and rotation. Rotation
            // matters for fractional-scale calculation because logical width
            // maps to physical height on 90/270 degree outputs.
            _entry.Transform = transform;
            _entry.RecomputeScale();
        }

        protected override void Mode(WlOutput eventSender, WlOutput.ModeEnum flags, int width, int height, int refresh)
        {
            if ((flags & WlOutput.ModeEnum.Current) == 0) return;
            _entry.PhysicalWidth = width;
            _entry.PhysicalHeight = height;
            _entry.ModeSeen = true;
            _entry.RecomputeScale();
        }

        protected override void Scale(WlOutput eventSender, int factor)
        {
            _entry.ScaleInt = Math.Max(1, factor);
            _entry.RecomputeScale();
        }

        protected override void Name(WlOutput eventSender, string name)
        {
            if (string.IsNullOrWhiteSpace(_entry.Name)) _entry.Name = name;
        }

        protected override void Description(WlOutput eventSender, string description) { }
        protected override void Done(WlOutput eventSender) => _entry.RecomputeScale();
    }

    private sealed class XdgOutputListener : ZxdgOutputV1.Listener
    {
        private readonly OutputEntry _entry;
        public XdgOutputListener(OutputEntry entry) => _entry = entry;

        protected override void LogicalPosition(ZxdgOutputV1 eventSender, int x, int y)
        {
            _entry.LogicalX = x;
            _entry.LogicalY = y;
        }

        protected override void LogicalSize(ZxdgOutputV1 eventSender, int width, int height)
        {
            _entry.LogicalWidth = width;
            _entry.LogicalHeight = height;
            _entry.RecomputeScale();
        }

        protected override void Name(ZxdgOutputV1 eventSender, string name)
        {
            if (!string.IsNullOrWhiteSpace(name)) _entry.Name = name;
        }

        protected override void Description(ZxdgOutputV1 eventSender, string description) { }
        protected override void Done(ZxdgOutputV1 eventSender) => _entry.RecomputeScale();
    }

    private readonly record struct GlobalInfo(uint Name, string Interface, uint Version);

    private sealed class EmptyCompositorListener : WlCompositor.Listener { }
    private sealed class EmptyLayerShellListener : ZwlrLayerShellV1.Listener { }
    private sealed class EmptyViewporterListener : WpViewporter.Listener { }
    private sealed class EmptyFractionalScaleManagerListener : WpFractionalScaleManagerV1.Listener { }
    private sealed class EmptyXdgOutputManagerListener : ZxdgOutputManagerV1.Listener { }
    private sealed class EmptyViewportListener : WpViewport.Listener { }
    private sealed class EmptyRegionListener : WlRegion.Listener { }

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
        try
        {
            _pointer?.Dispose();
            _pointer = null;
            if (_seat?.IsReleaseAvailable == true) _seat.Release();
            _seat = null;

            _layerSurface?.Destroy();
            _layerSurface = null;
            _inputRegion?.Destroy();
            _inputRegion = null;
            _viewport?.Destroy();
            _viewport = null;
            _surface?.Destroy();
            _surface = null;

            foreach (var output in _outputs)
            {
                output.XdgOutput?.Destroy();
                output.XdgOutput = null;
                if (output.Output?.IsReleaseAvailable == true) output.Output.Release();
                output.Output = null;
            }
            _outputs.Clear();

            _fractionalScale?.Dispose();
            _fractionalScale = null;
            _fractionalScaleManager?.Destroy();
            _fractionalScaleManager = null;
            _xdgOutputManager?.Destroy();
            _xdgOutputManager = null;
            _viewporter?.Destroy();
            _viewporter = null;
            _layerShell?.Destroy();
            _layerShell = null;
            if (_compositor?.IsReleaseAvailable == true) _compositor.Release();
            _compositor = null;
            _registry.Dispose();
            _display.Flush();
            _display.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn($"wayland overlay dispose: {ex.Message}");
        }
    }
}
