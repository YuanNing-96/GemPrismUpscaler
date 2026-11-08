using System.Diagnostics;
using GemPrismUpscaler.Core.Configuration;
using GemPrismUpscaler.Core.Diagnostics;
using GemPrismUpscaler.Core.Geometry;
using GemPrismUpscaler.Core.Platform.Linux.PipeWire;
using GemPrismUpscaler.Core.Platform.Linux.Portal;
using GemPrismUpscaler.Core.Platform.Linux.Wayland;
using GemPrismUpscaler.Core.Rendering;

namespace GemPrismUpscaler.Core.Engine;

/// <summary>
/// M2a native Wayland session: portal picks a window/monitor, PipeWire delivers
/// its frames, and the same layer-shell/Vulkan presentation path upscales them.
/// The overlay input region is empty; M2a only supports fullscreen/passthrough
/// input for native sources (windowed native input is M2b's nested compositor).
/// </summary>
public sealed class PortalUpscaleSession : IDisposable
{
    private readonly ScalingMode _mode;
    private readonly EffectDefinition _effect;
    private EffectDefinition _renderEffect;
    private readonly string? _monitorName;
    private readonly PortalSourceType _sourceType;
    private readonly bool _reuseToken;
    private readonly HotkeyBinding _hotkeyBinding;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _started = new(false);
    private WaylandOverlay? _overlay;
    private VulkanBlitter? _blitter;
    private PortalScreenCastClient? _portal;
    private PortalScreenCastSession? _session;
    private PortalGlobalShortcuts? _shortcuts;
    private Task? _shortcutTask;
    private PipeWireGstCapture? _capture;
    private byte[] _frame = Array.Empty<byte>();
    private bool _hasFrame;
    private bool _forceRender;
    private int _toggleRequested;
    private long _lastSequence = -1;
    private volatile bool _stop;
    private bool _disposed;
    private Exception? _initError;
    private string _exitReason = string.Empty;

    public RectI DestRect { get; private set; }
    public bool IsRunning => _thread.IsAlive;
    public bool IsOverlayVisible => _overlay?.IsVisible == true;
    public string ExitReason => Volatile.Read(ref _exitReason);
    public string EffectName => _renderEffect.Id;
    public string OverlayDescription => _overlay is null
        ? "-"
        : $"wayland:{_overlay.OutputName}";

    public event Action<string>? Message;

    public PortalUpscaleSession(ScalingMode mode, EffectDefinition effect,
        string? monitorName, PortalSourceType sourceType = PortalSourceType.Window,
        bool reuseToken = false, HotkeyBinding? hotkey = null)
    {
        _mode = mode;
        _effect = effect;
        _renderEffect = effect;
        _monitorName = monitorName;
        _sourceType = sourceType;
        _reuseToken = reuseToken;
        _hotkeyBinding = hotkey ?? HotkeyBinding.Default;
        _thread = new Thread(Run) { IsBackground = true, Name = "GemPrism-Portal" };
    }

    /// <summary>
    /// True when the portal stream already covers at least one output axis.
    /// KDE composites window streams at the output scale, so a 720p fullscreen
    /// game on a 4K output arrives as 4K; a 2x compute chain would only create
    /// an 8K intermediate without adding real detail.
    /// </summary>
    public static bool SourceAlreadyAtOutputSize(int sourceWidth, int sourceHeight,
        int outputWidth, int outputHeight)
        => sourceWidth > 0 && sourceHeight > 0 && outputWidth > 0 && outputHeight > 0 &&
           (sourceWidth >= outputWidth || sourceHeight >= outputHeight);

    public void Start()
    {
        _thread.Start();
        if (!_started.Wait(TimeSpan.FromSeconds(130)))
            throw new TimeoutException("portal session initialization timed out");
        if (_initError is not null)
            throw new InvalidOperationException($"portal session initialization failed: {_initError.Message}", _initError);
    }

    public void Stop()
    {
        _stop = true;
        if (_thread.IsAlive && _thread != Thread.CurrentThread)
        {
            if (!_thread.Join(TimeSpan.FromSeconds(3)))
                Emit("portal session stop timed out");
        }
        if (string.IsNullOrWhiteSpace(ExitReason)) SetExitReason("stopped");
    }

    /// <summary>Thread-safe request used by the tray menu and the portal shortcut.</summary>
    public void RequestToggleVisibility() => Interlocked.Exchange(ref _toggleRequested, 1);

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
            SetExitReason($"portal error: {ex.GetType().Name}: {ex.Message}");
            ErrorBus.Functional("portal", "Wayland 门户会话启动失败", ex);
            _started.Set();
        }
        finally
        {
            Cleanup();
        }
    }

    private void Initialize()
    {
        _portal = new PortalScreenCastClient();
        // Reusing a restore token makes KDE skip the picker and silently
        // capture the previous window. The default is to always let the user
        // choose; opt in explicitly when a fixed capture target is wanted.
        string? restoreToken = _reuseToken ? PortalTokenStore.Load() : null;
        Emit($"portal picker mode: {(_reuseToken && restoreToken is not null ? "reuse last window" : "choose every start")}");
        _session = _portal.StartAsync(_sourceType, restoreToken).GetAwaiter().GetResult();
        if (_session.Streams.Count == 0)
            throw new InvalidOperationException("portal returned no streams");
        var stream = _session.Streams[0];
        if (!string.IsNullOrWhiteSpace(_session.RestoreToken))
            PortalTokenStore.Save(_session.RestoreToken);
        if (stream.PipeWireSerial == 0)
            Log.Warn("portal stream has no pipewire-serial; target-object may fail");

        _overlay = WaylandOverlay.TryCreate(_monitorName, forwardPointer: false)
            ?? throw new InvalidOperationException("Wayland layer-shell overlay unavailable");
        _overlay.SetInputRegion(null); // passthrough

        // KDE window streams often omit the portal "size" property. GStreamer
        // reports the negotiated caps, so wait for those before creating the
        // Vulkan pipeline and content rect.
        _capture = new PipeWireGstCapture(_session.PipeWireFd, stream.PipeWireSerial);
        _capture.Start();
        if (!_capture.WaitForSize(60000))
            throw new InvalidOperationException($"pipewire caps timeout: {_capture.Error}");
        int width = _capture.Width;
        int height = _capture.Height;

        // KDE's portal window stream is already composited at the output
        // scale. For a 720p fullscreen game on a 4K output the stream is 4K,
        // so a 2x compute effect would allocate an 8K intermediate for zero
        // real upscaling. Disable the 2x chain and explain why.
        if (_effect.IsCompute && SourceAlreadyAtOutputSize(width, height,
                _overlay.Width, _overlay.Height))
        {
            _renderEffect = EffectRegistry.BuiltIn(EffectMode.Bilinear);
            Emit($"portal source {width}x{height} is already at output size " +
                 $"{_overlay.Width}x{_overlay.Height}; disabling 2x effect " +
                 $"(KWin composited the source; use XWayland window capture or M2b nested " +
                 $"for a true low-resolution buffer)");
        }

        _blitter = new VulkanBlitter(_overlay.VulkanDisplay, _overlay.VulkanSurface,
            _overlay.Width, _overlay.Height, _renderEffect);
        DestRect = LayoutCalculator.ComputeDestRect(width, height,
            _overlay.Width, _overlay.Height, _mode);
        _frame = new byte[_capture.Stride * height];

        StartGlobalShortcut();

        Emit($"portal session started effect={EffectName} stream={width}x{height} " +
             $"portalSize={stream.Width}x{stream.Height} source={_sourceType} " +
             $"monitor={_overlay.OutputName} dest={DestRect} " +
             $"extent={_blitter.SwapchainExtent.Width}x{_blitter.SwapchainExtent.Height}");
    }

    private void StartGlobalShortcut()
    {
        try
        {
            Emit("portal global shortcut: creating client");
            _shortcuts = new PortalGlobalShortcuts();
            Emit("portal global shortcut: client created");
            _shortcuts.Activated += id =>
            {
                if (string.Equals(id, "toggle-overlay", StringComparison.Ordinal))
                    RequestToggleVisibility();
            };
            string description = "显示/隐藏宝石棱镜超分覆盖层";
            string trigger = _hotkeyBinding.ToPortalTrigger();
            _shortcutTask = Task.Run(() => _shortcuts.BindAsync("toggle-overlay", description, trigger));
            Emit($"portal global shortcut requested trigger='{trigger}' " +
                 "(KDE may ask for approval; XWayland focus uses XGrabKey)");
        }
        catch (Exception ex)
        {
            Log.Warn($"portal global shortcut unavailable: {ex.Message}");
            _shortcuts = null;
        }
    }

    private void ToggleOverlay()
    {
        if (_overlay is null) return;
        if (_overlay.IsVisible)
        {
            _overlay.Hide();
            _overlay.SetInputRegion(null);
            Emit("portal overlay hidden (user)");
        }
        else
        {
            _overlay.Show();
            _overlay.SetInputRegion(null);
            _forceRender = true;
            Emit("portal overlay shown (user)");
        }
    }

    private void Loop()
    {
        var stats = new FrameStats();
        var lastFrameAt = Environment.TickCount64;
        while (!_stop)
        {
            _overlay?.Pump(0);
            if (_capture is null || _blitter is null || _overlay is null) break;
            if (_capture.Faulted)
            {
                SetExitReason($"pipewire capture failed: {_capture.Error}");
                break;
            }

            if (Interlocked.Exchange(ref _toggleRequested, 0) != 0)
                ToggleOverlay();

            long sequence = _capture.Sequence;
            bool newFrame = sequence != _lastSequence;
            if (newFrame)
            {
                _lastSequence = sequence;
                if (_capture.TryCopyFrame(_frame, out _))
                {
                    _hasFrame = true;
                    lastFrameAt = Environment.TickCount64;
                }
            }

            if (_overlay.IsVisible && _hasFrame && (newFrame || _forceRender))
            {
                _forceRender = false;
                var captureClock = Stopwatch.StartNew();
                try
                {
                    _blitter.Render(_frame, _capture.Width, _capture.Height,
                        _capture.Stride, DestRect, null);
                    stats.OnCaptured();
                    stats.OnPresented();
                    stats.OnFrame(new FrameTimings(
                        captureClock.Elapsed.TotalMilliseconds, 0,
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
                    SetExitReason($"portal present error: {ex.GetType().Name}: {ex.Message}");
                    break;
                }
            }

            var report = stats.Tick();
            if (report is not null)
            {
                Emit($"stats {report.ToLogString()} hidden={!_overlay.IsVisible} popup=False " +
                     $"userHidden={!_overlay.IsVisible}");
                StatsCsv.TryWrite(report);
            }

            // Avoid a busy loop when the source is static (PipeWire may stop
            // sending buffers) while still noticing a lost stream promptly.
            if (Environment.TickCount64 - lastFrameAt > 1000 && _capture.Faulted)
                break;
            Thread.Sleep(2);
        }
        Emit("portal session loop exited");
    }

    private void Cleanup()
    {
        try { _shortcutTask?.Wait(TimeSpan.FromMilliseconds(500)); } catch { }
        try { _shortcuts?.Dispose(); } catch (Exception ex) { Emit($"dispose global shortcut: {ex.Message}"); }
        _shortcuts = null;
        try { _capture?.Dispose(); } catch (Exception ex) { Emit($"dispose pipewire: {ex.Message}"); }
        try { _blitter?.Dispose(); } catch (Exception ex) { Emit($"dispose blitter: {ex.Message}"); }
        try { _overlay?.Dispose(); } catch (Exception ex) { Emit($"dispose overlay: {ex.Message}"); }
        try { _session?.Dispose(); } catch (Exception ex) { Emit($"dispose portal session: {ex.Message}"); }
        try { _portal?.Dispose(); } catch (Exception ex) { Emit($"dispose portal: {ex.Message}"); }
        _capture = null; _blitter = null; _overlay = null; _session = null; _portal = null;
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _started.Dispose();
    }
}
