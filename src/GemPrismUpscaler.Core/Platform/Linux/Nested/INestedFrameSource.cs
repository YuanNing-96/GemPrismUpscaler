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
    bool TryCapture(out NestedFrame frame);
}
