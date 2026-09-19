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
        var hasMask = layer.Mask != null && layer.MaskEnabled;
        if (layer.IsGroup && !hasMask && layer.Opacity >= 1 && layer.Blend == BlendMode.Normal && clipped.Count == 0)
        {
            RenderNodes(layer.Children, tile, options); // Pass-through folder.
            return;
        }
        if (layer.Kind == LayerKind.Raster && !hasMask && clipped.Count == 0)
        {
            DrawPixels(layer, tile.Canvas, layer.Opacity, layer.Blend);
            return;
        }

        using var content = tile.Sibling();
        if (layer.IsGroup) RenderNodes(layer.Children, content, options);
        else DrawPixels(layer, content.Canvas, 1, BlendMode.Normal);
        if (hasMask) MultiplyByMask(layer, content);

        if (clipped.Count > 0)
        {
            content.Canvas.Flush();
            using var baseAlpha = Pixels.Clone(content.Bitmap);
            foreach (var top in clipped)
            {
                if (top.IsAdjustment) { ApplyAdjustment(top, content); continue; }
                using var over = tile.Sibling();
                if (top.IsGroup) RenderNodes(top.Children, over, options);
                else DrawPixels(top, over.Canvas, 1, BlendMode.Normal);
                if (top.Mask != null && top.MaskEnabled) MultiplyByMask(top, over);
                using (var clip = new SKPaint { BlendMode = SKBlendMode.DstIn }) over.DrawRaw(baseAlpha, clip);
                over.Canvas.Flush();
                using var blend = new SKPaint { BlendMode = top.Blend.ToSkia(), Color = SKColors.White.WithAlpha(ToByte(top.Opacity)) };
                content.DrawRaw(over.Bitmap, blend);
            }
            // Blending can raise alpha slightly at soft edges; the unit never shows beyond its base.
            using var restore = new SKPaint { BlendMode = SKBlendMode.DstIn };
            content.DrawRaw(baseAlpha, restore);
        }

        content.Canvas.Flush();
        using var paint = new SKPaint { BlendMode = layer.Blend.ToSkia(), Color = SKColors.White.WithAlpha(ToByte(layer.Opacity)) };
        tile.DrawRaw(content.Bitmap, paint);
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

    private static void DrawPixels(Layer layer, SKCanvas canvas, double opacity, BlendMode blend)
    {
        if (layer.Pixels == null) return;
        var matrix = layer.Matrix;
        canvas.Save();
        canvas.Concat(in matrix);
        using var paint = new SKPaint { BlendMode = blend.ToSkia(), Color = SKColors.White.WithAlpha(ToByte(opacity)), IsAntialias = true };
        DrawBitmap(canvas, layer.Pixels, paint);
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
        var hasMask = layer.Mask != null && layer.MaskEnabled;
        using var replace = new SKPaint { BlendMode = SKBlendMode.Src };
        if (!hasMask && layer.Opacity >= 1)
        {
            tile.DrawRaw(adjusted, replace);
            return;
        }
        // result = backdrop × (1 - m) + adjusted × m, where m is the mask times the layer's opacity.
        using var coverage = tile.Sibling(mask: true);
        coverage.Canvas.Clear(SKColors.Black.WithAlpha(ToByte(layer.Opacity)));
        if (hasMask) MultiplyByMask(layer, coverage);
        coverage.Canvas.Flush();
        using (var adjustedCanvas = new SKCanvas(adjusted))
        using (var keep = new SKPaint { BlendMode = SKBlendMode.DstIn })
            adjustedCanvas.DrawBitmap(coverage.Bitmap, 0, 0, keep);
        using (var remove = new SKPaint { BlendMode = SKBlendMode.DstOut }) tile.DrawRaw(coverage.Bitmap, remove);
        using var plus = new SKPaint { BlendMode = SKBlendMode.Plus };
        tile.DrawRaw(adjusted, plus);
    }
}
