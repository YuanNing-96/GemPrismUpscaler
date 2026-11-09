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

    private SwayIpc? _ipc;
    private SwayOutputPool? _pool;
    private NestedWindowTracker? _tracker;
    private NestedInput? _input;
    private NestedWindowProbe? _probe;
    private bool _probeUnavailable;
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
        _tracker = new NestedWindowTracker(
            _ipc, SwayConfig.DefaultOutput, _session.Token, _session.CommandPid,
            _preferredMain, ownsIpc: false);
        _pool = new SwayOutputPool(_ipc, SwayConfig.DefaultOutput, _outputWidth, _outputHeight);
        _pool.Initialize();
        _input = new NestedInput(_session.Display, _session.WaylandDisplay);
        EnsureMinimizeProbe();

        Log.Info($"nested multi-window: monitor='{_monitorName ?? "auto"}' " +
                 $"output={_outputWidth}x{_outputHeight} base='{_pool.BaseOutput}' " +
                 $"mode={(_mode == ScalingMode.Fit ? "fit" : "stretch")} effect='{_effect.Id}'");
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
                         $"outputs={_pool!.Outputs.Count}");
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
        if (_pool is null || _tracker is null) return;

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
        _pool.Refresh();

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
                        nowTick - s.MissingSince >= MissingWindowGraceMs)
            .ToList();
        foreach (var state in removals)
            CloseState(state, releaseOutput: true);

        var used = _states.Values
            .Where(s => !string.IsNullOrWhiteSpace(s.OutputName))
            .Select(s => s.OutputName)
            .ToHashSet(StringComparer.Ordinal);
        try
        {
            if (used.Count > 0) _pool.EnsureSpare(used);
            else _pool.TrimToBase();
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

        UpdateStateGeometry(state);
        state.ForceRender = true;
    }

    private bool AddWindow(NestedWindowInfo window)
    {
        if (_pool is null || _tracker is null) return false;

        // A new toplevel must never appear behind the current fullscreen
        // upscale host. Return every existing host to original mode first.
        ExitAllUpscale();

        var used = _states.Values
            .Where(s => !string.IsNullOrWhiteSpace(s.OutputName))
            .Select(s => s.OutputName)
            .ToHashSet(StringComparer.Ordinal);

        SwayOutputInfo output;
        try
        {
            output = _pool.Acquire(used);
        }
        catch (Exception ex)
        {
            Emit($"nested output acquire failed for '{DescribeWindow(window)}': {ex.Message}");
            return false;
        }

        bool committed = false;
        var state = new WindowState(window.Id) { OutputName = output.Name, OutputOrigin = output.Rect };
        try
        {
            _tracker.FloatWindow(window.Id);
            if (!_tracker.MoveToOutput(window.Id, output.Name))
                throw new InvalidOperationException($"move to output '{output.Name}' failed");

            // The move changes the global origin of the window. Re-read the
            // tree instead of cropping with the coordinates from the old
            // output; otherwise the first frame is displaced or black.
            Thread.Sleep(40);
            _tracker.Poll(allowFullscreen: false);
            if (_tracker.AllWindows.FirstOrDefault(w => w.Id == window.Id) is { } moved)
                window = moved;

            _pool.Refresh();
            var outputInfo = _pool.Find(output.Name) ?? output;
            state.OutputOrigin = outputInfo.Rect;

            var capture = WaitForCapture(output.Name);
            if (capture is null)
                throw new InvalidOperationException($"capture for output '{output.Name}' unavailable");
            state.Capture = capture;

            var crop = ComputeCrop(window.Rect, outputInfo.Rect, capture.Width, capture.Height);
            state.Crop = crop;
            state.SourceWidth = crop.Width;
            state.SourceHeight = crop.Height;
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
                state.WindowedWidth = InitialWindowedSize(window.RequestedWidth,
                    crop.Width, outputInfo.Rect.Width);
                state.WindowedHeight = InitialWindowedSize(window.RequestedHeight,
                    crop.Height, outputInfo.Rect.Height);
            }
            state.LastGameFullscreen = window.FullscreenMode != 0;

            string title = WindowTitle(window);
            bool dialog = window.LooksLikeDialog || window.TransientFor is not null;
            bool focusNew = dialog || _states.Count == 0;
            ulong parentWindow = dialog ? ParentHostWindow(window.TransientFor) : 0;
            state.Info = window;
            state.Host = new NestedHostWindow(_monitorName, title,
                state.SourceWidth, state.SourceHeight,
                startFullscreen: state.LastGameFullscreen,
                transientFor: parentWindow, dialog: dialog);
            state.LastHostWidth = state.Host.Width;
            state.LastHostHeight = state.Host.Height;
            state.LastTitle = title;

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

            state.Blitter = new VulkanBlitter(state.Host.X11, state.Host.Window,
                state.Host.Width, state.Host.Height, OriginalEffect,
                sourceRgb: capture.SourceIsRgb);
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
                 $"spare={(_pool.Outputs.Count - _states.Count)}");

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
                _pool.Release(output.Name, used);
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
        if (state.Host is null || state.Capture is null || _pool is null) return;

        var output = _pool.Find(state.OutputName);
        if (output is not null) state.OutputOrigin = output.Rect;

        string title = WindowTitle(state.Info);
        if (title != state.LastTitle)
        {
            state.LastTitle = title;
            state.Host.SetTitle(title);
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

    private static void ApplyHostRestoreSize(WindowState state)
    {
        if (state.Host is null) return;
        int width = state.WindowedWidth > 0 ? state.WindowedWidth : state.SourceWidth;
        int height = state.WindowedHeight > 0 ? state.WindowedHeight : state.SourceHeight;
        if (width > 0 && height > 0)
            state.Host.SetOriginalSize(width, height);
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
                ApplyHostRestoreSize(state);
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
                    ApplyHostRestoreSize(state);
                }
            }

            PumpHost(state, 10);
            state.Upscale = upscale;
            ReplaceBlitter(state, upscale ? _effect : OriginalEffect);
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

    private ulong ParentHostWindow(int? transientFor)
    {
        // sway stores the X11 parent window id in window_properties.transient_for,
        // not the sway con_id. Match it against the host's X11 window id.
        if (transientFor is int xid)
        {
            uint parentXid = (uint)xid;
            var match = _states.Values.FirstOrDefault(s =>
                s.Host is not null && (uint)s.Host.Window == parentXid);
            if (match?.Host is not null) return match.Host.Window;
        }

        if (_states.TryGetValue(_focusedId, out var focused) && focused.Host is not null)
            return focused.Host.Window;
        return _states.Values.FirstOrDefault(s => s.Host is not null)?.Host?.Window ?? 0;
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
    /// Mirror a game-side minimize onto the KDE host. wlroots marks the X11
    /// window iconic but keeps its surface on the output, so without this the
    /// host would keep showing the last (black) frame the client rendered
    /// before it stopped painting.
    /// </summary>
    private void SyncMinimizedStates()
    {
        if (_probe is null) return;
        long now = Environment.TickCount64;
        if (now - _lastMinimizePoll < MinimizePollMs) return;
        _lastMinimizePoll = now;

        foreach (var state in _states.Values.ToList())
        {
            if (state.Ignored || state.Info.X11Window == 0) continue;
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
            if (iconic == state.Minimized) continue;

            if (iconic)
            {
                state.Minimized = true;
                state.Host?.Iconify();
                Emit($"nested window '{DescribeWindow(state.Info)}' minimized; " +
                     "mirroring minimize on host");
            }
            else
            {
                state.Minimized = false;
                state.Host?.RestoreFromIconify();
                state.ForceRender = true;
                Emit($"nested window '{DescribeWindow(state.Info)}' restored; resuming host");
            }
        }
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
        state.ForceRender = true;
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
            long now = Environment.TickCount64;
            if (now - Interlocked.Read(ref _lastToggleAt) >= 300)
            {
                Interlocked.Exchange(ref _lastToggleAt, now);
                Interlocked.Exchange(ref _toggleRequestedId, state.Id);
                Interlocked.Exchange(ref _toggleRequested, 1);
            }
            return;
        }

        PrepareInput(state);
        _input.SetTarget(string.Equals(state.Info.Shell, "xwayland", StringComparison.Ordinal));
        _input.HandleKey(e.KeyCode, e.State == OverlayKeyState.Pressed);
    }

    private bool IsBlockedByModal(WindowState state)
    {
        if (state.Host is null) return false;
        ulong parentXid = state.Host.Window;
        foreach (var other in _states.Values)
        {
            if (other.Id == state.Id || other.Ignored || other.Host is null) continue;
            if (other.Info.TransientFor is int transient &&
                (uint)transient == (uint)parentXid)
            {
                return true;
            }
        }
        return false;
    }

    private void ProcessPendingRequests()
    {
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

    private void HandleHostClose(WindowState state)
    {
        if (state.Ignored) return;
        if (_waitForCommand)
        {
            SetExitReason("host window closed");
            _stop = true;
            return;
        }

        // Session mode: close only this KDE window. Keep the game window and
        // its output alive; if the game closes later the output is released by
        // the normal reconcile path.
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

        if (state.Host.Width != state.LastHostWidth || state.Host.Height != state.LastHostHeight)
        {
            Emit($"nested host resized {state.LastHostWidth}x{state.LastHostHeight} -> " +
                 $"{state.Host.Width}x{state.Host.Height}");
            state.LastHostWidth = state.Host.Width;
            state.LastHostHeight = state.Host.Height;
            ReplaceBlitter(state, state.Upscale ? _effect : OriginalEffect);
            state.Diff.Reset();
            state.ForceRender = true;
        }

        if (!state.Capture.TryCapture(out NestedFrame frame))
            return false;

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

        var pixels = CropFrame(state, state.Capture.Pixels, frame, state.Crop, out int cropStride);
        var dest = LayoutCalculator.ComputeDestRect(state.SourceWidth, state.SourceHeight,
            state.Host.Width, state.Host.Height, _mode);
        PrepareInput(state, dest);

        var dirty = state.Upscale
            ? state.Diff.Update(pixels, state.SourceWidth, state.SourceHeight, cropStride,
                dest, state.Blitter.DirtyHalo + 2)
            : DirtyFrame.Full;
        if (!state.ForceRender && dirty.IsStatic) return false;

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
                 $"{ex.GetType().Name}: {ex.Message}");
            state.Ignored = true;
            DisposeStateResources(state);
            return false;
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

    private void PrepareInput(WindowState state)
    {
        if (_input is null || state.Host is null) return;
        var dest = LayoutCalculator.ComputeDestRect(state.SourceWidth, state.SourceHeight,
            state.Host.Width, state.Host.Height, _mode);
        PrepareInput(state, dest);
    }

    private void PrepareInput(WindowState state, RectI dest)
    {
        if (_input is null || _pool is null) return;
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
        try { state.Blitter?.Dispose(); }
        catch (Exception ex) { Log.Warn($"nested blitter dispose: {ex.Message}"); }
        state.Blitter = new VulkanBlitter(state.Host.X11, state.Host.Window,
            state.Host.Width, state.Host.Height, effect,
            sourceRgb: state.Capture?.SourceIsRgb ?? true);
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
        public string OutputName { get; set; } = string.Empty;
        public RectI OutputOrigin { get; set; }
        public NestedCapture? Capture { get; set; }
        public RectI Crop { get; set; }
        public int SourceWidth { get; set; }
        public int SourceHeight { get; set; }
        public int WindowedWidth { get; set; }
        public int WindowedHeight { get; set; }
        public NestedHostWindow? Host { get; set; }
        public VulkanBlitter? Blitter { get; set; }
        public bool Upscale { get; set; }
        public bool Switching { get; set; }
        public bool ForceRender { get; set; }
        public bool CloseRequested { get; set; }
        public bool PendingOriginal { get; set; }
        public bool Ignored { get; set; }
        public long MissingSince { get; set; }
        public bool LastGameFullscreen { get; set; }
        public int LastHostWidth { get; set; }
        public int LastHostHeight { get; set; }
        public string? LastTitle { get; set; }
        public FrameDiff Diff { get; } = new();
        public byte[] CropBuffer { get; set; } = Array.Empty<byte>();
    }
}