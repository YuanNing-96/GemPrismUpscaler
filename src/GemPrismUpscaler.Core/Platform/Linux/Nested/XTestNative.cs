using System.Runtime.InteropServices;

namespace GemPrismUpscaler.Core.Platform.Linux.Nested;

internal static class XTestNative
{
    private const string Library = "libXtst.so.6";

    [DllImport(Library)]
    public static extern int XTestFakeMotionEvent(IntPtr display, int screen, int x, int y, ulong delay);

    [DllImport(Library)]
    public static extern int XTestFakeRelativeMotionEvent(IntPtr display, int dx, int dy, ulong delay);

    [DllImport(Library)]
    public static extern int XTestFakeButtonEvent(IntPtr display, uint button, int isPress, ulong delay);

    [DllImport(Library)]
    public static extern int XTestFakeKeyEvent(IntPtr display, uint keycode, int isPress, ulong delay);
}
