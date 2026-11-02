using GemPrismUpscaler.Core.Geometry;

namespace GemPrismUpscaler.Core.Platform.Linux.X11;

/// <summary>
/// Forwards pointer input from the full-screen overlay back to the source window.
/// XSendEvent is used instead of XTest so no KWin/EIS permission dialog appears.
///
/// Every transition into/out of the visible source rectangle also sends a
/// synthetic EnterNotify/LeaveNotify: many game engines (and toolkits) only keep
/// hover state while they believe the pointer is inside their window, and the
/// overlay map causes the real pointer to leave the source window.
/// </summary>
public sealed class X11Input
{
    private readonly X11Display _display;
    private readonly ulong _source;
    private uint _buttonState;
    private bool _inside;
    private int _lastSourceX;
    private int _lastSourceY;

    public X11Input(X11Display display, ulong source)
    {
        _display = display;
        _source = source;
    }

    public void ForwardMotion(int overlayX, int overlayY, CoordinateMapper mapper)
    {
        if (!mapper.TryMap(overlayX, overlayY, out int sx, out int sy))
        {
            Leave();
            return;
        }

        Enter(sx, sy);
        SendMotion(sx, sy);
    }

    public void ForwardButton(int overlayX, int overlayY, CoordinateMapper mapper, uint button, bool pressed)
    {
        uint bit = button switch
        {
            1 => 1u << 8,
            2 => 1u << 9,
            3 => 1u << 10,
            4 => 0,
            5 => 0,
            _ => 0
        };

        int sx, sy;
        if (mapper.TryMap(overlayX, overlayY, out sx, out sy))
        {
            // Position first: some engines only update their cursor on motion.
            Enter(sx, sy);
            SendMotion(sx, sy);
        }
        else if (pressed)
        {
            return;
        }
        else
        {
            // Drag that ended outside the content rect: still deliver the release,
            // otherwise the game keeps a stuck button.
            if ((_buttonState & bit) == 0) return;
            sx = _lastSourceX;
            sy = _lastSourceY;
        }

        uint stateBefore = CurrentState();
        var ev = NewEvent();
        ev.Type = pressed ? 4 : 5; // ButtonPress / ButtonRelease
        ev.Detail = button;
        ev.State = stateBefore;
        SetPosition(ref ev, sx, sy);
        X11Native.XSendEvent(_display.Handle, _source, 0,
            pressed ? X11Native.ButtonPressMask : X11Native.ButtonReleaseMask, ref ev);

        if (pressed) _buttonState |= bit;
        else _buttonState &= ~bit;

        _display.Flush();
    }

    public void ForwardWheel(int overlayX, int overlayY, CoordinateMapper mapper, bool up)
    {
        uint button = up ? 4u : 5u;
        ForwardButton(overlayX, overlayY, mapper, button, true);
        ForwardButton(overlayX, overlayY, mapper, button, false);
    }

    /// <summary>Called when the overlay is hidden; tells the source the pointer left.</summary>
    public void Reset()
    {
        Leave();
        _buttonState = 0;
    }

    private void Enter(int sx, int sy)
    {
        if (_inside) return;
        SendCrossing(enter: true, sx, sy);
        _inside = true;
    }

    private void Leave()
    {
        if (!_inside) return;
        _inside = false;
        SendCrossing(enter: false, 0, 0);
    }

    private void SendCrossing(bool enter, int sx, int sy)
    {
        var ev = new X11Native.XCrossingEvent
        {
            Type = enter ? X11Native.EnterNotify : X11Native.LeaveNotify,
            SendEvent = 1,
            Display = _display.Handle,
            Window = _source,
            Root = _display.Root,
            Subwindow = 0,
            Time = 0,
            X = sx,
            Y = sy,
            Mode = X11Native.NotifyNormal,
            Detail = X11Native.NotifyNonlinear,
            SameScreen = 1,
            Focus = X11WindowProbe.IsFocused(_display, _source) ? 1 : 0,
            State = CurrentState()
        };

        if (X11Native.XTranslateCoordinates(_display.Handle, _source, _display.Root, 0, 0,
                out int ox, out int oy, out _) != 0)
        {
            ev.XRoot = ox + sx;
            ev.YRoot = oy + sy;
        }
        else
        {
            ev.XRoot = sx;
            ev.YRoot = sy;
        }

        X11Native.XSendEvent(_display.Handle, _source, 0,
            enter ? X11Native.EnterWindowMask : X11Native.LeaveWindowMask, ref ev);
        _display.Flush();
    }

    private void SendMotion(int sx, int sy)
    {
        _lastSourceX = sx;
        _lastSourceY = sy;
        var ev = NewEvent();
        ev.Type = 6; // MotionNotify
        ev.Detail = 0;
        ev.State = CurrentState();
        SetPosition(ref ev, sx, sy);
        X11Native.XSendEvent(_display.Handle, _source, 0, X11Native.PointerMotionMask, ref ev);
        _display.Flush();
    }

    private X11Native.XEvent NewEvent() => new()
    {
        Display = _display.Handle,
        Window = _source,
        Root = _display.Root,
        Subwindow = 0,
        Time = 0,
        SendEvent = 1,
        SameScreen = 1
    };

    private void SetPosition(ref X11Native.XEvent ev, int sx, int sy)
    {
        ev.X = sx;
        ev.Y = sy;
        if (X11Native.XTranslateCoordinates(_display.Handle, _source, _display.Root, 0, 0,
                out int ox, out int oy, out _) != 0)
        {
            ev.XRoot = ox + sx;
            ev.YRoot = oy + sy;
        }
        else
        {
            ev.XRoot = sx;
            ev.YRoot = sy;
        }
    }

    private uint CurrentState()
    {
        uint state = _buttonState;
        if (X11Native.XQueryPointer(_display.Handle, _display.Root, out _, out _,
                out _, out _, out _, out _, out uint mask) != 0)
        {
            state |= mask & (X11Native.ShiftMask | X11Native.ControlMask | X11Native.Mod1Mask | X11Native.Mod4Mask);
        }
        return state;
    }
}
