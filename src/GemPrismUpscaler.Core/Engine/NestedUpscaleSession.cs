using System.Text.Json;
using GemPrismUpscaler.Core.Diagnostics;
using GemPrismUpscaler.Core.Geometry;
using GemPrismUpscaler.Core.Platform.Linux;
using GemPrismUpscaler.Core.Platform.Linux.Nested;
using GemPrismUpscaler.Core.Rendering;

namespace GemPrismUpscaler.Core.Engine;

/// <summary>
/// M2b presentation layer: every actual game toplevel gets its own private
/// headless output and its own KDE-facing window. One extra output is always
/// kept empty, so a new window never has to share an output with an existing
/// window. Each host shows either the original game frame at its native size,
/// or that window's frame upscaled fullscreen. Alt+Shift+A toggles only the
/// focused host; leaving upscale mode is automatic when that host loses focus.
/// </summary>
public sealed class NestedUpscaleSession : IDisposable
{
    private const int MissingWindowGraceMs = 1000;
    /// <summary>
    /// A game resizing its own window produces a burst of intermediate sizes
    /// (Wine animates or resizes in steps). Rebuilding a Wayland host window on
    /// every step would flicker, so wait until the requested size is stable.
    /// </summary>
    private const int HostResizeDebounceMs = 350;
    private const int FailedWindowRetryMs = 1200;
    private const int MinimizePollMs = 120;

    private static readonly EffectDefinition OriginalEffect = new()
    {
        Id = "original",
        DisplayName = "Original",
        Kind = EffectKind.Bilinear
    };

    private static readonly bool TraceInput =
        Environment.GetEnvironmentVariable("GEMPRISM_TRACE_INPUT") == "1";

    /// <summary>
    /// Diagnostic only: log a coarse fingerprint (mean luminance, uniform-dark
    /// share, signature) of every frame that is about to be presented. It tells
    /// "we presented a black frame" apart from "the compositor did something to
    /// the window".
    /// </summary>
    private static readonly bool TraceFrames =
        Environment.GetEnvironmentVariable("GEMPRISM_TRACE_FRAMES") == "1";

    private readonly NestedSession _session;
    private readonly ScalingMode _mode;
    private readonly EffectDefinition _effect;
    private readonly string? _monitorName;
    private readonly bool _waitForCommand;
    private readonly string? _preferredMain;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _started = new(false);
    private readonly Dictionary<long, WindowState> _states = new();
    private readonly object _stateGate = new();
    private readonly Dictionary<long, long> _retryAfter = new();
    private readonly Dictionary<string, (int Width, int Height)> _lastWindowedSizes = new();

    private volatile bool _stop;
    private bool _disposed;
    private string? _exitReason;
    private Exception? _initError;

    private readonly bool _toplevelMode;
    /// <summary>
    /// Native xdg_toplevel host windows instead of X11/XWayland windows. That
    /// is what enables wp_pointer_warp_v1, and it also drops the XWayland
    /// shadow cursor. GEMPRISM_HOST_X11=1 keeps the old backend for A/B.
    /// </summary>
    private bool _waylandHost;
    private SwayIpc? _ipc;
    private SwayOutputPool? _pool;
    private NestedWindowTracker? _tracker;
    private NestedInput? _input;
    private NestedWindowProbe? _probe;
    private bool _probeUnavailable;
    private Thread? _cursorSyncThread;
    private long _cursorHintSeq;
    private long _lastMinimizePoll;

    private int _outputWidth;
    private int _outputHeight;
    private long _focusedId;
    private long _toggleRequestedId;
    private int _toggleRequested;
    private long _lastToggleAt;
    private OverlayModifiers _modifiers;
    private bool _commandExitLogged;

    public NestedUpscaleSession(NestedSession session, ScalingMode mode, EffectDefinition effect,
        string? monitorName, bool waitForCommand, string? preferredMain = null)
    {
        _session = session;
        _mode = mode;
        _effect = effect;
        _monitorName = monitorName;
        _waitForCommand = waitForCommand;
        _preferredMain = preferredMain;
        _toplevelMode = Environment.GetEnvironmentVariable("GEMPRISM_TOPLEVEL_CAPTURE") is "1" or "true";
        _waylandHost = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")) &&
                       Environment.GetEnvironmentVariable("GEMPRISM_HOST_X11") is not ("1" or "true");
        _thread = new Thread(Run) { IsBackground = true, Name = "nested-upscale" };
    }

    public event Action<string>? Message;

    public bool IsRunning => _thread.IsAlive && !_stop;
    public string? ExitReason => Volatile.Read(ref _exitReason);
    public bool IsUpscaleMode
    {
        get
        {
            lock (_stateGate) return _states.Values.Any(s => s.Upscale);
        }
    }
    public string EffectName => _effect.DisplayName;

    public string OverlayDescription
    {
        get
        {
            lock (_stateGate)
            {
                int count = _states.Values.Count(s => s.Host is not null);
                if (count == 0) return "kde-window:pending";
                if (count == 1)
                {
                    var host = _states.Values.First(s => s.Host is not null).Host!;
                    return $"kde-window:{host.Width}x{host.Height}";
                }
                return $"kde-window-count:{count}";
            }
        }
    }

    public void Start()
    {
        _thread.Start();
        if (!_started.Wait(TimeSpan.FromSeconds(30)))
        {
            Stop();
            throw new TimeoutException("nested upscale initialization timed out");
        }
        if (_initError is not null)
            throw new InvalidOperationException(
                $"nested upscale initialization failed: {_initError.Message}", _initError);
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
            SetExitReason($"nested error: {ex.GetType().Name}: {ex.Message}");
            ErrorBus.Functional("nested", "嵌套超分会话失败", ex);
            _started.Set();
        }
        finally
        {
            Cleanup();
        }
    }

    private void Initialize()
    {
        if (_session.WaylandDisplay is null || _session.SwaySocket is null)
            throw new InvalidOperationException("nested session is not started");

        _outputWidth = _session.Width;
        _outputHeight = _session.Height;

        _ipc = SwayIpc.Connect(_session.SwaySocket);
        _cursorSyncThread = new Thread(CursorSyncLoop)
        {
            IsBackground = true,
            Name = "nested-cursor-sync"
        };
        _cursorSyncThread.Start();
        _tracker = new NestedWindowTracker(
            _ipc, SwayConfig.DefaultOutput, _session.Token, _session.CommandPid,
            _preferredMain, ownsIpc: false);
        if (_toplevelMode)
        {
            // Single stable output: clients see one monitor for their whole
            // lifetime (no simulated hotplug), and each window is captured
            // through ext-image-copy-capture by its own toplevel handle.
            _pool = null;
        }
        else
        {
            _pool = new SwayOutputPool(_ipc, SwayConfig.DefaultOutput, _outputWidth, _outputHeight,
                _session.RefreshMilliHz);
            _pool.Initialize();
        }
        _input = new NestedInput(_session.Display, _session.WaylandDisplay);
        EnsureMinimizeProbe();

        Log.Info($"nested {(_toplevelMode ? "single-output/toplevel-capture" : "multi-window")}: " +
                 $"monitor='{_monitorName ?? "auto"}' " +
                 $"output={_outputWidth}x{_outputHeight}@{_session.RefreshMilliHz / 1000.0:0.###}Hz " +
                 $"base='{_pool?.BaseOutput ?? SwayConfig.DefaultOutput}' " +
                 $"mode={(_mode == ScalingMode.Fit ? "fit" : "stretch")} effect='{_effect.Id}' " +
                 $"host={(_waylandHost ? "wayland" : "x11")}");
    }

    /// <summary>
    /// The game can move the nested pointer itself (Wine warps it onto the
    /// default button when a dialog opens). The patched sway reports those
    /// warps over IPC; mirror them onto the desktop cursor so the cursor the
    /// user sees stays where the game thinks it is.
    /// </summary>
    private void CursorSyncLoop()
    {
        while (!_stop)
        {
            Thread.Sleep(16);
            try
            {
                MirrorClientCursorWarp();
            }
            catch (Exception ex)
            {
                if (TraceInput)
                    Emit($"nested cursor sync failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private void MirrorClientCursorWarp()
    {
        if (_ipc is null || _input is null || !_input.HasPointerReference) return;

        long seq = 0;
        double x = 0, y = 0;
        bool supported = false;
        using (var seats = _ipc.GetSeats())
        {
            if (seats.RootElement.ValueKind != JsonValueKind.Array) return;
            foreach (var seat in seats.RootElement.EnumerateArray())
            {
                if (!seat.TryGetProperty("cursor_hint_seq", out var seqElement)) continue;
                seq = seqElement.GetInt64();
                x = seat.TryGetProperty("cursor_hint_x", out var xElement) ? xElement.GetDouble() : 0;
                y = seat.TryGetProperty("cursor_hint_y", out var yElement) ? yElement.GetDouble() : 0;
                supported = true;
                break;
            }
        }
        // System sway does not expose the hint; the absolute re-anchor in
        // NestedInput stays in charge there.
        if (!supported) return;
        if (seq == Interlocked.Read(ref _cursorHintSeq)) return;
        Interlocked.Exchange(ref _cursorHintSeq, seq);

        WindowState? state = null;
        lock (_stateGate)
        {
            if (_states.TryGetValue(_focusedId, out var focused))
                state = focused;
        }
        if (state is null || state.Host is null || !state.Host.IsFocused) return;
        if (state.Ignored || state.Switching) return;

        int cropX = state.Crop.X, cropY = state.Crop.Y;
        int sourceWidth = state.SourceWidth, sourceHeight = state.SourceHeight;
        int hostWidth = state.Host.Width, hostHeight = state.Host.Height;
        if (sourceWidth <= 0 || sourceHeight <= 0 || hostWidth <= 0 || hostHeight <= 0) return;

        double localX = x - cropX;
        double localY = y - cropY;
        if (localX < 0 || localY < 0 || localX >= sourceWidth || localY >= sourceHeight) return;

        var reference = _input.PointerReference;
        if (Math.Abs(x - reference.X) <= 2 && Math.Abs(y - reference.Y) <= 2) return;

        var dest = LayoutCalculator.ComputeDestRect(sourceWidth, sourceHeight,
            hostWidth, hostHeight, _mode);
        int hostX = dest.X + (int)Math.Round(localX * dest.Width / sourceWidth);
        int hostY = dest.Y + (int)Math.Round(localY * dest.Height / sourceHeight);
        hostX = Math.Clamp(hostX, 0, hostWidth - 1);
        hostY = Math.Clamp(hostY, 0, hostHeight - 1);

        if (TraceInput)
            Emit($"nested cursor hint warp seq={seq} sway={x:0},{y:0} host={hostX},{hostY}");
        state.Host.WarpPointer(hostX, hostY);
        _input.SetPointerReference((int)Math.Round(x), (int)Math.Round(y));
    }

    private void Loop()
    {
        var stats = new FrameStats();
        long lastWaitLog = Environment.TickCount64;
        bool waitingLogged = false;

        while (!_stop)
        {
            IReadOnlyList<NestedWindowInfo> windows;
            try
            {
                _tracker!.Poll(allowFullscreen: false);
                windows = _tracker.AllWindows;
            }
            catch (Exception ex)
            {
                SetExitReason($"nested window tracking failed: {ex.GetType().Name}: {ex.Message}");
                break;
            }

            try
            {
                Reconcile(windows);
            }
            catch (Exception ex)
            {
                SetExitReason($"nested window reconcile failed: {ex.GetType().Name}: {ex.Message}");
                break;
            }

            if (_states.Count == 0)
            {
                if (_waitForCommand && !_session.IsCommandRunning)
                {
                    SetExitReason($"command exited code={_session.CommandExitCode}");
                    break;
                }
                long now = Environment.TickCount64;
                if (!waitingLogged || now - lastWaitLog >= 5000)
                {
                    waitingLogged = true;
                    lastWaitLog = now;
                    Emit($"nested waiting for game windows; candidates={_tracker!.LastCandidateCount} " +
                         $"outputs={_pool?.Outputs.Count ?? 0}");
                }
                var idleReport = stats.Tick();
                if (idleReport is not null)
                {
                    Emit($"stats {idleReport.ToLogString()} windows=0");
                    StatsCsv.TryWrite(idleReport);
                }
                Thread.Sleep(50);
                continue;
            }
            waitingLogged = false;

            if (_waitForCommand && !_session.IsCommandRunning && !_commandExitLogged)
            {
                _commandExitLogged = true;
                Emit($"nested command exited code={_session.CommandExitCode}; " +
                     "waiting for remaining game windows to close");
            }

            // Pump every host before changing modes or disposing anything. X11
            // event callbacks only set flags; Close/Dispose must happen outside
            // XPending/XNextEvent or KWin/Xlib can crash the process.
            foreach (var state in _states.Values.ToList())
            {
                if (!_stop) state.Host?.Pump();
            }
            ProcessPendingRequests();
            SyncMinimizedStates();
            if (_stop) break;

            // The command may have exited while hosts still existed; once the
            // last game window is gone, end one-shot sessions.
            if (_waitForCommand && !_session.IsCommandRunning && _states.Count == 0)
            {
                SetExitReason($"command exited code={_session.CommandExitCode}");
                break;
            }

            bool rendered = false;
            foreach (var state in _states.Values.ToList())
            {
                if (_stop) break;
                if (!_states.ContainsKey(state.Id)) continue;
                if (state.Ignored || state.Host is null || state.Blitter is null) continue;
                if (RenderState(state, stats)) rendered = true;
                CheckCaptureStall(state);
            }

            if (rendered)
            {
                // Report is emitted below; RenderState already recorded samples.
            }

            var report = stats.Tick();
            if (report is not null)
            {
                Emit($"stats {report.ToLogString()} windows={_states.Count} " +
                     $"modes={DescribeModes()}");
                StatsCsv.TryWrite(report);
            }

            Thread.Sleep(2);
        }

        Emit("nested session loop exited");
    }

    private void Reconcile(IReadOnlyList<NestedWindowInfo> windows)
    {
        if (_tracker is null || (!_toplevelMode && _pool is null)) return;

        var byId = windows.ToDictionary(w => w.Id);
        var newWindows = windows.Where(w => !_states.ContainsKey(w.Id)).ToList();

        // Deiconifying an XWayland window (unmap+map) makes sway destroy and
        // recreate the view with a new con id, but it is still the same client
        // window. Reuse the existing host instead of popping a second KDE
        // window and closing the old one a second later.
        foreach (var window in newWindows.Where(w => w.X11Window != 0).ToList())
        {
            var existing = _states.Values.FirstOrDefault(s =>
                s.Info.X11Window != 0 && s.Info.X11Window == window.X11Window &&
                s.Id != window.Id);
            if (existing is not null) RekeyWindow(existing, window);
        }
        newWindows = windows.Where(w => !_states.ContainsKey(w.Id)).ToList();

        foreach (var window in newWindows)
        {
            if (_stop) break;
            long now = Environment.TickCount64;
            if (_retryAfter.TryGetValue(window.Id, out long retryAt) && now < retryAt)
                continue;
            if (AddWindow(window))
                _retryAfter.Remove(window.Id);
            else
                _retryAfter[window.Id] = Environment.TickCount64 + FailedWindowRetryMs;
        }

        // Output creation and window moves can change XWayland's layout and
        // therefore a window's global rect. Re-poll after the spare exists so
        // the first crop never uses coordinates from the old output.
        IReadOnlyList<NestedWindowInfo> latest = windows;
        try
        {
            _tracker.Poll(allowFullscreen: false);
            latest = _tracker.AllWindows;
        }
        catch { }
        byId = latest.ToDictionary(w => w.Id);
        if (!_toplevelMode) _pool!.Refresh();

        var nowTick = Environment.TickCount64;
        foreach (var state in _states.Values)
        {
            if (byId.TryGetValue(state.Id, out var info))
            {
                state.Info = info;
                state.MissingSince = 0;
                UpdateStateGeometry(state);
            }
            else if (state.MissingSince == 0)
            {
                state.MissingSince = nowTick;
            }
        }

        var removals = _states.Values
            .Where(s => s.MissingSince != 0 &&
                        // We iconified this window ourselves: its view is gone
                        // on purpose until the host is restored from the
                        // taskbar. Only drop it if the client window died.
                        (!s.Minimized ||
                         (s.Info.X11Window != 0 && _probe is not null &&
                          !_probe.IsAlive(s.Info.X11Window))) &&
                        (s.CloseForwarded ||
                         nowTick - s.MissingSince >= MissingWindowGraceMs))
            .ToList();
        foreach (var state in removals)
            CloseState(state, releaseOutput: true);

        if (_toplevelMode) return;
        var used = _states.Values
            .Where(s => !string.IsNullOrWhiteSpace(s.OutputName))
            .Select(s => s.OutputName)
            .ToHashSet(StringComparer.Ordinal);
        try
        {
            if (used.Count > 0) _pool!.EnsureSpare(used);
            else _pool!.TrimToBase();
        }
        catch (Exception ex)
        {
            // A transient output-creation/unplug failure must not tear down
            // all existing window hosts. Retry on the next reconcile tick.
            Emit($"nested output pool maintenance failed: " +
                 $"{ex.GetType().Name}: {ex.Message}");
        }

        if (_states.Count == 0) _focusedId = 0;
    }

    /// <summary>
    /// Same client window, new sway view id (XWayland unmap+map). Keep the
    /// host, capture and output, update the id/geometry, and put the window
    /// back on its output so the crop stays valid.
    /// </summary>
    private void RekeyWindow(WindowState state, NestedWindowInfo window)
    {
        long oldId = state.Id;
        lock (_stateGate) _states.Remove(oldId);
        state.Id = window.Id;
        state.Info = window;
        state.MissingSince = 0;
        lock (_stateGate) _states[state.Id] = state;
        if (_focusedId == oldId) _focusedId = state.Id;
        if (_toggleRequestedId == oldId) _toggleRequestedId = state.Id;

        Emit($"nested window view re-created '{DescribeWindow(window)}' " +
             $"id {oldId} -> {window.Id}; reusing host on '{state.OutputName}'");

        try
        {
            if (!string.IsNullOrWhiteSpace(state.OutputName))
            {
                _tracker?.MoveToOutput(window.Id, state.OutputName);
                Thread.Sleep(30);
                _tracker?.Poll(allowFullscreen: false);
                if (_tracker?.AllWindows.FirstOrDefault(w => w.Id == window.Id) is { } moved)
                    state.Info = moved;
            }
        }
        catch (Exception ex)
        {
            Emit($"nested window rekey move failed for '{DescribeWindow(window)}': " +
                 $"{ex.GetType().Name}: {ex.Message}");
        }

        // The re-created view is a new seat target: without giving it keyboard
        // focus again Wine keeps the game paused (no repaint, no hover), because
        // the focus event that followed the host restore still carried the old
        // con id.
        if (_focusedId == state.Id)
        {
            try
            {
                bool ok = _tracker?.FocusWindow(state.Id) ?? false;
                Emit($"nested view re-created focus id={state.Id} nested_focus_ok={ok}");
            }
            catch (Exception ex)
            {
                Emit($"nested view re-created focus failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        UpdateStateGeometry(state);
        // sway destroys the toplevel and creates a new one for the same X11
        // window (unmap/map, e.g. when the game is deiconified), so the capture
        // source of the old view is dead. Without a new capture the host keeps
        // presenting the last frame while the game is already painting again.
        RecreateCapture(state);
        state.ForceRender = true;
    }

    private void RecreateCapture(WindowState state)
    {
        if (!_toplevelMode || state.Capture is null) return;
        // The new toplevel may not have committed a buffer yet when the view is
        // reported; the session would then be stuck at 1x1. Retry briefly until
        // the capture reports the client's real size.
        ToplevelCapture capture;
        try
        {
            capture = new ToplevelCapture(_session.WaylandDisplay!,
                state.Info.AppId, state.Info.Name);
        }
        catch (Exception ex)
        {
            Emit($"nested capture re-create failed for '{DescribeWindow(state.Info)}': " +
                 $"{ex.GetType().Name}: {ex.Message}");
            state.CaptureRetryAt = Environment.TickCount64 + 200;
            return;
        }

        var old = state.Capture;
        state.Capture = capture;
        state.SourceWidth = capture.Width;
        state.SourceHeight = capture.Height;
        state.Diff.Reset();
        try { old?.Dispose(); } catch { }
        if (capture.Width <= 1 || capture.Height <= 1)
        {
            // The mapped client has not committed a buffer yet, and the capture
            // session does not report a later size change, so a session created
            // now stays 1x1 forever. Ask for a new one in a moment.
            state.CaptureRetryAt = Environment.TickCount64 + 150;
            state.SuppressBlackUntil = Environment.TickCount64 + 400;
            Emit($"nested capture for '{DescribeWindow(state.Info)}' has no buffer " +
                 "yet; retrying");
            return;
        }
        state.CaptureRetryAt = 0;
        Emit($"nested capture re-created for '{DescribeWindow(state.Info)}' " +
             $"{capture.Width}x{capture.Height}");
    }

    private bool AddWindow(NestedWindowInfo window)
    {
        if (_tracker is null || (!_toplevelMode && _pool is null)) return false;

        // A new toplevel must never appear behind the current fullscreen
        // upscale host. Return every existing host to original mode first.
        ExitAllUpscale();

        var used = _states.Values
            .Where(s => !string.IsNullOrWhiteSpace(s.OutputName))
            .Select(s => s.OutputName)
            .ToHashSet(StringComparer.Ordinal);

        bool committed = false;
        SwayOutputInfo outputInfo;
        if (_toplevelMode)
        {
            outputInfo = new SwayOutputInfo(SwayConfig.DefaultOutput,
                new RectI(0, 0, _outputWidth, _outputHeight), true, _outputWidth, _outputHeight);
        }
        else
        {
            try
            {
                outputInfo = _pool!.Acquire(used);
            }
            catch (Exception ex)
            {
                Emit($"nested output acquire failed for '{DescribeWindow(window)}': {ex.Message}");
                return false;
            }
        }

        var state = new WindowState(window.Id)
        {
            OutputName = _toplevelMode ? string.Empty : outputInfo.Name,
            OutputOrigin = outputInfo.Rect
        };
        try
        {
            _tracker.FloatWindow(window.Id);
            if (!_toplevelMode)
            {
                if (!_tracker.MoveToOutput(window.Id, outputInfo.Name))
                    throw new InvalidOperationException($"move to output '{outputInfo.Name}' failed");
                // The move changes the global origin of the window. Re-read the
                // tree instead of cropping with the coordinates from the old
                // output; otherwise the first frame is displaced or black.
                Thread.Sleep(40);
                _tracker.Poll(allowFullscreen: false);
                _pool!.Refresh();
                outputInfo = _pool.Find(outputInfo.Name) ?? outputInfo;
                state.OutputOrigin = outputInfo.Rect;
            }

            if (_tracker.AllWindows.FirstOrDefault(w => w.Id == window.Id) is { } moved)
                window = moved;

            INestedFrameSource? capture = _toplevelMode
                ? new ToplevelCapture(_session.WaylandDisplay!, window.AppId, window.Name)
                : WaitForCapture(outputInfo.Name);
            if (capture is null)
                throw new InvalidOperationException(
                    $"capture for '{DescribeWindow(window)}' unavailable");
            state.Capture = capture;

            // Toplevel capture returns the client's own buffer; the window rect
            // is the area the nested compositor displays and what pointer
            // coordinates must target, so it doubles as the input crop.
            var crop = _toplevelMode
                ? window.Rect
                : ComputeCrop(window.Rect, outputInfo.Rect, capture.Width, capture.Height);
            state.Crop = crop;
            state.SourceWidth = _toplevelMode ? capture.Width : crop.Width;
            state.SourceHeight = _toplevelMode ? capture.Height : crop.Height;
            string windowKey = WindowKey(window);
            bool legacyLarge = window.FullscreenMode != 0 ||
                (window.Rect.Height >= outputInfo.Rect.Height &&
                 window.Rect.Width < outputInfo.Rect.Width &&
                 window.Rect.Width >= outputInfo.Rect.Width * 0.65);
            if (legacyLarge &&
                _lastWindowedSizes.TryGetValue(windowKey, out var lastWindowed))
            {
                // Legacy games destroy the 800x600 window and create a new
                // fullscreen-sized one. The new window's geometry is already
                // 3840x2160, so use the size remembered from the window it
                // replaced for the later windowed restore.
                state.WindowedWidth = lastWindowed.Width;
                state.WindowedHeight = lastWindowed.Height;
            }
            else
            {
                // Toplevel capture: the window rect is what the user sees, so
                // it is the only trustworthy restore size. Wine often reports
                // a smaller requested size (the game's design resolution)
                // while sway actually shows a larger window; using the request
                // would restore the host to the wrong size (black bars).
                state.WindowedWidth = _toplevelMode
                    ? crop.Width
                    : InitialWindowedSize(window.RequestedWidth, crop.Width,
                        outputInfo.Rect.Width);
                state.WindowedHeight = _toplevelMode
                    ? crop.Height
                    : InitialWindowedSize(window.RequestedHeight, crop.Height,
                        outputInfo.Rect.Height);
            }
            state.LastGameFullscreen = window.FullscreenMode != 0;

            string title = WindowTitle(window);
            bool dialog = window.LooksLikeDialog || window.TransientFor is not null;
            bool focusNew = dialog || _states.Count == 0;
            var parentState = dialog ? FindDialogParent(window) : null;
            state.Info = window;
            state.IsDialog = dialog;
            state.ParentId = parentState?.Id;
            state.Host = CreateHostWindow(title, state.SourceWidth, state.SourceHeight,
                state.LastGameFullscreen, parentState, dialog);
            state.LastHostWidth = state.Host.Width;
            state.LastHostHeight = state.Host.Height;
            state.LastTitle = title;
            WireHost(state);

            state.Blitter = state.Host.CreateBlitter(OriginalEffect, capture.SourceIsRgb);
            state.Diff.Reset();
            state.ForceRender = true;

            lock (_stateGate) _states[state.Id] = state;
            committed = true;
            PrepareInput(state);
            Emit($"nested window '{DescribeWindow(window)}' output='{state.OutputName}' " +
                 $"origin={state.OutputOrigin} crop={state.Crop} " +
                 $"source={state.SourceWidth}x{state.SourceHeight} " +
                 $"client={state.Host.Width}x{state.Host.Height} " +
                 $"fullscreen={state.LastGameFullscreen} " +
                 $"spare={((_pool?.Outputs.Count ?? _states.Count) - _states.Count)}");

            if (focusNew)
            {
                state.Host.Show();
                state.Host.Focus();
                _focusedId = state.Id;
                _tracker.FocusWindow(state.Id);
            }
            return true;
        }
        catch (Exception ex)
        {
            Emit($"nested window setup failed for '{DescribeWindow(window)}': " +
                 $"{ex.GetType().Name}: {ex.Message}");
            if (committed) CloseState(state, releaseOutput: true);
            else
            {
                DisposeStateResources(state);
                if (!_toplevelMode && _pool is not null &&
                    !string.IsNullOrEmpty(state.OutputName))
                    _pool.Release(state.OutputName, used);
            }
            return false;
        }
    }

    private NestedCapture? WaitForCapture(string outputName)
    {
        if (_session.WaylandDisplay is null) return null;
        NestedCapture? capture = null;
        try
        {
            capture = new NestedCapture(_session.WaylandDisplay, outputName);
            long deadline = Environment.TickCount64 + 2000;
            while (Environment.TickCount64 < deadline && !_stop)
            {
                if (capture.TryCapture(out _)) return capture;
                Thread.Sleep(15);
            }
        }
        catch (Exception ex)
        {
            Emit($"nested capture create '{outputName}' failed: {ex.GetType().Name}: {ex.Message}");
        }
        try { capture?.Dispose(); } catch { }
        return null;
    }

    private void UpdateStateGeometry(WindowState state)
    {
        if (state.Host is null || state.Capture is null) return;
        if (!_toplevelMode && _pool is null) return;

        SwayOutputInfo? output = null;
        if (_pool is not null)
        {
            output = _pool.Find(state.OutputName);
            if (output is not null) state.OutputOrigin = output.Rect;
        }

        string title = WindowTitle(state.Info);
        if (title != state.LastTitle)
        {
            state.LastTitle = title;
            state.Host.SetTitle(title);
        }

        if (_toplevelMode)
        {
            // Toplevel capture always delivers the client buffer; only the
            // window rect (the input target) changes on move/resize. Keep the
            // windowed restore size in sync with the real rect so leaving
            // upscale returns to the size the user was looking at.
            bool gameFullscreen = state.Info.FullscreenMode != 0;
            if (!state.Upscale && !gameFullscreen &&
                state.Info.Rect.Width > 0 && state.Info.Rect.Height > 0)
            {
                bool sizeChanged = state.WindowedWidth != state.Info.Rect.Width ||
                                   state.WindowedHeight != state.Info.Rect.Height;
                state.WindowedWidth = state.Info.Rect.Width;
                state.WindowedHeight = state.Info.Rect.Height;
                // Follow a game that changed its own window size. An X11 host
                // resizes in place; a Wayland host is rebuilt (debounced)
                // because xdg_toplevel has no resize request.
                if (sizeChanged) ApplyHostRestoreSize(state);
            }

            // The host mirrors the game's own fullscreen state in original
            // mode, so switching fullscreen inside the game is visible without
            // pressing the upscale hotkey.
            if (gameFullscreen != state.LastGameFullscreen)
            {
                state.LastGameFullscreen = gameFullscreen;
                if (!state.Upscale && !state.Switching && state.Host is not null)
                    ApplyOriginal(state);
            }

            state.Crop = state.Info.Rect;
            UpdateDest(state);
            return;
        }

        // v1.0: fullscreen is an explicit game state only. Do not promote large
        // borderless Wine windows to sway fullscreen; those windows are
        // presented as windowed, and windowed upscaling is the supported path.
        var crop = ComputeCrop(state.Info.Rect, state.OutputOrigin,
            state.Capture.Width, state.Capture.Height);
        bool sourceChanged = crop.Width != state.SourceWidth ||
                             crop.Height != state.SourceHeight ||
                             !crop.Equals(state.Crop);
        bool fullscreen = state.Info.FullscreenMode != 0;
        bool fullscreenChanged = fullscreen != state.LastGameFullscreen;

        bool restoreSizeChanged = TrackWindowedSize(state, crop, output, fullscreen);

        state.Crop = crop;
        state.SourceWidth = crop.Width;
        state.SourceHeight = crop.Height;
        state.LastGameFullscreen = fullscreen;
        RememberWindowedSize(state, fullscreen);

        if (state.Upscale)
        {
            if (sourceChanged)
            {
                // Update only the restore geometry while the host is
                // fullscreen. Never replace it with the fullscreen crop.
                ApplyHostRestoreSize(state);
                state.Diff.Reset();
                state.ForceRender = true;
            }
            return;
        }

        if (fullscreenChanged)
        {
            ApplyOriginal(state);
        }
        else if (sourceChanged || restoreSizeChanged)
        {
            ApplyHostRestoreSize(state);
            if (!state.LastGameFullscreen)
            {
                PumpHost(state, 6);
                if (state.Host.Width != state.LastHostWidth ||
                    state.Host.Height != state.LastHostHeight)
                {
                    ReplaceBlitter(state, OriginalEffect);
                    state.LastHostWidth = state.Host.Width;
                    state.LastHostHeight = state.Host.Height;
                }
            }
            state.Diff.Reset();
            state.ForceRender = true;
            Emit($"nested window '{DescribeWindow(state.Info)}' crop={state.Crop} " +
                 $"client={state.Host.Width}x{state.Host.Height}");
        }
    }

    private static string WindowKey(NestedWindowInfo info)
        => $"{info.Pid}:{info.AppId ?? string.Empty}:{(info.Name ?? string.Empty).Trim()}";

    private void RememberWindowedSize(WindowState state, bool effectiveFullscreen)
    {
        if (effectiveFullscreen) return;
        if (state.WindowedWidth < 64 || state.WindowedHeight < 64) return;
        _lastWindowedSizes[WindowKey(state.Info)] =
            (state.WindowedWidth, state.WindowedHeight);
    }

    private static bool TrackWindowedSize(WindowState state, RectI crop,
        SwayOutputInfo? output, bool effectiveFullscreen)
    {
        if (effectiveFullscreen) return false;
        int outputWidth = output?.Rect.Width ?? 0;
        int outputHeight = output?.Rect.Height ?? 0;
        bool fillsOutput = outputWidth > 0 && outputHeight > 0 &&
                            crop.Width >= outputWidth && crop.Height >= outputHeight;
        if (fillsOutput && state.WindowedWidth > 0) return false;
        if (crop.Width <= 0 || crop.Height <= 0) return false;

        bool changed = state.WindowedWidth != crop.Width ||
                       state.WindowedHeight != crop.Height;
        state.WindowedWidth = crop.Width;
        state.WindowedHeight = crop.Height;
        return changed;
    }

    private static int InitialWindowedSize(int requested, int crop, int output)
    {
        if (requested >= 64 && requested < output) return requested;
        return crop;
    }

    /// <summary>
    /// Make the host window cover the game window's current size.
    ///
    /// <para>X11 can move/resize the window in place. A Wayland toplevel cannot:
    /// xdg-shell has no resize request, so the host is rebuilt when the request
    /// has been stable for <see cref="HostResizeDebounceMs"/> (or immediately
    /// during a mode switch, where the caller is already pausing the loop).</para>
    /// </summary>
    private void ApplyHostRestoreSize(WindowState state, bool immediate = false)
    {
        if (state.Host is null) return;
        int width = state.WindowedWidth > 0 ? state.WindowedWidth : state.SourceWidth;
        int height = state.WindowedHeight > 0 ? state.WindowedHeight : state.SourceHeight;
        if (width < 64 || height < 64) return;

        if (state.Host.CanResize)
        {
            state.Host.SetOriginalSize(width, height);
            return;
        }

        // Never rebuild while the host is fullscreen; the size is applied when
        // the windowed mode is entered again.
        if (state.Upscale || state.LastGameFullscreen)
        {
            state.ResizeTarget = null;
            return;
        }

        if (Math.Abs(width - state.Host.Width) <= 2 &&
            Math.Abs(height - state.Host.Height) <= 2)
        {
            state.ResizeTarget = null;
            return;
        }

        var target = (Width: width, Height: height);
        if (state.ResizeTarget != target)
        {
            state.ResizeTarget = target;
            state.ResizeTargetAt = Environment.TickCount64;
        }
        if (immediate) RebuildHost(state, width, height);
    }

    private void ApplyOriginal(WindowState state)
    {
        if (state.Host is null || state.Blitter is null || state.Switching) return;
        state.Switching = true;
        try
        {
            state.Upscale = false;
            if (state.LastGameFullscreen)
            {
                state.Host.SetFullscreen(true);
            }
            else
            {
                // Clear fullscreen first. SetOriginalSize must not be called
                // with the fullscreen crop: it becomes KWin's restore size
                // and leaves the "windowed" host at 4K without decorations.
                state.Host.SetFullscreen(false);
                ApplyHostRestoreSize(state, immediate: true);
            }
            PumpHost(state, 8);
            ReplaceBlitter(state, OriginalEffect);
            state.LastHostWidth = state.Host.Width;
            state.LastHostHeight = state.Host.Height;
            state.Diff.Reset();
            state.ForceRender = true;
            UpdateDest(state);
            Emit($"nested window '{DescribeWindow(state.Info)}' " +
                 $"mode=original client={state.Host.Width}x{state.Host.Height} " +
                 $"fullscreen={state.LastGameFullscreen}");
        }
        catch (Exception ex)
        {
            Emit($"nested original presentation failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            state.Switching = false;
        }
    }

    private void SetMode(WindowState state, bool upscale)
    {
        if (state.Switching || state.Host is null || state.Blitter is null)
            return;
        if (state.Upscale == upscale) return;

        if (upscale)
        {
            foreach (var other in _states.Values.ToList())
            {
                if (other.Id != state.Id && other.Upscale)
                    SetMode(other, false);
            }
        }

        state.Switching = true;
        try
        {
            var effect = upscale ? _effect : OriginalEffect;
            bool prepared = PrepareModePresentation(state, upscale, effect);

            if (upscale)
            {
                state.Host.SetFullscreen(true);
            }
            else
            {
                if (state.LastGameFullscreen)
                {
                    state.Host.SetFullscreen(true);
                }
                else
                {
                    state.Host.SetFullscreen(false);
                    ApplyHostRestoreSize(state, immediate: true);
                }
            }

            PumpHost(state, 10);
            state.Upscale = upscale;
            if (!prepared) ReplaceBlitter(state, effect);
            // The swapchain may have been recreated; a fresh capture session
            // always delivers its first frame, so a static game is not left
            // black.
            if (state.Capture is ToplevelCapture toplevelCapture)
                toplevelCapture.RestartSession();
            state.LastHostWidth = state.Host.Width;
            state.LastHostHeight = state.Host.Height;
            state.Diff.Reset();
            state.ForceRender = true;
            UpdateDest(state);
            Emit($"nested window '{DescribeWindow(state.Info)}' " +
                 $"mode={(upscale ? "upscale" : "original")} " +
                 $"client={state.Host.Width}x{state.Host.Height}");
        }
        catch (Exception ex)
        {
            Emit($"nested mode switch failed: {ex.GetType().Name}: {ex.Message}");
            state.Upscale = false;
        }
        finally
        {
            state.Switching = false;
        }
    }

    /// <summary>
    /// G4 A: build the presenter for the size the window is about to become
    /// *before* asking the compositor to change its geometry. KWin otherwise
    /// scales the old window-sized buffer into the new fullscreen surface for
    /// the ~300 ms a fresh swapchain and its pipelines take to build (the
    /// "first upscale frame is stretched" report).
    ///
    /// A native Wayland surface accepts a buffer of any size and maps it onto
    /// the current logical window through wp_viewport, so the prepared buffer
    /// can be presented while the window is still small. The configure that
    /// follows only changes the viewport destination; the frame the compositor
    /// already holds is then shown at the new size instead of the old one.
    /// </summary>
    private bool PrepareModePresentation(WindowState state, bool upscale, EffectDefinition effect)
    {
        if (!state.Host!.CanPrepareSize || state.Capture is null) return false;

        bool fullscreenTarget = upscale || state.LastGameFullscreen;
        int width = fullscreenTarget ? _outputWidth : Math.Max(1, state.WindowedWidth);
        int height = fullscreenTarget ? _outputHeight : Math.Max(1, state.WindowedHeight);
        if (width < 2 || height < 2) return false;
        if (width == state.Host.Width && height == state.Host.Height) return false;

        // A surface that already has a live swapchain cannot serve a second
        // VkDevice ("no graphics+present queue family" from the driver), so
        // retire the old presenter first. KWin keeps the last attached buffer
        // on screen until the prepared one presents, so the small window does
        // not flash while the new swapchain is built.
        var previous = state.Blitter;
        state.Blitter = null;
        try { previous?.Dispose(); } catch { }

        VulkanBlitter prepared;
        try
        {
            prepared = state.Host.CreateBlitter(effect, state.Capture.SourceIsRgb,
                width, height);
        }
        catch (Exception ex)
        {
            Emit($"nested mode prepare failed for '{DescribeWindow(state.Info)}': " +
                 $"{ex.GetType().Name}: {ex.Message}");
            try
            {
                state.Blitter = state.Host.CreateBlitter(upscale ? OriginalEffect : _effect,
                    state.Capture.SourceIsRgb);
            }
            catch (Exception recover)
            {
                Emit($"nested mode prepare recovery failed: " +
                     $"{recover.GetType().Name}: {recover.Message}");
            }
            state.LastHostWidth = state.Host.Width;
            state.LastHostHeight = state.Host.Height;
            return false;
        }

        state.Blitter = prepared;
        state.Upscale = upscale;
        state.LastHostWidth = state.Host.Width;
        state.LastHostHeight = state.Host.Height;
        state.Diff.Reset();
        state.ForceRender = true;
        UpdateDest(state);
        Emit($"nested mode prepared {width}x{height} for " +
             $"'{DescribeWindow(state.Info)}' before the window resize");

        // Present now, while the window is still its old size. The configure
        // that follows only moves the viewport destination; the compositor
        // already holds this target-sized frame.
        RenderState(state, new FrameStats());
        return true;
    }

    /// <summary>
    /// Pick the KDE host that should own a dialog. sway reports the game's
    /// X11 parent id in window_properties.transient_for, so match it against
    /// the *inner* game windows (same X server), not the KDE-facing hosts.
    /// </summary>
    private WindowState? FindDialogParent(NestedWindowInfo dialog)
    {
        if (dialog.TransientFor is int xid and not 0)
        {
            var match = _states.Values.FirstOrDefault(s =>
                s.Id != dialog.Id && s.Info.X11Window != 0 &&
                s.Info.X11Window == (ulong)xid && s.Host is not null);
            if (match is not null) return match;
        }

        if (_states.TryGetValue(_focusedId, out var focused) &&
            focused.Id != dialog.Id && focused.Host is not null)
            return focused;
        return _states.Values.FirstOrDefault(s => s.Host is not null && s.Id != dialog.Id);
    }

    /// <summary>
    /// Create the KDE-facing window for one game window. The X11 backend needs
    /// the parent X11 window id and the dialog hint *before* the window is
    /// mapped, while the Wayland backend sets xdg_toplevel.set_parent.
    /// </summary>
    private IHostWindow CreateHostWindow(string title, int width, int height, bool fullscreen,
        WindowState? parent, bool dialog)
    {
        if (_waylandHost)
        {
            try
            {
                return new WaylandHostWindow(_monitorName, title, width, height, fullscreen,
                    parent?.Host as WaylandHostWindow, dialog);
            }
            catch (Exception ex)
            {
                Emit($"nested wayland host unavailable ({ex.GetType().Name}: {ex.Message}); " +
                     "falling back to the X11 host window for this session");
                _waylandHost = false;
            }
        }

        ulong transientFor = parent?.Host is NestedHostWindow x11Parent ? x11Parent.Window : 0;
        return new NestedHostWindow(_monitorName, title, width, height,
            startFullscreen: fullscreen, transientFor: transientFor, dialog: dialog);
    }

    /// <summary>
    /// Subscribe the session to one host window. Called for the initial window
    /// and again whenever a Wayland host is recreated at a new size.
    /// </summary>
    private void WireHost(WindowState state)
    {
        if (state.Host is null) return;
        state.Host.Pointer += e => OnPointer(state, e);
        state.Host.Key += e => OnKey(state, e);
        state.Host.Modifiers += m =>
        {
            _modifiers = m;
            _input?.HandleModifiers(m);
        };
        state.Host.FocusChanged += focused => OnHostFocusChanged(state, focused);
        state.Host.Closed += () => state.CloseRequested = true;
        state.Host.CloseRequested += () => state.CloseRequested = true;

        // The host draws through a Vulkan swapchain, which has no retained
        // front buffer. If the compositor discards our window contents
        // (occluded, newly mapped) and the game frame is static, the dirty
        // diff would skip presenting and the host would stay black. An
        // Expose must always force one fresh present.
        state.Host.Exposed += () => state.ForceRender = true;
        state.Host.Restored += () => RestoreMinimizedWindow(state);
    }

    /// <summary>
    /// Replace a Wayland host window with a new one at <paramref name="width"/>
    /// x <paramref name="height"/>. xdg_toplevel has no client-side resize
    /// request (set_min_size/set_max_size only constrain later compositor
    /// decisions), so following a game that changed its window size means a new
    /// toplevel. Games resize rarely; the brief window flash is the price for a
    /// correct size and no letterboxing.
    /// </summary>
    private void RebuildHost(WindowState state, int width, int height)
    {
        if (state.Host is null || state.Host.CanResize) return;
        state.ResizeTarget = null;
        if (width < 64 || height < 64) return;

        string title = state.LastTitle ?? WindowTitle(state.Info);
        WindowState? parent = null;
        if (state.IsDialog && state.ParentId is long parentId &&
            _states.TryGetValue(parentId, out var parentState) && parentState.Host is not null)
            parent = parentState;

        int oldWidth = state.Host.Width;
        int oldHeight = state.Host.Height;
        bool wasFocused = state.Id == _focusedId;
        try
        {
            try { state.Blitter?.Dispose(); }
            catch (Exception ex) { Log.Warn($"nested host recreate blitter dispose: {ex.Message}"); }
            state.Blitter = null;
            try { state.Host.Dispose(); }
            catch (Exception ex) { Log.Warn($"nested host recreate dispose: {ex.Message}"); }

            state.Host = CreateHostWindow(title, width, height, fullscreen: false, parent,
                state.IsDialog);
            WireHost(state);
            state.Blitter = state.Host.CreateBlitter(state.Upscale ? _effect : OriginalEffect,
                state.Capture?.SourceIsRgb ?? true);
            state.LastHostWidth = state.Host.Width;
            state.LastHostHeight = state.Host.Height;
            state.Diff.Reset();
            state.ForceRender = true;
            if (wasFocused) state.Host.Show();
            UpdateDest(state);
            Emit($"nested host recreated for '{DescribeWindow(state.Info)}' " +
                 $"{oldWidth}x{oldHeight} -> {state.Host.Width}x{state.Host.Height} " +
                 "(wayland toplevel cannot be resized)");
        }
        catch (Exception ex)
        {
            Emit($"nested host recreate failed for '{DescribeWindow(state.Info)}': " +
                 $"{ex.GetType().Name}: {ex.Message}");
            state.Ignored = true;
            DisposeStateResources(state);
        }
    }

    private void ExitAllUpscale()
    {
        foreach (var state in _states.Values.ToList())
        {
            if (state.Upscale)
                SetMode(state, false);
        }
    }

    private void EnsureMinimizeProbe()
    {
        if (_probe is not null || _probeUnavailable) return;
        string? display = _session.Display;
        if (string.IsNullOrWhiteSpace(display))
        {
            _probeUnavailable = true;
            Emit("nested minimize probe unavailable: nested DISPLAY unknown");
            return;
        }
        try
        {
            _probe = new NestedWindowProbe(display);
            Emit($"nested minimize probe attached to DISPLAY={display}");
        }
        catch (Exception ex)
        {
            _probeUnavailable = true;
            Emit($"nested minimize probe unavailable ({display}): " +
                 $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Mirror a game-side minimize onto the KDE host. Wine asks the window
    /// manager to iconify the window with <c>WM_CHANGE_STATE(IconicState)</c>;
    /// KWin honors that request, but the nested sway has no minimize concept
    /// and ignores it, so the window stays on the headless output, the client
    /// stops painting and the host would keep showing the last (black) frame it
    /// rendered. Mirror the request on the host instead, and let the host's
    /// taskbar restore drive the game back (see <see cref="RestoreMinimizedWindow"/>).
    /// </summary>
    private void SyncMinimizedStates()
    {
        if (_probe is null) return;

        // Minimize requests arrive as X11 events; draining them is cheap, so do
        // it on every loop iteration to win the race against the black frame
        // the client paints while it hides itself.
        while (_probe.TryTakeMinimizeRequest(out ulong xid))
            MirrorMinimizeRequest(xid);

        // Secondary trigger: clients (or a real WM) that set WM_STATE=IconicState
        // themselves. Polled rarely because the property read costs a round trip.
        long now = Environment.TickCount64;
        if (now - _lastMinimizePoll < MinimizePollMs) return;
        _lastMinimizePoll = now;

        foreach (var state in _states.Values.ToList())
        {
            if (state.Ignored || state.Info.X11Window == 0 || state.Minimized) continue;
            bool iconic;
            try
            {
                iconic = _probe.IsIconified(state.Info.X11Window);
            }
            catch (Exception ex)
            {
                Emit($"nested minimize probe failed for '{DescribeWindow(state.Info)}': " +
                     $"{ex.GetType().Name}: {ex.Message}");
                continue;
            }
            if (iconic) MirrorMinimize(state);
        }
    }

    private void MirrorMinimizeRequest(ulong xid)
    {
        foreach (var state in _states.Values.ToList())
        {
            if (state.Ignored || state.Info.X11Window != xid || state.Minimized) continue;
            MirrorMinimize(state);
        }
    }

    private void MirrorMinimize(WindowState state)
    {
        // The client hides itself and paints one black frame before it stops
        // painting; presenting that frame is the "host flashes black on Esc"
        // report. Hold black frames for a moment so the last real frame stays
        // on screen.
        //
        // The host window is not minimized and the game window is not
        // unmapped: Wine cannot be brought back from a client-side map inside
        // the nested session. It re-sends the minimize request ~25ms after
        // every map (even when the map follows KWin's exact
        // _NET_WM_STATE_HIDDEN/WM_STATE order, verified against a direct run),
        // because its Win32 minimized state is never cleared by a map. The
        // window would bounce to the taskbar right after the user restores it,
        // which is worse than leaving it on screen with the last frame.
        state.SuppressBlackUntil = Environment.TickCount64 + 600;
        Emit($"nested window '{DescribeWindow(state.Info)}' asked to minimize; " +
             "holding the black frame (host keeps the last frame)");
    }

    /// <summary>
    /// The user restored the KDE host from the taskbar. Ask the game window to
    /// come back too, otherwise the host can only show the stale frame the
    /// client left behind when it minimized itself.
    /// </summary>
    private void RestoreMinimizedWindow(WindowState state)
    {
        if (!state.Minimized) return;
        state.Minimized = false;
        // The view id is stale while the window is iconic. Restart the
        // missing-window grace so the re-created view can be rekeyed instead of
        // the host being closed and re-created (which would move the window).
        state.MissingSince = 0;
        state.ForceRender = true;
        // Deiconify unmaps and maps the window, which destroys and re-creates
        // its toplevel: the capture of the dying surface returns one black
        // frame, so hold black frames for a moment until the client repaints.
        state.SuppressBlackUntil = Environment.TickCount64 + 500;
        if (state.Info.X11Window == 0 || _probe is null) return;
        Emit($"nested host restored; deiconifying '{DescribeWindow(state.Info)}'");
        try
        {
            if (!_probe.Deiconify(state.Info.X11Window))
                Emit($"nested window '{DescribeWindow(state.Info)}' could not be deiconified");
        }
        catch (Exception ex)
        {
            Emit($"nested window deiconify failed for '{DescribeWindow(state.Info)}': " +
                 $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private void OnHostFocusChanged(WindowState state, bool focused)
    {
        // An iconified host is not on screen; do not let focus churn (which
        // KWin emits while minimizing) switch nested focus or exit upscale.
        if (state.Minimized)
        {
            Emit($"nested host focus id={state.Id} focused={focused} ignored (minimized)");
            return;
        }
        if (focused)
        {
            if (state.Switching)
            {
                Emit($"nested host focus id={state.Id} focused=true ignored (switching)");
                return;
            }
            _focusedId = state.Id;
            bool nestedFocused = false;
            try { nestedFocused = _tracker?.FocusWindow(state.Id) ?? false; } catch { }
            Emit($"nested host focus id={state.Id} focused=true nested_focus_ok={nestedFocused}");
            if (state.Host is not null)
            {
                state.Host.Show();
                // Re-showing maps/raises an X11 window whose swapchain front
                // buffer is gone; present once even if the game frame is
                // static.
                state.ForceRender = true;
                PrepareInput(state);
            }
            return;
        }

        Emit($"nested host focus id={state.Id} focused=false upscale={state.Upscale} " +
             $"switching={state.Switching}");
        if (_focusedId == state.Id) _focusedId = 0;
        if (state.Upscale && !state.Switching)
            state.PendingOriginal = true;
    }

    private void OnPointer(WindowState state, OverlayPointerEvent e)
    {
        if (TraceInput)
            Emit($"nested pointer trace kind={e.Kind} host={e.X},{e.Y} " +
                 $"shell={state.Info.Shell} ignored={state.Ignored} switching={state.Switching}");
        if (state.Ignored || _input is null) return;
        // Do not forward input into a window that has not presented a frame
        // yet. A synthetic click arriving during Wine's window/renderer
        // initialization can leave the game stuck (observed with 腐姬).
        if (!state.SawFirstFrame)
        {
            if (TraceInput)
                Emit($"nested input trace: dropped {e.Kind} before first frame " +
                     $"'{DescribeWindow(state.Info)}'");
            return;
        }
        if (e.Kind is OverlayPointerKind.ButtonPress or OverlayPointerKind.ButtonRelease
            or OverlayPointerKind.WheelUp or OverlayPointerKind.WheelDown or OverlayPointerKind.Axis &&
            IsBlockedByModal(state))
        {
            return;
        }
        PrepareInput(state);
        _input.SetTarget(string.Equals(state.Info.Shell, "xwayland", StringComparison.Ordinal));
        _input.HandlePointer(e);
    }

    private void OnKey(WindowState state, OverlayKeyEvent e)
    {
        if (state.Ignored || _input is null) return;
        if (e.State == OverlayKeyState.Repeated) return;
        if (IsBlockedByModal(state)) return;

        // Alt+Shift+Q exits the nested session.
        if (e.KeyCode == 16 && e.State == OverlayKeyState.Pressed &&
            (_modifiers.Depressed & 1) != 0 && (_modifiers.Depressed & 8) != 0)
        {
            SetExitReason("exit hotkey");
            _stop = true;
            return;
        }

        // Alt+Shift+A toggles the focused window only.
        if (e.KeyCode == 30 && e.State == OverlayKeyState.Pressed &&
            (_modifiers.Depressed & 1) != 0 && (_modifiers.Depressed & 8) != 0)
        {
            if (state.IsDialog)
            {
                Emit($"nested upscale ignored for dialog '{DescribeWindow(state.Info)}'");
                return;
            }
            long now = Environment.TickCount64;
            if (now - Interlocked.Read(ref _lastToggleAt) >= 300)
            {
                Interlocked.Exchange(ref _lastToggleAt, now);
                Interlocked.Exchange(ref _toggleRequestedId, state.Id);
                Interlocked.Exchange(ref _toggleRequested, 1);
            }
            return;
        }

        if (!state.SawFirstFrame)
        {
            if (TraceInput)
                Emit($"nested input trace: dropped key {e.KeyCode} before first frame " +
                     $"'{DescribeWindow(state.Info)}'");
            return;
        }
        PrepareInput(state);
        _input.SetTarget(string.Equals(state.Info.Shell, "xwayland", StringComparison.Ordinal));
        _input.HandleKey(e.KeyCode, e.State == OverlayKeyState.Pressed);
    }

    private bool IsBlockedByModal(WindowState state)
    {
        if (state.Host is null) return false;
        // A modal dialog is transient for the game window it blocks. Both ids
        // live in the nested compositor's XWayland, so compare those instead
        // of the KDE-facing host ids (different X server, never equal).
        ulong parentXid = state.Info.X11Window;
        if (parentXid == 0) return false;
        foreach (var other in _states.Values)
        {
            if (other.Id == state.Id || other.Ignored || other.Host is null) continue;
            if (other.Info.TransientFor is int transient && (ulong)transient == parentXid)
                return true;
        }
        return false;
    }

    private void ProcessPendingRequests()
    {
        // A Wayland host is rebuilt only after the requested size has been
        // stable for a moment, so an animated game resize does not spawn a
        // window per intermediate step.
        long nowTick = Environment.TickCount64;
        foreach (var state in _states.Values.ToList())
        {
            if (state.ResizeTarget is not { } target) continue;
            if (state.Ignored || state.Upscale || state.LastGameFullscreen || state.Minimized)
            {
                state.ResizeTarget = null;
                continue;
            }
            if (nowTick - state.ResizeTargetAt < HostResizeDebounceMs) continue;
            RebuildHost(state, target.Width, target.Height);
        }

        int toggle = Interlocked.Exchange(ref _toggleRequested, 0);
        if (toggle != 0)
        {
            long id = Interlocked.Read(ref _toggleRequestedId);
            if (_states.TryGetValue(id, out var state) && state.Host is not null)
                SetMode(state, !state.Upscale);
        }

        foreach (var state in _states.Values.ToList())
        {
            if (state.PendingOriginal)
            {
                state.PendingOriginal = false;
                SetMode(state, false);
            }
            if (state.CloseRequested)
            {
                state.CloseRequested = false;
                HandleHostClose(state);
            }
        }
    }

    /// <summary>
    /// The user clicked the KDE close button on our mirror window. On a normal
    /// desktop that click is the compositor sending <c>xdg_toplevel.close</c>
    /// to the game client, so forward the same request to the game window in
    /// the nested compositor instead of tearing down the session.
    ///
    /// <para>Closing the mirror used to end the session and kill the whole
    /// process tree, which also killed the game when the user only wanted to
    /// dismiss an exit-confirmation dialog ("close the dialog, game keeps
    /// running" is what KDE does).</para>
    /// </summary>
    private void HandleHostClose(WindowState state)
    {
        if (state.Ignored || _stop) return;

        // An iconified game window has no sway view to forward to; ask the X11
        // window itself, the way a window manager would.
        if (state.Minimized && state.Info.X11Window != 0 && _probe is not null)
        {
            bool sent = _probe.RequestClose(state.Info.X11Window);
            Emit($"nested host close requested for iconified " +
                 $"'{DescribeWindow(state.Info)}'; sent WM_DELETE_WINDOW (ok={sent})");
            if (sent)
            {
                state.CloseForwarded = true;
                return;
            }
        }

        // A dialog is a separate sway view, so a plain close request reaches
        // exactly that view: sway sends xdg_toplevel.close to a Wayland client
        // and WM_DELETE_WINDOW to an XWayland one.
        if (_ipc is not null && state.Id != 0)
        {
            bool forwarded;
            try
            {
                forwarded = _ipc.RunCommand($"[con_id={state.Id}] kill");
            }
            catch (Exception ex)
            {
                forwarded = false;
                Log.Warn($"nested host close forward failed: {ex.Message}");
            }
            Emit($"nested host close requested for '{DescribeWindow(state.Info)}'; " +
                 $"forwarded to the nested window (ok={forwarded})");
            if (forwarded)
            {
                // The view disappears from sway's tree once the client honours
                // the request; Reconcile then disposes this mirror without the
                // usual grace period, so the window does not linger for a
                // second after the click. If the client ignores the request the
                // window stays, exactly as it would for a direct Wayland client.
                state.CloseForwarded = true;
                return;
            }
        }

        // No IPC or the request was rejected: only close this mirror window and
        // keep the session (and the game) running.
        state.Ignored = true;
        if (_focusedId == state.Id) _focusedId = 0;
        DisposeStateResources(state);
        Emit($"nested host window closed for '{DescribeWindow(state.Info)}'; " +
             "nested session stays idle");
    }

    private bool RenderState(WindowState state, FrameStats stats)
    {
        if (state.Host is null || state.Blitter is null || state.Capture is null)
            return false;
        // A minimized client stopped painting; capturing would only feed the
        // stale/black frame into the (possibly still visible) host.
        if (state.Minimized)
            return false;
        // A capture created before the client committed its first buffer stays
        // 1x1; recreate it until the compositor reports the real size.
        if (_toplevelMode && (state.Capture.Width <= 1 || state.Capture.Height <= 1) &&
            Environment.TickCount64 >= state.CaptureRetryAt)
        {
            RecreateCapture(state);
            if (state.Capture is null) return false;
        }

        if (state.Host.Width != state.LastHostWidth || state.Host.Height != state.LastHostHeight)
        {
            Emit($"nested host resized {state.LastHostWidth}x{state.LastHostHeight} -> " +
                 $"{state.Host.Width}x{state.Host.Height}");
            state.LastHostWidth = state.Host.Width;
            state.LastHostHeight = state.Host.Height;
            ReplaceBlitter(state, state.Upscale ? _effect : OriginalEffect);
            if (state.Capture is ToplevelCapture resizedCapture)
                resizedCapture.RestartSession();
            state.Diff.Reset();
            state.ForceRender = true;
        }

        NestedFrame frame;
        bool reusedLastFrame = false;
        if (!state.Capture.TryCapture(out frame))
        {
            // A freshly created host window has no buffer yet, so KWin does not
            // map it and the game window seems to vanish. Re-present the last
            // captured frame into it instead of waiting for the client to
            // repaint (which may never happen for a static game).
            // Use the dimensions of the last *converted* frame: the capture's
            // current Width/Height change as soon as new buffer constraints
            // arrive, before a frame of that size exists.
            if (!state.ForceRender || !state.Capture.TryGetLastFrame(out frame))
                return false;
            reusedLastFrame = true;
        }

        ReadOnlySpan<byte> pixels;
        int cropStride;
        if (_toplevelMode)
        {
            if (frame.Width != state.SourceWidth || frame.Height != state.SourceHeight)
            {
                state.SourceWidth = frame.Width;
                state.SourceHeight = frame.Height;
                state.Diff.Reset();
                state.ForceRender = true;
            }
            pixels = state.Capture.Pixels;
            cropStride = frame.Stride;
        }
        else
        {
            var crop = ComputeCrop(state.Info.Rect, state.OutputOrigin, frame.Width, frame.Height);
            if (!crop.Equals(state.Crop) || crop.Width != state.SourceWidth ||
                crop.Height != state.SourceHeight)
            {
                state.Crop = crop;
                state.SourceWidth = crop.Width;
                state.SourceHeight = crop.Height;
                ApplyHostRestoreSize(state);
                state.Diff.Reset();
                state.ForceRender = true;
            }
            pixels = CropFrame(state, state.Capture.Pixels, frame, state.Crop, out cropStride);
        }
        var dest = LayoutCalculator.ComputeDestRect(state.SourceWidth, state.SourceHeight,
            state.Host.Width, state.Host.Height, _mode);
        PrepareInput(state, dest);

        var dirty = state.Upscale
            ? state.Diff.Update(pixels, state.SourceWidth, state.SourceHeight, cropStride,
                dest, state.Blitter.DirtyHalo + 2)
            : DirtyFrame.Full;
        if (!state.ForceRender && dirty.IsStatic) return false;

        if (TraceFrames)
            TraceFrame(state, pixels, cropStride, frame, dest, reusedLastFrame);

        if (state.SuppressBlackUntil != 0 &&
            Environment.TickCount64 < state.SuppressBlackUntil &&
            IsUniformlyBlack(pixels, state.SourceWidth, state.SourceHeight, cropStride))
        {
            // Keep the last good frame instead of a torn-down surface's copy.
            state.ForceRender = false;
            Emit($"nested black frame held back for " +
                 $"'{DescribeWindow(state.Info)}'");
            return false;
        }
        state.SuppressBlackUntil = 0;

        state.ForceRender = false;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            state.Blitter.Render(pixels, state.SourceWidth, state.SourceHeight, cropStride,
                dest, dirty);
        }
        catch (Exception ex) when (IsOutOfDate(ex))
        {
            Emit($"nested swapchain out of date for '{DescribeWindow(state.Info)}'; " +
                 $"recreating ({ex.Message})");
            try
            {
                ReplaceBlitter(state, state.Upscale ? _effect : OriginalEffect);
                state.LastHostWidth = state.Host.Width;
                state.LastHostHeight = state.Host.Height;
                state.Diff.Reset();
                state.ForceRender = true;
            }
            catch (Exception recover)
            {
                Emit($"nested swapchain recovery failed: {recover.GetType().Name}: {recover.Message}");
                state.Ignored = true;
                DisposeStateResources(state);
            }
            return false;
        }
        catch (Exception ex)
        {
            Emit($"nested present error for '{DescribeWindow(state.Info)}': " +
                 $"{ex.GetType().Name}: {ex.Message} " +
                 $"source={state.SourceWidth}x{state.SourceHeight} " +
                 $"capture={state.Capture?.Width}x{state.Capture?.Height} " +
                 $"stride={state.Capture?.Stride}");
            state.Ignored = true;
            DisposeStateResources(state);
            return false;
        }

        state.LastFrameAt = Environment.TickCount64;
        if (!state.SawFirstFrame)
        {
            state.SawFirstFrame = true;
            Emit($"nested first frame '{DescribeWindow(state.Info)}' " +
                 $"{state.SourceWidth}x{state.SourceHeight}");
        }
        stats.OnCaptured();
        stats.OnPresented();
        stats.OnFrame(new FrameTimings(
            clock.Elapsed.TotalMilliseconds, 0,
            state.Blitter.LastUploadCpuMs, state.Blitter.LastEffectCpuMs,
            state.Blitter.LastPresentCpuMs, state.Blitter.LastTotalCpuMs,
            state.Blitter.LastGpuUploadMs, state.Blitter.LastGpuEffectMs,
            state.Blitter.LastGpuTailMs, state.Blitter.LastGpuTimingsValid));
        return true;
    }

    /// <summary>
    /// Diagnostic aid for the "host flashes black for a moment" reports: sample
    /// a grid over the frame that is about to be presented and log a fingerprint
    /// so the log shows whether the app itself presented a black frame.
    /// </summary>
    private void TraceFrame(WindowState state, ReadOnlySpan<byte> pixels, int stride,
        in NestedFrame frame, in RectI dest, bool reused)
    {
        int w = state.SourceWidth, h = state.SourceHeight;
        if (w <= 0 || h <= 0 || stride < w * 4 || pixels.Length < (long)stride * h) return;
        int stepX = Math.Max(1, w / 64), stepY = Math.Max(1, h / 36);
        long sum = 0;
        int samples = 0, dark = 0;
        uint sig = 2166136261;
        for (int y = 0; y < h; y += stepY)
        {
            for (int x = 0; x < w; x += stepX)
            {
                int o = y * stride + x * 4;
                int lum = (pixels[o] * 11 + pixels[o + 1] * 59 + pixels[o + 2] * 30) / 100;
                sum += lum;
                if (lum < 8) dark++;
                sig = (sig ^ (uint)lum) * 16777619;
                samples++;
            }
        }
        Emit($"frame-trace id={state.Id} seq={frame.Sequence} {w}x{h} dest={dest.Width}x{dest.Height} " +
             $"mean={(samples > 0 ? sum / samples : 0)} dark%={(samples > 0 ? dark * 100 / samples : 0)} " +
             $"sig={sig:x8} reused={reused}");
    }

    /// <summary>
    /// True for frames that are effectively one solid black colour (a cleared
    /// or torn-down surface), which is never real game content.
    /// </summary>
    private static bool IsUniformlyBlack(ReadOnlySpan<byte> pixels, int width, int height,
        int stride)
    {
        if (width <= 0 || height <= 0 || stride < width * 4 ||
            pixels.Length < (long)stride * height)
            return false;
        int stepX = Math.Max(1, width / 64), stepY = Math.Max(1, height / 36);
        long sum = 0;
        int samples = 0, dark = 0;
        for (int y = 0; y < height; y += stepY)
        {
            for (int x = 0; x < width; x += stepX)
            {
                int o = y * stride + x * 4;
                int lum = (pixels[o] * 11 + pixels[o + 1] * 59 + pixels[o + 2] * 30) / 100;
                sum += lum;
                samples++;
                if (lum < 8) dark++;
            }
        }
        return samples > 0 && dark * 100 >= samples * 95 && sum <= samples * 3;
    }

    /// <summary>
    /// warn when a visible window produces no frames for a while. With
    /// ext-image-copy-capture a static scene legitimately produces no frames,
    /// but a long silence is also what a stuck game looks like, so record it
    /// with the state needed to tell the two apart.
    /// </summary>
    private void CheckCaptureStall(WindowState state)
    {
        long now = Environment.TickCount64;
        if (state.LastFrameAt == 0) { state.LastFrameAt = now; return; }
        if (now - state.LastFrameAt < 5000 || now - state.LastStallLog < 15000) return;
        state.LastStallLog = now;
        Emit($"nested no-frame watchdog '{DescribeWindow(state.Info)}' " +
             $"{(now - state.LastFrameAt) / 1000}s host={state.Host?.Width}x{state.Host?.Height} " +
             $"focused={state.Id == _focusedId} minimized={state.Minimized} " +
             $"fullscreen={state.Info.FullscreenMode != 0} upscale={state.Upscale}");
    }

    private void PrepareInput(WindowState state)
    {
        if (_input is null || state.Host is null) return;
        var dest = LayoutCalculator.ComputeDestRect(state.SourceWidth, state.SourceHeight,
            state.Host.Width, state.Host.Height, _mode);
        PrepareInput(state, dest);
    }

    private void PrepareInput(WindowState state, RectI dest)
    {
        if (_input is null || state.Capture is null) return;
        if (_toplevelMode)
        {
            _input.SetMapping(dest, state.Crop, state.Capture.Width, state.Capture.Height,
                0, 0, _outputWidth, _outputHeight);
            return;
        }
        if (_pool is null) return;
        var layout = _pool.LayoutBounds;
        _input.SetMapping(dest, state.Crop, state.Capture?.Width ?? _outputWidth,
            state.Capture?.Height ?? _outputHeight,
            state.OutputOrigin.X, state.OutputOrigin.Y,
            layout.Width, layout.Height);
    }

    private void UpdateDest(WindowState state)
    {
        if (state.Host is null || _input is null) return;
        var dest = LayoutCalculator.ComputeDestRect(state.SourceWidth, state.SourceHeight,
            state.Host.Width, state.Host.Height, _mode);
        PrepareInput(state, dest);
    }

    private void ReplaceBlitter(WindowState state, EffectDefinition effect)
    {
        if (state.Host is null) return;
        ReplaceBlitter(state, state.Host.CreateBlitter(effect,
            state.Capture?.SourceIsRgb ?? true));
    }

    private void ReplaceBlitter(WindowState state, VulkanBlitter blitter)
    {
        try { state.Blitter?.Dispose(); }
        catch (Exception ex) { Log.Warn($"nested blitter dispose: {ex.Message}"); }
        state.Blitter = blitter;
    }

    private void PumpHost(WindowState state, int count)
    {
        for (int i = 0; i < count; i++)
        {
            Thread.Sleep(15);
            state.Host?.Pump();
        }
    }

    private void CloseState(WindowState state, bool releaseOutput)
    {
        lock (_stateGate) _states.Remove(state.Id);
        if (_focusedId == state.Id) _focusedId = 0;
        _retryAfter.Remove(state.Id);
        string output = state.OutputName;
        DisposeStateResources(state);

        if (releaseOutput && _pool is not null)
        {
            var used = _states.Values
                .Where(s => !string.IsNullOrWhiteSpace(s.OutputName))
                .Select(s => s.OutputName)
                .ToHashSet(StringComparer.Ordinal);
            try { _pool.Release(output, used); }
            catch (Exception ex) { Emit($"nested output release '{output}' failed: {ex.Message}"); }
        }
        Emit($"nested window removed; windows={_states.Count}");
    }

    private static void DisposeStateResources(WindowState state)
    {
        try { state.Blitter?.Dispose(); }
        catch (Exception ex) { Log.Warn($"nested blitter dispose: {ex.Message}"); }
        try { state.Host?.Dispose(); }
        catch (Exception ex) { Log.Warn($"nested host dispose: {ex.Message}"); }
        try { state.Capture?.Dispose(); }
        catch (Exception ex) { Log.Warn($"nested capture dispose: {ex.Message}"); }
        state.Blitter = null;
        state.Host = null;
        state.Capture = null;
        state.Upscale = false;
    }

    private void Cleanup()
    {
        List<WindowState> states;
        lock (_stateGate)
        {
            states = _states.Values.ToList();
            _states.Clear();
        }
        foreach (var state in states) DisposeStateResources(state);

        _stop = true;
        if (_cursorSyncThread is { IsAlive: true } && _cursorSyncThread != Thread.CurrentThread)
            _cursorSyncThread.Join(TimeSpan.FromMilliseconds(200));
        _cursorSyncThread = null;

        try { _input?.Dispose(); } catch (Exception ex) { Emit($"dispose input: {ex.Message}"); }
        try { _probe?.Dispose(); } catch (Exception ex) { Emit($"dispose minimize probe: {ex.Message}"); }
        try { _tracker?.Dispose(); } catch (Exception ex) { Emit($"dispose tracker: {ex.Message}"); }
        try { _ipc?.Dispose(); } catch (Exception ex) { Emit($"dispose sway ipc: {ex.Message}"); }
        _input = null;
        _probe = null;
        _tracker = null;
        _pool = null;
        _ipc = null;
    }

    public void Stop()
    {
        if (_disposed) return;
        _stop = true;
        if (_thread.IsAlive && _thread != Thread.CurrentThread)
            _thread.Join(TimeSpan.FromSeconds(5));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _started.Dispose();
    }

    private static RectI ComputeCrop(RectI rect, RectI output, int frameWidth, int frameHeight)
    {
        if (frameWidth <= 0 || frameHeight <= 0) return new RectI(0, 0, 1, 1);
        int x = Math.Clamp(rect.X - output.X, 0, frameWidth - 1);
        int y = Math.Clamp(rect.Y - output.Y, 0, frameHeight - 1);
        int w = Math.Clamp(rect.Width, 1, frameWidth - x);
        int h = Math.Clamp(rect.Height, 1, frameHeight - y);
        return new RectI(x, y, w, h);
    }

    private static ReadOnlySpan<byte> CropFrame(WindowState state, ReadOnlySpan<byte> source,
        NestedFrame frame, RectI crop, out int stride)
    {
        if (crop.X == 0 && crop.Y == 0 && crop.Width == frame.Width && crop.Height == frame.Height)
        {
            stride = frame.Stride;
            return source;
        }

        stride = crop.Width * 4;
        int required = stride * crop.Height;
        if (state.CropBuffer.Length < required) state.CropBuffer = new byte[required];
        for (int row = 0; row < crop.Height; row++)
        {
            int sourceOffset = (crop.Y + row) * frame.Stride + crop.X * 4;
            source.Slice(sourceOffset, stride).CopyTo(
                state.CropBuffer.AsSpan(row * stride, stride));
        }
        return state.CropBuffer.AsSpan(0, required);
    }

    private static bool IsOutOfDate(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current.Message.Contains("OutOfDate", StringComparison.OrdinalIgnoreCase) ||
                current.Message.Contains("out of date", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string WindowTitle(NestedWindowInfo info)
    {
        string name = string.IsNullOrWhiteSpace(info.Name) ? "nested game" : info.Name;
        return $"GemPrism - {name}";
    }

    private static string DescribeWindow(NestedWindowInfo info)
    {
        string name = string.IsNullOrWhiteSpace(info.Name) ? "(untitled)" : info.Name;
        return $"id={info.Id} '{name}' pid={info.Pid} shell={info.Shell} rect={info.Rect}";
    }

    private string DescribeModes()
    {
        if (_states.Count == 0) return "-";
        return string.Join(",", _states.Values.Select(s =>
            $"{s.Id}:{(s.Upscale ? "up" : "orig")}{(s.Ignored ? "/ignored" : "")}"));
    }

    private void SetExitReason(string reason)
    {
        Volatile.Write(ref _exitReason, reason);
        Emit($"stopping: {reason}");
    }

    private void Emit(string message)
    {
        Log.Write(message);
        try { Message?.Invoke(message); } catch { }
    }

    private sealed class WindowState
    {
        public WindowState(long id) => Id = id;

        public long Id { get; set; }
        public NestedWindowInfo Info { get; set; } = null!;
        public bool Minimized { get; set; }
        public bool IsDialog { get; set; }
        public bool SawFirstFrame { get; set; }
        public long LastFrameAt { get; set; }
        public long LastStallLog { get; set; }
        public string OutputName { get; set; } = string.Empty;
        public RectI OutputOrigin { get; set; }
        public INestedFrameSource? Capture { get; set; }
        public RectI Crop { get; set; }
        public int SourceWidth { get; set; }
        public int SourceHeight { get; set; }
        public int WindowedWidth { get; set; }
        public int WindowedHeight { get; set; }
        public IHostWindow? Host { get; set; }
        public VulkanBlitter? Blitter { get; set; }
        public bool Upscale { get; set; }
        public bool Switching { get; set; }
        /// <summary>
        /// The user closed this host window and the close request was forwarded
        /// to the game window; remove the mirror as soon as that view is gone
        /// instead of waiting for the missing-window grace period.
        /// </summary>
        public bool CloseForwarded { get; set; }
        /// <summary>When to retry a capture that has no client buffer yet.</summary>
        public long CaptureRetryAt { get; set; }
        /// <summary>Host window that owns this dialog, for re-parenting on rebuild.</summary>
        public long? ParentId { get; set; }
        /// <summary>Size a Wayland host is waiting to be rebuilt at.</summary>
        public (int Width, int Height)? ResizeTarget { get; set; }
        public long ResizeTargetAt { get; set; }
        public bool ForceRender { get; set; }
        public bool CloseRequested { get; set; }
        public bool PendingOriginal { get; set; }
        public bool Ignored { get; set; }
        /// <summary>
        /// While non-zero and in the future, uniformly black frames are not
        /// presented. Deiconifying a window re-creates its toplevel, and the
        /// capture of the dying surface delivers one black frame before the
        /// client repaints; presenting it would flash the host black right
        /// after it comes back from the taskbar.
        /// </summary>
        public long SuppressBlackUntil { get; set; }
        public long MissingSince { get; set; }
        public bool LastGameFullscreen { get; set; }
        public int LastHostWidth { get; set; }
        public int LastHostHeight { get; set; }
        public string? LastTitle { get; set; }
        public FrameDiff Diff { get; } = new();
        public byte[] CropBuffer { get; set; } = Array.Empty<byte>();
    }
}