using Composa.Rendering;
using SkiaSharp;

namespace Composa.Selections;

public static class MagicWand
{
    /// <summary>
    /// Selects pixels whose color is within <paramref name="tolerance"/> (0–255, the largest channel difference)
    /// of the pixel at the seed, either connected to it or anywhere in the image.
    /// </summary>
    public static unsafe SKBitmap Select(SKBitmap source, int seedX, int seedY, int tolerance, bool contiguous, bool smooth = true)
    {
        int width = source.Width, height = source.Height;
        var result = Pixels.NewMask(width, height);
        if (seedX < 0 || seedY < 0 || seedX >= width || seedY >= height) return result;
        var src = (byte*)source.GetPixels();
        var dst = (byte*)result.GetPixels();
        int srcStride = source.RowBytes, dstStride = result.RowBytes;
        var seed = src + (long)seedY * srcStride + seedX * 4;
        int sr = seed[0], sg = seed[1], sb = seed[2], sa = seed[3];

        bool Matches(byte* p) =>
            Math.Abs(p[0] - sr) <= tolerance && Math.Abs(p[1] - sg) <= tolerance
            && Math.Abs(p[2] - sb) <= tolerance && Math.Abs(p[3] - sa) <= tolerance;

        if (!contiguous)
        {
            for (var y = 0; y < height; y++)
            {
                byte* s = src + (long)y * srcStride, d = dst + (long)y * dstStride;
                for (var x = 0; x < width; x++, s += 4) if (Matches(s)) d[x] = 255;
            }
        }
        else
        {
            var stack = new Stack<(int X, int Y)>();
            stack.Push((seedX, seedY));
            while (stack.Count > 0)
            {
                var (x, y) = stack.Pop();
                byte* s = src + (long)y * srcStride, d = dst + (long)y * dstStride;
                if (d[x] != 0 || !Matches(s + x * 4)) continue;
                int left = x, right = x;
                while (left > 0 && d[left - 1] == 0 && Matches(s + (left - 1) * 4)) left--;
                while (right < width - 1 && d[right + 1] == 0 && Matches(s + (right + 1) * 4)) right++;
                for (var i = left; i <= right; i++) d[i] = 255;
                for (var ny = y - 1; ny <= y + 1; ny += 2)
                {
                    if (ny < 0 || ny >= height) continue;
                    byte* ns = src + (long)ny * srcStride, nd = dst + (long)ny * dstStride;
                    var inRun = false;
                    for (var i = left; i <= right; i++)
                    {
                        var open = nd[i] == 0 && Matches(ns + i * 4);
                        if (open && !inRun) stack.Push((i, ny));
                        inRun = open;
                    }
                }
            }
        }
        if (!smooth) return result;
        // A half-pixel blur softens the staircase the hard threshold leaves along diagonal edges.
        using var hard = result;
        return SelectionMask.Feather(hard, 1f);
    }
}
