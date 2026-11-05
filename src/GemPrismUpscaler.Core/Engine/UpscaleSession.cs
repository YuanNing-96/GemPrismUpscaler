using System.Diagnostics;
using GemPrismUpscaler.Core.Configuration;
using GemPrismUpscaler.Core.Diagnostics;
using GemPrismUpscaler.Core.Geometry;
using GemPrismUpscaler.Core.Platform.Linux.X11;
using GemPrismUpscaler.Core.Rendering;

namespace GemPrismUpscaler.Core.Engine;

/// <summary>
/// M0a engine: capture a source X11 window, present a full-screen Vulkan overlay,
/// forward pointer input, and hide for popup windows of the same process.
/// Frame-driven: static source content does not cause repeated captures/presents.
///
/// Visibility is a small state machine (see <see cref="OverlayVisibility"/>):
/// - Alt+Shift+A toggles the overlay;
/// - a same-process dialog hides it;
/// - a minimized/unmapped source hides it;
/// - losing input focus hides it, so switching to another app reveals the desktop;
/// - a closed source stops the engine so the UI can reset.
/// Every transition back to visible forces one fresh capture+present, because the
/// Vulkan/X11 surface contents are undefined after an unmap.
/// </summary>
public sealed class UpscaleSession : IDisposable
{
    private const int PollIntervalMs = 200;

    private readonly WindowInfo _source;
    private readonly ScalingMode _mode;
    private readonly EffectDefinition _effect;
    private readonly HotkeyBinding _hotkeyBinding;
    private readonly ManualResetEventSlim _started = new(false);
    private readonly OverlayVisibility _visibility = new();
    private Thread? _thread;
    private X11Display? _display;
    private X11Overlay? _overlay;
    private X11Capture? _capture;
    private X11Damage? _damage;
    private X11Input? _input;
    private X11Hotkey? _hotkey;
    private VulkanBlitter? _blitter;
    private volatile bool _stop;
    private bool _disposed;
    private Exception? _initError;
    private bool _focusedOnce;
    private bool _forceRedraw = true;
    private readonly FrameDiff _diff = new();
    private List<MonitorInfo> _monitors = new();
    private MonitorInfo? _monitor;
    private string? _manualMonitorName;
    private readonly int _outputWidthOverride;
    private readonly int _outputHeightOverride;
    private int _screenWidth;
    private int _screenHeight;
    private string _exitReason = string.Empty;
    private int _toggleRequested;
    private int _userHiddenRequest = -1;

    public event Action<string>? Message;

    public RectI DestRect { get; private set; }
    public ulong OverlayWindowId => _overlay?.Window ?? 0;
    public bool IsRunning => _thread is { IsAlive: true };
    public bool IsOverlayVisible => _overlay?.IsVisible == true;
    public string OverlayTitle => "GemPrismOverlay";
    public string ExitReason => Volatile.Read(ref _exitReason);
    public string EffectName => _effect.Id;
    public MonitorInfo? Monitor => _monitor;
    public string MonitorName => _monitor?.Name ?? string.Empty;

    /// <summary>
    /// Hide the overlay when the source window loses input focus (and restore it
    /// when focus returns). Enabled by default; the source is focused on start.
    /// </summary>
    public bool FollowFocus { get; set; } = true;

    /// <summary>Dirty-tile rendering; can be disabled for A/B and fallback.</summary>
    public bool DirtyRectEnabled { get; set; } = true;

    public UpscaleSession(WindowInfo source, ScalingMode mode = ScalingMode.Fit,
        EffectMode effect = EffectMode.Anime4KS, string? monitorName = null,
        int outputWidthOverride = 0, int outputHeightOverride = 0,
        HotkeyBinding? hotkey = null, EffectDefinition? effectDefinition = null)
    {
        _source = source;
        _mode = mode;
        _effect = effectDefinition ?? EffectRegistry.BuiltIn(effect);
        _manualMonitorName = monitorName;
        _outputWidthOverride = outputWidthOverride;
        _outputHeightOverride = outputHeightOverride;
        _hotkeyBinding = hotkey ?? HotkeyBinding.Default;
    }

    public void Start()
    {
        if (_thread is not null) throw new InvalidOperationException("already started");
        _thread = new Thread(Run) { IsBackground = true, Name = "GemPrism-Render" };
        _thread.Start();
        if (!_started.Wait(TimeSpan.FromSeconds(15)))
            throw new TimeoutException("engine initialization timed out");
        if (_initError is not null)
            throw new InvalidOperationException($"engine initialization failed: {_initError.Message}", _initError);
    }

    public void Stop()
    {
        _stop = true;
        var t = _thread;
        if (t is not null && t.IsAlive && t != Thread.CurrentThread)
        {
            if (!t.Join(TimeSpan.FromSeconds(3)))
                Emit("engine stop timed out; render thread will exit as a background thread");
        }
        if (string.IsNullOrWhiteSpace(ExitReason))
            SetExitReason("stopped");
    }

    /// <summary>Thread-safe request used by the tray menu/hotkey path.</summary>
    public void RequestToggleVisibility() => Interlocked.Exchange(ref _toggleRequested, 1);

    /// <summary>Thread-safe request to force the overlay hidden/shown.</summary>
    public void RequestUserHidden(bool hidden) => Interlocked.Exchange(ref _userHiddenRequest, hidden ? 1 : 0);

    private void Run()
    {
        try
        {
            Initialize();
            _started.Set();
            Loop();
        }
        catch (Exception ex)
        {
            _initError ??= ex;
            SetExitReason($"engine error: {ex.GetType().Name}: {ex.Message}");
            ErrorBus.Functional("engine", "引擎启动失败", ex);
            _started.Set();
        }
        finally
        {
            Cleanup();
        }
    }

    private void Initialize()
    {
        _display = new X11Display();
        _screenWidth = _display.Width;
        _screenHeight = _display.Height;
        _monitors = X11MonitorEnumerator.List(_display);

        var geometry = GetSourceBounds();
        if (geometry is null) throw new InvalidOperationException("source window is not readable");

        if (_outputWidthOverride > 0 && _outputHeightOverride > 0)
        {
            _monitor = new MonitorInfo("benchmark", 0, 0, _outputWidthOverride, _outputHeightOverride, true);
            Emit($"output size override {_outputWidthOverride}x{_outputHeightOverride}");
        }
        else
        {
            _monitor = MonitorSelector.Select(_monitors, geometry.Value, _manualMonitorName);
            if (_monitor is null) throw new InvalidOperationException("no XRandR monitor available");
            if (!string.IsNullOrWhiteSpace(_manualMonitorName) &&
                !string.Equals(_manualMonitorName, _monitor.Value.Name, StringComparison.OrdinalIgnoreCase))
            {
                Emit($"preferred monitor '{_manualMonitorName}' not found; using {_monitor.Value.Name}");
                _manualMonitorName = null;
            }
        }

        DestRect = LayoutCalculator.ComputeDestRect(geometry.Value.Width, geometry.Value.Height,
            _monitor.Value.Width, _monitor.Value.Height, _mode);

        _overlay = new X11Overlay(_display, _monitor.Value.X, _monitor.Value.Y,
            _monitor.Value.Width, _monitor.Value.Height);
        _capture = new X11Capture(_display, _source.WindowId, geometry.Value.Width, geometry.Value.Height);
        _damage = new X11Damage(_display, _source.WindowId);
        _input = new X11Input(_display, _source.WindowId);
        _hotkey = new X11Hotkey(_display, _hotkeyBinding);
        if (!_hotkey.IsGrabbed)
            ErrorBus.Functional("hotkey", _hotkey.Error ?? $"热键 {_hotkeyBinding} 未能注册");
        _blitter = new VulkanBlitter(_display, _overlay.Window, _monitor.Value.Width, _monitor.Value.Height, _effect);

        // Starting the upscaler from the config UI leaves focus on our window.
        // Give it back to the game so keyboard/hover/activation work immediately.
        // Do not force-activate a minimized game just because it was selected.
        var initialState = X11WindowProbe.GetState(_display, _source.WindowId);
        if (initialState.Viewable && !initialState.Hidden)
        {
            FocusSource();
            for (int i = 0; i < 10 && !X11WindowProbe.IsFocused(_display, _source.WindowId); i++)
                Thread.Sleep(20);
        }
        _focusedOnce = X11WindowProbe.IsFocused(_display, _source.WindowId);

        Emit($"engine started version={AppInfo.Version} effect={EffectName} source=0x{_source.WindowId:x} '{_source.Title}' " +
             $"{geometry.Value.Width}x{geometry.Value.Height} root={_screenWidth}x{_screenHeight} " +
             $"monitor={_monitor.Value} dest={DestRect} " +
             $"device='{_blitter.DeviceName}' format={_blitter.SwapchainFormatName} " +
             $"extent={_blitter.SwapchainExtent.Width}x{_blitter.SwapchainExtent.Height} " +
             $"damage={_damage.Available} hotkey={_hotkey.IsGrabbed} focus={_focusedOnce} overlay=0x{_overlay.Window:x}");
    }

    /// <summary>
    /// Source window rectangle in X11 root coordinates. Used for popup/lifecycle
    /// checks, display selection, and resize detection.
    /// </summary>
    private RectI? GetSourceBounds()
    {
        if (_display is null) return null;
        if (X11Native.XGetGeometry(_display.Handle, _source.WindowId, out _, out _, out _,
                out uint width, out uint height, out _, out _) == 0)
            return null;
        if (width < 2 || height < 2) return null;

        int x = _source.X;
        int y = _source.Y;
        if (X11Native.XTranslateCoordinates(_display.Handle, _source.WindowId, _display.Root, 0, 0,
                out int rootX, out int rootY, out _) != 0)
        {
            x = rootX;
            y = rootY;
        }
        return new RectI(x, y, (int)width, (int)height);
    }

    private void Loop()
    {
        bool dirty = true;
        long lastPoll = 0;
        long lastFallback = 0;
        int captureFailures = 0;
        var stats = new FrameStats();

        while (!_stop)
        {
            ApplyPendingVisibilityRequests(ref dirty);
            ProcessEvents(ref dirty);

            if (_blitter is null || _capture is null || _overlay is null || _display is null || _input is null)
                break;

            long now = Environment.TickCount64;
            if (now - lastPoll >= PollIntervalMs)
            {
                lastPoll = now;
                if (!PollSource())
                    break;
                TrackPopups();
            }

            if (_forceRedraw)
            {
                _forceRedraw = false;
                _diff.Reset();
                dirty = true;
            }

            // Do not capture while the overlay is hidden; keep polling above so a
            // minimized/closed source is still noticed.
            if (!_overlay.IsVisible)
            {
                dirty = false;
                Thread.Sleep(10);
                continue;
            }

            bool fallback = _damage is null || !_damage.Available;
            if (fallback && now - lastFallback >= 16)
            {
                lastFallback = now;
                dirty = true;
            }

            if (dirty)
            {
                dirty = false;
                var geom = GetSourceBounds();
                if (geom is null)
                {
                    SetExitReason("source window closed");
                    break;
                }
                if (geom.Value.Width != _capture.Width || geom.Value.Height != _capture.Height)
                {
                    _capture.Recreate(geom.Value.Width, geom.Value.Height);
                    DestRect = LayoutCalculator.ComputeDestRect(geom.Value.Width, geom.Value.Height,
                        _monitor?.Width ?? _screenWidth, _monitor?.Height ?? _screenHeight, _mode);
                    _diff.Reset();
                    Emit($"source resized to {geom.Value.Width}x{geom.Value.Height} dest={DestRect}");
                }

                var captureClock = Stopwatch.StartNew();
                bool captured = _capture.TryCapture();
                double captureMs = captureClock.Elapsed.TotalMilliseconds;
                if (!captured)
                {
                    _diff.Reset();
                    captureFailures++;
                    if (captureFailures > 30)
                    {
                        SetExitReason("capture failed repeatedly (source may be closed)");
                        break;
                    }
                }
                else
                {
                    captureFailures = 0;
                    stats.OnCaptured();

                    DirtyFrame? dirtyFrame = null;
                    double diffMs = 0;
                    if (DirtyRectEnabled && _effect.IsCompute && !_effect.RequiresFullFrame)
                    {
                        var diffClock = Stopwatch.StartNew();
                        dirtyFrame = _diff.Update(_capture.Pixels, _capture.Width, _capture.Height,
                            _capture.Stride, DestRect, _blitter.DirtyHalo + 2);
                        diffMs = diffClock.Elapsed.TotalMilliseconds;
                    }

                    if (dirtyFrame is not { IsStatic: true })
                    {
                        try
                        {
                            _blitter.Render(_capture.Pixels, _capture.Width, _capture.Height, _capture.Stride,
                                DestRect, dirtyFrame);
                            stats.OnPresented();
                            stats.OnFrame(new FrameTimings(
                                captureMs,
                                diffMs,
                                _blitter.LastUploadCpuMs,
                                _blitter.LastEffectCpuMs,
                                _blitter.LastPresentCpuMs,
                                _blitter.LastTotalCpuMs,
                                _blitter.LastGpuUploadMs,
                                _blitter.LastGpuEffectMs,
                                _blitter.LastGpuTailMs,
                                _blitter.LastGpuTimingsValid));
                        }
                        catch (Exception ex)
                        {
                            SetExitReason($"present error: {ex.GetType().Name}: {ex.Message}");
                            break;
                        }
                    }
                }
            }

            var report = stats.Tick();
            if (report is not null)
            {
                Emit($"stats {report.ToLogString()} hidden={_overlay.IsVisible == false} " +
                     $"popup={_visibility.PopupHidden} userHidden={_visibility.UserHidden}");
                StatsCsv.TryWrite(report);
            }

            Thread.Sleep(2);
        }

        Emit($"engine loop exited ({_visibility.ReasonText})");
    }

    private void ApplyPendingVisibilityRequests(ref bool dirty)
    {
        if (_overlay is null) return;

        int toggle = Interlocked.Exchange(ref _toggleRequested, 0);
        int hidden = Interlocked.Exchange(ref _userHiddenRequest, -1);
        if (toggle == 0 && hidden < 0) return;

        if (toggle != 0)
            _visibility.UserHidden = !_visibility.UserHidden;
        if (hidden >= 0)
            _visibility.UserHidden = hidden == 1;

        if (!_visibility.UserHidden)
        {
            FocusSource();
            _visibility.FocusLost = false;
            _focusedOnce = true;
        }

        ApplyVisibility();
        dirty = true;
        Emit(_visibility.UserHidden ? "visibility: overlay hidden (user)" : "visibility: overlay shown (user)");
    }

    private void ProcessEvents(ref bool dirty)
    {
        if (_display is null) return;
        while (X11Native.XPending(_display.Handle) > 0)
        {
            X11Native.XEvent ev = default;
            X11Native.XNextEvent(_display.Handle, ref ev);

            if (_damage is not null && _damage.IsDamageEvent(ev))
            {
                dirty = true;
                _damage.Acknowledge();
                continue;
            }

            if (_hotkey is not null && _hotkey.IsHotkey(ev))
            {
                _visibility.UserHidden = !_visibility.UserHidden;
                if (!_visibility.UserHidden)
                {
                    // Showing via hotkey should also make the game usable again.
                    FocusSource();
                    _visibility.FocusLost = false;
                    _focusedOnce = true;
                }
                ApplyVisibility();
                dirty = true;
                Emit(_visibility.UserHidden ? "hotkey: overlay hidden" : "hotkey: overlay shown");
                continue;
            }

            if (_overlay is null || _input is null) continue;
            if (ev.Window != _overlay.Window) continue;

            switch (ev.Type)
            {
                case 4: // ButtonPress
                    if (!X11WindowProbe.IsFocused(_display, _source.WindowId))
                    {
                        FocusSource();
                        _visibility.FocusLost = false;
                        _focusedOnce = true;
                    }
                    if (ev.Detail == 4 || ev.Detail == 5)
                        _input.ForwardWheel((int)ev.X, (int)ev.Y, Mapper, ev.Detail == 4);
                    else
                        _input.ForwardButton((int)ev.X, (int)ev.Y, Mapper, ev.Detail, true);
                    break;
                case 5: // ButtonRelease
                    if (ev.Detail != 4 && ev.Detail != 5)
                        _input.ForwardButton((int)ev.X, (int)ev.Y, Mapper, ev.Detail, false);
                    break;
                case 6: // MotionNotify
                    _input.ForwardMotion((int)ev.X, (int)ev.Y, Mapper);
                    break;
            }
        }
    }

    private CoordinateMapper Mapper
    {
        get
        {
            var g = GetSourceBounds();
            return g is null
                ? new CoordinateMapper(DestRect, Math.Max(1, _capture?.Width ?? 1), Math.Max(1, _capture?.Height ?? 1))
                : new CoordinateMapper(DestRect, g.Value.Width, g.Value.Height);
        }
    }

    /// <summary>
    /// Checks that the source window still exists and is viewable, and tracks
    /// focus/minimize transitions. Returns false when the source is gone.
    /// </summary>
    private bool PollSource()
    {
        if (_display is null) return false;

        var state = X11WindowProbe.GetState(_display, _source.WindowId);
        if (!state.Exists)
        {
            SetExitReason("game window closed");
            return false;
        }

        var bounds = GetSourceBounds();
        if (bounds is null)
        {
            SetExitReason("source window closed");
            return false;
        }
        UpdateMonitor(bounds.Value);

        bool suspended = !state.Viewable || state.Hidden;
        bool resumed = _visibility.SourceSuspended && !suspended;
        _visibility.SourceSuspended = suspended;

        if (FollowFocus)
        {
            // Focus on our own overlay (e.g. after a click) is not a "switch to
            // another app" and must not make the overlay hide itself.
            bool focused = X11WindowProbe.IsFocused(_display, _source.WindowId) ||
                           (_overlay is not null && X11WindowProbe.IsFocused(_display, _overlay.Window));
            if (focused)
            {
                _focusedOnce = true;
                _visibility.FocusLost = false;
            }
            else if (_focusedOnce)
            {
                _visibility.FocusLost = true;
            }
        }

        if (resumed)
        {
            _forceRedraw = true;
            Emit("source viewable again: overlay resumed");
        }

        ApplyVisibility();
        return true;
    }

    /// <summary>
    /// Re-evaluates the target monitor. In auto mode the overlay follows the
    /// game window across screens; in manual mode it stays on the selected
    /// monitor unless that monitor disappears.
    /// </summary>
    private void UpdateMonitor(RectI bounds)
    {
        if (_display is null) return;
        if (_outputWidthOverride > 0 && _outputHeightOverride > 0) return;

        List<MonitorInfo> monitors;
        try { monitors = X11MonitorEnumerator.List(_display); }
        catch (Exception ex)
        {
            Emit($"monitor scan error: {ex.Message}");
            return;
        }
        if (monitors.Count == 0) return;
        _monitors = monitors;

        MonitorInfo? selected;
        if (!string.IsNullOrWhiteSpace(_manualMonitorName))
        {
            selected = MonitorSelector.Select(monitors, bounds, _manualMonitorName);
            if (selected is null ||
                !string.Equals(selected.Value.Name, _manualMonitorName, StringComparison.OrdinalIgnoreCase))
            {
                Emit($"selected monitor '{_manualMonitorName}' is no longer available; switching to auto");
                _manualMonitorName = null;
                selected = MonitorSelector.Select(monitors, bounds);
            }
        }
        else
        {
            selected = MonitorSelector.Select(monitors, bounds);
        }

        if (selected is null) return;
        if (_monitor is { } current && current == selected.Value) return;
        RebuildOutput(selected.Value);
    }

    /// <summary>
    /// Recreates the X11 overlay and Vulkan swapchain for a different monitor
    /// size/position. Called on the render thread only, so no present is in
    /// flight while the old objects are destroyed.
    /// </summary>
    private void RebuildOutput(MonitorInfo monitor)
    {
        if (_display is null || _capture is null) return;

        var previous = _monitor;
        Emit($"output monitor changed {previous?.ToString() ?? "(none)"} -> {monitor}");

        try { _blitter?.Dispose(); }
        catch (Exception ex) { Emit($"dispose old blitter: {ex.Message}"); }
        _blitter = null;

        try { _overlay?.Dispose(); }
        catch (Exception ex) { Emit($"dispose old overlay: {ex.Message}"); }
        _overlay = null;

        _monitor = monitor;
        _overlay = new X11Overlay(_display, monitor.X, monitor.Y, monitor.Width, monitor.Height);
        _blitter = new VulkanBlitter(_display, _overlay.Window, monitor.Width, monitor.Height, _effect);
        DestRect = LayoutCalculator.ComputeDestRect(_capture.Width, _capture.Height,
            monitor.Width, monitor.Height, _mode);

        _input?.Reset();
        _diff.Reset();
        _forceRedraw = true;
        ApplyVisibility();
        if (_overlay.IsVisible) SyncPointerToSource();
        Emit($"output size {monitor.Width}x{monitor.Height} dest={DestRect} " +
             $"extent={_blitter.SwapchainExtent.Width}x{_blitter.SwapchainExtent.Height}");
    }

    private void TrackPopups()
    {
        if (_display is null || _overlay is null) return;
        if (_source.Pid <= 0) return;

        bool popup = false;
        try
        {
            foreach (var w in X11WindowEnumerator.List(_display))
            {
                if (w.WindowId == _source.WindowId) continue;
                if (w.Pid != _source.Pid) continue;
                // A minimized/unmapped dialog must not keep the overlay hidden.
                var state = X11WindowProbe.GetState(_display, w.WindowId);
                if (!state.Exists || !state.Viewable || state.Hidden) continue;
                // Ignore tiny helper/IME windows that Wine creates alongside the game.
                if (w.Width < 64 || w.Height < 64) continue;
                popup = true;
                break;
            }
        }
        catch (Exception ex)
        {
            Emit($"popup scan error: {ex.Message}");
            return;
        }

        if (popup == _visibility.PopupHidden) return;
        _visibility.PopupHidden = popup;
        if (!popup) _forceRedraw = true; // surface was unmapped; never come back black
        ApplyVisibility();
        Emit(popup ? "popup detected: overlay hidden" : "popup closed: overlay restored");
    }

    private void ApplyVisibility()
    {
        if (_overlay is null) return;

        bool show = _visibility.IsVisible;
        if (show == _overlay.IsVisible) return;

        if (show)
        {
            _forceRedraw = true;
            _diff.Reset();
            _overlay.Show();
            SyncPointerToSource();
            Emit("overlay shown");
        }
        else
        {
            _overlay.Hide();
            _input?.Reset();
            Emit($"overlay hidden ({_visibility.ReasonText})");
        }
    }

    /// <summary>
    /// Pushes the current pointer position into the source window right after the
    /// overlay appears, so hover state is correct before the user moves the mouse.
    /// </summary>
    private void SyncPointerToSource()
    {
        if (_display is null || _input is null) return;
        if (X11Native.XQueryPointer(_display.Handle, _display.Root, out _, out _,
                out int rootX, out int rootY, out _, out _, out _) == 0)
            return;
        // The pointer query is root-relative; ForwardMotion expects overlay-local
        // coordinates, so subtract the monitor origin on multi-monitor desktops.
        int localX = rootX - (_monitor?.X ?? 0);
        int localY = rootY - (_monitor?.Y ?? 0);
        _input.ForwardMotion(localX, localY, Mapper);
    }

    /// <summary>
    /// Asks the compositor to focus the source window. EWMH activation is needed
    /// because KWin's focus-stealing prevention ignores a bare XSetInputFocus from
    /// a background client (the config UI has focus when the session starts).
    /// </summary>
    private void FocusSource()
    {
        if (_display is null) return;
        // The source may have been destroyed/unmapped between poll and focus;
        // XSetInputFocus on a dead window produces a non-fatal BadMatch.
        var state = X11WindowProbe.GetState(_display, _source.WindowId);
        if (!state.Exists || !state.Viewable) return;
        X11WindowProbe.Activate(_display, _source.WindowId);
        X11Native.XSetInputFocus(_display.Handle, _source.WindowId, X11Native.RevertToParent, 0);
        _display.Sync();
    }

    private void SetExitReason(string reason)
    {
        Volatile.Write(ref _exitReason, reason);
        Emit($"stopping: {reason}");
    }

    private void Cleanup()
    {
        try { _blitter?.Dispose(); } catch (Exception ex) { Emit($"dispose blitter: {ex.Message}"); }
        try { _capture?.Dispose(); } catch (Exception ex) { Emit($"dispose capture: {ex.Message}"); }
        try { _damage?.Dispose(); } catch (Exception ex) { Emit($"dispose damage: {ex.Message}"); }
        try { _hotkey?.Dispose(); } catch (Exception ex) { Emit($"dispose hotkey: {ex.Message}"); }
        try { _overlay?.Dispose(); } catch (Exception ex) { Emit($"dispose overlay: {ex.Message}"); }
        try { _display?.Dispose(); } catch (Exception ex) { Emit($"dispose display: {ex.Message}"); }
        _blitter = null; _capture = null; _damage = null; _hotkey = null; _overlay = null; _display = null;
    }

    private void Emit(string message)
    {
        Log.Write(message);
        try { Message?.Invoke(message); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _started.Dispose();
    }
}
