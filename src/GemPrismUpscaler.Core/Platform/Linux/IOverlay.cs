using GemPrismUpscaler.Core.Geometry;

namespace GemPrismUpscaler.Core.Platform.Linux;

public enum OverlayBackend
{
    X11,
    Wayland
}

public enum OverlayPointerKind
{
    Motion,
    ButtonPress,
    ButtonRelease,
    WheelUp,
    WheelDown,
    /// <summary>Raw vertical axis value, carried in Button as value*1000.</summary>
    Axis,
    Enter,
    Leave
}

/// <summary>
/// Pointer event delivered by a native overlay. Coordinates are in the
/// overlay's physical pixel space, matching <see cref="IOverlay.Width"/> /
/// <see cref="IOverlay.Height"/>.
/// </summary>
public readonly record struct OverlayPointerEvent(OverlayPointerKind Kind, int X, int Y, int Button = 0);

public enum OverlayKeyState
{
    Pressed,
    Released,
    Repeated
}

/// <summary>Keyboard event from the presentation surface. KeyCode is evdev.</summary>
public readonly record struct OverlayKeyEvent(int KeyCode, OverlayKeyState State);

public readonly record struct OverlayModifiers(int Depressed, int Latched, int Locked, int Group);

/// <summary>
/// Full-screen presentation surface. X11 uses an override-redirect window;
/// Wayland uses a wlr-layer-shell surface on one output.
/// </summary>
public interface IOverlay : IDisposable
{
    bool IsVisible { get; }

    /// <summary>Output size in physical pixels.</summary>
    int Width { get; }
    int Height { get; }

    /// <summary>Output scale (1.0 for X11; fractional on Wayland).</summary>
    double Scale { get; }

    string OutputName { get; }

    bool IsWayland { get; }

    /// <summary>X11 window id, or 0 for a Wayland layer surface.</summary>
    IntPtr X11Window { get; }

    /// <summary>Native wl_display pointer for Vulkan WSI, or 0 for X11.</summary>
    IntPtr VulkanDisplay { get; }
    IntPtr VulkanSurface { get; }

    event Action<OverlayPointerEvent>? Pointer;

    /// <summary>XKB keymap bytes delivered by the compositor (Wayland only).</summary>
    event Action<byte[]>? Keymap;

    /// <summary>Keyboard events captured by the overlay (Wayland only).</summary>
    event Action<OverlayKeyEvent>? Key;

    event Action<OverlayModifiers>? Modifiers;

    void Show();
    void Hide();

    /// <summary>Dispatch native events. X11 is a no-op; Wayland pumps its queue.</summary>
    void Pump(int timeoutMs);

    /// <summary>
    /// Set the overlay input region from a physical-pixel rectangle. Null means
    /// an empty region (input passthrough). X11 ignores this because its input
    /// shape stays full and the engine forwards pointer events.
    /// </summary>
    void SetInputRegion(RectI? physicalRect);

    /// <summary>Wayland only: capture pointer events (forward) or leave them transparent.</summary>
    void SetPointerForwarding(bool enabled);

    /// <summary>Wayland only: request keyboard focus and capture key events.</summary>
    void SetKeyboardForwarding(bool enabled);
}
