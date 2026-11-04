using GemPrismUpscaler.Core.Geometry;

namespace GemPrismUpscaler.Core.Rendering;

/// <summary>
/// Result of comparing the current captured frame with the previous one.
///
/// The three rectangle lists have different jobs:
/// - UploadRects: changed source tiles; only these rows are copied to staging.
/// - ComputeRects: changed tiles expanded by the CNN halo; the effect chain
///   recomputes these regions.
/// - OutputRects: ComputeRects mapped to the overlay's content rectangle,
///   expanded by the Lanczos support. The presenter only shades these.
/// </summary>
public sealed class DirtyFrame
{
    public static readonly DirtyFrame Full = new(
        true, Array.Empty<RectI>(), Array.Empty<RectI>(), Array.Empty<RectI>(), 0, 0);

    public DirtyFrame(bool fullFrame, IReadOnlyList<RectI> uploadRects,
        IReadOnlyList<RectI> computeRects, IReadOnlyList<RectI> outputRects,
        int changedTiles, int totalTiles)
    {
        FullFrame = fullFrame;
        UploadRects = uploadRects;
        ComputeRects = computeRects;
        OutputRects = outputRects;
        ChangedTiles = changedTiles;
        TotalTiles = totalTiles;
    }

    public bool FullFrame { get; }
    public bool IsStatic => !FullFrame && UploadRects.Count == 0;
    public IReadOnlyList<RectI> UploadRects { get; }
    public IReadOnlyList<RectI> ComputeRects { get; }
    public IReadOnlyList<RectI> OutputRects { get; }
    public int ChangedTiles { get; }
    public int TotalTiles { get; }

    public static DirtyFrame Static(int totalTiles) => new(
        false, Array.Empty<RectI>(), Array.Empty<RectI>(), Array.Empty<RectI>(), 0, totalTiles);
}