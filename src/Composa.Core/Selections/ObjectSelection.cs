using Composa.Filters;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Selections;

/// <summary>
/// Selects the object under a clicked point. The macOS app asks Apple's Vision framework for a foreground instance
/// mask; here the backdrop is what touches the image's edges in a near-uniform color, and an object is a connected
/// piece of everything else. It suits product shots and portraits on plain backdrops.
/// </summary>
public static class ObjectSelection
{
    /// <summary>The whole foreground: everything that is not the border-connected backdrop. Null when there is none.</summary>
    public static SKBitmap? Subject(SKBitmap source)
    {
        using var backdrop = ImageFilters.Backdrop(source, 40);
        var subject = SelectionMask.Invert(backdrop);
        // Transparent pixels are backdrop too, whatever their color.
        RemoveTransparent(source, subject);
        if (SelectionMask.IsEmpty(subject)) { subject.Dispose(); return null; }
        using var hard = subject;
        return SelectionMask.Feather(hard, 1f);
    }

    /// <summary>
    /// The outline of the object at a point: the connected foreground piece around it, tightened or loosened by
    /// <paramref name="edgeOffset"/> pixels. Null when the point is on the backdrop or outside the image.
    /// </summary>
    public static unsafe SKBitmap? Select(SKBitmap source, int x, int y, int edgeOffset, bool smooth = true)
    {
        if (x < 0 || y < 0 || x >= source.Width || y >= source.Height) return null;
        using var backdrop = ImageFilters.Backdrop(source, 40);
        var bg = (byte*)backdrop.GetPixels();
        if (bg[(long)y * backdrop.RowBytes + x] != 0 || ((byte*)source.GetPixels())[(long)y * source.RowBytes + x * 4 + 3] < 8) return null;
        using var foreground = SelectionMask.Invert(backdrop);
        RemoveTransparent(source, foreground);
        // The connected piece of foreground the click landed on.
        using var piece = ConnectedPiece(foreground, x, y);
        var result = piece;
        var steps = Math.Min(10, Math.Abs(edgeOffset));
        if (steps > 0)
        {
            var adjusted = edgeOffset > 0 ? SelectionMask.Contract(piece, steps) : SelectionMask.Expand(piece, steps);
            if (adjusted == null) return null;
            result = adjusted;
        }
        if (!smooth) return ReferenceEquals(result, piece) ? Pixels.Clone(result) : result;
        // Rounds off the one-pixel stair steps of a traced mask.
        var soft = SelectionMask.Feather(result, 2f);
        if (!ReferenceEquals(result, piece)) result.Dispose();
        return soft;
    }

    /// <summary>The 4-connected run of set mask pixels around a seed, as a new mask.</summary>
    private static unsafe SKBitmap ConnectedPiece(SKBitmap mask, int seedX, int seedY)
    {
        int width = mask.Width, height = mask.Height;
        var result = Pixels.NewMask(width, height);
        byte* src = (byte*)mask.GetPixels(), dst = (byte*)result.GetPixels();
        int srcStride = mask.RowBytes, dstStride = result.RowBytes;
        bool Set(int px, int py) => src[(long)py * srcStride + px] >= 128;
        if (!Set(seedX, seedY)) return result;
        var stack = new Stack<(int X, int Y)>();
        stack.Push((seedX, seedY));
        while (stack.Count > 0)
        {
            var (x, y) = stack.Pop();
            var d = dst + (long)y * dstStride;
            if (d[x] != 0 || !Set(x, y)) continue;
            int left = x, right = x;
            while (left > 0 && d[left - 1] == 0 && Set(left - 1, y)) left--;
            while (right < width - 1 && d[right + 1] == 0 && Set(right + 1, y)) right++;
            for (var i = left; i <= right; i++) d[i] = 255;
            for (var ny = y - 1; ny <= y + 1; ny += 2)
            {
                if (ny < 0 || ny >= height) continue;
                var nd = dst + (long)ny * dstStride;
                var inRun = false;
                for (var i = left; i <= right; i++)
                {
                    var open = nd[i] == 0 && Set(i, ny);
                    if (open && !inRun) stack.Push((i, ny));
                    inRun = open;
                }
            }
        }
        return result;
    }

    private static unsafe void RemoveTransparent(SKBitmap source, SKBitmap mask)
    {
        byte* s = (byte*)source.GetPixels(), m = (byte*)mask.GetPixels();
        for (var y = 0; y < source.Height; y++)
        for (var x = 0; x < source.Width; x++)
            if (s[(long)y * source.RowBytes + x * 4 + 3] < 8) m[(long)y * mask.RowBytes + x] = 0;
    }
}
