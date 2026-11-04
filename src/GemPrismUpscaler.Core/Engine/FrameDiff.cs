using System.Numerics;
using System.Runtime.InteropServices;
using GemPrismUpscaler.Core.Geometry;
using GemPrismUpscaler.Core.Rendering;

namespace GemPrismUpscaler.Core.Engine;

/// <summary>
/// 64-pixel tile hashing for the dirty-rectangle path. XDamage on this stack
/// reports the whole window even for a small repaint, so the rectangle has to
/// come from the pixels themselves.
///
/// A tile is uploaded only when its hash changes; the CNN recomputes an area
/// expanded by the variant's receptive-field halo; the presenter maps that area
/// to the content rectangle and adds the Lanczos support. When too much of the
/// frame changes the diff falls back to a full frame, which also keeps fully
/// animated scenes at the M1a cost plus the hash.
/// </summary>
internal sealed class FrameDiff
{
    private readonly int _tileSize;
    private readonly double _fullFrameRatio;
    private ulong[] _hashes = Array.Empty<ulong>();
    private int _gridX;
    private int _gridY;
    private int _width;
    private int _height;
    private bool _hasPrevious;

    public FrameDiff(int tileSize = 64, double fullFrameRatio = 0.5)
    {
        if (tileSize < 8) throw new ArgumentOutOfRangeException(nameof(tileSize));
        if (fullFrameRatio is <= 0 or > 1) throw new ArgumentOutOfRangeException(nameof(fullFrameRatio));
        _tileSize = tileSize;
        _fullFrameRatio = fullFrameRatio;
    }

    public int TileSize => _tileSize;

    /// <summary>Forces the next update to be a full frame (show/resize/monitor change).</summary>
    public void Reset() => _hasPrevious = false;

    public DirtyFrame Update(ReadOnlySpan<byte> pixels, int width, int height, int stride,
        RectI dest, int halo)
    {
        if (width <= 0 || height <= 0 || stride < width * 4)
            throw new ArgumentOutOfRangeException(nameof(width), "invalid frame geometry");
        if (pixels.Length < stride * height)
            throw new ArgumentException("frame buffer too small", nameof(pixels));

        int gridX = (width + _tileSize - 1) / _tileSize;
        int gridY = (height + _tileSize - 1) / _tileSize;
        if (width != _width || height != _height || gridX != _gridX || gridY != _gridY)
        {
            _width = width;
            _height = height;
            _gridX = gridX;
            _gridY = gridY;
            _hashes = new ulong[gridX * gridY];
            _hasPrevious = false;
        }

        var changed = new List<RectI>();
        long changedArea = 0;
        int index = 0;
        for (int ty = 0; ty < gridY; ty++)
        {
            int y = ty * _tileSize;
            int tileHeight = Math.Min(_tileSize, height - y);
            for (int tx = 0; tx < gridX; tx++)
            {
                int x = tx * _tileSize;
                int tileWidth = Math.Min(_tileSize, width - x);
                ulong hash = HashTile(pixels, stride, x, y, tileWidth, tileHeight);
                if (_hasPrevious && _hashes[index] != hash)
                {
                    changed.Add(new RectI(x, y, tileWidth, tileHeight));
                    changedArea += (long)tileWidth * tileHeight;
                }
                _hashes[index] = hash;
                index++;
            }
        }

        bool firstFrame = !_hasPrevious;
        _hasPrevious = true;
        if (firstFrame)
            return new DirtyFrame(true, Array.Empty<RectI>(), Array.Empty<RectI>(),
                Array.Empty<RectI>(), 0, index);
        if (changed.Count == 0) return DirtyFrame.Static(index);

        if (changedArea >= (long)(width * (long)height * _fullFrameRatio))
            return new DirtyFrame(true, Array.Empty<RectI>(), Array.Empty<RectI>(),
                Array.Empty<RectI>(), changed.Count, index);

        var upload = MergeRowRuns(changed);
        var compute = ExpandAndMerge(upload, Math.Max(0, halo), width, height);
        var output = MapToOutput(compute, dest, width, height, 2);
        return new DirtyFrame(false, upload, compute, output, changed.Count, index);
    }

    private static ulong HashTile(ReadOnlySpan<byte> pixels, int stride, int x, int y, int width, int height)
    {
        const ulong prime1 = 0x9E3779B185EBCA87UL;
        const ulong prime2 = 0xC2B2AE3D27D4EB4FUL;
        ulong hash = 0x9E3779B97F4A7C15UL;
        int rowBytes = width * 4;

        for (int row = 0; row < height; row++)
        {
            var line = pixels.Slice((y + row) * stride + x * 4, rowBytes);
            int wholeWords = line.Length & ~7;
            var words = MemoryMarshal.Cast<byte, ulong>(line[..wholeWords]);
            int wordCount = 0;
            foreach (ulong word in words)
            {
                hash ^= word;
                hash *= prime1;
                hash = BitOperations.RotateLeft(hash, 31);
                hash *= prime2;
                wordCount++;
            }

            for (int i = wholeWords; i < line.Length; i++)
            {
                hash ^= line[i];
                hash *= 0x100000001B3UL;
            }

            // Include the row's word count so a shorter edge tile cannot alias.
            hash ^= (ulong)wordCount;
        }

        hash ^= (ulong)((width << 16) | height);
        hash ^= hash >> 33;
        hash *= 0xFF51AFD7ED558CCDUL;
        hash ^= hash >> 33;
        return hash;
    }

    private static List<RectI> MergeRowRuns(List<RectI> tiles)
    {
        var result = new List<RectI>();
        int i = 0;
        while (i < tiles.Count)
        {
            RectI start = tiles[i];
            int right = start.Right;
            int y = start.Y;
            int height = start.Height;
            i++;
            while (i < tiles.Count && tiles[i].Y == y && tiles[i].X <= right)
            {
                right = Math.Max(right, tiles[i].Right);
                i++;
            }
            result.Add(new RectI(start.X, y, right - start.X, height));
        }
        return result;
    }

    private static List<RectI> ExpandAndMerge(List<RectI> rects, int halo, int width, int height)
    {
        if (halo == 0) return Merge(rects);
        var expanded = new List<RectI>(rects.Count);
        foreach (var rect in rects)
        {
            int left = Math.Clamp(rect.X - halo, 0, width);
            int top = Math.Clamp(rect.Y - halo, 0, height);
            int right = Math.Clamp(rect.Right + halo, 0, width);
            int bottom = Math.Clamp(rect.Bottom + halo, 0, height);
            if (right > left && bottom > top)
                expanded.Add(new RectI(left, top, right - left, bottom - top));
        }
        return Merge(expanded);
    }

    private static List<RectI> MapToOutput(List<RectI> sourceRects, RectI dest,
        int sourceWidth, int sourceHeight, int padding)
    {
        var mapped = new List<RectI>(sourceRects.Count);
        foreach (var rect in sourceRects)
        {
            int left = dest.X + (int)Math.Floor((double)rect.X * dest.Width / sourceWidth) - padding;
            int top = dest.Y + (int)Math.Floor((double)rect.Y * dest.Height / sourceHeight) - padding;
            int right = dest.X + (int)Math.Ceiling((double)rect.Right * dest.Width / sourceWidth) + padding;
            int bottom = dest.Y + (int)Math.Ceiling((double)rect.Bottom * dest.Height / sourceHeight) + padding;
            left = Math.Clamp(left, dest.X, dest.Right);
            top = Math.Clamp(top, dest.Y, dest.Bottom);
            right = Math.Clamp(right, dest.X, dest.Right);
            bottom = Math.Clamp(bottom, dest.Y, dest.Bottom);
            if (right > left && bottom > top)
                mapped.Add(new RectI(left, top, right - left, bottom - top));
        }
        return Merge(mapped);
    }

    private static List<RectI> Merge(List<RectI> rects)
    {
        if (rects.Count <= 1) return rects;
        rects.Sort(static (a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));

        var merged = new List<RectI>(rects.Count);
        foreach (var rect in rects)
        {
            bool absorbed = false;
            for (int i = 0; i < merged.Count; i++)
            {
                if (IntersectsOrTouches(merged[i], rect))
                {
                    merged[i] = Union(merged[i], rect);
                    absorbed = true;
                    // The union may now touch an earlier rectangle; merge again.
                    for (int j = i + 1; j < merged.Count;)
                    {
                        if (IntersectsOrTouches(merged[i], merged[j]))
                        {
                            merged[i] = Union(merged[i], merged[j]);
                            merged.RemoveAt(j);
                        }
                        else j++;
                    }
                    break;
                }
            }
            if (!absorbed) merged.Add(rect);
        }
        return merged;
    }

    private static bool IntersectsOrTouches(RectI a, RectI b) =>
        a.X <= b.Right && b.X <= a.Right && a.Y <= b.Bottom && b.Y <= a.Bottom;

    private static RectI Union(RectI a, RectI b)
    {
        int left = Math.Min(a.X, b.X);
        int top = Math.Min(a.Y, b.Y);
        int right = Math.Max(a.Right, b.Right);
        int bottom = Math.Max(a.Bottom, b.Bottom);
        return new RectI(left, top, right - left, bottom - top);
    }
}