using System.Diagnostics;
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
    private readonly string? _monitorName;
    private readonly PortalSourceType _sourceType;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _started = new(false);
    private WaylandOverlay? _overlay;
    private VulkanBlitter? _blitter;
    private PortalScreenCastClient? _portal;
    private PortalScreenCastSession? _session;
    private PipeWireGstCapture? _capture;
    private byte[] _frame = Array.Empty<byte>();
    private long _lastSequence = -1;
    private volatile bool _stop;
    private bool _disposed;
    private Exception? _initError;
    private string _exitReason = string.Empty;

    public RectI DestRect { get; private set; }
    public bool IsRunning => _thread.IsAlive;
    public string ExitReason => Volatile.Read(ref _exitReason);
    public string EffectName => _effect.Id;
    public string OverlayDescription => _overlay is null
        ? "-"
        : $"wayland:{_overlay.OutputName}";

    public event Action<string>? Message;

    public PortalUpscaleSession(ScalingMode mode, EffectDefinition effect,
        string? monitorName, PortalSourceType sourceType = PortalSourceType.Window)
    {
        _mode = mode;
        _effect = effect;
        _monitorName = monitorName;
        _sourceType = sourceType;
        _thread = new Thread(Run) { IsBackground = true, Name = "GemPrism-Portal" };
    }

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
        string? restoreToken = PortalTokenStore.Load();
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

        _blitter = new VulkanBlitter(_overlay.VulkanDisplay, _overlay.VulkanSurface,
            _overlay.Width, _overlay.Height, _effect);
        DestRect = LayoutCalculator.ComputeDestRect(width, height,
            _overlay.Width, _overlay.Height, _mode);
        _frame = new byte[_capture.Stride * height];

        Emit($"portal session started effect={EffectName} stream={width}x{height} " +
             $"portalSize={stream.Width}x{stream.Height} source={_sourceType} " +
             $"monitor={_overlay.OutputName} dest={DestRect} " +
             $"extent={_blitter.SwapchainExtent.Width}x{_blitter.SwapchainExtent.Height}");
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

            long sequence = _capture.Sequence;
            if (sequence != _lastSequence)
            {
                _lastSequence = sequence;
                var captureClock = Stopwatch.StartNew();
                if (_capture.TryCopyFrame(_frame, out _))
                {
                    double captureMs = captureClock.Elapsed.TotalMilliseconds;
                    stats.OnCaptured();
                    try
                    {
                        _blitter.Render(_frame, _capture.Width, _capture.Height,
                            _capture.Stride, DestRect, null);
                        stats.OnPresented();
                        stats.OnFrame(new FrameTimings(
                            captureMs, 0,
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
                    lastFrameAt = Environment.TickCount64;
                }
            }

            var report = stats.Tick();
            if (report is not null)
            {
                Emit($"stats {report.ToLogString()} hidden=False popup=False userHidden=False");
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
