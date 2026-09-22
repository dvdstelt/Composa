using Composa.Model;
using SkiaSharp;

namespace Composa.Rendering;

/// <summary>
/// Draws a layer's effects around its pixels. The result is the layer as it should appear (shadow and glow behind,
/// stroke around, pixels on top, overlay and inner shadow over them) on a canvas grown by <c>Inset</c> pixels on every side,
/// so the caller places it by shifting the layer's own matrix by the same amount. The last few results are kept, so
/// redrawing the canvas does not rebuild them.
/// </summary>
public static class LayerEffectsRenderer
{
    public const long MaxPixels = 100_000_000;

    private sealed record Entry(SKBitmap Pixels, SKBitmap? Mask, LayerEffects Effects, SKBitmap Result, int Inset);

    private static readonly List<Entry> Cache = [];
    private const int MaxEntries = 12;
    private const long Budget = 384L * 1024 * 1024;

    static LayerEffectsRenderer() => Pixels.Invalidated += bitmap =>
    {
        // A stroke in progress keeps its effects from the moment it started; the final pixels are rendered once it ends.
        if (Pixels.IsLive(bitmap)) return;
        lock (Cache) Cache.RemoveAll(e => ReferenceEquals(e.Pixels, bitmap) || ReferenceEquals(e.Mask, bitmap));
    };

    /// <summary>
    /// The layer's pixels, through its mask, with its visible effects around them; null when there is nothing to
    /// draw or the result would be too large, in which case the caller draws the layer as it is.
    /// </summary>
    public static (SKBitmap Image, int Inset)? Cached(SKBitmap pixels, SKBitmap? mask, LayerEffects? effects)
    {
        if (effects == null) return null;
        var visible = effects.Visible();
        if (visible.IsEmpty) return null;
        lock (Cache)
        {
            var index = Cache.FindIndex(e => ReferenceEquals(e.Pixels, pixels) && ReferenceEquals(e.Mask, mask) && e.Effects == visible);
            if (index >= 0)
            {
                var hit = Cache[index];
                if (index != Cache.Count - 1) { Cache.RemoveAt(index); Cache.Add(hit); }
                return (hit.Result, hit.Inset);
            }
        }
        var made = Render(pixels, mask, visible);
        if (made == null) return null;
        lock (Cache)
        {
            // Two render bands may have built the same image at once; the first one in wins.
            var other = Cache.Find(e => ReferenceEquals(e.Pixels, pixels) && ReferenceEquals(e.Mask, mask) && e.Effects == visible);
            if (other != null) { made.Value.Image.Dispose(); return (other.Result, other.Inset); }
            Cache.Add(new Entry(pixels, mask, visible, made.Value.Image, made.Value.Inset));
            // Evicted results are left to the garbage collector: a render on another thread may still be drawing them.
            while (Cache.Count > MaxEntries || (Cache.Count > 1 && Cache.Sum(e => (long)e.Result.ByteCount) > Budget)) Cache.RemoveAt(0);
        }
        return made;
    }

    /// <summary>Drops every cached result, for tests and when memory is short.</summary>
    public static void ClearCache()
    {
        lock (Cache) Cache.Clear();
    }

    /// <summary>Renders the effects without the cache. <paramref name="visible"/> should come from <see cref="LayerEffects.Visible"/>.</summary>
    public static (SKBitmap Image, int Inset)? Render(SKBitmap pixels, SKBitmap? mask, LayerEffects visible)
    {
        var inset = visible.Margin();
        if (inset == 0) inset = 2;
        long width = pixels.Width + inset * 2L, height = pixels.Height + inset * 2L;
        if (width * height > MaxPixels || width > Document.MaxSide * 2 || height > Document.MaxSide * 2) return null;
        var result = Pixels.NewColor((int)width, (int)height);
        using var canvas = new SKCanvas(result);

        // The layer as it is shown: its pixels through its mask, with room around them for the effects.
        using var shown = Pixels.NewColor((int)width, (int)height);
        using (var shownCanvas = new SKCanvas(shown))
        {
            shownCanvas.DrawBitmap(pixels, inset, inset);
            if (mask != null)
            {
                using var keep = new SKPaint { BlendMode = SKBlendMode.DstIn };
                shownCanvas.DrawBitmap(mask, SKRect.Create(inset, inset, pixels.Width, pixels.Height), keep);
            }
        }
        // Its coverage: white where the layer is.
        using var coverage = Pixels.NewMask((int)width, (int)height);
        using (var coverageCanvas = new SKCanvas(coverage)) coverageCanvas.DrawBitmap(shown, 0, 0);

        if (visible.Shadow is { Opacity: > 0 } shadow)
        {
            var (dx, dy) = shadow.Offset;
            using var moved = Shifted(coverage, (float)dx, (float)dy, (float)(shadow.Blur / 2));
            Tint(canvas, moved, shadow.Color, shadow.Opacity);
        }
        if (visible.OuterGlow is { Opacity: > 0, Size: > 0 } glow)
        {
            // The shape softened on every side, with the shape itself cut out so a translucent layer is not lit from
            // behind by its own glow. Half the size as sigma puts the visible edge of the glow about a size away.
            using var soft = Shifted(coverage, 0, 0, (float)(glow.Size / 2));
            using (var cutCanvas = new SKCanvas(soft))
            using (var cut = new SKPaint { BlendMode = SKBlendMode.DstOut })
                cutCanvas.DrawBitmap(coverage, 0, 0, cut);
            Tint(canvas, soft, glow.Color, glow.Opacity);
        }
        // An outside stroke sits behind the layer's own pixels; an inside one is drawn over them, or the pixels would
        // simply cover it.
        var stroke = visible.Stroke is { Size: > 0, Opacity: > 0 } s ? s : null;
        if (stroke is { Inside: false })
        {
            using var ring = Ring(coverage, stroke);
            Tint(canvas, ring, stroke.Color, stroke.Opacity);
        }
        canvas.DrawBitmap(shown, 0, 0);
        if (visible.ColorOverlay is { Opacity: > 0 } overlay) Tint(canvas, coverage, overlay.Color, overlay.Opacity);
        if (visible.InnerShadow is { Opacity: > 0 } inner)
        {
            // What lies outside the layer, moved and softened, kept to the layer's own shape.
            var (dx, dy) = inner.Offset;
            using var moved = Shifted(coverage, (float)dx, (float)dy, (float)(inner.Blur / 2));
            using var inside = Pixels.NewMask((int)width, (int)height);
            using (var insideCanvas = new SKCanvas(inside))
            {
                insideCanvas.DrawBitmap(coverage, 0, 0);
                using var cut = new SKPaint { BlendMode = SKBlendMode.DstOut };
                insideCanvas.DrawBitmap(moved, 0, 0, cut);
            }
            Tint(canvas, inside, inner.Color, inner.Opacity);
        }
        if (stroke is { Inside: true })
        {
            using var ring = Ring(coverage, stroke);
            Tint(canvas, ring, stroke.Color, stroke.Opacity);
        }
        canvas.Flush();
        return (result, inset);
    }

    /// <summary>The coverage moved by an offset and softened by a Gaussian of <paramref name="sigma"/>.</summary>
    private static SKBitmap Shifted(SKBitmap coverage, float dx, float dy, float sigma)
    {
        var moved = Pixels.NewMask(coverage.Width, coverage.Height);
        using var canvas = new SKCanvas(moved);
        using var paint = new SKPaint();
        if (sigma > 0.01f) paint.ImageFilter = SKImageFilter.CreateBlur(sigma, sigma);
        canvas.DrawBitmap(coverage, dx, dy, paint);
        return moved;
    }

    /// <summary>
    /// Where a stroke lands: the shape grown (or shrunk) by its size, less the shape itself. A square reach, not a
    /// round one: a round one eats into the corners of a rectangle, which reads as a wobbly edge.
    /// </summary>
    private static SKBitmap Ring(SKBitmap coverage, StrokeEffect stroke)
    {
        var reach = Math.Max(1, (int)Math.Round(stroke.Size));
        var ring = Pixels.NewMask(coverage.Width, coverage.Height);
        using var canvas = new SKCanvas(ring);
        using var cut = new SKPaint { BlendMode = SKBlendMode.DstOut };
        if (stroke.Inside)
        {
            using var shrink = SKImageFilter.CreateErode(reach, reach);
            cut.ImageFilter = shrink;
            canvas.DrawBitmap(coverage, 0, 0);
            canvas.DrawBitmap(coverage, 0, 0, cut);
        }
        else
        {
            using var grow = SKImageFilter.CreateDilate(reach, reach);
            using var grown = new SKPaint { ImageFilter = grow };
            canvas.DrawBitmap(coverage, 0, 0, grown);
            canvas.DrawBitmap(coverage, 0, 0, cut);
        }
        return ring;
    }

    /// <summary>Fills the coverage with a color at an opacity.</summary>
    private static void Tint(SKCanvas canvas, SKBitmap coverage, uint color, double opacity)
    {
        var tint = new SKColor(color).WithAlpha((byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255));
        // SrcIn keeps the coverage's alpha and replaces its color, whatever a mask-only bitmap draws as.
        using var filter = SKColorFilter.CreateBlendMode(tint, SKBlendMode.SrcIn);
        using var paint = new SKPaint { ColorFilter = filter };
        canvas.DrawBitmap(coverage, 0, 0, paint);
    }
}
