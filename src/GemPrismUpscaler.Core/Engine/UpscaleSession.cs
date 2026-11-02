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
    private readonly EffectMode _effect;
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
    private int _screenWidth;
    private int _screenHeight;
    private string _exitReason = string.Empty;

    public event Action<string>? Message;

    public RectI DestRect { get; private set; }
    public ulong OverlayWindowId => _overlay?.Window ?? 0;
    public bool IsRunning => _thread is { IsAlive: true };
    public string OverlayTitle => "GemPrismOverlay";
    public string ExitReason => Volatile.Read(ref _exitReason);
    public string EffectName => _effect == EffectMode.Anime4K ? "anime4k" : "bilinear";

    /// <summary>
    /// Hide the overlay when the source window loses input focus (and restore it
    /// when focus returns). Enabled by default; the source is focused on start.
    /// </summary>
    public bool FollowFocus { get; set; } = true;

    public UpscaleSession(WindowInfo source, ScalingMode mode = ScalingMode.Fit,
        EffectMode effect = EffectMode.Anime4K)
    {
        _source = source;
        _mode = mode;
        _effect = effect;
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
            t.Join(TimeSpan.FromSeconds(8));
    }

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

        var geometry = GetSourceGeometry();
        if (geometry is null) throw new InvalidOperationException("source window is not readable");
        DestRect = LayoutCalculator.ComputeDestRect(geometry.Value.Width, geometry.Value.Height,
            _screenWidth, _screenHeight, _mode);

        _overlay = new X11Overlay(_display, _screenWidth, _screenHeight);
        _capture = new X11Capture(_display, _source.WindowId, geometry.Value.Width, geometry.Value.Height);
        _damage = new X11Damage(_display, _source.WindowId);
        _input = new X11Input(_display, _source.WindowId);
        _hotkey = new X11Hotkey(_display);
        _blitter = new VulkanBlitter(_display, _overlay.Window, _screenWidth, _screenHeight, _effect);

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
             $"{geometry.Value.Width}x{geometry.Value.Height} screen={_screenWidth}x{_screenHeight} dest={DestRect} " +
             $"device='{_blitter.DeviceName}' format={_blitter.SwapchainFormatName} " +
             $"extent={_blitter.SwapchainExtent.Width}x{_blitter.SwapchainExtent.Height} " +
             $"damage={_damage.Available} hotkey={_hotkey.IsGrabbed} focus={_focusedOnce} overlay=0x{_overlay.Window:x}");
    }

    private (int Width, int Height)? GetSourceGeometry()
    {
        if (_display is null) return null;
        if (X11Native.XGetGeometry(_display.Handle, _source.WindowId, out _, out _, out _,
                out uint width, out uint height, out _, out _) == 0)
            return null;
        if (width < 2 || height < 2) return null;
        return ((int)width, (int)height);
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
                var geom = GetSourceGeometry();
                if (geom is null)
                {
                    SetExitReason("source window closed");
                    break;
                }
                if (geom.Value.Width != _capture.Width || geom.Value.Height != _capture.Height)
                {
                    _capture.Recreate(geom.Value.Width, geom.Value.Height);
                    DestRect = LayoutCalculator.ComputeDestRect(geom.Value.Width, geom.Value.Height,
                        _screenWidth, _screenHeight, _mode);
                    Emit($"source resized to {geom.Value.Width}x{geom.Value.Height} dest={DestRect}");
                }

                if (!_capture.TryCapture())
                {
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
                    try
                    {
                        _blitter.Render(_capture.Buffer, _capture.Width, _capture.Height, _capture.Stride, DestRect);
                        stats.OnPresented();
                    }
                    catch (Exception ex)
                    {
                        SetExitReason($"present error: {ex.GetType().Name}: {ex.Message}");
                        break;
                    }
                }
            }

            var report = stats.Tick();
            if (report is not null)
                Emit($"stats {report} hidden={_overlay.IsVisible == false} popup={_visibility.PopupHidden} userHidden={_visibility.UserHidden}");

            Thread.Sleep(2);
        }

        Emit($"engine loop exited ({_visibility.ReasonText})");
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
            var g = GetSourceGeometry();
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
        _input.ForwardMotion(rootX, rootY, Mapper);
    }

    /// <summary>
    /// Asks the compositor to focus the source window. EWMH activation is needed
    /// because KWin's focus-stealing prevention ignores a bare XSetInputFocus from
    /// a background client (the config UI has focus when the session starts).
    /// </summary>
    private void FocusSource()
    {
        if (_display is null) return;
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
