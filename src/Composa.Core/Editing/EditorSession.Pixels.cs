using Composa.Filters;
using Composa.Model;
using Composa.Painting;
using Composa.Rendering;
using Composa.Selections;
using SkiaSharp;

namespace Composa.Editing;

public sealed class ClipboardImage(SKBitmap pixels, SKPointI origin)
{
    public SKBitmap Pixels { get; } = pixels;
    public SKPointI Origin { get; } = origin;
}

/// <summary>
/// Whole layers copied with nothing selected. Paste brings them back complete (folder contents, masks, effects,
/// editable text and shapes, adjustments): in the project they came from as copies above the active layer, in
/// another project centered on its canvas. The layers are clones sharing the source's immutable bitmaps.
/// </summary>
public sealed class ClipboardLayers(IReadOnlyList<Layer> layers, EditorSession source, int canvasWidth, int canvasHeight)
{
    /// <summary>Bottom to top, roots only: a layer inside a copied folder comes with the folder.</summary>
    public IReadOnlyList<Layer> Layers { get; } = layers;
    public EditorSession Source { get; } = source;
    public int CanvasWidth { get; } = canvasWidth;
    public int CanvasHeight { get; } = canvasHeight;
}

public sealed partial class EditorSession
{
    /// <summary>Pixels copied inside the app, shared by every open project.</summary>
    public static ClipboardImage? Clipboard { get; set; }
    /// <summary>Layers copied whole (Copy with nothing selected), shared by every open project. Set alongside <see cref="Clipboard"/> and cleared by a pixel copy.</summary>
    public static ClipboardLayers? CopiedLayers { get; set; }

    /// <summary>The active layer when it (or its mask) can take pixel edits.</summary>
    public Layer? EditableLayer => ActiveLayer is { } layer && (IsEditingMask || (layer.Pixels != null && !layer.IsLive)) ? layer : null;

    public bool CanEditPixels => EditableLayer != null;

    /// <summary>Fill also recolors live text: a text layer with nothing selected takes the color as its own.</summary>
    public bool CanFill => CanEditPixels || (!IsEditingMask && document.Selection == null && ActiveLayer?.Text != null);

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
        if (layer.Pixels is not { } pixels) return;
        if (!layer.Transform.IsPureTranslation(pixels.Width, pixels.Height)) { PadToCoverCanvas(layer); return; }
        int x = (int)layer.Transform.X, y = (int)layer.Transform.Y;
        var have = new SKRectI(x, y, x + pixels.Width, y + pixels.Height);
        var want = Geometry.Union(have, document.Bounds);
        if (want == have || (long)want.Width * want.Height > IO.ImageFiles.MaxPixels * 2) return;
        var grown = Pixels.NewColor(want.Width, want.Height);
        using (var canvas = new SKCanvas(grown)) canvas.DrawBitmap(pixels, have.Left - want.Left, have.Top - want.Top);
        layer.Pixels = grown;
        if (layer.Mask is { } mask)
        {
            // A hide-all mask stays hide-all over the new area; any other mask reveals it.
            var hidesAll = mask.GetPixel(0, 0).Alpha == 0 && mask.GetPixel(mask.Width - 1, 0).Alpha == 0
                && mask.GetPixel(0, mask.Height - 1).Alpha == 0 && mask.GetPixel(mask.Width - 1, mask.Height - 1).Alpha == 0;
            var grownMask = Pixels.NewMask(want.Width, want.Height, hidesAll ? (byte)0 : (byte)255);
            using var canvas = new SKCanvas(grownMask);
            using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
            canvas.DrawBitmap(mask, new SKRect(have.Left - want.Left, have.Top - want.Top, have.Right - want.Left, have.Bottom - want.Top), paint);
            layer.Mask = grownMask;
        }
        layer.Transform = LayerTransform.Identity(want.Width, want.Height) with { X = want.Left, Y = want.Top };
    }

    /// <summary>
    /// The same for a scaled, flipped or rotated layer: transparent source pixels are added around the bitmap until the
    /// canvas is covered, and the placement is adjusted so that the existing pixels stay exactly where they are.
    /// </summary>
    private void PadToCoverCanvas(Layer layer)
    {
        var pixels = layer.Pixels!;
        var t = layer.Transform;
        if (t.Distort != null || !layer.Matrix.TryInvert(out var inverse)) return;
        var needed = inverse.MapRect(new SKRect(0, 0, document.Width, document.Height));
        int left = Math.Max(0, (int)Math.Ceiling(-needed.Left)), top = Math.Max(0, (int)Math.Ceiling(-needed.Top));
        int right = Math.Max(0, (int)Math.Ceiling(needed.Right - pixels.Width)), bottom = Math.Max(0, (int)Math.Ceiling(needed.Bottom - pixels.Height));
        if (left + top + right + bottom == 0) return;
        long width = pixels.Width + left + right, height = pixels.Height + top + bottom;
        if (width > Document.MaxSide || height > Document.MaxSide || width * height > IO.ImageFiles.MaxPixels * 2) return;

        var grown = Pixels.NewColor((int)width, (int)height);
        using (var canvas = new SKCanvas(grown)) canvas.DrawBitmap(pixels, left, top);
        layer.Pixels = grown;
        if (layer.Mask is { } mask)
        {
            var hidesAll = mask.GetPixel(0, 0).Alpha == 0 && mask.GetPixel(mask.Width - 1, mask.Height - 1).Alpha == 0;
            var grownMask = Pixels.NewMask((int)width, (int)height, hidesAll ? (byte)0 : (byte)255);
            using var canvas = new SKCanvas(grownMask);
            using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
            canvas.DrawBitmap(mask, new SKRect(left, top, left + pixels.Width, top + pixels.Height), paint);
            layer.Mask = grownMask;
        }

        // In the unrotated frame the padding extends the box; flips swap which side it lands on.
        double sx = t.Width / pixels.Width, sy = t.Height / pixels.Height;
        double padLeft = (t.FlipHorizontal ? right : left) * sx, padTop = (t.FlipVertical ? bottom : top) * sy;
        double x = t.X - padLeft, y = t.Y - padTop, w = width * sx, h = height * sy;
        // The box turns about its center, which has just moved by delta; shifting the box by R·delta - delta keeps
        // every existing pixel where it was.
        double deltaX = x + w / 2 - (t.X + t.Width / 2), deltaY = y + h / 2 - (t.Y + t.Height / 2);
        double radians = t.Rotation * Math.PI / 180, cos = Math.Cos(radians), sin = Math.Sin(radians);
        x += deltaX * cos - deltaY * sin - deltaX;
        y += deltaX * sin + deltaY * cos - deltaY;
        layer.Transform = t with { X = x, Y = y, Width = w, Height = h };
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
        if (!IsEditingMask && document.Selection == null && ActiveLayer is { Text: not null } text && RecolorText(text, color)) return;
        if (EditableLayer is not { } layer) return;
        Apply(name, () =>
        {
            if (!IsEditingMask) EnsureCoversCanvas(layer);
            var original = Target(layer);
            var filled = Pixels.Clone(original);
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
            var cleared = Pixels.Clone(original);
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
    /// <param name="coverCanvas">Grow the layer to the canvas first, for edits that may fill beyond the layer's own edges.</param>
    public bool BeginPreview(string name, bool coverCanvas = false)
    {
        if (EditableLayer is not { } layer) return false;
        Begin(name);
        if (coverCanvas && !IsEditingMask) EnsureCoversCanvas(layer);
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
            var copy = Pixels.Clone(source);
            adjustment.Apply(copy, (int)(previewTransform?.X ?? 0), (int)(previewTransform?.Y ?? 0));
            return (copy, 0, 0);
        }
        using var color = MaskToColor(source);
        adjustment.Apply(color);
        return (ColorToMask(color), 0, 0);
    });

    /// <summary>
    /// Starts a filter's live preview. Vignette also paints an empty layer, which has no pixels of its own yet: the
    /// layer is grown to the canvas first, and the vignette then frames and fills the whole canvas.
    /// </summary>
    public bool BeginFilter(FilterKind kind)
    {
        var fillsCanvas = kind == FilterKind.Vignette && !IsEditingMask && EditableLayer is { Pixels: { } pixels } && IsClear(pixels);
        if (!BeginPreview(FilterSettings.DisplayName(kind), coverCanvas: fillsCanvas)) return false;
        previewFillsClear = fillsCanvas;
        return true;
    }

    private bool previewFillsClear;

    private static unsafe bool IsClear(SKBitmap pixels)
    {
        var data = (byte*)pixels.GetPixels();
        for (var y = 0; y < pixels.Height; y++)
        {
            var row = data + (long)y * pixels.RowBytes;
            for (var x = 3; x < pixels.Width * 4; x += 4) if (row[x] != 0) return false;
        }
        return true;
    }

    public void PreviewFilter(FilterSettings settings)
    {
        // Radii and distances are given in document pixels; a scaled-down photo has several source pixels to each.
        if (previewLayer is { } target)
        {
            var matrix = TargetMatrix(target);
            var scale = Math.Sqrt(Math.Abs(matrix.ScaleX * matrix.ScaleY - matrix.SkewX * matrix.SkewY));
            if (scale > 1e-6 && Math.Abs(scale - 1) > 1e-3)
                settings = settings with { Radius = settings.Radius / scale, BloomRadius = settings.BloomRadius / scale, TonalRadius = settings.TonalRadius / scale };
            // A floating layer's blur spreads past its edges; one that fills the canvas has nothing to spread into.
            var bounds = target.Pixels != null ? target.Bounds : new SKRect(0, 0, document.Width, document.Height);
            settings = settings with { ClampEdges = bounds.Left <= 0.5f && bounds.Top <= 0.5f && bounds.Right >= document.Width - 0.5f && bounds.Bottom >= document.Height - 0.5f };
            if (settings.Kind == FilterKind.Vignette)
            {
                // An empty layer takes the vignette across the canvas; a layer with pixels is framed and recolored as it is.
                SKRect? frame = null;
                if (previewFillsClear && matrix.TryInvert(out var inverse)) frame = inverse.MapRect(new SKRect(0, 0, document.Width, document.Height));
                settings = settings with { VignetteFillsClear = previewFillsClear, VignetteFrame = frame };
            }
        }
        PreviewFilterCore(settings);
    }

    private void PreviewFilterCore(FilterSettings settings) => Preview(source =>
    {
        if (source.ColorType != SKColorType.Alpha8) return ImageFilters.Run(source, settings);
        using var color = MaskToColor(source);
        var (result, growX, growY) = ImageFilters.Run(color, settings);
        using (result) return (ColorToMask(result), growX, growY);
    });

    public void PreviewContentAwareFill() => Preview(source =>
    {
        if (previewSelection == null || source.ColorType == SKColorType.Alpha8) return (Pixels.Clone(source), 0, 0);
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
        if (!BeginFilter(settings.Kind)) return;
        PreviewFilter(settings);
        CommitPreview();
    }

    public void ContentAwareFill()
    {
        if (document.Selection == null || IsEditingMask) return;
        if (Selections.SelectionMask.Bounds(document.Selection) is var hole && (long)hole.Width * hole.Height > Inpaint.MaxArea)
            throw new InvalidOperationException("The selection is too large for Content-Aware Fill. Select a smaller area (up to about 16 megapixels).");
        // Growing the layer to the canvas lets a selection past the image's edge extend the image.
        if (!BeginPreview("Content-Aware Fill", coverCanvas: true)) return;
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
        {
            // Filters that spread past the edges leave partly transparent pixels there; the mask keeps their straight
            // value, otherwise a blurred mask would fade out along its whole border.
            var p = s + (long)y * color.RowBytes + x * 4;
            d[(long)y * mask.RowBytes + x] = p[3] == 0 ? (byte)0 : (byte)Math.Min(255, (p[0] * 255 + p[3] / 2) / p[3]);
        }
        return mask;
    }

    // ---- Gradient and shapes ------------------------------------------------------------------------------------

    /// <summary>Draws a gradient between two document points onto the pending edit's original pixels (call Begin first).</summary>
    public void DrawGradient(Layer layer, SKBitmap original, SKPoint from, SKPoint to)
    {
        if (!IsInteracting || document.Find(layer.Id) != layer) return; // The edit this drag belonged to is over.
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
        var painted = Pixels.Clone(original);
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
            case ShapeKind.Line:
                // The ends sit where they were dragged, as fractions of the box; a line without stored ends runs corner
                // to corner, inset by half its thickness so the stroke stays inside the layer.
                var thickness = (float)Math.Max(1, style.LineWidth);
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = thickness;
                paint.StrokeCap = SKStrokeCap.Round;
                var inset = new SKPoint(Math.Min(thickness, width) / 2, Math.Min(thickness, height) / 2);
                var from = style.StartX is { } sx && style.StartY is { } sy ? new SKPoint((float)(sx * width), (float)(sy * height)) : inset;
                var to = style.EndX is { } ex && style.EndY is { } ey ? new SKPoint((float)(ex * width), (float)(ey * height)) : new SKPoint(width - inset.X, height - inset.Y);
                canvas.DrawLine(from, to, paint);
                break;
            case ShapeKind.Ellipse: canvas.DrawOval(rect, paint); break;
            case ShapeKind.RoundedRectangle:
                var radius = (float)Math.Min(style.CornerRadius, Math.Min(width, height) / 2.0);
                canvas.DrawRoundRect(rect, radius, radius, paint);
                break;
            default: canvas.DrawRect(rect, paint); break;
        }
        return pixels;
    }

    /// <summary>Adds a live shape layer covering a document rectangle (a line runs corner to corner).</summary>
    public Layer? AddShape(SKRect rect)
    {
        if (ShapeKind == ShapeKind.Line) return AddLine(new SKPoint(rect.Left, rect.Top), new SKPoint(rect.Right, rect.Bottom));
        rect = SKRect.Create((float)Math.Round(rect.Left), (float)Math.Round(rect.Top), (float)Math.Round(rect.Width), (float)Math.Round(rect.Height));
        if (rect.Width < 1 || rect.Height < 1) return null;
        var style = new ShapeStyle(ShapeKind, (uint)Foreground, ShapeCornerRadius);
        return AddShapeLayer(style, rect, ShapeKind == ShapeKind.Ellipse ? "Ellipse" : "Rectangle");
    }

    /// <summary>Adds a live line between two document points, <see cref="ShapeLineWidth"/> thick with round ends.</summary>
    public Layer? AddLine(SKPoint from, SKPoint to)
    {
        var thickness = Math.Clamp(ShapeLineWidth, 1, 5000);
        if (!float.IsFinite(from.X) || !float.IsFinite(from.Y) || !float.IsFinite(to.X) || !float.IsFinite(to.Y)) return null;
        // The layer is the box around the two ends with room for the stroke's thickness (and its round ends).
        var half = (float)(thickness / 2);
        var box = new SKRect(Math.Min(from.X, to.X) - half, Math.Min(from.Y, to.Y) - half, Math.Max(from.X, to.X) + half, Math.Max(from.Y, to.Y) + half);
        box = SKRect.Create(MathF.Floor(box.Left), MathF.Floor(box.Top), MathF.Ceiling(box.Width), MathF.Ceiling(box.Height));
        if (box.Width < 1 || box.Height < 1 || (from.X == to.X && from.Y == to.Y)) return null;
        var style = new ShapeStyle(ShapeKind.Line, (uint)Foreground, 0)
        {
            LineWidth = thickness,
            StartX = (from.X - box.Left) / box.Width, StartY = (from.Y - box.Top) / box.Height,
            EndX = (to.X - box.Left) / box.Width, EndY = (to.Y - box.Top) / box.Height
        };
        return AddShapeLayer(style, box, "Line");
    }

    private Layer? AddShapeLayer(ShapeStyle style, SKRect rect, string stem)
    {
        if ((long)rect.Width * (long)rect.Height > IO.ImageFiles.MaxPixels) { Problem?.Invoke("That shape is too large. A shape can cover up to 100 megapixels."); return null; }
        var layer = Layer.Raster(document.UniqueName(stem), RenderShape(style, (int)rect.Width, (int)rect.Height), rect.Left, rect.Top);
        layer.Shape = style;
        Apply(stem, () => document.InsertAboveActive(layer));
        Invalidate(AffectedArea(layer));
        LayersChanged?.Invoke();
        return layer;
    }

    /// <summary>Turns a live shape into ordinary pixels so it can be painted on.</summary>
    public void RasterizeShape(Layer layer)
    {
        if (!layer.IsLive) return;
        Apply("Rasterize Layer", () => { layer.Shape = null; layer.Text = null; });
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

    /// <summary>Copy takes pixels from a pixel layer (or a mask); with nothing selected it also takes the layers themselves, folders and adjustments included.</summary>
    public bool CanCopy => ActiveLayer is { } layer && (layer.Pixels != null || CanCopyLayers);

    /// <summary>Copy with no selection copies the selected layers whole, for Paste here or in another project.</summary>
    public bool CanCopyLayers => ActiveLayer != null && document.Selection == null && !IsEditingMask;

    public bool Copy()
    {
        CopiedLayers = CanCopyLayers
            ? new ClipboardLayers(SelectedRoots().Select(l => l.Clone()).ToList(), this, document.Width, document.Height)
            : null;
        if (ActiveLayer is not { Pixels: not null } layer || GrabLayer(layer) is not { } image)
        {
            // A folder or an adjustment layer has no pixels of its own; other apps get its rendering when it has one.
            if (CopiedLayers == null) return false;
            Clipboard = ActiveLayer is { IsGroup: true } group && GrabLayer(group) is { } rendered ? rendered : null;
            return true;
        }
        Clipboard = image;
        return true;
    }

    public bool CopyMerged()
    {
        if (Grab(Composite(), document.Bounds) is not { } image) return false;
        Clipboard = image;
        CopiedLayers = null;
        return true;
    }

    public void Cut()
    {
        if (!Copy()) return;
        if (document.Selection != null) ClearSelection(); else DeleteSelectedLayers();
    }

    /// <summary>
    /// Pastes as a new layer: where it was copied from when that still fits the canvas, otherwise centered. Layers
    /// copied whole come back complete instead; an image handed in from another app always pastes as pixels.
    /// </summary>
    public Layer? Paste(ClipboardImage? image = null, string name = "Pasted Layer")
    {
        if (image == null && CopiedLayers is { } layers) return PasteLayers(layers);
        image ??= Clipboard;
        if (image == null) return null;
        var pixels = Pixels.Clone(image.Pixels);
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

    /// <summary>
    /// Layers copied whole, pasted above the active layer as one undo step. In their own project they keep their
    /// place, like Duplicate Layer; in another they are centered on the canvas together, keeping their positions
    /// relative to each other. Returns the topmost pasted layer, which becomes active with every copy selected.
    /// </summary>
    private Layer? PasteLayers(ClipboardLayers copied)
    {
        if (copied.Layers.Count == 0) return null;
        var pasted = copied.Layers.Select(l => l.Clone(newIds: true)).ToList();
        if (copied.Source != this)
        {
            // Centered on this canvas: a single layer by its own center, several by the center of what they cover.
            var pictured = pasted.SelectMany(l => Document.Flatten([l])).Where(l => l.Pixels != null).Select(l => l.Bounds).ToList();
            var covered = pictured.Count > 0 ? pictured.Aggregate(SKRect.Union) : new SKRect(0, 0, copied.CanvasWidth, copied.CanvasHeight);
            var dx = Math.Round(document.Width / 2.0 - covered.MidX);
            var dy = Math.Round(document.Height / 2.0 - covered.MidY);
            foreach (var layer in pasted.SelectMany(l => Document.Flatten([l])))
            {
                if (layer.Pixels != null) layer.Transform = layer.Transform.Translated(dx, dy);
                else if (layer.Mask is { } mask && (dx != 0 || dy != 0 || mask.Width != document.Width || mask.Height != document.Height))
                    layer.Mask = RemapDocumentMask(mask, document.Width, document.Height, SKMatrix.CreateTranslation((float)dx, (float)dy), 255);
            }
        }
        Apply(pasted.Count > 1 ? "Paste Layers" : "Paste Layer", () =>
        {
            if (ActiveLayer is { } active)
            {
                var siblings = active.IsGroup && !active.Collapsed ? active.Children : document.SiblingsOf(active.Id)!;
                var index = active.IsGroup && !active.Collapsed ? siblings.Count : siblings.IndexOf(active) + 1;
                siblings.InsertRange(index, pasted);
            }
            else document.Layers.AddRange(pasted);
            document.SetActive(pasted[^1].Id);
            foreach (var layer in pasted) document.SelectedLayerIds.Add(layer.Id);
        });
        EditingMask = false;
        InvalidateAll();
        LayersChanged?.Invoke();
        return pasted[^1];
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
