namespace GemPrismUpscaler.Core.Platform.Linux.Nested;

/// <summary>
/// One window's frame source. Implemented by wlr-screencopy capture (whole
/// output, cropped per window) and by per-toplevel ext-image-copy-capture.
/// </summary>
public interface INestedFrameSource : IDisposable
{
    int Width { get; }
    int Height { get; }
    int Stride { get; }
    bool SourceIsRgb { get; }
    ReadOnlySpan<byte> Pixels { get; }

    /// <summary>
    /// True once at least one frame has been captured; <see cref="Pixels"/>
    /// then holds that frame, which lets a freshly created host window show the
    /// current game image without waiting for the client to repaint.
    /// </summary>
    bool HasFrame { get; }

    /// <summary>
    /// The last successfully converted frame. Its size may differ from
    /// <see cref="Width"/>/<see cref="Height"/> while the compositor is
    /// renegotiating buffer constraints, so callers must use these dimensions
    /// and not the current ones.
    /// </summary>
    bool TryGetLastFrame(out NestedFrame frame);

    bool TryCapture(out NestedFrame frame);
}
