using System.Diagnostics;
using System.Runtime.InteropServices;
using GemPrismUpscaler.Core.Diagnostics;
using NWayland;
using NWayland.Protocols.ExtForeignToplevelListV1;
using NWayland.Protocols.ExtImageCaptureSourceV1;
using NWayland.Protocols.ExtImageCopyCaptureV1;
using NWayland.Protocols.Wayland;

namespace GemPrismUpscaler.Core.Platform.Linux.Nested;

/// <summary>
/// Per-toplevel capture through <c>ext-image-copy-capture-v1</c> with a
/// foreign-toplevel capture source. Unlike wlr-screencopy of a whole output,
/// this returns the window's own buffer, so occluding windows cannot affect
/// the captured image, and no crop/origin bookkeeping is needed.
/// </summary>
public sealed class ToplevelCapture : INestedFrameSource
{
    private const int PROT_READ = 1;
    private const int PROT_WRITE = 2;
    private const int MAP_SHARED = 1;
    private const short POLLIN = 1;

    [DllImport("libc", SetLastError = true)]
    private static extern int memfd_create(string name, uint flags);
    [DllImport("libc", SetLastError = true)]
    private static extern int ftruncate(int fd, long length);
    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr mmap(IntPtr addr, UIntPtr length, int prot, int flags,
        int fd, IntPtr offset);
    [DllImport("libc", SetLastError = true)]
    private static extern int munmap(IntPtr addr, UIntPtr length);
    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);
    [DllImport("libc", SetLastError = true)]
    private static extern int poll(ref PollFd fds, ulong nfds, int timeout);

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int fd;
        public short events;
        public short revents;
    }

    private readonly WlDisplay _display;
    private readonly WlShm _shm;
    private readonly ExtForeignToplevelListV1 _list;
    private readonly ExtImageCopyCaptureManagerV1 _manager;
    private readonly ExtForeignToplevelImageCaptureSourceManagerV1 _sourceManager;
    private readonly FrameListener _pending = new();

    private ExtForeignToplevelHandleV1? _handle;
    private ExtImageCaptureSourceV1? _source;
    private ExtImageCopyCaptureSessionV1? _session;
    private WlShmPool? _pool;
    private WlBuffer? _buffer;
    private IntPtr _ptr;
    private int _fd = -1;
    private int _bufferSize;
    private int _sourceStride;
    private int _bpp = 4;
    private WlShm.FormatEnum _format = WlShm.FormatEnum.Xrgb8888;
    private byte[] _raw = Array.Empty<byte>();
    private byte[] _pixels = Array.Empty<byte>();
    private NestedFrame _lastFrame;
    private bool _stopReported;
    private bool _constraintsDone;
    private bool _sizeDirty;
    private long _sequence;
    private bool _disposed;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Stride { get; private set; }
    public long Sequence => Interlocked.Read(ref _sequence);
    // ConvertToBgra always writes B,G,R,A order, so the blitter must sample
    // B8G8R8A8 (not R8G8B8A8). The wlr-screencopy path used the opposite
    // convention because its raw reads arrive as R,G,B.
    public bool SourceIsRgb => false;
    public string AppId { get; }
    public string Title { get; }
    public bool HasToplevel => _handle is not null && _session is not null;

    public ToplevelCapture(string waylandDisplay, string? appId, string? title)
    {
        AppId = appId ?? string.Empty;
        Title = title ?? string.Empty;
        _display = WlDisplay.Connect(waylandDisplay, new WlDisplayOptions());

        var registryListener = new RegistryListener();
        var globals = new List<(uint Name, string Interface, uint Version)>();
        registryListener.GlobalAdded = (name, iface, version) => globals.Add((name, iface, version));
        var registry = _display.GetRegistry(registryListener, null);
        _display.Roundtrip();

        var shmGlobal = globals.FirstOrDefault(g => g.Interface == "wl_shm");
        var listGlobal = globals.FirstOrDefault(g => g.Interface == "ext_foreign_toplevel_list_v1");
        var managerGlobal = globals.FirstOrDefault(g => g.Interface == "ext_image_copy_capture_manager_v1");
        var sourceGlobal = globals.FirstOrDefault(g =>
            g.Interface == "ext_foreign_toplevel_image_capture_source_manager_v1");
        if (shmGlobal.Name == 0 || listGlobal.Name == 0 || managerGlobal.Name == 0 ||
            sourceGlobal.Name == 0)
            throw new InvalidOperationException(
                "nested compositor lacks ext-image-copy-capture / foreign-toplevel globals");

        _shm = WlShm.Bind(registry, shmGlobal.Name, 1, new ShmListener(), null);
        var handles = new List<ExtForeignToplevelHandleV1>();
        var handleListeners = new List<HandleListener>();
        var listListener = new ListListener(handles, handleListeners);
        _list = ExtForeignToplevelListV1.Bind(registry, listGlobal.Name, 1, listListener, null);
        _manager = ExtImageCopyCaptureManagerV1.Bind(registry, managerGlobal.Name, 1,
            new ManagerListener(), null);
        _sourceManager = ExtForeignToplevelImageCaptureSourceManagerV1.Bind(registry,
            sourceGlobal.Name, 1, new SourceManagerListener(), null);

        _display.Roundtrip();
        _display.Roundtrip();

        for (int i = 0; i < handles.Count; i++)
        {
            var listener = handleListeners[i];
            if (Matches(listener))
            {
                _handle = handles[i];
                break;
            }
        }
        if (_handle is null)
            throw new InvalidOperationException(
                $"toplevel app_id='{appId}' title='{title}' not found");

        _source = _sourceManager.CreateSource(_handle, new SourceListener(), null);
        var sessionListener = new SessionListener(this);
        _session = _manager.CreateSession(_source, (ExtImageCopyCaptureManagerV1.OptionsEnum)0,
            sessionListener, null);

        var deadline = Stopwatch.StartNew();
        while (!_constraintsDone && deadline.ElapsedMilliseconds < 3000)
            Pump(100);
        if (!_constraintsDone)
            throw new InvalidOperationException("capture session did not send buffer constraints");
        EnsureBuffer();
        Log.Info($"toplevel capture: app_id='{AppId}' title='{Title}' {Width}x{Height} " +
                 $"format={_format} bpp={_bpp}");
    }

    private bool Matches(HandleListener listener)
    {
        if (!string.IsNullOrEmpty(AppId) &&
            !string.Equals(listener.AppIdValue, AppId, StringComparison.Ordinal))
            return false;
        return string.IsNullOrEmpty(Title) ||
               string.Equals(listener.TitleValue, Title, StringComparison.Ordinal);
    }

    /// <summary>Pump the Wayland connection for at most <paramref name="timeoutMs"/>.</summary>
    private bool Pump(int timeoutMs)
    {
        _display.DispatchPending();
        _display.Flush();
        var fd = new PollFd { fd = _display.GetFd(), events = POLLIN };
        int ready = poll(ref fd, 1, timeoutMs);
        if (ready <= 0) return false;
        _display.Dispatch();
        return true;
    }

    private void EnsureBuffer()
    {
        if (!_constraintsDone || Width <= 0 || Height <= 0) return;
        if (_buffer is not null && !_sizeDirty) return;

        DestroyBuffer();
        _bpp = _format is WlShm.FormatEnum.Bgr888 or WlShm.FormatEnum.Rgb888 ? 3 : 4;
        _sourceStride = Width * _bpp;
        _bufferSize = _sourceStride * Height;
        Stride = Width * 4;
        _pixels = new byte[Stride * Height];
        _raw = new byte[_bufferSize];
        _lastFrame = default;

        _fd = memfd_create("gemprism-toplevel-capture", 0);
        if (_fd < 0) throw new InvalidOperationException("memfd_create failed");
        if (ftruncate(_fd, _bufferSize) != 0) throw new InvalidOperationException("ftruncate failed");
        _ptr = mmap(IntPtr.Zero, (UIntPtr)_bufferSize, PROT_READ | PROT_WRITE, MAP_SHARED, _fd,
            IntPtr.Zero);
        if (_ptr == new IntPtr(-1)) throw new InvalidOperationException("mmap failed");

        _pool = _shm.CreatePool(_fd, _bufferSize, new ShmPoolListener(), null);
        _buffer = _pool.CreateBuffer(0, Width, Height, _sourceStride, _format,
            new BufferListener(), null);
        _sizeDirty = false;
        _sequence = 0;
    }

    private void DestroyBuffer()
    {
        try { _buffer?.Destroy(); } catch { }
        try { _pool?.Destroy(); } catch { }
        _buffer = null;
        _pool = null;
        if (_ptr != IntPtr.Zero) { munmap(_ptr, (UIntPtr)_bufferSize); _ptr = IntPtr.Zero; }
        if (_fd >= 0) { close(_fd); _fd = -1; }
    }

    /// <summary>
    /// Capture one frame. Returns false if no frame is ready yet (the compositor
    /// may wait for the source to change) or the copy failed; call again later.
    /// </summary>
    public bool TryCapture(out NestedFrame frame, int timeoutMs = 0)
    {
        frame = default;
        if (_disposed || _session is null || _buffer is null) return false;
        EnsureBuffer();
        if (_buffer is null) return false;

        try
        {
            if (_pending.Frame is null)
            {
                _pending.Reset();
                var pendingFrame = _session.CreateFrame(_pending, null);
                _pending.Frame = pendingFrame;
                pendingFrame.AttachBuffer(_buffer);
                if (_pending.HasDamage)
                    pendingFrame.DamageBuffer(_pending.DamageX, _pending.DamageY,
                        _pending.DamageW, _pending.DamageH);
                else
                    pendingFrame.DamageBuffer(0, 0, Width, Height);
                pendingFrame.Capture();
                _display.Flush();
            }

            var deadline = Stopwatch.StartNew();
            while (!_pending.IsReady && !_pending.IsFailed)
            {
                int remaining = timeoutMs <= 0 ? 0 : timeoutMs - (int)deadline.ElapsedMilliseconds;
                if (!Pump(Math.Max(0, remaining)) && timeoutMs <= 0) return false;
                if (timeoutMs > 0 && deadline.ElapsedMilliseconds >= timeoutMs) return false;
            }

            if (_pending.IsFailed)
            {
                string failure = _pending.Failure;
                DestroyPending();
                // BufferConstraints is the normal answer to a resize: the
                // compositor wants a buffer of the new size. Reallocate on the
                // next call instead of logging a warning for every attempt.
                if (string.Equals(failure, "BufferConstraints", StringComparison.Ordinal))
                {
                    _sizeDirty = true;
                    Log.Debug($"toplevel capture '{AppId}' buffer constraints changed; resizing");
                    return false;
                }
                // The window closed: every later attempt fails the same way, so
                // report it once instead of flooding the log at loop rate.
                if (string.Equals(failure, "Stopped", StringComparison.Ordinal))
                {
                    if (!_stopReported)
                    {
                        _stopReported = true;
                        Log.Info($"toplevel capture '{AppId}' stopped (window closed)");
                    }
                    return false;
                }
                Log.Warn($"toplevel capture '{AppId}' failed: {failure}");
                return false;
            }

            if (Width <= 0 || Height <= 0) { DestroyPending(); return false; }
            Marshal.Copy(_ptr, _raw, 0, _bufferSize);
            ConvertToBgra();
            _pending.CopyDamageToNext();
            DestroyPending();
            Interlocked.Increment(ref _sequence);
            frame = new NestedFrame(Width, Height, Stride, Sequence);
            _lastFrame = frame;
            _stopReported = false;
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"toplevel capture '{AppId}' exception: {ex.GetType().Name}: {ex.Message}");
            DestroyPending();
            return false;
        }
    }

    /// <summary>
    /// Recreate the capture session. The first frame of a new session is always
    /// copied by the compositor, so this yields a fresh frame even when the
    /// source content is static. Needed after the host recreates its swapchain
    /// (window/fullscreen switch), otherwise the new window stays black until
    /// the game repaints.
    /// </summary>
    public void RestartSession()
    {
        if (_disposed || _source is null) return;
        DestroyPending();
        try { _session?.Destroy(); } catch { }
        _session = null;
        _constraintsDone = false;
        _sizeDirty = true;
        DestroyBuffer();
        _session = _manager.CreateSession(_source,
            (ExtImageCopyCaptureManagerV1.OptionsEnum)0, new SessionListener(this), null);
        var deadline = Stopwatch.StartNew();
        while (!_constraintsDone && deadline.ElapsedMilliseconds < 1500)
            Pump(50);
        EnsureBuffer();
    }

    private void DestroyPending()
    {
        try { _pending.Frame?.Destroy(); } catch { }
        _pending.Reset();
    }

    /// <summary>
    /// Normalise the source format into 32-bit BGRA. wl_shm reports BGR888 as
    /// a 24-bit value B:G:R, so little-endian memory is R,G,B (verified against
    /// the nested compositor).
    /// </summary>
    private void ConvertToBgra()
    {
        int dstStride = Stride;
        if (_bpp == 3)
        {
            bool bgrMemory = _format == WlShm.FormatEnum.Rgb888;
            for (int y = 0; y < Height; y++)
            {
                int s = y * _sourceStride;
                int d = y * dstStride;
                for (int x = 0; x < Width; x++, s += 3, d += 4)
                {
                    byte c0 = _raw[s], c1 = _raw[s + 1], c2 = _raw[s + 2];
                    _pixels[d] = bgrMemory ? c0 : c2;
                    _pixels[d + 1] = c1;
                    _pixels[d + 2] = bgrMemory ? c2 : c0;
                    _pixels[d + 3] = 255;
                }
            }
            return;
        }
        bool swapRedBlue = _format is WlShm.FormatEnum.Xbgr8888 or WlShm.FormatEnum.Abgr8888;
        if (!swapRedBlue)
        {
            for (int y = 0; y < Height; y++)
                Buffer.BlockCopy(_raw, y * _sourceStride, _pixels, y * dstStride, dstStride);
            return;
        }
        // xbgr8888/abgr8888 hold R,G,B,A in memory (the compositor's XWayland
        // path advertises xbgr8888); our blitter samples B,G,R,A.
        for (int y = 0; y < Height; y++)
        {
            int s = y * _sourceStride;
            int d = y * dstStride;
            for (int x = 0; x < Width; x++, s += 4, d += 4)
            {
                _pixels[d] = _raw[s + 2];
                _pixels[d + 1] = _raw[s + 1];
                _pixels[d + 2] = _raw[s];
                _pixels[d + 3] = 255;
            }
        }
    }

    public ReadOnlySpan<byte> Pixels => _pixels;

    public bool HasFrame => _lastFrame.Width > 0;

    public bool TryGetLastFrame(out NestedFrame frame)
    {
        frame = _lastFrame;
        return _lastFrame.Width > 0 && _lastFrame.Height > 0;
    }

    bool INestedFrameSource.TryCapture(out NestedFrame frame) => TryCapture(out frame, 0);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DestroyPending();
        DestroyBuffer();
        try { _session?.Destroy(); } catch { }
        try { _source?.Destroy(); } catch { }
        try { _handle?.Destroy(); } catch { }
        try { _manager.Destroy(); } catch { }
        try { _sourceManager.Destroy(); } catch { }
        try { _list.Destroy(); } catch { }
        _display.Dispose();
    }

    private sealed class RegistryListener : WlRegistry.Listener
    {
        public Action<uint, string, uint>? GlobalAdded;
        protected override void Global(WlRegistry s, uint name, string iface, uint version)
            => GlobalAdded?.Invoke(name, iface, version);
    }

    private sealed class ShmListener : WlShm.Listener { }
    private sealed class ShmPoolListener : WlShmPool.Listener { }
    private sealed class BufferListener : WlBuffer.Listener { }
    private sealed class ManagerListener : ExtImageCopyCaptureManagerV1.Listener { }
    private sealed class SourceManagerListener
        : ExtForeignToplevelImageCaptureSourceManagerV1.Listener { }
    private sealed class SourceListener : ExtImageCaptureSourceV1.Listener { }

    private sealed class ListListener : ExtForeignToplevelListV1.Listener
    {
        private readonly List<ExtForeignToplevelHandleV1> _handles;
        private readonly List<HandleListener> _listeners;

        public ListListener(List<ExtForeignToplevelHandleV1> handles,
            List<HandleListener> listeners)
        {
            _handles = handles;
            _listeners = listeners;
        }

        protected override void Toplevel(ExtForeignToplevelListV1 s,
            NewId<ExtForeignToplevelHandleV1, ExtForeignToplevelHandleV1.Listener> id)
        {
            var listener = new HandleListener();
            var handle = id.GetAndConsume(listener);
            _handles.Add(handle);
            _listeners.Add(listener);
        }
    }

    private sealed class HandleListener : ExtForeignToplevelHandleV1.Listener
    {
        public string AppIdValue { get; private set; } = string.Empty;
        public string TitleValue { get; private set; } = string.Empty;
        public string IdentifierValue { get; private set; } = string.Empty;
        public bool IsClosed { get; private set; }

        protected override void AppId(ExtForeignToplevelHandleV1 s, string appId)
            => AppIdValue = appId;
        protected override void Title(ExtForeignToplevelHandleV1 s, string title)
            => TitleValue = title;
        protected override void Identifier(ExtForeignToplevelHandleV1 s, string identifier)
            => IdentifierValue = identifier;
        protected override void Closed(ExtForeignToplevelHandleV1 s) => IsClosed = true;
    }

    private sealed class SessionListener : ExtImageCopyCaptureSessionV1.Listener
    {
        private readonly ToplevelCapture _owner;
        public SessionListener(ToplevelCapture owner) => _owner = owner;

        protected override void BufferSize(ExtImageCopyCaptureSessionV1 s, uint width, uint height)
        {
            if ((int)width != _owner.Width || (int)height != _owner.Height)
            {
                _owner.Width = (int)width;
                _owner.Height = (int)height;
                _owner._sizeDirty = true;
            }
        }

        protected override void ShmFormat(ExtImageCopyCaptureSessionV1 s, WlShm.FormatEnum format)
        {
            // wlroots reports DRM fourcc values here (BG24/XR24); prefer the
            // advertised 32-bit formats when available, otherwise 3-byte BGR.
            if (format is WlShm.FormatEnum.Argb8888 or WlShm.FormatEnum.Xrgb8888)
                _owner._format = format;
            else if (_owner._format is not (WlShm.FormatEnum.Argb8888 or WlShm.FormatEnum.Xrgb8888)
                     && format is WlShm.FormatEnum.Bgr888 or WlShm.FormatEnum.Rgb888
                         or WlShm.FormatEnum.Xbgr8888 or WlShm.FormatEnum.Abgr8888)
                _owner._format = format;
        }

        protected override void DmabufDevice(ExtImageCopyCaptureSessionV1 s, ReadOnlySpan<byte> device)
        {
        }

        protected override void DmabufFormat(ExtImageCopyCaptureSessionV1 s, uint format,
            ReadOnlySpan<byte> modifiers)
        {
        }

        protected override void Done(ExtImageCopyCaptureSessionV1 s)
        {
            _owner._constraintsDone = true;
            _owner._sizeDirty = true;
        }

        protected override void Stopped(ExtImageCopyCaptureSessionV1 s)
            => Log.Warn($"toplevel capture '{_owner.AppId}' session stopped");
    }

    private sealed class FrameListener : ExtImageCopyCaptureFrameV1.Listener
    {
        public ExtImageCopyCaptureFrameV1? Frame;
        public bool IsReady;
        public bool IsFailed;
        public string Failure = string.Empty;
        public bool HasDamage;
        public int DamageX, DamageY, DamageW, DamageH;

        public void Reset()
        {
            Frame = null;
            IsReady = false;
            IsFailed = false;
            Failure = string.Empty;
            HasDamage = DamageW > 0;
        }

        public void CopyDamageToNext()
        {
            // Use this frame's damage as the damage hint for the next capture.
            if (DamageW > 0)
            {
                DamageX = Math.Max(0, DamageX);
                DamageY = Math.Max(0, DamageY);
            }
        }

        protected override void Transform(ExtImageCopyCaptureFrameV1 s, WlOutput.TransformEnum transform)
        {
        }

        protected override void Damage(ExtImageCopyCaptureFrameV1 s, int x, int y, int width,
            int height)
        {
            HasDamage = true;
            if (DamageW == 0)
            {
                DamageX = x; DamageY = y; DamageW = width; DamageH = height;
            }
            else
            {
                int x0 = Math.Min(DamageX, x), y0 = Math.Min(DamageY, y);
                int x1 = Math.Max(DamageX + DamageW, x + width);
                int y1 = Math.Max(DamageY + DamageH, y + height);
                DamageX = x0; DamageY = y0; DamageW = x1 - x0; DamageH = y1 - y0;
            }
        }

        protected override void PresentationTime(ExtImageCopyCaptureFrameV1 s, uint tvSecHi,
            uint tvSecLo, uint tvNsec)
        {
        }

        protected override void Ready(ExtImageCopyCaptureFrameV1 s) => IsReady = true;

        protected override void Failed(ExtImageCopyCaptureFrameV1 s,
            ExtImageCopyCaptureFrameV1.FailureReasonEnum reason)
        {
            IsFailed = true;
            Failure = reason.ToString();
        }
    }
}