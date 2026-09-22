using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Selections;

public enum SelectionMode { Replace, Add, Subtract, Intersect }

/// <summary>Operations on selections: document-sized Alpha8 coverage bitmaps, never modified in place.</summary>
public static class SelectionMask
{
    public static SKBitmap FromPath(int width, int height, SKPath path, bool antialias = true, float feather = 0)
    {
        var mask = Pixels.NewMask(width, height);
        using var canvas = new SKCanvas(mask);
        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = antialias, Style = SKPaintStyle.Fill };
        if (feather > 0) paint.ImageFilter = SKImageFilter.CreateBlur(feather, feather);
        canvas.DrawPath(path, paint);
        return mask;
    }

    public static SKBitmap FromRect(int width, int height, SKRect rect, float feather = 0)
    {
        using var path = new SKPath();
        path.AddRect(rect);
        return FromPath(width, height, path, antialias: false, feather);
    }

    public static SKBitmap FromEllipse(int width, int height, SKRect rect, float feather = 0)
    {
        using var path = new SKPath();
        path.AddOval(rect);
        return FromPath(width, height, path, antialias: true, feather);
    }

    public static SKBitmap FromPolygon(int width, int height, IReadOnlyList<SKPoint> points, float feather = 0)
    {
        using var path = new SKPath { FillType = SKPathFillType.EvenOdd };
        if (points.Count > 2) path.AddPoly(points.ToArray(), close: true);
        return FromPath(width, height, path, antialias: true, feather);
    }

    public static SKBitmap All(int width, int height) => Pixels.NewMask(width, height, 255);

    /// <summary>Combines a new shape with the current selection. Returns null when nothing remains selected.</summary>
    public static SKBitmap? Combine(SKBitmap? current, SKBitmap shape, SelectionMode mode)
    {
        if (current == null || mode == SelectionMode.Replace)
            return mode is SelectionMode.Subtract or SelectionMode.Intersect || IsEmpty(shape) ? null : shape;
        var result = Pixels.Clone(current);
        using var canvas = new SKCanvas(result);
        using var paint = new SKPaint
        {
            BlendMode = mode switch
            {
                SelectionMode.Add => SKBlendMode.SrcOver,
                SelectionMode.Subtract => SKBlendMode.DstOut,
                _ => SKBlendMode.DstIn
            }
        };
        canvas.DrawBitmap(shape, 0, 0, paint);
        canvas.Flush();
        if (!IsEmpty(result)) return result;
        result.Dispose();
        return null;
    }

    public static SKBitmap Invert(SKBitmap mask)
    {
        var result = Pixels.Clone(mask);
        var span = result.GetPixelSpan();
        for (var i = 0; i < span.Length; i++) span[i] = (byte)(255 - span[i]);
        return result;
    }

    public static bool IsEmpty(SKBitmap mask) => mask.GetPixelSpan().IndexOfAnyExcept((byte)0) < 0;

    /// <summary>The smallest rectangle holding every selected pixel, or empty.</summary>
    public static unsafe SKRectI Bounds(SKBitmap mask, byte threshold = 1)
    {
        int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
        var pixels = (byte*)mask.GetPixels();
        for (var y = 0; y < mask.Height; y++)
        {
            var row = new ReadOnlySpan<byte>(pixels + (long)y * mask.RowBytes, mask.Width);
            var first = threshold <= 1 ? row.IndexOfAnyExcept((byte)0) : IndexOfAtLeast(row, threshold);
            if (first < 0) continue;
            var last = mask.Width - 1;
            while (row[last] < threshold) last--;
            left = Math.Min(left, first); right = Math.Max(right, last);
            top = Math.Min(top, y); bottom = y;
        }
        return right < 0 ? SKRectI.Empty : new SKRectI(left, top, right + 1, bottom + 1);
    }

    private static int IndexOfAtLeast(ReadOnlySpan<byte> row, byte threshold)
    {
        for (var i = 0; i < row.Length; i++) if (row[i] >= threshold) return i;
        return -1;
    }

    private static SKBitmap Filtered(SKBitmap mask, SKImageFilter filter)
    {
        var result = Pixels.NewMask(mask.Width, mask.Height);
        using var canvas = new SKCanvas(result);
        using var paint = new SKPaint { ImageFilter = filter };
        canvas.DrawBitmap(mask, 0, 0, paint);
        return result;
    }

    public static SKBitmap Expand(SKBitmap mask, int pixels)
    {
        using var filter = SKImageFilter.CreateDilate(pixels, pixels);
        return Filtered(mask, filter);
    }

    public static SKBitmap? Contract(SKBitmap mask, int pixels)
    {
        // Erode treats the area beyond the bitmap as unselected, so a selection touching the canvas edge pulls
        // away from it, as in Photoshop.
        using var filter = SKImageFilter.CreateErode(pixels, pixels);
        var result = Filtered(mask, filter);
        if (!IsEmpty(result)) return result;
        result.Dispose();
        return null;
    }

    public static SKBitmap Feather(SKBitmap mask, float radius)
    {
        using var filter = SKImageFilter.CreateBlur(radius / 2, radius / 2);
        return Filtered(mask, filter);
    }

    public static SKBitmap? Translate(SKBitmap mask, int dx, int dy)
    {
        var result = Pixels.NewMask(mask.Width, mask.Height);
        using var canvas = new SKCanvas(result);
        canvas.DrawBitmap(mask, dx, dy);
        canvas.Flush();
        if (!IsEmpty(result)) return result;
        result.Dispose();
        return null;
    }

    /// <summary>Resizes or repositions a selection for a changed canvas.</summary>
    public static SKBitmap? Remap(SKBitmap mask, int width, int height, SKMatrix matrix)
    {
        var result = Pixels.NewMask(width, height);
        using var canvas = new SKCanvas(result);
        canvas.SetMatrix(in matrix);
        canvas.DrawImage(Pixels.ImageOf(mask), 0, 0, new SKSamplingOptions(SKFilterMode.Linear));
        canvas.Flush();
        if (!IsEmpty(result)) return result;
        result.Dispose();
        return null;
    }

    /// <summary>The coverage of a layer's pixels (alpha) or of its mask, in document space.</summary>
    public static SKBitmap? FromLayer(Document document, Layer layer, bool fromMask)
    {
        var result = Pixels.NewMask(document.Width, document.Height);
        using var canvas = new SKCanvas(result);
        if (fromMask)
        {
            if (layer.Mask == null) return null;
            var matrix = DocumentRenderer.MaskMatrix(layer);
            canvas.SetMatrix(in matrix);
            canvas.DrawImage(Pixels.ImageOf(layer.Mask), 0, 0, new SKSamplingOptions(SKFilterMode.Linear));
        }
        else
        {
            if (layer.Pixels == null) return null;
            var matrix = layer.Matrix;
            canvas.SetMatrix(in matrix);
            canvas.DrawImage(Pixels.ImageOf(layer.Pixels), 0, 0, DocumentRenderer.SamplingFor(matrix));
        }
        canvas.Flush();
        if (!IsEmpty(result)) return result;
        result.Dispose();
        return null;
    }

    /// <summary>The boundary between selected (at least half covered) and unselected pixels, for the marching ants.</summary>
    public static unsafe SKPath Outline(SKBitmap mask)
    {
        using var region = new SKRegion();
        var pixels = (byte*)mask.GetPixels();
        var bounds = Bounds(mask, 128);
        if (bounds.IsEmpty) return new SKPath();
        // Rows with identical runs merge into one tall rectangle, keeping the region small for simple shapes.
        var previous = new List<(int Start, int End)>();
        var current = new List<(int Start, int End)>();
        var previousTop = bounds.Top;
        using var builder = new SKPath();
        void Flush(int bottom)
        {
            foreach (var (start, end) in previous) builder.AddRect(new SKRect(start, previousTop, end, bottom));
        }
        for (var y = bounds.Top; y < bounds.Bottom; y++)
        {
            current.Clear();
            var row = pixels + (long)y * mask.RowBytes;
            for (var x = bounds.Left; x < bounds.Right; x++)
            {
                if (row[x] < 128) continue;
                var start = x;
                while (x < bounds.Right && row[x] >= 128) x++;
                current.Add((start, x));
            }
            if (!current.SequenceEqual(previous))
            {
                Flush(y);
                (previous, current) = (current, previous);
                previousTop = y;
            }
        }
        Flush(bounds.Bottom);
        region.SetPath(builder, new SKRegion(bounds));
        return region.GetBoundaryPath();
    }
}
