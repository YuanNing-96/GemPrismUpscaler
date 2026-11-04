using GemPrismUpscaler.Core.Engine;
using GemPrismUpscaler.Core.Geometry;
using GemPrismUpscaler.Core.Rendering;
using Xunit;

namespace GemPrismUpscaler.Tests;

public class M1bDirtyRectTests
{
    private const int Width = 128;
    private const int Height = 64;

    private static byte[] Frame(int width = Width, int height = Height, int padding = 0)
    {
        int stride = width * 4 + padding;
        var buffer = new byte[stride * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int offset = y * stride + x * 4;
                buffer[offset] = (byte)(x * 2);
                buffer[offset + 1] = (byte)(y * 3);
                buffer[offset + 2] = (byte)(x + y);
                buffer[offset + 3] = 255;
            }
        return buffer;
    }

    private static void SetPixel(byte[] buffer, int stride, int x, int y, byte value)
    {
        buffer[y * stride + x * 4] = value;
    }

    [Fact]
    public void FirstFrameIsFull()
    {
        var diff = new FrameDiff(32);
        var buffer = Frame();
        var dirty = diff.Update(buffer, Width, Height, Width * 4, new RectI(0, 0, 256, 128), 5);
        Assert.True(dirty.FullFrame);
        Assert.Equal(8, dirty.TotalTiles);
    }

    [Fact]
    public void IdenticalFrameIsStatic()
    {
        var diff = new FrameDiff(32);
        var buffer = Frame();
        diff.Update(buffer, Width, Height, Width * 4, new RectI(0, 0, 256, 128), 5);
        var dirty = diff.Update(buffer, Width, Height, Width * 4, new RectI(0, 0, 256, 128), 5);
        Assert.True(dirty.IsStatic);
        Assert.Empty(dirty.UploadRects);
    }

    [Fact]
    public void PaddingIsIgnoredByTheHash()
    {
        var diff = new FrameDiff(32);
        int stride = Width * 4 + 16;
        var buffer = Frame(padding: 16);
        diff.Update(buffer, Width, Height, stride, new RectI(0, 0, 256, 128), 5);

        for (int y = 0; y < Height; y++)
            for (int i = 0; i < 16; i++)
                buffer[y * stride + Width * 4 + i] = (byte)(i + y);

        var dirty = diff.Update(buffer, Width, Height, stride, new RectI(0, 0, 256, 128), 5);
        Assert.True(dirty.IsStatic);
    }

    [Fact]
    public void OneChangedTileProducesBoundedUploadAndHalo()
    {
        var diff = new FrameDiff(32);
        var buffer = Frame();
        int stride = Width * 4;
        diff.Update(buffer, Width, Height, stride, new RectI(0, 0, 256, 128), 5);

        SetPixel(buffer, stride, 10, 10, 0);
        var dirty = diff.Update(buffer, Width, Height, stride, new RectI(0, 0, 256, 128), 5);

        Assert.False(dirty.FullFrame);
        Assert.Equal(1, dirty.ChangedTiles);
        var upload = Assert.Single(dirty.UploadRects);
        Assert.Equal(new RectI(0, 0, 32, 32), upload);
        var compute = Assert.Single(dirty.ComputeRects);
        Assert.Equal(new RectI(0, 0, 37, 37), compute);
        var output = Assert.Single(dirty.OutputRects);
        Assert.True(output.X >= 0 && output.Y >= 0);
        Assert.True(output.Right <= 256 && output.Bottom <= 128);
    }

    [Fact]
    public void WholeFrameChangeFallsBackToFull()
    {
        var diff = new FrameDiff(32);
        var buffer = Frame();
        diff.Update(buffer, Width, Height, Width * 4, new RectI(0, 0, 256, 128), 5);

        for (int i = 0; i < buffer.Length; i += 4) buffer[i] ^= 0xFF;
        var dirty = diff.Update(buffer, Width, Height, Width * 4, new RectI(0, 0, 256, 128), 5);
        Assert.True(dirty.FullFrame);
    }

    [Fact]
    public void SizeChangeResetsToFull()
    {
        var diff = new FrameDiff(32);
        var first = Frame();
        diff.Update(first, Width, Height, Width * 4, new RectI(0, 0, 256, 128), 5);

        int width = 64;
        int height = 64;
        var second = Frame(width, height);
        var dirty = diff.Update(second, width, height, width * 4, new RectI(0, 0, 128, 128), 5);
        Assert.True(dirty.FullFrame);
    }

    [Fact]
    public void OutputMappingStaysInsideTheContentRectangle()
    {
        var diff = new FrameDiff(32);
        var buffer = Frame();
        var dest = new RectI(10, 20, 200, 100);
        diff.Update(buffer, Width, Height, Width * 4, dest, 5);

        SetPixel(buffer, Width * 4, 100, 50, 0);
        var dirty = diff.Update(buffer, Width, Height, Width * 4, dest, 5);

        Assert.NotEmpty(dirty.OutputRects);
        foreach (var rect in dirty.OutputRects)
        {
            Assert.True(rect.X >= dest.X && rect.Y >= dest.Y);
            Assert.True(rect.Right <= dest.Right && rect.Bottom <= dest.Bottom);
        }
    }

    [Fact]
    public void FullFrameSentinelIsNotStatic()
    {
        Assert.True(DirtyFrame.Full.FullFrame);
        Assert.False(DirtyFrame.Full.IsStatic);
        Assert.True(DirtyFrame.Static(8).IsStatic);
    }
}