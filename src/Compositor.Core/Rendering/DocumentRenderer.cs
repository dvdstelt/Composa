using System.Runtime.CompilerServices;
using Compositor.Model;
using SkiaSharp;

namespace Compositor.Rendering;

/// <summary>Pixel formats and helpers shared by everything that touches bitmaps.</summary>
public static class Pixels
{
    public static SKImageInfo ColorInfo(int width, int height) => new(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
    public static SKImageInfo MaskInfo(int width, int height) => new(width, height, SKColorType.Alpha8, SKAlphaType.Premul);

    public static SKBitmap NewColor(int width, int height)
    {
        var bitmap = new SKBitmap(ColorInfo(Math.Max(1, width), Math.Max(1, height)));
        bitmap.Erase(SKColors.Transparent);
        return bitmap;
    }

    public static SKBitmap NewMask(int width, int height, byte fill = 0)
    {
        var bitmap = new SKBitmap(MaskInfo(Math.Max(1, width), Math.Max(1, height)));
        bitmap.GetPixelSpan().Fill(fill);
        return bitmap;
    }

    private static readonly ConditionalWeakTable<SKBitmap, SKImage> Images = new();

    /// <summary>A no-copy image over the bitmap's pixels. Call <see cref="Invalidate"/> after changing them.</summary>
    public static SKImage ImageOf(SKBitmap bitmap)
    {
        lock (Images)
        {
            if (Images.TryGetValue(bitmap, out var image)) return image;
            image = SKImage.FromPixels(bitmap.PeekPixels());
            Images.Add(bitmap, image);
            return image;
        }
    }

    public static void Invalidate(SKBitmap bitmap)
    {
        lock (Images)
        {
            if (!Images.TryGetValue(bitmap, out var image)) return;
            Images.Remove(bitmap);
            image.Dispose();
        }
    }
}

/// <summary>A buffer covering <see cref="Area"/> of the document, with a canvas already translated into document space.</summary>
internal sealed class Tile : IDisposable
{
    public SKBitmap Bitmap { get; }
    public SKCanvas Canvas { get; }
    public SKRectI Area { get; }

    public Tile(SKRectI area, bool mask = false)
    {
        Area = area;
        Bitmap = mask ? Pixels.NewMask(area.Width, area.Height) : Pixels.NewColor(area.Width, area.Height);
        Canvas = new SKCanvas(Bitmap);
        Canvas.Translate(-area.Left, -area.Top);
    }

    /// <summary>Draws this tile onto another at its document position.</summary>
    public void DrawOnto(SKCanvas canvas, SKPaint? paint = null) => canvas.DrawBitmap(Bitmap, Area.Left, Area.Top, paint);

    public void Dispose()
    {
        Canvas.Dispose();
        Bitmap.Dispose();
    }
}

public sealed class RenderOptions
{
    /// <summary>Layers whose stored pixels are replaced while rendering, for live previews.</summary>
    public Dictionary<Guid, Layer>? Overrides { get; init; }
    /// <summary>Ignore every layer's visibility flag except these (Alt-click solo).</summary>
    public Guid? Solo { get; init; }
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
    public static void Render(Document document, SKBitmap target, SKRectI area, RenderOptions? options = null)
    {
        area = Geometry.Intersect(area, document.Bounds);
        if (area.IsEmpty) return;
        using var tile = new Tile(area);
        RenderNodes(document.Layers, tile, options);
        using var canvas = new SKCanvas(target);
        using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
        tile.DrawOnto(canvas, paint);
        Pixels.Invalidate(target);
    }

    /// <summary>Renders only the given layers (and their clipped followers), for merges and copies.</summary>
    public static SKBitmap RenderLayers(Document document, IEnumerable<Layer> layers, SKRectI area)
    {
        using var tile = new Tile(area);
        RenderNodes(layers.ToList(), tile, null);
        return tile.Bitmap.Copy();
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
            if (layer.Clipped) { clipped.Insert(0, layer); continue; } // A clipped layer with no base shows nothing.
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

        using var content = new Tile(tile.Area);
        if (layer.IsGroup) RenderNodes(layer.Children, content, options);
        else DrawPixels(layer, content.Canvas, 1, BlendMode.Normal);
        if (hasMask) MultiplyByMask(layer, content);

        if (clipped.Count > 0)
        {
            using var baseAlpha = content.Bitmap.Copy();
            foreach (var top in clipped)
            {
                if (top.IsAdjustment) { ApplyAdjustment(top, content); continue; }
                using var over = new Tile(tile.Area);
                if (top.IsGroup) RenderNodes(top.Children, over, options);
                else DrawPixels(top, over.Canvas, 1, BlendMode.Normal);
                if (top.Mask != null && top.MaskEnabled) MultiplyByMask(top, over);
                using (var clip = new SKPaint { BlendMode = SKBlendMode.DstIn }) over.Canvas.DrawBitmap(baseAlpha, tile.Area.Left, tile.Area.Top, clip);
                using var blend = new SKPaint { BlendMode = top.Blend.ToSkia(), Color = SKColors.White.WithAlpha(ToByte(top.Opacity)) };
                over.DrawOnto(content.Canvas, blend);
            }
            using var restore = new SKPaint { BlendMode = SKBlendMode.DstIn };
            content.Canvas.DrawBitmap(baseAlpha, tile.Area.Left, tile.Area.Top, restore);
        }

        using var paint = new SKPaint { BlendMode = layer.Blend.ToSkia(), Color = SKColors.White.WithAlpha(ToByte(layer.Opacity)) };
        content.DrawOnto(tile.Canvas, paint);
    }

    private static byte ToByte(double opacity) => (byte)Math.Clamp(Math.Round(opacity * 255), 0, 255);

    public static SKSamplingOptions SamplingFor(SKMatrix matrix)
    {
        if (matrix.ScaleX == 1 && matrix.ScaleY == 1 && matrix.SkewX == 0 && matrix.SkewY == 0 && matrix.Persp0 == 0 && matrix.Persp1 == 0
            && matrix.TransX == MathF.Round(matrix.TransX) && matrix.TransY == MathF.Round(matrix.TransY))
            return new SKSamplingOptions(SKFilterMode.Nearest);
        var scale = Math.Sqrt(Math.Abs(matrix.ScaleX * matrix.ScaleY - matrix.SkewX * matrix.SkewY));
        return scale < 0.6 ? new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear) : new SKSamplingOptions(SKCubicResampler.CatmullRom);
    }

    private static void DrawPixels(Layer layer, SKCanvas canvas, double opacity, BlendMode blend)
    {
        if (layer.Pixels == null) return;
        var matrix = layer.Matrix;
        canvas.Save();
        canvas.Concat(in matrix);
        using var paint = new SKPaint { BlendMode = blend.ToSkia(), Color = SKColors.White.WithAlpha(ToByte(opacity)), IsAntialias = true };
        canvas.DrawImage(Pixels.ImageOf(layer.Pixels), 0, 0, SamplingFor(canvas.TotalMatrix), paint);
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
        canvas.DrawImage(Pixels.ImageOf(mask), 0, 0, SamplingFor(canvas.TotalMatrix), paint);
        canvas.Restore();
    }

    private static void ApplyAdjustment(Layer layer, Tile tile)
    {
        if (layer.Adjustment == null || layer.Adjustment.IsIdentity) return;
        tile.Canvas.Flush();
        using var adjusted = tile.Bitmap.Copy();
        layer.Adjustment.Apply(adjusted, tile.Area.Left, tile.Area.Top);
        var hasMask = layer.Mask != null && layer.MaskEnabled;
        using var replace = new SKPaint { BlendMode = SKBlendMode.Src };
        if (!hasMask && layer.Opacity >= 1)
        {
            tile.Canvas.DrawBitmap(adjusted, tile.Area.Left, tile.Area.Top, replace);
            return;
        }
        // result = backdrop * (1 - m) + adjusted * m, where m is the mask times the layer's opacity.
        using var coverage = new Tile(tile.Area, mask: true);
        coverage.Canvas.Clear(SKColors.Black.WithAlpha(ToByte(layer.Opacity)));
        if (hasMask) MultiplyByMask(layer, coverage);
        using var adjustedCanvas = new SKCanvas(adjusted);
        using (var keep = new SKPaint { BlendMode = SKBlendMode.DstIn }) adjustedCanvas.DrawBitmap(coverage.Bitmap, 0, 0, keep);
        using (var remove = new SKPaint { BlendMode = SKBlendMode.DstOut }) coverage.DrawOnto(tile.Canvas, remove);
        using var plus = new SKPaint { BlendMode = SKBlendMode.Plus };
        tile.Canvas.DrawBitmap(adjusted, tile.Area.Left, tile.Area.Top, plus);
    }
}
