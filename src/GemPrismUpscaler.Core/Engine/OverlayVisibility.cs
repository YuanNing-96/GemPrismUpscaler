namespace GemPrismUpscaler.Core.Engine;

/// <summary>
/// Why the full-screen overlay is currently hidden. Kept as a small pure state
/// object so the visibility rules can be unit tested without X11.
/// </summary>
public enum OverlayHideReason
{
    None = 0,
    User = 1,
    SourceUnmapped = 2,
    Popup = 3,
    FocusLost = 4
}

public sealed class OverlayVisibility
{
    /// <summary>Alt+Shift+A toggle.</summary>
    public bool UserHidden { get; set; }

    /// <summary>Source window is minimized, unmapped, or EWMH-hidden.</summary>
    public bool SourceSuspended { get; set; }

    /// <summary>Another top-level window of the same process is visible (save/exit dialog).</summary>
    public bool PopupHidden { get; set; }

    /// <summary>The source process no longer owns the input focus.</summary>
    public bool FocusLost { get; set; }

    public bool IsVisible => HideReason == OverlayHideReason.None;

    public OverlayHideReason HideReason
    {
        get
        {
            if (UserHidden) return OverlayHideReason.User;
            if (SourceSuspended) return OverlayHideReason.SourceUnmapped;
            if (PopupHidden) return OverlayHideReason.Popup;
            if (FocusLost) return OverlayHideReason.FocusLost;
            return OverlayHideReason.None;
        }
    }

    public string ReasonText => HideReason switch
    {
        OverlayHideReason.User => "user",
        OverlayHideReason.SourceUnmapped => "source not viewable",
        OverlayHideReason.Popup => "popup window",
        OverlayHideReason.FocusLost => "source lost focus",
        _ => "none"
    };

    public void Reset()
    {
        UserHidden = false;
        SourceSuspended = false;
        PopupHidden = false;
        FocusLost = false;
    }
}
