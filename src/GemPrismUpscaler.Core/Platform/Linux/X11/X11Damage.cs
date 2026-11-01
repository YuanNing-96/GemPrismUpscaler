namespace GemPrismUpscaler.Core.Platform.Linux.X11;

public sealed class X11Damage : IDisposable
{
    private readonly X11Display _display;
    private readonly ulong _window;
    private ulong _damage;
    private bool _disposed;

    public int EventBase { get; }
    public bool Available => _damage != 0;

    public X11Damage(X11Display display, ulong window)
    {
        _display = display;
        _window = window;
        if (X11Native.XDamageQueryExtension(display.Handle, out int eventBase, out _) == 0)
        {
            EventBase = -1;
            return;
        }
        EventBase = eventBase;
        _damage = X11Native.XDamageCreate(display.Handle, window, X11Native.XDamageReportNonEmpty);
        display.Sync();
    }

    internal bool IsDamageEvent(in X11Native.XEvent ev) => Available && ev.Type == EventBase;

    public void Acknowledge()
    {
        if (!Available) return;
        X11Native.XDamageSubtract(_display.Handle, _damage, 0, 0);
        _display.Flush();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_damage != 0)
        {
            X11Native.XDamageDestroy(_display.Handle, _damage);
            _damage = 0;
            _display.Sync();
        }
    }
}
