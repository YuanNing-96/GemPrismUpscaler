using System.Runtime.InteropServices;
using GemPrismUpscaler.Core.Diagnostics;

namespace GemPrismUpscaler.Core.Platform.Linux.X11;

/// <summary>
/// Captures a client window with MIT-SHM. On KWin/XWayland a covered window
/// still reads its own buffer, so no XComposite redirect is required.
/// </summary>
public sealed class X11Capture : IDisposable
{
    private readonly X11Display _display;
    private readonly ulong _window;
    private IntPtr _shmInfoPtr;
    private IntPtr _image;
    private IntPtr _shmAddr;
    private int _shmId = -1;
    private bool _attached;
    private bool _usingShm;
    private bool _disposed;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Stride { get; private set; }
    public byte[] Buffer { get; private set; } = Array.Empty<byte>();
    public bool IsAvailable => _attached && _image != IntPtr.Zero;

    /// <summary>
    /// Read-only view of the most recent frame. On the primary XShm path this
    /// points directly into shared memory, so the dirty-tile hash and staging
    /// upload avoid a managed full-frame copy. The XGetImage fallback fills
    /// <see cref="Buffer"/> instead.
    /// </summary>
    public unsafe ReadOnlySpan<byte> Pixels => _usingShm && _shmAddr != IntPtr.Zero
        ? new ReadOnlySpan<byte>((void*)_shmAddr, Stride * Height)
        : Buffer;

    public X11Capture(X11Display display, ulong window, int width, int height)
    {
        _display = display;
        _window = window;
        Create(width, height);
    }

    private void Create(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        Width = width;
        Height = height;

        if (X11Native.XShmQueryExtension(_display.Handle) == 0)
            throw new InvalidOperationException("MIT-SHM extension unavailable");

        int size = width * height * 4;
        _shmId = X11Native.shmget(X11Native.IPC_PRIVATE, (nuint)size, X11Native.IPC_CREAT | X11Native.ShmMode0600);
        if (_shmId < 0)
            throw new InvalidOperationException($"shmget failed (errno={Marshal.GetLastWin32Error()})");

        _shmAddr = X11Native.shmat(_shmId, IntPtr.Zero, 0);
        if (_shmAddr == new IntPtr(-1))
            throw new InvalidOperationException("shmat failed");

        _shmInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<X11Native.XShmSegmentInfo>());
        var shm = new X11Native.XShmSegmentInfo { ShmId = _shmId, ShmAddr = _shmAddr, ReadOnly = 0 };
        Marshal.StructureToPtr(shm, _shmInfoPtr, false);
        _image = X11Native.XShmCreateImage(_display.Handle,
            X11Native.XDefaultVisual(_display.Handle, _display.Screen),
            (uint)X11Native.XDefaultDepth(_display.Handle, _display.Screen),
            X11Native.ZPixmap, _shmAddr, _shmInfoPtr, (uint)width, (uint)height);

        if (_image == IntPtr.Zero)
            throw new InvalidOperationException("XShmCreateImage failed");

        if (X11Native.XShmAttach(_display.Handle, _shmInfoPtr) == 0)
            throw new InvalidOperationException("XShmAttach failed");

        _attached = true;
        _display.Sync();

        var img = Marshal.PtrToStructure<X11Native.XImage>(_image);
        Stride = img.BytesPerLine;
        if (Stride <= 0) Stride = width * 4;
        Buffer = new byte[Stride * height];
    }

    private bool _shmFailureLogged;
    public bool TryCapture()
    {
        if (!IsAvailable) return false;
        if (X11Native.XShmGetImage(_display.Handle, _window, _image, 0, 0, X11Native.AllPlanes) != 0)
        {
            _usingShm = true;
            return true;
        }

        if (!_shmFailureLogged)
        {
            _shmFailureLogged = true;
            Log.Write($"XShmGetImage failed (XLastError={X11Display.LastError}); falling back to XGetImage");
        }
        return TryCaptureFallback();
    }

    private bool TryCaptureFallback()
    {
        IntPtr imgPtr = X11Native.XGetImage(_display.Handle, _window, 0, 0, (uint)Width, (uint)Height,
            X11Native.AllPlanes, X11Native.ZPixmap);
        if (imgPtr == IntPtr.Zero) return false;
        try
        {
            var img = Marshal.PtrToStructure<X11Native.XImage>(imgPtr);
            if (img.Data == IntPtr.Zero || img.BytesPerLine <= 0) return false;
            int rowBytes = Width * 4;
            for (int y = 0; y < Height; y++)
                Marshal.Copy(img.Data + y * img.BytesPerLine, Buffer, y * Stride, rowBytes);
            _usingShm = false;
            return true;
        }
        finally
        {
            X11Native.XDestroyImage(imgPtr);
        }
    }

    public void Recreate(int width, int height)
    {
        if (width == Width && height == Height) return;
        ReleaseShm();
        Create(width, height);
    }

    private void ReleaseShm()
    {
        if (_attached)
        {
            if (_shmInfoPtr != IntPtr.Zero) X11Native.XShmDetach(_display.Handle, _shmInfoPtr);
            _display.Sync();
            _attached = false;
        }
        if (_image != IntPtr.Zero)
        {
            X11Native.XFree(_image);
            _image = IntPtr.Zero;
        }
        if (_shmAddr != IntPtr.Zero)
        {
            X11Native.shmdt(_shmAddr);
            _shmAddr = IntPtr.Zero;
        }
        if (_shmId >= 0)
        {
            X11Native.shmctl(_shmId, X11Native.IPC_RMID, IntPtr.Zero);
            _shmId = -1;
        }
        if (_shmInfoPtr != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_shmInfoPtr);
            _shmInfoPtr = IntPtr.Zero;
        }
        Buffer = Array.Empty<byte>();
        _usingShm = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseShm();
    }
}