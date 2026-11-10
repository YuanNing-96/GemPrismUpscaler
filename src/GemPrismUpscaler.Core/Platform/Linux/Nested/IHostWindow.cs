using GemPrismUpscaler.Core.Platform.Linux;
using GemPrismUpscaler.Core.Rendering;

namespace GemPrismUpscaler.Core.Platform.Linux.Nested;

/// <summary>
/// The KDE-facing window that presents one nested game window. Two backends
/// implement it: <see cref="NestedHostWindow"/> (X11, runs under XWayland) and
/// <see cref="WaylandHostWindow"/> (native xdg_toplevel). The Wayland backend
/// exists because a native surface is the only way to use
/// <c>wp_pointer_warp_v1</c>, which lets the game move KWin's visible cursor
/// the same way it does when it runs directly on the desktop.
///
/// Width/Height are the presentation pixel size (the Vulkan swapchain extent),
/// not the window's logical size. On a fractionally scaled Wayland output the
/// two differ by the output scale.
/// </summary>
public interface IHostWindow : IDisposable
{
    bool IsVisible { get; }
    bool IsFocused { get; }
    bool IsFullscreen { get; }

    /// <summary>True when this window is a native Wayland surface.</summary>
    bool IsWayland { get; }

    int Width { get; }
    int Height { get; }
    string MonitorName { get; }

    /// <summary>Native wl_display/wl_surface for Vulkan WSI, 0 for X11.</summary>
    IntPtr VulkanDisplay { get; }
    IntPtr VulkanSurface { get; }

    event Action<OverlayPointerEvent>? Pointer;
    event Action<OverlayKeyEvent>? Key;
    event Action<OverlayModifiers>? Modifiers;
    event Action<bool>? FocusChanged;

    /// <summary>
    /// The compositor asked us to repaint (newly mapped, uncovered, resized).
    /// A Vulkan swapchain keeps no reusable front buffer, so the session must
    /// re-present the last frame; otherwise a static game stays black.
    /// </summary>
    event Action? Exposed;

    /// <summary>KWin showed the host again after a game-side minimize.</summary>
    event Action? Restored;

    event Action? Closed;
    event Action? CloseRequested;

    void Show();
    void Hide();
    void Focus();
    void Activate();
    void Raise();
    void Iconify();
    void RestoreFromIconify();

    void SetFullscreen(bool fullscreen);
    void SetOriginalSize(int width, int height);
    void SetTitle(string title);

    /// <summary>Dispatch native events. Must run on the session render thread.</summary>
    void Pump();

    /// <summary>
    /// Move the visible desktop cursor onto this window at a window-local
    /// presentation pixel. Returns false when the backend cannot move the
    /// compositor's cursor (X11 under XWayland warps only its shadow pointer).
    /// </summary>
    bool WarpPointer(int x, int y);

    /// <summary>Create the presenter for this window's current size.</summary>
    VulkanBlitter CreateBlitter(EffectDefinition effect, bool sourceRgb);
}