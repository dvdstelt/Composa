using Compositor.Filters;
using Compositor.Model;
using Compositor.Painting;
using Compositor.Rendering;
using Compositor.Selections;
using SkiaSharp;

namespace Compositor.Editing;

public sealed class ClipboardImage(SKBitmap pixels, SKPointI origin)
{
    public SKBitmap Pixels { get; } = pixels;
    public SKPointI Origin { get; } = origin;
}

public sealed partial class EditorSession
{
    /// <summary>Pixels copied inside the app, shared by every open project.</summary>
    public static ClipboardImage? Clipboard { get; set; }

    /// <summary>The active layer when it (or its mask) can take pixel edits.</summary>
    public Layer? EditableLayer => ActiveLayer is { } layer && (IsEditingMask || (layer.Pixels != null && layer.Shape == null)) ? layer : null;

    public bool CanEditPixels => EditableLayer != null;

    private SKBitmap Target(Layer layer) => IsEditingMask ? layer.Mask! : layer.Pixels!;

    private void SetTarget(Layer layer, SKBitmap bitmap)
    {
        if (IsEditingMask) layer.Mask = bitmap; else layer.Pixels = bitmap;
    }

    /// <summary>The matrix from the edit target's pixel grid to the document.</summary>
    public SKMatrix TargetMatrix(Layer layer) => IsEditingMask ? DocumentRenderer.MaskMatrix(layer) : layer.Matrix;

    /// <summary>The selection resampled into the edit target's grid, or null when nothing is selected.</summary>
    private SKBitmap? SelectionInTargetSpace(Layer layer)
    {
        if (document.Selection == null) return null;
        if (layer.Pixels == null) return document.Selection;
        return SelectionInLayerSpace(layer);
    }

    /// <summary>Grows a pure-translation layer so its bitmap covers the whole canvas, making every canvas pixel paintable.</summary>
    public void EnsureCoversCanvas(Layer layer)
    {
        if (layer.Pixels is not { } pixels || !layer.Transform.IsPureTranslation(pixels.Width, pixels.Height)) return;
        int x = (int)layer.Transform.X, y = (int)layer.Transform.Y;
        var have = new SKRectI(x, y, x + pixels.Width, y + pixels.Height);
        var want = Geometry.Union(have, document.Bounds);
        if (want == have || (long)want.Width * want.Height > IO.ImageFiles.MaxPixels * 2) return;
        var grown = Pixels.NewColor(want.Width, want.Height);
        using (var canvas = new SKCanvas(grown)) canvas.DrawBitmap(pixels, have.Left - want.Left, have.Top - want.Top);
        layer.Pixels = grown;
        if (layer.Mask is { } mask)
        {
            var grownMask = Pixels.NewMask(want.Width, want.Height, 255);
            using var canvas = new SKCanvas(grownMask);
            using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
            canvas.DrawBitmap(mask, new SKRect(have.Left - want.Left, have.Top - want.Top, have.Right - want.Left, have.Bottom - want.Top), paint);
            layer.Mask = grownMask;
        }
        layer.Transform = LayerTransform.Identity(want.Width, want.Height) with { X = want.Left, Y = want.Top };
    }

    /// <summary>original + (modified - original) × selection, for both color and mask bitmaps. Takes ownership of <paramref name="modified"/>.</summary>
    private static unsafe SKBitmap MixBySelection(SKBitmap original, SKBitmap modified, SKBitmap? selection)
    {
        if (selection == null) return modified;
        var bpp = original.BytesPerPixel;
        byte* o = (byte*)original.GetPixels(), m = (byte*)modified.GetPixels(), s = (byte*)selection.GetPixels();
        int width = Math.Min(original.Width, selection.Width), height = Math.Min(original.Height, selection.Height);
        Parallel.For(0, original.Height, y =>
        {
            byte* orow = o + (long)y * original.RowBytes, mrow = m + (long)y * modified.RowBytes;
            for (var x = 0; x < original.Width; x++)
            {
                var cover = x < width && y < height ? s[(long)y * selection.RowBytes + x] : 0;
                if (cover == 255) continue;
                for (var c = 0; c < bpp; c++)
                {
                    var i = x * bpp + c;
                    mrow[i] = (byte)(orow[i] + ((mrow[i] - orow[i]) * cover + 127) / 255);
                }
            }
        });
        return modified;
    }

    // ---- Fill and clear -----------------------------------------------------------------------------------------

    public void Fill(SKColor color, string name = "Fill")
    {
        if (EditableLayer is not { } layer) return;
        Apply(name, () =>
        {
            if (!IsEditingMask) EnsureCoversCanvas(layer);
            var original = Target(layer);
            var filled = original.Copy();
            if (IsEditingMask) filled.GetPixelSpan().Fill((byte)((color.Red * 54 + color.Green * 183 + color.Blue * 19) >> 8));
            else filled.Erase(color);
            var selection = SelectionInTargetSpace(layer);
            SetTarget(layer, MixBySelection(original, filled, selection));
            if (selection != document.Selection) selection?.Dispose();
        });
        Invalidate(AffectedArea(layer));
        LayersChanged?.Invoke();
    }

    /// <summary>Delete: erases the selected pixels (or paints the mask black).</summary>
    public void ClearSelection()
    {
        if (EditableLayer is not { } layer || document.Selection == null) return;
        Apply("Clear", () =>
        {
            var original = Target(layer);
            var cleared = original.Copy();
            cleared.Erase(SKColors.Transparent);
            var selection = SelectionInTargetSpace(layer);
            SetTarget(layer, MixBySelection(original, cleared, selection));
            if (selection != document.Selection) selection?.Dispose();
        });
        Invalidate(AffectedArea(layer));
        LayersChanged?.Invoke();
    }

    // ---- Previewed adjustments and filters ----------------------------------------------------------------------

    private Layer? previewLayer;
    private SKBitmap? previewOriginal;
    private SKBitmap? previewOriginalMask;
    private LayerTransform? previewTransform;
    private SKBitmap? previewSelection;

    public bool IsPreviewing => previewLayer != null;
    /// <summary>The untouched pixels behind the running preview, for histograms.</summary>
    public SKBitmap? PreviewOriginal => previewOriginal;

    /// <summary>Starts a live, cancellable change to the active layer's pixels or mask.</summary>
    public bool BeginPreview(string name)
    {
        if (EditableLayer is not { } layer) return false;
        Begin(name);
        previewLayer = layer;
        previewOriginal = Target(layer);
        previewOriginalMask = layer.Mask;
        previewTransform = layer.Transform;
        previewSelection = SelectionInTargetSpace(layer);
        return true;
    }

    public void PreviewAdjustment(Adjustment adjustment) => Preview(source =>
    {
        if (source.ColorType != SKColorType.Alpha8)
        {
            var copy = source.Copy();
            adjustment.Apply(copy, (int)(previewTransform?.X ?? 0), (int)(previewTransform?.Y ?? 0));
            return (copy, 0, 0);
        }
        using var color = MaskToColor(source);
        adjustment.Apply(color);
        return (ColorToMask(color), 0, 0);
    });

    public void PreviewFilter(FilterSettings settings) => Preview(source =>
    {
        if (source.ColorType != SKColorType.Alpha8) return ImageFilters.Run(source, settings);
        using var color = MaskToColor(source);
        var (result, growX, growY) = ImageFilters.Run(color, settings);
        using (result) return (ColorToMask(result), growX, growY);
    });

    public void PreviewContentAwareFill() => Preview(source =>
    {
        if (previewSelection == null || source.ColorType == SKColorType.Alpha8) return (source.Copy(), 0, 0);
        return (Inpaint.Fill(source, previewSelection), 0, 0);
    }, mix: false);

    private void Preview(Func<SKBitmap, (SKBitmap Result, int GrowX, int GrowY)> run, bool mix = true)
    {
        if (previewLayer is not { } layer || previewOriginal is not { } original) return;
        var before = AffectedArea(layer);
        var (result, growX, growY) = run(original);
        var canGrow = !IsEditingMask && previewSelection == null && previewOriginalMask == null
            && previewTransform!.IsPureTranslation(original.Width, original.Height);
        if ((growX > 0 || growY > 0) && !canGrow)
        {
            var cropped = new SKBitmap(original.Info);
            using (var canvas = new SKCanvas(cropped))
            using (var paint = new SKPaint { BlendMode = SKBlendMode.Src })
                canvas.DrawBitmap(result, -growX, -growY, paint);
            result.Dispose();
            result = cropped;
            growX = growY = 0;
        }
        if (growX == 0 && growY == 0 && mix) result = MixBySelection(original, result, previewSelection);
        var previous = Target(layer);
        SetTarget(layer, result);
        if (!ReferenceEquals(previous, original)) { Pixels.Invalidate(previous); previous.Dispose(); }
        layer.Transform = growX == 0 && growY == 0
            ? previewTransform!
            : LayerTransform.Identity(result.Width, result.Height) with { X = previewTransform!.X - growX, Y = previewTransform.Y - growY };
        Invalidate(Geometry.Union(before, AffectedArea(layer)));
    }

    public void CommitPreview()
    {
        if (previewLayer == null) return;
        EndPreview();
        Commit();
        LayersChanged?.Invoke();
    }

    public void CancelPreview()
    {
        if (previewLayer == null) return;
        EndPreview();
        Cancel();
    }

    private void EndPreview()
    {
        if (previewSelection != null && previewSelection != document.Selection) previewSelection.Dispose();
        previewLayer = null;
        previewOriginal = previewOriginalMask = previewSelection = null;
        previewTransform = null;
    }

    /// <summary>Applies an adjustment in one step (Invert, Auto Levels).</summary>
    public void Adjust(Adjustment adjustment)
    {
        if (!BeginPreview(adjustment.DisplayName)) return;
        PreviewAdjustment(adjustment);
        CommitPreview();
    }

    public void ApplyFilter(FilterSettings settings)
    {
        if (!BeginPreview(FilterSettings.DisplayName(settings.Kind))) return;
        PreviewFilter(settings);
        CommitPreview();
    }

    public void ContentAwareFill()
    {
        if (document.Selection == null || IsEditingMask || !BeginPreview("Content-Aware Fill")) return;
        PreviewContentAwareFill();
        CommitPreview();
    }

    private static unsafe SKBitmap MaskToColor(SKBitmap mask)
    {
        var color = Pixels.NewColor(mask.Width, mask.Height);
        byte* s = (byte*)mask.GetPixels(), d = (byte*)color.GetPixels();
        for (var y = 0; y < mask.Height; y++)
        for (var x = 0; x < mask.Width; x++)
        {
            var v = s[(long)y * mask.RowBytes + x];
            var p = d + (long)y * color.RowBytes + x * 4;
            p[0] = p[1] = p[2] = v; p[3] = 255;
        }
        return color;
    }

    private static unsafe SKBitmap ColorToMask(SKBitmap color)
    {
        var mask = Pixels.NewMask(color.Width, color.Height);
        byte* s = (byte*)color.GetPixels(), d = (byte*)mask.GetPixels();
        for (var y = 0; y < color.Height; y++)
        for (var x = 0; x < color.Width; x++)
            d[(long)y * mask.RowBytes + x] = s[(long)y * color.RowBytes + x * 4];
        return mask;
    }

    // ---- Gradient and shapes ------------------------------------------------------------------------------------

    /// <summary>Draws a gradient between two document points onto the pending edit's original pixels (call Begin first).</summary>
    public void DrawGradient(Layer layer, SKBitmap original, SKPoint from, SKPoint to)
    {
        var matrix = TargetMatrix(layer);
        if (!matrix.TryInvert(out var inverse)) return;
        var start = Foreground;
        var end = GradientToTransparent ? Foreground.WithAlpha(0) : Background;
        if (IsEditingMask)
        {
            byte Gray(SKColor c) => (byte)((c.Red * 54 + c.Green * 183 + c.Blue * 19) >> 8);
            // Alpha8 targets keep only alpha, so the gray value travels in the alpha channel.
            start = new SKColor(0, 0, 0, Gray(Foreground));
            end = new SKColor(0, 0, 0, Gray(Background));
        }
        var painted = original.Copy();
        using (var canvas = new SKCanvas(painted))
        {
            canvas.SetMatrix(in inverse);
            using var shader = GradientRadial
                ? SKShader.CreateRadialGradient(from, Math.Max(0.5f, SKPoint.Distance(from, to)), [start, end], SKShaderTileMode.Clamp)
                : SKShader.CreateLinearGradient(from, to, [start, end], SKShaderTileMode.Clamp);
            using var paint = new SKPaint { Shader = shader, IsDither = true };
            if (IsEditingMask) paint.BlendMode = SKBlendMode.Src;
            paint.Color = paint.Color.WithAlpha((byte)Math.Round(Math.Clamp(GradientOpacity, 0, 1) * 255));
            canvas.DrawPaint(paint);
        }
        var selection = SelectionInTargetSpace(layer);
        var previous = Target(layer);
        SetTarget(layer, MixBySelection(original, painted, selection));
        if (selection != document.Selection) selection?.Dispose();
        if (!ReferenceEquals(previous, original)) { Pixels.Invalidate(previous); previous.Dispose(); }
        Invalidate(AffectedArea(layer));
    }

    public static SKBitmap RenderShape(ShapeStyle style, int width, int height)
    {
        var pixels = Pixels.NewColor(width, height);
        using var canvas = new SKCanvas(pixels);
        using var paint = new SKPaint { Color = new SKColor(style.Fill), IsAntialias = true };
        var rect = new SKRect(0, 0, width, height);
        switch (style.Kind)
        {
            case ShapeKind.Ellipse: canvas.DrawOval(rect, paint); break;
            case ShapeKind.RoundedRectangle:
                var radius = (float)Math.Min(style.CornerRadius, Math.Min(width, height) / 2.0);
                canvas.DrawRoundRect(rect, radius, radius, paint);
                break;
            default: canvas.DrawRect(rect, paint); break;
        }
        return pixels;
    }

    /// <summary>Adds a live shape layer covering a document rectangle.</summary>
    public Layer? AddShape(SKRect rect)
    {
        rect = SKRect.Create((float)Math.Round(rect.Left), (float)Math.Round(rect.Top), (float)Math.Round(rect.Width), (float)Math.Round(rect.Height));
        if (rect.Width < 1 || rect.Height < 1) return null;
        var style = new ShapeStyle(ShapeKind, (uint)Foreground, ShapeCornerRadius);
        var layer = Layer.Raster(document.UniqueName(ShapeKind == ShapeKind.Ellipse ? "Ellipse" : "Rectangle"),
            RenderShape(style, (int)rect.Width, (int)rect.Height), rect.Left, rect.Top);
        layer.Shape = style;
        Apply("Shape", () => document.InsertAboveActive(layer));
        Invalidate(AffectedArea(layer));
        LayersChanged?.Invoke();
        return layer;
    }

    /// <summary>Turns a live shape into ordinary pixels so it can be painted on.</summary>
    public void RasterizeShape(Layer layer)
    {
        if (layer.Shape == null) return;
        Apply("Rasterize Layer", () => layer.Shape = null);
        LayersChanged?.Invoke();
    }

    // ---- Clipboard ----------------------------------------------------------------------------------------------

    private ClipboardImage? Grab(SKBitmap source, SKRectI sourceArea)
    {
        // `source` covers `sourceArea` of the document; the result is trimmed to the selection.
        var area = document.Selection != null ? Geometry.Intersect(SelectionMask.Bounds(document.Selection), sourceArea) : sourceArea;
        if (area.IsEmpty) return null;
        var pixels = Pixels.NewColor(area.Width, area.Height);
        using var canvas = new SKCanvas(pixels);
        canvas.DrawBitmap(source, sourceArea.Left - area.Left, sourceArea.Top - area.Top);
        if (document.Selection != null)
        {
            using var paint = new SKPaint { BlendMode = SKBlendMode.DstIn };
            canvas.DrawBitmap(document.Selection, -area.Left, -area.Top, paint);
        }
        return new ClipboardImage(pixels, new SKPointI(area.Left, area.Top));
    }

    private ClipboardImage? GrabLayer(Layer layer)
    {
        var copy = layer.Clone();
        copy.Visible = true; copy.Opacity = 1; copy.Blend = BlendMode.Normal; copy.Clipped = false;
        var area = document.Selection != null ? document.Bounds : Geometry.Union(document.Bounds, Geometry.RoundOut(layer.Bounds));
        using var rendered = DocumentRenderer.RenderLayers(document, [copy], area);
        return Grab(rendered, area);
    }

    public bool CanCopy => ActiveLayer is { Pixels: not null };

    public bool Copy()
    {
        if (ActiveLayer is not { Pixels: not null } layer || GrabLayer(layer) is not { } image) return false;
        Clipboard = image;
        return true;
    }

    public bool CopyMerged()
    {
        if (Grab(Composite(), document.Bounds) is not { } image) return false;
        Clipboard = image;
        return true;
    }

    public void Cut()
    {
        if (!Copy()) return;
        if (document.Selection != null) ClearSelection(); else DeleteSelectedLayers();
    }

    /// <summary>Pastes as a new layer: where it was copied from when that still fits the canvas, otherwise centered.</summary>
    public Layer? Paste(ClipboardImage? image = null, string name = "Pasted Layer")
    {
        image ??= Clipboard;
        if (image == null) return null;
        var pixels = image.Pixels.Copy();
        var placed = new SKRectI(image.Origin.X, image.Origin.Y, image.Origin.X + pixels.Width, image.Origin.Y + pixels.Height);
        var fits = document.Bounds.Contains(placed);
        var layer = Layer.Raster(document.UniqueName(name), pixels,
            fits ? placed.Left : Math.Round((document.Width - pixels.Width) / 2.0),
            fits ? placed.Top : Math.Round((document.Height - pixels.Height) / 2.0));
        Apply("Paste", () => document.InsertAboveActive(layer));
        EditingMask = false;
        Invalidate(AffectedArea(layer));
        LayersChanged?.Invoke();
        return layer;
    }

    /// <summary>Ctrl+J: the selected pixels on a new layer, or a duplicate of the layer when nothing is selected.</summary>
    public void LayerViaCopy()
    {
        if (document.Selection == null) { DuplicateSelectedLayers(); return; }
        if (ActiveLayer is not { Pixels: not null } source || GrabLayer(source) is not { } image) return;
        var layer = Layer.Raster(document.UniqueName("Layer"), image.Pixels, image.Origin.X, image.Origin.Y);
        Apply("Layer via Copy", () =>
        {
            document.InsertAboveActive(layer);
            document.Selection = null;
        });
        EditingMask = false;
        Invalidate(AffectedArea(layer));
        LayersChanged?.Invoke();
        SelectionChanged?.Invoke();
    }
}
