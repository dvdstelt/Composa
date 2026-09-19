using Compositor.Model;
using Compositor.Rendering;
using Compositor.Selections;
using SkiaSharp;

namespace Compositor.Editing;

public enum Anchor { TopLeft, Top, TopRight, Left, Center, Right, BottomLeft, Bottom, BottomRight }

public sealed partial class EditorSession
{
    /// <summary>Crops (or extends) the canvas to a document-space rectangle. Layer pixels outside it are kept.</summary>
    public void Crop(SKRectI rect, string name = "Crop")
    {
        rect = new SKRectI(rect.Left, rect.Top, rect.Left + Math.Clamp(rect.Width, 1, Document.MaxSide), rect.Top + Math.Clamp(rect.Height, 1, Document.MaxSide));
        if (rect == document.Bounds) return;
        Apply(name, () =>
        {
            var shift = SKMatrix.CreateTranslation(-rect.Left, -rect.Top);
            foreach (var layer in document.AllLayers())
            {
                if (layer.Pixels != null) layer.Transform = layer.Transform.Translated(-rect.Left, -rect.Top);
                else if (layer.Mask != null) layer.Mask = RemapDocumentMask(layer.Mask, rect.Width, rect.Height, shift, 255);
            }
            document.Selection = document.Selection == null ? null : SelectionMask.Remap(document.Selection, rect.Width, rect.Height, shift);
            document.Width = rect.Width;
            document.Height = rect.Height;
        });
        composite = null;
        InvalidateAll();
        LayersChanged?.Invoke();
        SelectionChanged?.Invoke();
    }

    public void ResizeCanvas(int width, int height, Anchor anchor)
    {
        int dx = document.Width - width, dy = document.Height - height;
        var column = (int)anchor % 3;
        var row = (int)anchor / 3;
        var left = column == 0 ? 0 : column == 1 ? dx / 2 : dx;
        var top = row == 0 ? 0 : row == 1 ? dy / 2 : dy;
        Crop(new SKRectI(left, top, left + width, top + height), "Canvas Size");
    }

    /// <summary>Resamples the whole document to a new pixel size.</summary>
    public void ResizeImage(int width, int height, double? resolution = null)
    {
        width = Math.Clamp(width, 1, Document.MaxSide);
        height = Math.Clamp(height, 1, Document.MaxSide);
        if (width == document.Width && height == document.Height)
        {
            if (resolution is { } only && only != document.Resolution) Apply("Image Size", () => document.Resolution = Math.Clamp(only, 1, 9600));
            return;
        }
        double sx = (double)width / document.Width, sy = (double)height / document.Height;
        Apply("Image Size", () =>
        {
            var scale = SKMatrix.CreateScale((float)sx, (float)sy);
            foreach (var layer in document.AllLayers())
            {
                if (layer.Pixels is { } pixels)
                {
                    var t = layer.Transform;
                    if (t.IsPureTranslation(pixels.Width, pixels.Height))
                    {
                        // Unscaled layers are resampled so they stay paintable at full resolution.
                        int w = Math.Max(1, (int)Math.Round(pixels.Width * sx)), h = Math.Max(1, (int)Math.Round(pixels.Height * sy));
                        layer.Pixels = Resample(pixels, w, h);
                        if (layer.Mask != null) layer.Mask = Resample(layer.Mask, w, h);
                        layer.Transform = LayerTransform.Identity(w, h) with { X = Math.Round(t.X * sx), Y = Math.Round(t.Y * sy) };
                    }
                    else
                    {
                        layer.Transform = t with
                        {
                            X = t.X * sx, Y = t.Y * sy, Width = t.Width * sx, Height = t.Height * sy,
                            Distort = t.Distort?.Select((v, i) => (float)(v * (i % 2 == 0 ? sx : sy))).ToArray()
                        };
                    }
                }
                else if (layer.Mask != null) layer.Mask = RemapDocumentMask(layer.Mask, width, height, scale, 0);
            }
            document.Selection = document.Selection == null ? null : SelectionMask.Remap(document.Selection, width, height, scale);
            document.Width = width;
            document.Height = height;
            if (resolution is { } dpi) document.Resolution = Math.Clamp(dpi, 1, 9600);
        });
        composite = null;
        InvalidateAll();
        LayersChanged?.Invoke();
        SelectionChanged?.Invoke();
    }

    public static SKBitmap Resample(SKBitmap source, int width, int height)
    {
        var result = new SKBitmap(source.Info.WithSize(width, height));
        var sampling = width < source.Width || height < source.Height
            ? new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)
            : new SKSamplingOptions(SKCubicResampler.CatmullRom);
        if (!source.ScalePixels(result, sampling)) throw new InvalidOperationException("The image could not be resampled.");
        return result;
    }

    private static SKBitmap RemapDocumentMask(SKBitmap mask, int width, int height, SKMatrix matrix, byte fill)
    {
        var result = Pixels.NewMask(width, height, fill);
        using var canvas = new SKCanvas(result);
        canvas.SetMatrix(in matrix);
        using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
        canvas.DrawImage(Pixels.ImageOf(mask), 0, 0, new SKSamplingOptions(SKFilterMode.Linear), paint);
        return result;
    }

    public void FlipCanvas(bool horizontally)
    {
        Apply(horizontally ? "Flip Canvas Horizontal" : "Flip Canvas Vertical", () =>
        {
            var mirror = horizontally
                ? SKMatrix.CreateScale(-1, 1, document.Width / 2f, 0)
                : SKMatrix.CreateScale(1, -1, 0, document.Height / 2f);
            foreach (var layer in document.AllLayers())
            {
                if (layer.Pixels != null) layer.Transform = Mirror(layer.Transform, horizontally, horizontally ? document.Width : document.Height);
                else if (layer.Mask != null) layer.Mask = RemapDocumentMask(layer.Mask, document.Width, document.Height, mirror, 255);
            }
            if (document.Selection != null) document.Selection = SelectionMask.Remap(document.Selection, document.Width, document.Height, mirror);
        });
        InvalidateAll();
        LayersChanged?.Invoke();
        SelectionChanged?.Invoke();
    }

    /// <summary>Flips the selected layers in place, each around its own center.</summary>
    public void FlipLayers(bool horizontally)
    {
        var layers = SelectedRoots().SelectMany(r => Document.Flatten([r])).Where(l => l.Pixels != null).ToList();
        if (layers.Count == 0) return;
        Apply(horizontally ? "Flip Layer Horizontal" : "Flip Layer Vertical", () =>
        {
            foreach (var layer in layers)
            {
                var t = layer.Transform;
                var extent = horizontally ? 2 * t.X + t.Width : 2 * t.Y + t.Height;
                layer.Transform = Mirror(t, horizontally, extent);
            }
        });
        InvalidateAll();
    }

    /// <summary>Reflects a transform across the vertical (or horizontal) line in the middle of 0…extent.</summary>
    private static LayerTransform Mirror(LayerTransform t, bool horizontally, double extent)
    {
        float[]? distort = null;
        if (t.Distort is { Length: 8 } d)
            distort = horizontally
                ? [-d[2], d[3], -d[0], d[1], -d[6], d[7], -d[4], d[5]]
                : [d[6], -d[7], d[4], -d[5], d[2], -d[3], d[0], -d[1]];
        return horizontally
            ? t with { X = extent - t.X - t.Width, FlipHorizontal = !t.FlipHorizontal, Rotation = -t.Rotation, Distort = distort }
            : t with { Y = extent - t.Y - t.Height, FlipVertical = !t.FlipVertical, Rotation = -t.Rotation, Distort = distort };
    }

    /// <summary>Shrinks the canvas to the union of every visible layer's pixels.</summary>
    public void TrimCanvas()
    {
        using var flat = Flatten();
        using var alpha = Pixels.NewMask(flat.Width, flat.Height);
        using (var canvas = new SKCanvas(alpha)) canvas.DrawBitmap(flat, 0, 0);
        var bounds = SelectionMask.Bounds(alpha);
        if (!bounds.IsEmpty) Crop(bounds, "Trim");
    }
}
