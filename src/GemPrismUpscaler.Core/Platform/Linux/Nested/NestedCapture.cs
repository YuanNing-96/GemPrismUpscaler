using System.Runtime.InteropServices;
using GemPrismUpscaler.Core.Diagnostics;
using NWayland;
using NWayland.Protocols.Wayland;
using NWayland.Protocols.Wlr.WlrScreencopyUnstableV1;

namespace GemPrismUpscaler.Core.Platform.Linux.Nested;

public readonly record struct NestedFrame(int Width, int Height, int Stride, long Sequence);

/// <summary>
/// Synchronous wlr-screencopy shm capture of the private nested output. One
/// frame is copied into a managed buffer per request; the shm pool is created
/// and destroyed for that frame. This is the safe first M2b path; DMA-BUF is
/// deliberately not part of M2b.
/// </summary>
public sealed class NestedCapture : IDisposable
{
    private const int PROT_READ = 1;
    private const int MAP_SHARED = 1;

    [DllImport("libc", SetLastError = true)]
    private static extern int memfd_create(string name, uint flags);
    [DllImport("libc", SetLastError = true)]
    private static extern int ftruncate(int fd, long length);
    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr mmap(IntPtr addr, UIntPtr length, int prot, int flags, int fd, IntPtr offset);
    [DllImport("libc", SetLastError = true)]
    private static extern int munmap(IntPtr addr, UIntPtr length);
    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    private readonly WlDisplay _display;
    private readonly WlRegistry _registry;
    private readonly WlShm _shm;
    private readonly WlOutput _output;
    private readonly ZwlrScreencopyManagerV1 _manager;
    private readonly string _outputName;
    private byte[] _pixels = Array.Empty<byte>();
    private long _sequence;
    private bool _disposed;
    private bool _sourceIsRgb = true;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Stride { get; private set; }
    public long Sequence => Interlocked.Read(ref _sequence);
    public string OutputName => _outputName;
    public bool SourceIsRgb => _sourceIsRgb;

    public NestedCapture(string waylandDisplay, string? preferredOutput = null)
    {
        _display = WlDisplay.Connect(waylandDisplay, new WlDisplayOptions());
        var registryListener = new RegistryListener();
        var globals = new List<(uint Name, string Interface, uint Version)>();
        registryListener.GlobalAdded = (name, iface, version) => globals.Add((name, iface, version));
        _registry = _display.GetRegistry(registryListener, null);
        _display.Roundtrip();

        var shmGlobal = globals.FirstOrDefault(g => g.Interface == "wl_shm");
        var outputGlobals = globals.Where(g => g.Interface == "wl_output").ToList();
        var managerGlobal = globals.FirstOrDefault(g => g.Interface == "zwlr_screencopy_manager_v1");
        if (shmGlobal.Name == 0 || outputGlobals.Count == 0 || managerGlobal.Name == 0)
            throw new InvalidOperationException("nested compositor lacks wl_shm/wl_output/zwlr_screencopy_manager_v1");

        _shm = WlShm.Bind(_registry, shmGlobal.Name, 1, new ShmListener(), null);

        // Bind every wl_output so we can select by name. The nested compositor
        // has one output per game window plus one spare; binding the first
        // global blindly would capture the wrong window when outputs are
        // created dynamically.
        var bound = new List<(WlOutput Output, OutputListener Listener)>();
        foreach (var global in outputGlobals)
        {
            var listener = new OutputListener();
            var output = WlOutput.Bind(_registry, global.Name, Math.Min(global.Version, 4),
                listener, null);
            bound.Add((output, listener));
        }
        _display.Roundtrip();
        _display.Roundtrip();

        (WlOutput Output, OutputListener Listener) selected;
        if (string.IsNullOrWhiteSpace(preferredOutput))
        {
            selected = bound.FirstOrDefault(b => b.Listener.Width > 0 && b.Listener.Height > 0);
            if (selected.Output is null) selected = bound[0];
        }
        else
        {
            selected = bound.FirstOrDefault(b =>
                string.Equals(b.Listener.OutputName, preferredOutput, StringComparison.OrdinalIgnoreCase));
            if (selected.Output is null)
            {
                string available = string.Join(", ",
                    bound.Select(b => string.IsNullOrWhiteSpace(b.Listener.OutputName)
                        ? "(unnamed)" : b.Listener.OutputName));
                throw new InvalidOperationException(
                    $"nested output '{preferredOutput}' not found; available=[{available}]");
            }
        }

        _output = selected.Output;
        _outputName = selected.Listener.OutputName;
        Width = selected.Listener.Width;
        Height = selected.Listener.Height;
        if (Width <= 0 || Height <= 0)
            throw new InvalidOperationException(
                $"nested output '{(_outputName.Length == 0 ? preferredOutput : _outputName)}' has no current mode");
        Stride = Width * 4;
        _pixels = new byte[Stride * Height];

        _manager = ZwlrScreencopyManagerV1.Bind(_registry, managerGlobal.Name,
            Math.Min(managerGlobal.Version, 3), new ManagerListener(), null);
        _display.Roundtrip();
        Log.Info($"nested capture: output='{_outputName}' {Width}x{Height} stride={Stride} " +
                 $"requested='{preferredOutput ?? "auto"}'");
    }

    /// <summary>Capture one frame. Returns false when the compositor failed.</summary>
    public bool TryCapture(out NestedFrame frame)
    {
        frame = default;
        if (_disposed) return false;

        int size = Stride * Height;
        int fd = memfd_create("gemprism-nested-capture", 0);
        if (fd < 0) throw new InvalidOperationException($"memfd_create failed: {Marshal.GetLastWin32Error()}");
        IntPtr ptr = IntPtr.Zero;
        WlShmPool? pool = null;
        WlBuffer? buffer = null;
        try
        {
            if (ftruncate(fd, size) != 0)
                throw new InvalidOperationException("ftruncate failed");
            ptr = mmap(IntPtr.Zero, (UIntPtr)size, PROT_READ, MAP_SHARED, fd, IntPtr.Zero);
            if (ptr == new IntPtr(-1))
                throw new InvalidOperationException("mmap failed");

            var listener = new FrameListener();
            listener.OnBuffer = (format, width, height, stride) =>
            {
                if (format is not (WlShm.FormatEnum.Xbgr8888 or WlShm.FormatEnum.Argb8888))
                    Log.Warn($"nested capture: unexpected shm format {format}");
                listener.SwapRedBlue = format is WlShm.FormatEnum.Xbgr8888 or WlShm.FormatEnum.Argb8888;
                pool = _shm.CreatePool(fd, size, new ShmPoolListener(), null);
                buffer = pool.CreateBuffer(0, (int)width, (int)height, (int)stride, format,
                    new BufferListener(), null);
                listener.Frame?.Copy(buffer);
            };
            listener.OnReady = () => listener.IsReady = true;
            listener.OnFailed = () => listener.IsFailed = true;

            var copyFrame = _manager.CaptureOutput(0, _output, listener, null);
            listener.Frame = copyFrame;
            _display.Roundtrip();
            for (int i = 0; i < 200 && !listener.IsReady && !listener.IsFailed; i++)
                _display.Dispatch();

            if (!listener.IsReady || listener.Width == 0 || listener.Height == 0)
                return false;

            if (listener.Width != Width || listener.Height != Height || listener.Stride != Stride)
            {
                Width = (int)listener.Width;
                Height = (int)listener.Height;
                Stride = (int)listener.Stride;
                _pixels = new byte[Stride * Height];
                Log.Info($"nested capture: resized {Width}x{Height} stride={Stride}");
            }

            Marshal.Copy(ptr, _pixels, 0, Stride * Height);
            // Xbgr8888/Argb8888 is RGBX in memory; the Vulkan source image uses
            // R8G8B8A8 for this path, so no red/blue CPU swap is needed.
            _sourceIsRgb = listener.SwapRedBlue;
            Interlocked.Increment(ref _sequence);
            frame = new NestedFrame(Width, Height, Stride, Sequence);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"nested capture failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
        finally
        {
            try { buffer?.Destroy(); } catch { }
            try { pool?.Destroy(); } catch { }
            if (ptr != IntPtr.Zero) munmap(ptr, (UIntPtr)size);
            if (fd >= 0) close(fd);
        }
    }

    /// <summary>
    /// wl_shm Xbgr8888/Argb8888 is RGBX in memory on little-endian; the Vulkan
    /// upload path expects BGRA. Swap red/blue and make the X byte opaque.
    /// </summary>
    private void NormalizeBgra(bool swapRedBlue)
    {
        var pixels = _pixels.AsSpan(0, Stride * Height);
        if (swapRedBlue)
        {
            for (int i = 0; i + 3 < pixels.Length; i += 4)
            {
                byte b = pixels[i];
                pixels[i] = pixels[i + 2];
                pixels[i + 2] = b;
                pixels[i + 3] = 255;
            }
        }
        else
        {
            for (int i = 3; i < pixels.Length; i += 4)
                pixels[i] = 255;
        }
    }

    public ReadOnlySpan<byte> Pixels => _pixels;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _manager.Destroy(); } catch { }
        try { _output.Release(); } catch { }
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
    private sealed class ManagerListener : ZwlrScreencopyManagerV1.Listener { }

    private sealed class OutputListener : WlOutput.Listener
    {
        public string OutputName { get; private set; } = string.Empty;
        public int Width { get; private set; }
        public int Height { get; private set; }

        protected override void Name(WlOutput s, string name) => OutputName = name;
        protected override void Mode(WlOutput s, WlOutput.ModeEnum flags, int width, int height, int refresh)
        {
            if (flags.HasFlag(WlOutput.ModeEnum.Current))
            {
                Width = width;
                Height = height;
            }
        }
    }

    private sealed class FrameListener : ZwlrScreencopyFrameV1.Listener
    {
        public ZwlrScreencopyFrameV1? Frame;
        public bool IsReady;
        public bool IsFailed;
        public bool SwapRedBlue;
        public uint Width;
        public uint Height;
        public uint Stride;
        public Action<WlShm.FormatEnum, uint, uint, uint>? OnBuffer;
        public Action? OnReady;
        public Action? OnFailed;

        protected override void Buffer(ZwlrScreencopyFrameV1 s, WlShm.FormatEnum format,
            uint width, uint height, uint stride)
        {
            Width = width;
            Height = height;
            Stride = stride;
            OnBuffer?.Invoke(format, width, height, stride);
        }

        protected override void Ready(ZwlrScreencopyFrameV1 s, uint tvSecHi, uint tvSecLo, uint tvNsec)
            => OnReady?.Invoke();

        protected override void Failed(ZwlrScreencopyFrameV1 s) => OnFailed?.Invoke();
    }
}