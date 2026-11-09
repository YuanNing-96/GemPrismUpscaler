using System.Runtime.InteropServices;

namespace GemPrismUpscaler.Core.Platform.Linux.X11;

/// <summary>Minimal Xlib/Xext/Xdamage P/Invoke surface used by the M0a slice.</summary>
internal static unsafe class X11Native
{
    public const string X11 = "libX11.so.6";
    public const string Xext = "libXext.so.6";
    public const string Xdamage = "libXdamage.so.1";
    public const string Xrandr = "libXrandr.so.2";
    public const string Libc = "libc.so.6";

    public const ulong AllPlanes = ulong.MaxValue;
    public const int ZPixmap = 2;
    public const int IsViewable = 2;
    public const int InputOutput = 1;
    public const int CopyFromParent = 0;

    public const long ButtonPressMask = 1L << 2;
    public const long ButtonReleaseMask = 1L << 3;
    public const long EnterWindowMask = 1L << 4;
    public const long LeaveWindowMask = 1L << 5;
    public const long PointerMotionMask = 1L << 6;
    public const long KeyPressMask = 1L << 0;
    public const long KeyReleaseMask = 1L << 1;
    public const long FocusChangeMask = 1L << 21;
    public const long ExposureMask = 1L << 15;
    public const long StructureNotifyMask = 1L << 17;
    public const long SubstructureNotifyMask = 1L << 19;
    public const long SubstructureRedirectMask = 1L << 20;

    public const long CWBackPixel = 1L << 1;
    public const long CWOverrideRedirect = 1L << 9;
    public const long CWEventMask = 1L << 11;

    public const uint ShiftMask = 1u << 0;
    public const uint LockMask = 1u << 1;
    public const uint ControlMask = 1u << 2;
    public const uint Mod1Mask = 1u << 3;
    public const uint Mod2Mask = 1u << 4;
    public const uint Mod4Mask = 1u << 6;
    public const uint ModifierMask = ShiftMask | ControlMask | Mod1Mask | Mod4Mask;
    public const uint IgnoreLockMasks = LockMask | Mod2Mask;

    public const int GrabModeAsync = 1;
    public const int InputHint = 1;
    public const int XDamageReportNonEmpty = 3;

    public const int RevertToParent = 2;
    public const int NotifyNormal = 0;
    public const int NotifyNonlinear = 3;
    public const int EnterNotify = 7;
    public const int LeaveNotify = 8;
    public const int FocusIn = 9;
    public const int FocusOut = 10;
    public const int Expose = 12;
    public const int MapNotify = 19;
    public const int ConfigureNotify = 22;
    public const int ClientMessage = 33;
    public const int ButtonPress = 4;
    public const int ButtonRelease = 5;
    public const int MotionNotify = 6;
    public const int KeyPress = 2;
    public const int KeyRelease = 3;
    public const ulong XA_ATOM = 4;
    public const ulong XA_CARDINAL = 6;
    public const ulong XA_WINDOW = 33;
    public const int PropModeReplace = 0;

    public const ulong XK_A = 0x0061;
    public const ulong XK_Escape = 0xFF1B;

    public const int IPC_PRIVATE = 0;
    public const int IPC_CREAT = 0x1000;
    public const int IPC_RMID = 0;
    public const int ShmMode0600 = 0x180;

    [StructLayout(LayoutKind.Sequential)]
    public struct XRectangle
    {
        public short X;
        public short Y;
        public ushort Width;
        public ushort Height;
    }

    /// <summary>XEvent is a 192-byte union; fields below cover key/button/motion.</summary>
    [StructLayout(LayoutKind.Sequential, Size = 192)]
    public struct XEvent
    {
        public int Type;
        public ulong Serial;
        public int SendEvent;
        public IntPtr Display;
        public ulong Window;
        public ulong Root;
        public ulong Subwindow;
        public ulong Time;
        public int X;
        public int Y;
        public int XRoot;
        public int YRoot;
        public uint State;
        public uint Detail; // keycode, button, or is_hint
        public int SameScreen;
    }

    [StructLayout(LayoutKind.Sequential, Size = 192)]
    public struct XClientMessageEvent
    {
        public int Type;
        public ulong Serial;
        public int SendEvent;
        public IntPtr Display;
        public ulong Window;
        public ulong MessageType;
        public int Format;
        public long Data0;
        public long Data1;
        public long Data2;
        public long Data3;
        public long Data4;
    }

    [StructLayout(LayoutKind.Sequential, Size = 192)]
    public struct XCrossingEvent
    {
        public int Type;
        public ulong Serial;
        public int SendEvent;
        public IntPtr Display;
        public ulong Window;
        public ulong Root;
        public ulong Subwindow;
        public ulong Time;
        public int X;
        public int Y;
        public int XRoot;
        public int YRoot;
        public int Mode;
        public int Detail;
        public int SameScreen;
        public int Focus;
        public uint State;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XWindowAttributes
    {
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public int BorderWidth;
        public int Depth;
        public IntPtr Visual;
        public ulong Root;
        public int Class;
        public int BitGravity;
        public int WinGravity;
        public int BackingStore;
        public ulong BackingPlanes;
        public ulong BackingPixel;
        public int SaveUnder;
        public IntPtr Colormap;
        public int MapInstalled;
        public int MapState;
        public long AllEventMasks;
        public long YourEventMask;
        public long DoNotPropagateMask;
        public int OverrideRedirect;
        public IntPtr Screen;
    }

    [StructLayout(LayoutKind.Sequential, Size = 192)]
    public struct XDamageNotifyEvent
    {
        public int Type;
        public ulong Serial;
        public int SendEvent;
        public IntPtr Display;
        public ulong Drawable;
        public ulong Damage;
        public int Level;
        public int More;
        public ulong Timestamp;
        public XRectangle Area;
        public XRectangle Geometry;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XShmSegmentInfo
    {
        public ulong ShmSeg;
        public int ShmId;
        public IntPtr ShmAddr;
        public int ReadOnly;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XImage
    {
        public int Width;
        public int Height;
        public int XOffset;
        public int Format;
        public IntPtr Data;
        public int ByteOrder;
        public int BitmapUnit;
        public int BitmapBitOrder;
        public int BitmapPad;
        public int Depth;
        public int BytesPerLine;
        public int BitsPerPixel;
        public ulong RedMask;
        public ulong GreenMask;
        public ulong BlueMask;
        public IntPtr Obdata;
    }

    /// <summary>
    /// XRRMonitorInfo from libXrandr 1.5. Layout on 64-bit:
    /// Atom name (8), two Bool (8), int noutput/x/y/width/height/mwidth/mheight
    /// (7*4 = 28), padding (4), RROutput* outputs (8) => 56 bytes.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct XRRMonitorInfo
    {
        public ulong Name;
        public int Primary;
        public int Automatic;
        public int NOutput;
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public int MWidth;
        public int MHeight;
        public IntPtr Outputs;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XSetWindowAttributes
    {
        public IntPtr BackgroundPixmap;
        public ulong BackgroundPixel;
        public IntPtr BorderPixmap;
        public ulong BorderPixel;
        public int BitGravity;
        public int WinGravity;
        public int BackingStore;
        public ulong BackingPlanes;
        public ulong BackingPixel;
        public int SaveUnder;
        public long EventMask;
        public long DoNotPropagateMask;
        public int OverrideRedirect;
        public IntPtr Colormap;
        public IntPtr Cursor;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XWMHints
    {
        public long Flags;
        public int Input;
        public int InitialState;
        public IntPtr IconPixmap;
        public ulong IconWindow;
        public IntPtr IconMask;
        public ulong WindowGroup;
    }

    public delegate int XErrorHandler(IntPtr display, IntPtr errorEvent);

    [DllImport(X11, EntryPoint = "XOpenDisplay")] public static extern IntPtr XOpenDisplay([MarshalAs(UnmanagedType.LPStr)] string? name);
    [DllImport(X11)] public static extern int XCloseDisplay(IntPtr display);
    [DllImport(X11)] public static extern int XDefaultScreen(IntPtr display);
    [DllImport(X11)] public static extern ulong XRootWindow(IntPtr display, int screen);
    [DllImport(X11)] public static extern int XDisplayWidth(IntPtr display, int screen);
    [DllImport(X11)] public static extern int XDisplayHeight(IntPtr display, int screen);
    [DllImport(X11)] public static extern IntPtr XDefaultVisual(IntPtr display, int screen);
    [DllImport(X11)] public static extern int XDefaultDepth(IntPtr display, int screen);
    [DllImport(X11)] public static extern ulong XInternAtom(IntPtr display, [MarshalAs(UnmanagedType.LPStr)] string name, int onlyIfExists);
    [DllImport(X11)] public static extern ulong XGetSelectionOwner(IntPtr display, ulong selection);
    [DllImport(X11)] public static extern int XGetWindowProperty(IntPtr display, ulong window, ulong property, long offset, long length,
        int delete, ulong reqType, out ulong actualType, out int actualFormat, out ulong nitems, out ulong bytesAfter, out IntPtr prop);
    [DllImport(X11)] public static extern int XFree(IntPtr data);
    [DllImport(X11)] public static extern int XGetGeometry(IntPtr display, ulong drawable, out ulong root, out int x, out int y,
        out uint width, out uint height, out uint borderWidth, out uint depth);
    [DllImport(X11)] public static extern int XGetWindowAttributes(IntPtr display, ulong window, out XWindowAttributes attributes);
    [DllImport(X11)] public static extern int XSetInputFocus(IntPtr display, ulong focus, int revertTo, ulong time);
    [DllImport(X11)] public static extern int XTranslateCoordinates(IntPtr display, ulong src, ulong dst, int srcX, int srcY,
        out int dstX, out int dstY, out ulong child);
    [DllImport(X11)] public static extern int XQueryTree(IntPtr display, ulong window, out ulong root, out ulong parent, out IntPtr children, out uint nchildren);
    [DllImport(X11)] public static extern ulong XCreateSimpleWindow(IntPtr display, ulong parent, int x, int y, uint width, uint height,
        uint borderWidth, ulong borderPixel, ulong backgroundPixel);
    [DllImport(X11)] public static extern int XChangeWindowAttributes(IntPtr display, ulong window, ulong valueMask, ref XSetWindowAttributes attributes);
    [DllImport(X11)] public static extern int XSelectInput(IntPtr display, ulong window, long eventMask);
    [DllImport(X11)] public static extern int XMapRaised(IntPtr display, ulong window);
    [DllImport(X11)] public static extern int XMapWindow(IntPtr display, ulong window);
    [DllImport(X11)] public static extern int XUnmapWindow(IntPtr display, ulong window);
    [DllImport(X11)] public static extern int XIconifyWindow(IntPtr display, ulong window, int screen);
    [DllImport(X11)] public static extern int XRaiseWindow(IntPtr display, ulong window);
    [DllImport(X11)] public static extern int XDestroyWindow(IntPtr display, ulong window);
    [DllImport(X11)] public static extern int XStoreName(IntPtr display, ulong window, [MarshalAs(UnmanagedType.LPStr)] string name);
    [DllImport(X11)] public static extern int XSetWMHints(IntPtr display, ulong window, ref XWMHints hints);
    [DllImport(X11)] public static extern int XChangeProperty(IntPtr display, ulong window, ulong property,
        ulong type, int format, int mode, ref ulong data, int nelements);
    [DllImport(X11)] public static extern int XChangeProperty(IntPtr display, ulong window, ulong property,
        ulong type, int format, int mode, byte[] data, int nelements);
    [DllImport(X11)] public static extern int XChangeProperty(IntPtr display, ulong window, ulong property,
        ulong type, int format, int mode, ulong[] data, int nelements);
    [DllImport(X11)] public static extern int XMoveResizeWindow(IntPtr display, ulong window,
        int x, int y, uint width, uint height);
    [DllImport(X11)] public static extern int XDeleteProperty(IntPtr display, ulong window, ulong property);
    [DllImport(X11)] public static extern int XSync(IntPtr display, int discard);
    [DllImport(X11)] public static extern int XWarpPointer(IntPtr display, ulong srcW, ulong destW,
        int srcX, int srcY, int srcWidth, int srcHeight, int destX, int destY);

    [DllImport(X11)] public static extern IntPtr XGetImage(IntPtr display, ulong drawable, int x, int y, uint width, uint height, ulong planeMask, int format);
    [DllImport(X11)] public static extern int XDestroyImage(IntPtr image);
    [DllImport(X11)] public static extern int XFlush(IntPtr display);
    [DllImport(X11)] public static extern int XPending(IntPtr display);
    [DllImport(X11)] public static extern int XNextEvent(IntPtr display, ref XEvent xevent);
    [DllImport(X11)] public static extern IntPtr XSetErrorHandler(XErrorHandler handler);
    [DllImport(X11)] public static extern IntPtr XGetAtomName(IntPtr display, ulong atom);
    [DllImport(X11)] public static extern int XSendEvent(IntPtr display, ulong window, int propagate, long eventMask, ref XEvent xevent);
    [DllImport(X11)] public static extern int XSendEvent(IntPtr display, ulong window, int propagate, long eventMask, ref XCrossingEvent xevent);
    [DllImport(X11)] public static extern int XSendEvent(IntPtr display, ulong window, int propagate, long eventMask, ref XClientMessageEvent xevent);
    [DllImport(X11)] public static extern int XGrabKey(IntPtr display, int keycode, uint modifiers, ulong grabWindow, int ownerEvents,
        int pointerMode, int keyboardMode);
    [DllImport(X11)] public static extern int XUngrabKey(IntPtr display, int keycode, uint modifiers, ulong grabWindow);
    [DllImport(X11)] public static extern int XKeysymToKeycode(IntPtr display, ulong keysym);
    [DllImport(X11)] public static extern ulong XStringToKeysym([MarshalAs(UnmanagedType.LPStr)] string name);
    [DllImport(X11)] public static extern int XQueryPointer(IntPtr display, ulong window, out ulong root, out ulong child,
        out int rootX, out int rootY, out int winX, out int winY, out uint mask);
    [DllImport(X11)] public static extern int XGetInputFocus(IntPtr display, out ulong focus, out int revertTo);
    [DllImport(X11)] public static extern int XClearWindow(IntPtr display, ulong window);

    [DllImport(Xext)] public static extern int XShmQueryExtension(IntPtr display);
    [DllImport(Xext)] public static extern IntPtr XShmCreateImage(IntPtr display, IntPtr visual, uint depth, int format, IntPtr data,
        IntPtr shminfo, uint width, uint height);
    [DllImport(Xext)] public static extern int XShmAttach(IntPtr display, IntPtr shminfo);
    [DllImport(Xext)] public static extern int XShmGetImage(IntPtr display, ulong drawable, IntPtr image, int x, int y, ulong planeMask);
    [DllImport(Xext)] public static extern int XShmDetach(IntPtr display, IntPtr shminfo);

    [DllImport(Xdamage)] public static extern int XDamageQueryExtension(IntPtr display, out int eventBase, out int errorBase);
    [DllImport(Xdamage)] public static extern ulong XDamageCreate(IntPtr display, ulong drawable, int level);
    [DllImport(Xdamage)] public static extern void XDamageDestroy(IntPtr display, ulong damage);
    [DllImport(Xdamage)] public static extern void XDamageSubtract(IntPtr display, ulong damage, ulong repair, ulong parts);

    [DllImport(Xrandr)] public static extern IntPtr XRRGetMonitors(IntPtr display, ulong window, int getActive, out int nmonitors);
    [DllImport(Xrandr)] public static extern void XRRFreeMonitors(IntPtr monitors);
    [DllImport(Xrandr)] public static extern int XRRSelectInput(IntPtr display, ulong window, int mask);

    [DllImport(Libc, EntryPoint = "shmget")] public static extern int shmget(int key, nuint size, int shmflg);
    [DllImport(Libc, EntryPoint = "shmat")] public static extern IntPtr shmat(int shmid, IntPtr shmaddr, int shmflg);
    [DllImport(Libc, EntryPoint = "shmdt")] public static extern int shmdt(IntPtr shmaddr);
    [DllImport(Libc, EntryPoint = "shmctl")] public static extern int shmctl(int shmid, int cmd, IntPtr buf);
}