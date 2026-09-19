using Compositor.Model;
using Compositor.Rendering;
using Compositor.Selections;
using SkiaSharp;

namespace Compositor.Editing;

public sealed partial class EditorSession
{
    private Layer? floatLayer;
    private SKBitmap? floatBase, floatPixels, floatSelection, floatPreview, floatOriginal;
    private SKPointI floatOffset;
    private SKPointI floatOrigin;

    public bool IsMovingPixels => floatLayer != null;

    /// <summary>True when dragging inside the selection should move the selected pixels rather than the whole layer.</summary>
    public bool CanMovePixels =>
        document.Selection != null && !IsEditingMask && ActiveLayer is { Pixels: { } pixels, Shape: null } layer
        && layer.Transform.IsPureTranslation(pixels.Width, pixels.Height) && document.IsEffectivelyVisible(layer);

    /// <summary>Lifts the selected pixels off the active layer so they can be dragged; with <paramref name="duplicate"/> the originals stay.</summary>
    public bool BeginMovePixels(bool duplicate)
    {
        if (!CanMovePixels || ActiveLayer is not { } layer) return false;
        Begin(duplicate ? "Duplicate Selection" : "Move Selection Pixels");
        EnsureCoversCanvas(layer);
        var original = layer.Pixels!;
        using var selection = SelectionInLayerSpace(layer)!;
        var bounds = SelectionMask.Bounds(selection);
        if (bounds.IsEmpty) { Cancel(); return false; }

        floatPixels = Pixels.NewColor(bounds.Width, bounds.Height);
        using (var canvas = new SKCanvas(floatPixels))
        {
            canvas.DrawBitmap(original, -bounds.Left, -bounds.Top);
            using var keep = new SKPaint { BlendMode = SKBlendMode.DstIn };
            canvas.DrawBitmap(selection, -bounds.Left, -bounds.Top, keep);
        }
        floatBase = Pixels.Clone(original);
        if (!duplicate)
        {
            using var canvas = new SKCanvas(floatBase);
            using var remove = new SKPaint { BlendMode = SKBlendMode.DstOut };
            canvas.DrawBitmap(selection, 0, 0, remove);
        }
        floatOrigin = new SKPointI(bounds.Left, bounds.Top);
        floatSelection = document.Selection;
        floatOriginal = original;
        floatOffset = SKPointI.Empty;
        floatLayer = layer;
        return true;
    }

    public void MovePixelsBy(int dx, int dy)
    {
        if (floatLayer is not { } layer || floatBase == null || floatPixels == null) return;
        floatOffset = new SKPointI(dx, dy);
        // Back at the start nothing has moved: with a feathered selection, re-compositing the lifted pixels over their
        // own hole would otherwise leave a faint translucent seam.
        SKBitmap? moved = null;
        if (dx != 0 || dy != 0)
        {
            moved = Pixels.Clone(floatBase);
            using var canvas = new SKCanvas(moved);
            canvas.DrawBitmap(floatPixels, floatOrigin.X + dx, floatOrigin.Y + dy);
        }
        layer.Pixels = moved ?? floatOriginal!;
        // Only bitmaps made by earlier moves are disposed; the layer's original pixels belong to the undo history.
        if (floatPreview != null) { Pixels.Invalidate(floatPreview); floatPreview.Dispose(); }
        floatPreview = moved;
        document.Selection = SelectionMask.Translate(floatSelection!, dx, dy) ?? floatSelection;
        Invalidate(AffectedArea(layer));
        SelectionChanged?.Invoke();
    }

    public void EndMovePixels(bool keep)
    {
        if (floatLayer == null) return;
        if (floatOffset == SKPointI.Empty) keep = false;
        floatLayer = null;
        floatPixels?.Dispose();
        if (!keep && floatPreview != null) { Pixels.Invalidate(floatPreview); floatPreview.Dispose(); }
        floatBase = floatPixels = floatSelection = floatPreview = floatOriginal = null;
        if (keep) { Commit(); LayersChanged?.Invoke(); }
        else Cancel();
    }
}
