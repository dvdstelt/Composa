using Compositor.Model;
using Compositor.Rendering;
using SkiaSharp;

namespace Compositor.Painting;

public enum BrushMode { Paint, Erase, Clone, Heal, Blur, Smudge, Dodge, Burn }

public sealed record BrushSettings
{
    /// <summary>Diameter in document pixels.</summary>
    public double Size { get; init; } = 40;
    /// <summary>0 (fully soft) … 1 (hard edge).</summary>
    public double Hardness { get; init; } = 0.8;
    /// <summary>The most a whole stroke can cover, 0…1. For Blur, Smudge, Dodge and Burn this is the strength.</summary>
    public double Opacity { get; init; } = 1;
    /// <summary>Distance between dabs as a fraction of the diameter.</summary>
    public double Spacing { get; init; } = 0.08;
}

/// <summary>
/// One drag of a brush-like tool over a bitmap (layer pixels or a mask). The stroke works on a private copy of
/// the bitmap and recomputes touched pixels from the untouched original plus the coverage painted so far, so
/// overlapping dabs never build up past the stroke's opacity.
/// </summary>
public sealed unsafe class BrushStroke : IDisposable
{
    private readonly SKBitmap original;
    private readonly BrushSettings settings;
    private readonly BrushMode mode;
    private readonly bool isMask;
    private readonly byte[] coverage;
    private readonly int width, height, bytesPerPixel;
    private readonly float radius;
    private readonly float[] premulColor = new float[4];
    private readonly byte maskValue;
    private SKBitmap? blurred;
    private float[]? carry;
    private int carrySize;
    private SKPoint? last;
    private float residual;

    /// <summary>The bitmap being painted; becomes the layer's new pixels when the stroke commits.</summary>
    public SKBitmap Working { get; }
    /// <summary>Everything the stroke has touched so far, in bitmap coordinates.</summary>
    public SKRectI Touched { get; private set; } = SKRectI.Empty;

    /// <summary>Document-space selection limiting the stroke, with the matrix from bitmap to document space.</summary>
    public SKBitmap? Selection { get; init; }
    public SKMatrix ToDocument { get; init; } = SKMatrix.Identity;

    /// <summary>Clone source pixels (same format as the target) and the offset from target to source coordinates.</summary>
    public SKBitmap? CloneSource { get; init; }
    public SKPointI CloneOffset { get; set; }

    public BrushStroke(SKBitmap target, BrushSettings settings, BrushMode mode, SKColor color, double scale = 1)
    {
        original = target;
        Working = target.Copy();
        this.settings = settings;
        this.mode = mode;
        isMask = target.ColorType == SKColorType.Alpha8;
        bytesPerPixel = isMask ? 1 : 4;
        width = target.Width;
        height = target.Height;
        coverage = new byte[(long)width * height];
        radius = (float)Math.Max(0.5, settings.Size / 2 / Math.Max(1e-6, scale));
        var alpha = color.Alpha / 255f;
        premulColor[0] = color.Red * alpha; premulColor[1] = color.Green * alpha; premulColor[2] = color.Blue * alpha; premulColor[3] = color.Alpha;
        maskValue = (byte)((color.Red * 54 + color.Green * 183 + color.Blue * 19) >> 8);
    }

    /// <summary>Extends the stroke to a point (bitmap coordinates). Returns the changed rectangle.</summary>
    public SKRectI AddPoint(SKPoint point)
    {
        var dirty = SKRectI.Empty;
        if (last is not { } from)
        {
            dirty = Dab(point);
            last = point;
            residual = 0;
        }
        else
        {
            var spacing = Math.Max(0.5f, radius * 2 * (float)settings.Spacing);
            float dx = point.X - from.X, dy = point.Y - from.Y;
            var length = MathF.Sqrt(dx * dx + dy * dy);
            if (length <= 0) return dirty;
            var travelled = spacing - residual;
            while (travelled <= length)
            {
                var t = travelled / length;
                dirty = Geometry.Union(dirty, Dab(new SKPoint(from.X + dx * t, from.Y + dy * t)));
                travelled += spacing;
            }
            residual = length - (travelled - spacing);
            last = point;
        }
        if (!dirty.IsEmpty)
        {
            Touched = Geometry.Union(Touched, dirty);
            Pixels.Invalidate(Working);
        }
        return dirty;
    }

    /// <summary>A straight segment from the last point, for Shift-click lines.</summary>
    public SKRectI LineTo(SKPoint point) => AddPoint(point);

    private float Falloff(float distance)
    {
        var hardness = (float)Math.Clamp(settings.Hardness, 0, 1);
        var edge = Math.Clamp(radius - distance + 0.5f, 0, 1);
        if (hardness >= 0.995f) return edge;
        var inner = radius * hardness;
        if (distance <= inner) return edge;
        var t = Math.Clamp((distance - inner) / Math.Max(1e-3f, radius - inner), 0, 1);
        return 0.5f * (1 + MathF.Cos(MathF.PI * t)) * edge;
    }

    private SKRectI Dab(SKPoint center)
    {
        var rect = Geometry.Intersect(new SKRectI(
            (int)MathF.Floor(center.X - radius - 1), (int)MathF.Floor(center.Y - radius - 1),
            (int)MathF.Ceiling(center.X + radius + 1), (int)MathF.Ceiling(center.Y + radius + 1)), new SKRectI(0, 0, width, height));
        if (rect.IsEmpty) return rect;
        if (mode == BrushMode.Smudge) { Smudge(center, rect); return rect; }

        var changed = false;
        for (var y = rect.Top; y < rect.Bottom; y++)
        {
            var row = (long)y * width;
            for (var x = rect.Left; x < rect.Right; x++)
            {
                float dx = x + 0.5f - center.X, dy = y + 0.5f - center.Y;
                var c = (byte)(Falloff(MathF.Sqrt(dx * dx + dy * dy)) * 255 + 0.5f);
                if (c <= coverage[row + x]) continue;
                coverage[row + x] = c;
                changed = true;
            }
        }
        if (!changed) return SKRectI.Empty;
        Recompute(rect);
        return rect;
    }

    private float SelectionAt(int x, int y)
    {
        if (Selection == null) return 1;
        var p = ToDocument.MapPoint(x + 0.5f, y + 0.5f);
        int sx = (int)MathF.Floor(p.X), sy = (int)MathF.Floor(p.Y);
        if (sx < 0 || sy < 0 || sx >= Selection.Width || sy >= Selection.Height) return 0;
        return ((byte*)Selection.GetPixels())[(long)sy * Selection.RowBytes + sx] / 255f;
    }

    private void Recompute(SKRectI rect)
    {
        if (mode == BrushMode.Blur && blurred == null) blurred = MakeBlurred();
        var src = (byte*)original.GetPixels();
        var dst = (byte*)Working.GetPixels();
        var stride = original.RowBytes;
        var opacity = (float)Math.Clamp(settings.Opacity, 0, 1);
        byte* clone = CloneSource != null ? (byte*)CloneSource.GetPixels() : null;
        byte* soft = blurred != null ? (byte*)blurred.GetPixels() : null;
        Span<float> source = stackalloc float[4];

        for (var y = rect.Top; y < rect.Bottom; y++)
        {
            for (var x = rect.Left; x < rect.Right; x++)
            {
                var c = coverage[(long)y * width + x];
                if (c == 0) continue;
                var a = c / 255f * opacity * SelectionAt(x, y);
                var index = (long)y * stride + x * bytesPerPixel;
                if (a <= 0) { for (var i = 0; i < bytesPerPixel; i++) dst[index + i] = src[index + i]; continue; }

                switch (mode)
                {
                    case BrushMode.Paint:
                        if (isMask) source[0] = maskValue; else premulColor.CopyTo(source);
                        break;
                    case BrushMode.Erase:
                        source.Clear();
                        break;
                    case BrushMode.Heal:
                        // Shown as a dark veil while dragging; the heal itself runs when the stroke ends.
                        source[0] = source[1] = source[2] = 0; source[3] = 255; a *= 0.45f;
                        break;
                    case BrushMode.Clone:
                        int cx = x + CloneOffset.X, cy = y + CloneOffset.Y;
                        if (clone == null || cx < 0 || cy < 0 || cx >= CloneSource!.Width || cy >= CloneSource.Height) { source.Clear(); a = 0; }
                        else
                        {
                            var ci = (long)cy * CloneSource.RowBytes + cx * bytesPerPixel;
                            for (var i = 0; i < bytesPerPixel; i++) source[i] = clone[ci + i];
                        }
                        break;
                    case BrushMode.Blur:
                        for (var i = 0; i < bytesPerPixel; i++) source[i] = soft![index + i];
                        break;
                    case BrushMode.Dodge:
                    case BrushMode.Burn:
                        if (isMask) source[0] = mode == BrushMode.Dodge ? src[index] + (255 - src[index]) * 0.5f : src[index] * 0.5f;
                        else
                        {
                            float alpha = src[index + 3];
                            for (var i = 0; i < 3; i++)
                                source[i] = mode == BrushMode.Dodge ? src[index + i] + (alpha - src[index + i]) * 0.5f : src[index + i] * 0.5f;
                            source[3] = alpha;
                        }
                        break;
                }
                for (var i = 0; i < bytesPerPixel; i++)
                    dst[index + i] = (byte)Math.Clamp(src[index + i] * (1 - a) + source[i] * a + 0.5f, 0, 255);
            }
        }
    }

    private SKBitmap MakeBlurred()
    {
        var result = new SKBitmap(original.Info);
        result.Erase(SKColors.Transparent);
        using var canvas = new SKCanvas(result);
        var sigma = Math.Max(1.5f, radius * 0.25f);
        using var paint = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp), BlendMode = SKBlendMode.Src };
        canvas.DrawBitmap(original, 0, 0, paint);
        return result;
    }

    // Smudge drags a buffer of paint along: each dab lays the carried pixels down, then picks up what was beneath.
    private void Smudge(SKPoint center, SKRectI rect)
    {
        var size = (int)MathF.Ceiling(radius * 2 + 3);
        var dst = (byte*)Working.GetPixels();
        var stride = Working.RowBytes;
        int originX = (int)MathF.Floor(center.X - radius - 1), originY = (int)MathF.Floor(center.Y - radius - 1);
        if (carry == null)
        {
            carrySize = size;
            carry = new float[size * size * 4];
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                int px = Math.Clamp(originX + x, 0, width - 1), py = Math.Clamp(originY + y, 0, height - 1);
                for (var i = 0; i < bytesPerPixel; i++) carry[(y * size + x) * 4 + i] = dst[(long)py * stride + px * bytesPerPixel + i];
            }
            return;
        }
        var strength = (float)Math.Clamp(settings.Opacity, 0, 1) * 0.95f;
        for (var y = rect.Top; y < rect.Bottom; y++)
        for (var x = rect.Left; x < rect.Right; x++)
        {
            int lx = x - originX, ly = y - originY;
            if (lx < 0 || ly < 0 || lx >= carrySize || ly >= carrySize) continue;
            float dx = x + 0.5f - center.X, dy = y + 0.5f - center.Y;
            var c = Falloff(MathF.Sqrt(dx * dx + dy * dy)) * SelectionAt(x, y);
            var index = (long)y * stride + x * bytesPerPixel;
            var slot = (ly * carrySize + lx) * 4;
            for (var i = 0; i < bytesPerPixel; i++)
            {
                float under = dst[index + i];
                carry[slot + i] = carry[slot + i] * strength + under * (1 - strength);
                if (c > 0) dst[index + i] = (byte)Math.Clamp(under + (carry[slot + i] - under) * c + 0.5f, 0, 255);
            }
        }
    }

    /// <summary>The painted coverage as an Alpha8 mask, for tools that finish their work after the drag (healing).</summary>
    public SKBitmap CoverageMask()
    {
        var mask = Pixels.NewMask(width, height);
        var dst = (byte*)mask.GetPixels();
        for (var y = 0; y < height; y++)
            fixed (byte* row = &coverage[(long)y * width]) Buffer.MemoryCopy(row, dst + (long)y * mask.RowBytes, width, width);
        return mask;
    }

    public void Dispose() => blurred?.Dispose();
}
