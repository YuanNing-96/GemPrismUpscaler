using System.Diagnostics;
using System.Runtime.InteropServices;
using GemPrismUpscaler.Core.Diagnostics;
using Microsoft.Win32.SafeHandles;

namespace GemPrismUpscaler.Core.Platform.Linux.PipeWire;

/// <summary>
/// M2a PipeWire capture bridge. The portal hands us a PipeWire remote fd and a
/// stream serial; GStreamer's pipewiresrc consumes that stream and writes raw
/// BGRA frames to a dedicated pipe fd (not stdout, because gst-launch -v also
/// writes its caps trace to stdout). Caps are parsed from that trace because
/// KDE's portal may omit the window stream "size" property.
///
/// This keeps M2a small; a native libpipewire client can replace it later
/// without changing the session API.
/// </summary>
public sealed class PipeWireGstCapture : IDisposable
{
    private readonly int _inheritedFd;
    private readonly ulong _serial;
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _capsKnown = new(false);
    private Process? _process;
    private Thread? _reader;
    private Thread? _stdout;
    private Thread? _stderr;
    private int _frameReadFd = -1;
    private int _frameWriteFd = -1;
    private byte[] _frame = Array.Empty<byte>();
    private int _width;
    private int _height;
    private long _sequence;
    private volatile bool _stop;
    private volatile bool _faulted;
    private string _error = string.Empty;
    private bool _disposed;

    public int Width => Volatile.Read(ref _width);
    public int Height => Volatile.Read(ref _height);
    public int Stride => Width * 4;
    public bool Faulted => _faulted;
    public string Error => _error;
    public long Sequence => Interlocked.Read(ref _sequence);

    public PipeWireGstCapture(SafeHandle pipeWireFd, ulong serial)
    {
        _inheritedFd = DupForChild(pipeWireFd);
        _serial = serial;
    }

    public void Start()
    {
        var fds = new int[2];
        if (pipe(fds) < 0)
            throw new InvalidOperationException($"pipe() failed: {Marshal.GetLastWin32Error()}");
        _frameReadFd = fds[0];
        _frameWriteFd = fds[1];
        if (fcntl(_frameWriteFd, F_SETFD, 0) < 0)
        {
            close(_frameReadFd); close(_frameWriteFd);
            throw new InvalidOperationException($"fcntl(frame pipe CLOEXEC) failed: {Marshal.GetLastWin32Error()}");
        }

        string pipeline =
            $"pipewiresrc fd={_inheritedFd} target-object={_serial} ! " +
            $"videoconvert ! video/x-raw,format=BGRA ! fdsink fd={_frameWriteFd} sync=false";
        var psi = new ProcessStartInfo("gst-launch-1.0")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-v");
        foreach (string token in pipeline.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            psi.ArgumentList.Add(token);

        try
        {
            _process = Process.Start(psi) ?? throw new InvalidOperationException("gst-launch failed to start");
        }
        catch (Exception ex)
        {
            _faulted = true;
            _error = ex.Message;
            throw;
        }
        finally
        {
            // The child inherited the write end; the parent must close it or
            // the reader will never see EOF.
            close(_frameWriteFd);
            _frameWriteFd = -1;
        }

        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "GemPrism-PipeWire" };
        _stdout = new Thread(StdoutLoop) { IsBackground = true, Name = "GemPrism-PipeWireCaps" };
        _stderr = new Thread(StderrLoop) { IsBackground = true, Name = "GemPrism-PipeWireLog" };
        _reader.Start();
        _stdout.Start();
        _stderr.Start();
        Log.Info($"pipewire capture starting serial={_serial} fd={_inheritedFd} frameFd={_frameReadFd}");
    }

    /// <summary>Wait until GStreamer reports the negotiated raw caps.</summary>
    public bool WaitForSize(int timeoutMs)
    {
        if (_capsKnown.Wait(timeoutMs)) return true;
        Fault("pipewire did not report video caps in time");
        return false;
    }

    private void ReadLoop()
    {
        try
        {
            if (!_capsKnown.Wait(8000) || _stop) return;
            int width = Width;
            int height = Height;
            if (width <= 0 || height <= 0)
            {
                Fault($"invalid pipewire caps {width}x{height}");
                return;
            }

            using var stream = new FileStream(new SafeFileHandle((IntPtr)_frameReadFd, ownsHandle: false),
                FileAccess.Read, 1, false);
            _frameReadFd = -1;
            var buffer = new byte[width * height * 4];
            while (!_stop)
            {
                int read = 0;
                while (read < buffer.Length && !_stop)
                {
                    int n = stream.Read(buffer, read, buffer.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
                if (read != buffer.Length)
                {
                    if (!_stop) Fault("pipewire stream ended");
                    return;
                }

                lock (_gate)
                {
                    var swap = _frame;
                    _frame = buffer;
                    buffer = swap;
                    Interlocked.Increment(ref _sequence);
                }
            }
        }
        catch (Exception ex)
        {
            if (!_stop) Fault($"pipewire read: {ex.Message}");
        }
    }

    private void StdoutLoop()
    {
        var process = _process;
        if (process is null) return;
        try
        {
            string? line;
            while ((line = process.StandardOutput.ReadLine()) is not null)
            {
                if (!line.Contains("width=(int)", StringComparison.Ordinal) ||
                    !line.Contains("height=(int)", StringComparison.Ordinal) ||
                    !line.Contains("video/x-raw", StringComparison.Ordinal))
                    continue;

                int width = ParseInt(line, "width=(int)");
                int height = ParseInt(line, "height=(int)");
                if (width <= 0 || height <= 0 || _capsKnown.IsSet) continue;
                Volatile.Write(ref _width, width);
                Volatile.Write(ref _height, height);
                lock (_gate) _frame = new byte[width * height * 4];
                _capsKnown.Set();
                Log.Info($"pipewire caps {width}x{height}");
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"gst caps reader ended: {ex.Message}");
        }
    }

    private void StderrLoop()
    {
        var process = _process;
        if (process is null) return;
        try
        {
            string? line;
            while ((line = process.StandardError.ReadLine()) is not null)
            {
                if (line.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("error:", StringComparison.OrdinalIgnoreCase))
                    Log.Warn($"gst-launch: {line}");
                else
                    Log.Debug($"gst-launch: {line}");
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"gst stderr reader ended: {ex.Message}");
        }
    }

    private static int ParseInt(string line, string key)
    {
        int index = line.IndexOf(key, StringComparison.Ordinal);
        if (index < 0) return 0;
        index += key.Length;
        int end = index;
        while (end < line.Length && char.IsDigit(line[end])) end++;
        return int.TryParse(line.AsSpan(index, end - index), out int value) ? value : 0;
    }

    /// <summary>Copy the latest frame under the capture lock.</summary>
    public bool TryCopyFrame(Span<byte> destination, out long sequence)
    {
        lock (_gate)
        {
            sequence = Interlocked.Read(ref _sequence);
            if (_frame.Length == 0 || destination.Length < _frame.Length) return false;
            _frame.AsSpan().CopyTo(destination);
            return true;
        }
    }

    private void Fault(string message)
    {
        _faulted = true;
        _error = message;
        Log.Warn(message);
    }

    private static int DupForChild(SafeHandle handle)
    {
        int source = (int)handle.DangerousGetHandle();
        int duplicate = dup(source);
        if (duplicate < 0)
            throw new InvalidOperationException($"dup(portal fd) failed: {Marshal.GetLastWin32Error()}");
        if (fcntl(duplicate, F_SETFD, 0) < 0)
        {
            close(duplicate);
            throw new InvalidOperationException($"fcntl(CLOEXEC) failed: {Marshal.GetLastWin32Error()}");
        }
        return duplicate;
    }

    private const int F_SETFD = 2;

    [DllImport("libc", SetLastError = true)]
    private static extern int dup(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int pipe(int[] fds);

    [DllImport("libc", SetLastError = true)]
    private static extern int fcntl(int fd, int cmd, int arg);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop = true;
        _capsKnown.Set();
        try
        {
            if (_process is { HasExited: false })
                _process.Kill(entireProcessTree: true);
        }
        catch { }
        try { _process?.Dispose(); } catch { }
        try { if (_frameReadFd >= 0) close(_frameReadFd); } catch { }
        try { if (_frameWriteFd >= 0) close(_frameWriteFd); } catch { }
        try { close(_inheritedFd); } catch { }
    }
}
