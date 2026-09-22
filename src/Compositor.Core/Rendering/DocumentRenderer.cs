using Compositor.Model;
using SkiaSharp;

namespace Compositor.Rendering;

/// <summary>How a render maps the document onto its target: target pixel = (document point - Origin) × Scale.</summary>
public readonly record struct RenderView(float Scale, SKPoint Origin)
{
    public static readonly RenderView Identity = new(1, SKPoint.Empty);
}

/// <summary>A buffer covering <see cref="Area"/> of the render target, with a canvas that draws in document space.</summary>
internal sealed class Tile : IDisposable
{
    public SKBitmap Bitmap { get; }
    public SKCanvas Canvas { get; }
    /// <summary>In target pixels.</summary>
    public SKRectI Area { get; }
    public RenderView View { get; }

    public Tile(SKRectI area, RenderView view, bool mask = false)
    {
        Area = area;
        View = view;
        Bitmap = mask ? Pixels.NewMask(area.Width, area.Height) : Pixels.NewColor(area.Width, area.Height);
        Canvas = new SKCanvas(Bitmap);
        Canvas.Translate(-area.Left, -area.Top);
        Canvas.Scale(view.Scale);
        Canvas.Translate(-view.Origin.X, -view.Origin.Y);
    }

    /// <summary>A sibling buffer over the same area.</summary>
    public Tile Sibling(bool mask = false) => new(Area, View, mask);

    /// <summary>Draws a bitmap covering the same area straight onto this tile, pixel for pixel.</summary>
    public void DrawRaw(SKBitmap bitmap, SKPaint? paint = null)
    {
        Canvas.Save();
        Canvas.ResetMatrix();
        using var image = SKImage.FromPixels(bitmap.PeekPixels());
        Canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest), paint);
        Canvas.Restore();
    }

    /// <summary>The document point at this tile's top-left pixel, and the document distance between pixels.</summary>
    public (double X, double Y, double Step) DocumentGrid => (View.Origin.X + Area.Left / (double)View.Scale, View.Origin.Y + Area.Top / (double)View.Scale, 1.0 / View.Scale);

    public void Dispose()
    {
        Canvas.Dispose();
        Bitmap.Dispose();
    }
}

public sealed class RenderOptions
{
    /// <summary>Layers replaced while rendering (hidden for solo, or swapped for previews).</summary>
    public Dictionary<Guid, Layer>? Overrides { get; init; }
}

/// <summary>Flattens the layer tree: blend modes, opacity, masks, clipping masks, folders and adjustment layers.</summary>
public static class DocumentRenderer
{
    /// <summary>A new bitmap holding the whole flattened document.</summary>
    public static SKBitmap Flatten(Document document, RenderOptions? options = null)
    {
        var target = Pixels.NewColor(document.Width, document.Height);
        Render(document, target, document.Bounds, options);
        return target;
    }

    /// <summary>Re-renders <paramref name="area"/> of the document into a document-sized target.</summary>
    public static void Render(Document document, SKBitmap target, SKRectI area, RenderOptions? options = null) =>
        Render(document, target, Geometry.Intersect(area, document.Bounds), RenderView.Identity, options);

    /// <summary>
    /// Renders the document through a view (scaled and offset) into <paramref name="area"/> of a target bitmap. The
    /// canvas uses this to draw only what is on screen, at screen resolution, however large the document is.
    /// </summary>
    public static void Render(Document document, SKBitmap target, SKRectI area, RenderView view, RenderOptions? options = null)
    {
        area = Geometry.Intersect(area, new SKRectI(0, 0, target.Width, target.Height));
        if (area.IsEmpty) return;
        // Skia's raster backend draws on one thread, so large areas are split into bands rendered side by side.
        // Every layer operation is per-pixel, which makes the bands independent of each other.
        var bands = (long)area.Width * area.Height < 300_000 ? 1 : Math.Clamp(area.Height / 48, 1, Environment.ProcessorCount);
        if (bands > 1) Pixels.PrepareLevels(document, view.Scale);
        var bandHeight = (area.Height + bands - 1) / bands;
        Parallel.For(0, bands, band =>
        {
            var top = area.Top + band * bandHeight;
            var part = new SKRectI(area.Left, top, area.Right, Math.Min(area.Bottom, top + bandHeight));
            if (part.Height <= 0) return;
            using var tile = new Tile(part, view);
            RenderNodes(document.Layers, tile, options);
            tile.Canvas.Flush();
            CopyRows(tile.Bitmap, target, part);
        });
        Pixels.Invalidate(target);
    }

    private static unsafe void CopyRows(SKBitmap from, SKBitmap to, SKRectI area)
    {
        var source = (byte*)from.GetPixels();
        var destination = (byte*)to.GetPixels();
        long bytes = (long)area.Width * 4;
        for (var y = 0; y < area.Height; y++)
            Buffer.MemoryCopy(source + (long)y * from.RowBytes, destination + (long)(y + area.Top) * to.RowBytes + (long)area.Left * 4, bytes, bytes);
    }

    /// <summary>Renders only the given layers (and their clipped followers) over a document-space area, for merges and copies.</summary>
    public static SKBitmap RenderLayers(Document document, IEnumerable<Layer> layers, SKRectI area)
    {
        using var tile = new Tile(new SKRectI(0, 0, area.Width, area.Height), new RenderView(1, new SKPoint(area.Left, area.Top)));
        RenderNodes(layers.ToList(), tile, null);
        tile.Canvas.Flush();
        return Pixels.Clone(tile.Bitmap);
    }

    private static Layer Resolve(Layer layer, RenderOptions? options) =>
        options?.Overrides != null && options.Overrides.TryGetValue(layer.Id, out var replacement) ? replacement : layer;

    private static void RenderNodes(IReadOnlyList<Layer> nodes, Tile tile, RenderOptions? options)
    {
        for (var i = 0; i < nodes.Count; i++)
        {
            var layer = Resolve(nodes[i], options);
            // A clipping base and the clipped layers directly above it render as one unit.
            var clipped = new List<Layer>();
            while (i + 1 < nodes.Count && nodes[i + 1].Clipped) clipped.Add(Resolve(nodes[++i], options));
            if (layer.Clipped) continue; // A clipped layer with no base shows nothing.
            if (!layer.Visible || layer.Opacity <= 0) continue;
            clipped.RemoveAll(l => !l.Visible || l.Opacity <= 0);
            RenderUnit(layer, clipped, tile, options);
        }
    }

    private static void RenderUnit(Layer layer, List<Layer> clipped, Tile tile, RenderOptions? options)
    {
        if (layer.IsAdjustment)
        {
            ApplyAdjustment(layer, tile);
            return;
        }
        // Effects are drawn from an image that already has the mask applied, so the mask is not applied again.
        var effects = EffectsOf(layer);
        var hasMask = layer.Mask != null && layer.MaskEnabled && effects == null;
        if (layer.IsGroup && !hasMask && layer.Opacity >= 1 && layer.Blend == BlendMode.Normal && clipped.Count == 0)
        {
            RenderNodes(layer.Children, tile, options); // Pass-through folder.
            return;
        }
        if (layer.Kind == LayerKind.Raster && !hasMask && clipped.Count == 0 && !layer.Blend.IsCustom())
        {
            DrawPixels(layer, tile.Canvas, layer.Opacity, layer.Blend, effects);
            return;
        }

        // A Normal folder is rendered over a copy of what lies beneath it and then mixed back in by its mask and
        // opacity. For ordinary layers that equals compositing the folder on its own, and it is what lets adjustment
        // layers inside the folder see (and change) the picture below them.
        if (layer.IsGroup && layer.Blend == BlendMode.Normal && clipped.Count == 0)
        {
            tile.Canvas.Flush();
            using var seeded = tile.Sibling();
            seeded.DrawRaw(tile.Bitmap, Replace);
            RenderNodes(layer.Children, seeded, options);
            seeded.Canvas.Flush();
            Mix(tile, seeded.Bitmap, layer, hasMask);
            return;
        }

        using var content = tile.Sibling();
        if (layer.IsGroup) RenderNodes(layer.Children, content, options);
        else DrawPixels(layer, content.Canvas, 1, BlendMode.Normal, effects);
        if (hasMask) MultiplyByMask(layer, content);

        if (clipped.Count > 0)
        {
            // Clipped layers paint onto the base's colors as if the base were opaque; the base's own coverage is
            // applied once, at the end. Blending onto the still-transparent base instead would let its color bleed
            // through and square its alpha wherever it is soft or semi-transparent.
            content.Canvas.Flush();
            using var baseAlpha = Pixels.Clone(content.Bitmap);
            MakeOpaque(content.Bitmap);
            foreach (var top in clipped)
            {
                if (top.IsAdjustment) { ApplyAdjustment(top, content); continue; }
                using var over = tile.Sibling();
                var topEffects = EffectsOf(top);
                if (top.IsGroup) RenderNodes(top.Children, over, options);
                else DrawPixels(top, over.Canvas, 1, BlendMode.Normal, topEffects);
                if (top.Mask != null && top.MaskEnabled && topEffects == null) MultiplyByMask(top, over);
                over.Canvas.Flush();
                if (top.Blend.IsCustom()) { content.Canvas.Flush(); SeparableBlend.Composite(content.Bitmap, over.Bitmap, top.Blend, top.Opacity); continue; }
                using var blend = new SKPaint { BlendMode = top.Blend.ToSkia(), Color = SKColors.White.WithAlpha(ToByte(top.Opacity)) };
                content.DrawRaw(over.Bitmap, blend);
            }
            using var restore = new SKPaint { BlendMode = SKBlendMode.DstIn };
            content.DrawRaw(baseAlpha, restore);
        }

        content.Canvas.Flush();
        if (layer.Blend.IsCustom())
        {
            // Skia has no equivalent for these modes, so the layer's finished content is combined by hand.
            tile.Canvas.Flush();
            SeparableBlend.Composite(tile.Bitmap, content.Bitmap, layer.Blend, layer.Opacity);
            return;
        }
        using var paint = new SKPaint { BlendMode = layer.Blend.ToSkia(), Color = SKColors.White.WithAlpha(ToByte(layer.Opacity)) };
        tile.DrawRaw(content.Bitmap, paint);
    }

    private static readonly SKPaint Replace = new() { BlendMode = SKBlendMode.Src };

    /// <summary>Straightens premultiplied pixels and gives every covered pixel full alpha.</summary>
    private static unsafe void MakeOpaque(SKBitmap bitmap)
    {
        var pixels = (byte*)bitmap.GetPixels();
        for (var y = 0; y < bitmap.Height; y++)
        {
            var p = pixels + (long)y * bitmap.RowBytes;
            for (var x = 0; x < bitmap.Width; x++, p += 4)
            {
                int a = p[3];
                if (a is 0 or 255) continue;
                p[0] = (byte)Math.Min(255, (p[0] * 255 + a / 2) / a);
                p[1] = (byte)Math.Min(255, (p[1] * 255 + a / 2) / a);
                p[2] = (byte)Math.Min(255, (p[2] * 255 + a / 2) / a);
                p[3] = 255;
            }
        }
    }

    /// <summary>tile = tile × (1 - m) + replacement × m, where m is the layer's mask times its opacity.</summary>
    private static void Mix(Tile tile, SKBitmap replacement, Layer layer, bool hasMask)
    {
        if (!hasMask && layer.Opacity >= 1)
        {
            tile.DrawRaw(replacement, Replace);
            return;
        }
        using var coverage = tile.Sibling(mask: true);
        coverage.Canvas.Clear(SKColors.Black.WithAlpha(ToByte(layer.Opacity)));
        if (hasMask) MultiplyByMask(layer, coverage);
        coverage.Canvas.Flush();
        using (var canvas = new SKCanvas(replacement))
        using (var keep = new SKPaint { BlendMode = SKBlendMode.DstIn })
            canvas.DrawBitmap(coverage.Bitmap, 0, 0, keep);
        using (var remove = new SKPaint { BlendMode = SKBlendMode.DstOut }) tile.DrawRaw(coverage.Bitmap, remove);
        using var plus = new SKPaint { BlendMode = SKBlendMode.Plus };
        tile.DrawRaw(replacement, plus);
    }

    private static byte ToByte(double opacity) => (byte)Math.Clamp(Math.Round(opacity * 255), 0, 255);

    private static double ScaleOf(SKMatrix matrix) => Math.Sqrt(Math.Abs(matrix.ScaleX * matrix.ScaleY - matrix.SkewX * matrix.SkewY));

    /// <summary>The sampling for drawing a bitmap through a matrix: exact pixels when nothing is resampled, smooth otherwise.</summary>
    public static SKSamplingOptions SamplingFor(SKMatrix matrix)
    {
        if (matrix.ScaleX == 1 && matrix.ScaleY == 1 && matrix.SkewX == 0 && matrix.SkewY == 0 && matrix.Persp0 == 0 && matrix.Persp1 == 0
            && matrix.TransX == MathF.Round(matrix.TransX) && matrix.TransY == MathF.Round(matrix.TransY))
            return new SKSamplingOptions(SKFilterMode.Nearest);
        return ScaleOf(matrix) < 1 ? new SKSamplingOptions(SKFilterMode.Linear) : new SKSamplingOptions(SKCubicResampler.CatmullRom);
    }

    /// <summary>
    /// Draws a bitmap through the canvas's current matrix. Strong reductions read from a prebuilt half-size pyramid, so
    /// a 24-megapixel layer shown at 10% costs about as much as a small one, and stays free of aliasing.
    /// </summary>
    internal static void DrawBitmap(SKCanvas canvas, SKBitmap bitmap, SKPaint paint)
    {
        var scale = ScaleOf(canvas.TotalMatrix);
        var level = scale >= 0.5 || Pixels.IsLive(bitmap) ? 0 : (int)Math.Floor(Math.Log2(1 / scale));
        var image = Pixels.Level(bitmap, ref level);
        if (level == 0)
        {
            canvas.DrawImage(image, 0, 0, SamplingFor(canvas.TotalMatrix), paint);
            return;
        }
        canvas.Save();
        canvas.Scale((float)bitmap.Width / image.Width, (float)bitmap.Height / image.Height);
        canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Linear), paint);
        canvas.Restore();
    }

    /// <summary>The layer's pixels with its effects drawn around them, or null when it has none (or they cannot be drawn).</summary>
    private static (SKBitmap Image, int Inset)? EffectsOf(Layer layer) =>
        layer.Pixels == null || layer.Effects == null ? null : LayerEffectsRenderer.Cached(layer.Pixels, layer.Mask != null && layer.MaskEnabled ? layer.Mask : null, layer.Effects);

    private static void DrawPixels(Layer layer, SKCanvas canvas, double opacity, BlendMode blend, (SKBitmap Image, int Inset)? effects)
    {
        if (layer.Pixels == null) return;
        using var paint = new SKPaint { BlendMode = blend.ToSkia(), Color = SKColors.White.WithAlpha(ToByte(opacity)), IsAntialias = true };
        void Draw(SKCanvas target)
        {
            if (effects is { } built)
            {
                // The effects image is the layer's pixels grown by the inset on every side, so it lands in the same place.
                target.Save();
                target.Translate(-built.Inset, -built.Inset);
                DrawBitmap(target, built.Image, paint);
                target.Restore();
                // While a stroke is in progress the effects are those of the pixels at its start; the wet paint goes over them.
                if (Pixels.IsLive(layer.Pixels) && (layer.Mask == null || !layer.MaskEnabled)) DrawBitmap(target, layer.Pixels, paint);
            }
            else DrawBitmap(target, layer.Pixels, paint);
        }
        var pixels = layer.Pixels;
        var corners = layer.Transform.Distort == null ? null : layer.Transform.Corners(pixels.Width, pixels.Height);
        if (corners != null && !Geometry.IsConvex(corners))
        {
            // A folded shape (a corner dragged past its neighbours) has no perspective that takes the image to it, so
            // each half is taken there on its own, as two triangles meeting along the shape's diagonal.
            var source = new SKPoint[] { new(0, 0), new(pixels.Width, 0), new(pixels.Width, pixels.Height), new(0, pixels.Height) };
            foreach (var (i, j, k) in new[] { (0, 1, 2), (0, 2, 3) })
            {
                if (Geometry.Affine(source[i], source[j], source[k], corners[i], corners[j], corners[k]) is not { } affine) continue;
                using var triangle = new SKPath();
                triangle.AddPoly([corners[i], corners[j], corners[k]], close: true);
                canvas.Save();
                canvas.ClipPath(triangle, SKClipOperation.Intersect, antialias: false);
                canvas.Concat(in affine);
                Draw(canvas);
                canvas.Restore();
            }
            return;
        }
        var matrix = layer.Matrix;
        canvas.Save();
        canvas.Concat(in matrix);
        Draw(canvas);
        canvas.Restore();
    }

    /// <summary>The matrix placing a layer's mask on the document: the pixel transform for raster layers, identity otherwise.</summary>
    public static SKMatrix MaskMatrix(Layer layer)
    {
        if (layer.Mask == null || layer.Pixels == null) return SKMatrix.Identity;
        return layer.Transform.Matrix(layer.Mask.Width, layer.Mask.Height);
    }

    private static void MultiplyByMask(Layer layer, Tile content)
    {
        var mask = layer.Mask!;
        var matrix = MaskMatrix(layer);
        var canvas = content.Canvas;
        if (layer.Pixels == null)
        {
            // A folder's or adjustment's mask hides everything beyond its own extent.
            canvas.Save();
            canvas.ClipRect(new SKRect(0, 0, mask.Width, mask.Height), SKClipOperation.Difference);
            canvas.Clear(SKColors.Transparent);
            canvas.Restore();
        }
        canvas.Save();
        canvas.Concat(in matrix);
        using var paint = new SKPaint { BlendMode = SKBlendMode.DstIn };
        DrawBitmap(canvas, mask, paint);
        canvas.Restore();
    }

    private static void ApplyAdjustment(Layer layer, Tile tile)
    {
        if (layer.Adjustment == null || layer.Adjustment.IsIdentity) return;
        tile.Canvas.Flush();
        using var adjusted = Pixels.Clone(tile.Bitmap);
        var (originX, originY, step) = tile.DocumentGrid;
        // Bands already run side by side, so the adjustment itself stays on this thread.
        layer.Adjustment.Apply(adjusted, new SKRectI(0, 0, adjusted.Width, adjusted.Height), originX, originY, step, parallel: tile.Area.Height * (long)tile.Area.Width > 2_000_000);
        Mix(tile, adjusted, layer, layer.Mask != null && layer.MaskEnabled);
    }
}
