using System.Runtime.InteropServices;
using GemPrismUpscaler.Core.Diagnostics;

namespace GemPrismUpscaler.Core.Platform.Linux.X11;

public sealed class X11Display : IDisposable
{
    private static readonly X11Native.XErrorHandler ErrorHandler = OnError;
    private static int _lastError;
    private bool _disposed;

    public IntPtr Handle { get; }
    public int Screen { get; }
    public ulong Root { get; }
    public int Width { get; }
    public int Height { get; }

    public ulong AtomNetClientList { get; }
    public ulong AtomNetWmPid { get; }
    public ulong AtomNetWmName { get; }
    public ulong AtomWmName { get; }
    public ulong AtomWmClass { get; }
    public ulong AtomUtf8String { get; }
    public ulong AtomNetWmState { get; }
    public ulong AtomNetWmStateHidden { get; }
    public ulong AtomNetActiveWindow { get; }

    public X11Display(string? name = null)
    {
        Handle = X11Native.XOpenDisplay(name);
        if (Handle == IntPtr.Zero)
            throw new InvalidOperationException("XOpenDisplay failed; is DISPLAY set?");

        X11Native.XSetErrorHandler(ErrorHandler);
        Screen = X11Native.XDefaultScreen(Handle);
        Root = X11Native.XRootWindow(Handle, Screen);
        Width = X11Native.XDisplayWidth(Handle, Screen);
        Height = X11Native.XDisplayHeight(Handle, Screen);

        AtomNetClientList = X11Native.XInternAtom(Handle, "_NET_CLIENT_LIST", 0);
        AtomNetWmPid = X11Native.XInternAtom(Handle, "_NET_WM_PID", 0);
        AtomNetWmName = X11Native.XInternAtom(Handle, "_NET_WM_NAME", 0);
        AtomWmName = X11Native.XInternAtom(Handle, "WM_NAME", 0);
        AtomWmClass = X11Native.XInternAtom(Handle, "WM_CLASS", 0);
        AtomUtf8String = X11Native.XInternAtom(Handle, "UTF8_STRING", 0);
        AtomNetWmState = X11Native.XInternAtom(Handle, "_NET_WM_STATE", 0);
        AtomNetWmStateHidden = X11Native.XInternAtom(Handle, "_NET_WM_STATE_HIDDEN", 0);
        AtomNetActiveWindow = X11Native.XInternAtom(Handle, "_NET_ACTIVE_WINDOW", 0);
    }

    public static int LastError => Volatile.Read(ref _lastError);

    private static int OnError(IntPtr display, IntPtr errorEvent)
    {
        Interlocked.Increment(ref _lastError);
        try
        {
            int code = Marshal.ReadByte(errorEvent, 32);
            int req = Marshal.ReadByte(errorEvent, 33);
            int minor = Marshal.ReadByte(errorEvent, 34);
            Log.Write($"X error code={code} request={req}.{minor}");
        }
        catch { }
        return 0;
    }

    public void Sync() => X11Native.XSync(Handle, 0);
    public void Flush() => X11Native.XFlush(Handle);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        X11Native.XCloseDisplay(Handle);
    }
}

public sealed record WindowInfo(
    ulong WindowId,
    string Title,
    string ClassName,
    int Pid,
    int X,
    int Y,
    int Width,
    int Height)
{
    public override string ToString() =>
        $"{Title} [{Width}x{Height}] pid={Pid} 0x{WindowId:x}";
}

public static class X11WindowEnumerator
{
    public static List<WindowInfo> List(X11Display display)
    {
        var result = new List<WindowInfo>();
        foreach (var id in GetClientWindows(display))
        {
            var info = TryRead(display, id);
            if (info is not null)
                result.Add(info);
        }
        return result;
    }

    private static List<ulong> GetClientWindows(X11Display display)
    {
        var ids = new List<ulong>();
        if (TryGetProperty(display, display.Root, display.AtomNetClientList, 0, 4096, out var prop, out var nitems, out var format)
            && format == 32 && prop != IntPtr.Zero)
        {
            for (int i = 0; i < (int)nitems; i++)
            {
                long v = Marshal.ReadInt64(prop, i * 8);
                if (v != 0) ids.Add((ulong)v);
            }
            X11Native.XFree(prop);
            return ids;
        }

        if (X11Native.XQueryTree(display.Handle, display.Root, out _, out _, out var children, out var count) != 0 && children != IntPtr.Zero)
        {
            for (int i = 0; i < count; i++)
                ids.Add((ulong)Marshal.ReadInt64(children, i * 8));
            X11Native.XFree(children);
        }
        return ids;
    }

    private static WindowInfo? TryRead(X11Display display, ulong id)
    {
        if (id == 0 || id == display.Root) return null;
        if (X11Native.XGetGeometry(display.Handle, id, out var root, out _, out _, out uint width, out uint height, out _, out _) == 0)
            return null;
        if (width < 32 || height < 32) return null;

        if (X11Native.XTranslateCoordinates(display.Handle, id, display.Root, 0, 0, out int x, out int y, out _) == 0)
        {
            x = 0; y = 0;
        }

        string title = GetStringProperty(display, id, display.AtomNetWmName)
                       ?? GetStringProperty(display, id, display.AtomWmName)
                       ?? "(untitled)";
        string cls = GetWmClass(display, id);
        int pid = GetPid(display, id);

        if (title == "GemPrismOverlay" || cls.Contains("GemPrismOverlay", StringComparison.Ordinal))
            return null;
        // XWayland/KWin internal windows have no PID/title/class; don't show them.
        if (pid == 0 && title == "(untitled)" && string.IsNullOrWhiteSpace(cls))
            return null;

        return new WindowInfo(id, title, cls, pid, x, y, (int)width, (int)height);
    }

    public static int GetPid(X11Display display, ulong id)
    {
        if (!TryGetProperty(display, id, display.AtomNetWmPid, 0, 1, out var prop, out var nitems, out var format))
            return 0;
        int pid = 0;
        if (format == 32 && nitems >= 1 && prop != IntPtr.Zero)
            pid = (int)(Marshal.ReadInt64(prop) & 0xffffffff);
        if (prop != IntPtr.Zero) X11Native.XFree(prop);
        return pid;
    }

    private static string? GetStringProperty(X11Display display, ulong id, ulong atom)
    {
        if (!TryGetProperty(display, id, atom, 0, 1024, out var prop, out var nitems, out var format))
            return null;
        if (nitems == 0 || prop == IntPtr.Zero)
        {
            if (prop != IntPtr.Zero) X11Native.XFree(prop);
            return null;
        }
        string? value = format switch
        {
            8 => Marshal.PtrToStringUTF8(prop, (int)nitems) ?? Marshal.PtrToStringAnsi(prop, (int)nitems),
            _ => null
        };
        X11Native.XFree(prop);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim('\0', ' ');
    }

    private static string GetWmClass(X11Display display, ulong id)
    {
        if (!TryGetProperty(display, id, display.AtomWmClass, 0, 256, out var prop, out var nitems, out var format))
            return string.Empty;
        string value = string.Empty;
        if (format == 8 && prop != IntPtr.Zero)
            value = Marshal.PtrToStringAnsi(prop, (int)nitems) ?? string.Empty;
        if (prop != IntPtr.Zero) X11Native.XFree(prop);
        var parts = value.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[^1] : string.Empty;
    }

    private static bool TryGetProperty(X11Display display, ulong id, ulong atom, long offset, long length,
        out IntPtr prop, out ulong nitems, out int format)
    {
        prop = IntPtr.Zero;
        nitems = 0;
        format = 0;
        int rc = X11Native.XGetWindowProperty(display.Handle, id, atom, offset, length, 0, 0,
            out _, out format, out nitems, out _, out prop);
        return rc == 0 && prop != IntPtr.Zero && nitems > 0;
    }
}