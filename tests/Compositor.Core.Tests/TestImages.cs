using Compositor.Rendering;
using SkiaSharp;

namespace Compositor.Core.Tests;

internal static class TestImages
{
    public static SKBitmap Solid(int width, int height, SKColor color)
    {
        var bitmap = Pixels.NewColor(width, height);
        bitmap.Erase(color);
        return bitmap;
    }

    /// <summary>A smooth two-axis gradient with no transparent pixels.</summary>
    public static SKBitmap Gradient(int width, int height)
    {
        var bitmap = Pixels.NewColor(width, height);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            bitmap.SetPixel(x, y, new SKColor((byte)(x * 255 / Math.Max(1, width - 1)), (byte)(y * 255 / Math.Max(1, height - 1)), 128));
        return bitmap;
    }

    public static void AssertColor(SKColor expected, SKColor actual, int tolerance = 2)
    {
        Assert.True(Math.Abs(expected.Red - actual.Red) <= tolerance && Math.Abs(expected.Green - actual.Green) <= tolerance
            && Math.Abs(expected.Blue - actual.Blue) <= tolerance && Math.Abs(expected.Alpha - actual.Alpha) <= tolerance,
            $"Expected {expected} but found {actual}");
    }
}
