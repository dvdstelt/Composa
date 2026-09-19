using Compositor.Model;
using Compositor.Painting;
using Compositor.Rendering;
using SkiaSharp;

namespace Compositor.Editing;

public sealed partial class EditorSession
{
    private BrushStroke? stroke;
    private Layer? strokeLayer;
    private SKBitmap? strokeOriginal;
    private SKMatrix strokeToLayer;
    private BrushMode strokeMode;
    private SKPoint? cloneSource;
    private SKPoint? cloneOffset;
    private SKPoint? lastStrokeEnd;

    public bool IsStroking => stroke != null;
    /// <summary>Where the Clone Stamp samples from, in document space.</summary>
    public SKPoint? CloneSource => cloneSource;
    /// <summary>Where the clone source currently is while painting, for the crosshair overlay.</summary>
    public SKPoint? CloneSamplePoint(SKPoint cursor) =>
        cloneSource == null ? null : cloneOffset is { } o && CloneAligned ? new SKPoint(cursor.X + o.X, cursor.Y + o.Y) : cloneSource;
    /// <summary>Where the previous stroke ended, so Shift-click can draw a straight line from it.</summary>
    public SKPoint? LastStrokeEnd => lastStrokeEnd;

    public void SetCloneSource(SKPoint documentPoint)
    {
        cloneSource = documentPoint;
        cloneOffset = null;
    }

    /// <summary>The brush mode the current tool paints with.</summary>
    public BrushMode CurrentBrushMode => Tool switch
    {
        Tool.SpotHealing => BrushMode.Heal,
        Tool.CloneStamp => BrushMode.Clone,
        Tool.Smear => SmearMode switch
        {
            SmearMode.Liquify => BrushMode.Liquify, SmearMode.Smudge => BrushMode.Smudge, SmearMode.Dodge => BrushMode.Dodge, SmearMode.Burn => BrushMode.Burn, _ => BrushMode.Blur
        },
        _ => EraserMode ? BrushMode.Erase : BrushMode.Paint
    };

    /// <summary>Starts painting at a document point. Returns false (with a reason) when the active layer can't be painted.</summary>
    public bool BeginStroke(SKPoint point, out string? problem, bool lineFromLast = false)
    {
        problem = null;
        if (ActiveLayer is not { } layer) { problem = "Select a layer to paint on."; return false; }
        if (!IsEditingMask && layer.Pixels == null) { problem = layer.IsGroup ? "Folders can't be painted on. Select a layer inside." : "Adjustment layers have no pixels. Add a mask to paint on."; return false; }
        if (!IsEditingMask && layer.Shape != null) { problem = "This is a live shape. Rasterize it (Layer menu) to paint on it."; return false; }
        if (!document.IsEffectivelyVisible(layer)) { problem = "The layer is hidden."; return false; }
        var mode = CurrentBrushMode;
        if (mode == BrushMode.Clone && cloneSource == null) { problem = "Alt-click to set the clone source first."; return false; }
        if (mode == BrushMode.Heal && IsEditingMask) { problem = "The Spot Healing Brush works on pixels, not masks."; return false; }

        Begin(mode switch
        {
            BrushMode.Erase => "Eraser", BrushMode.Clone => "Clone Stamp", BrushMode.Heal => "Spot Healing Brush",
            BrushMode.Liquify => "Liquify", BrushMode.Blur => "Blur", BrushMode.Smudge => "Smudge", BrushMode.Dodge => "Dodge", BrushMode.Burn => "Burn", _ => "Brush"
        });
        if (!IsEditingMask) EnsureCoversCanvas(layer);
        var target = Target(layer);
        var matrix = TargetMatrix(layer);
        if (!matrix.TryInvert(out strokeToLayer)) { Cancel(); problem = "The layer is too small to paint on."; return false; }
        var scale = Math.Sqrt(Math.Abs(matrix.ScaleX * matrix.ScaleY - matrix.SkewX * matrix.SkewY));

        SKBitmap? source = null;
        var offset = SKPointI.Empty;
        if (mode == BrushMode.Clone)
        {
            if (!CloneAligned || cloneOffset == null) cloneOffset = new SKPoint(cloneSource!.Value.X - point.X, cloneSource.Value.Y - point.Y);
            var o = cloneOffset.Value;
            var translationOnly = layer.Pixels != null && layer.Transform.IsPureTranslation(layer.Pixels.Width, layer.Pixels.Height) || layer.Pixels == null;
            if (SampleAllLayers && !IsEditingMask && translationOnly)
            {
                source = Composite().Copy();
                offset = new SKPointI((int)Math.Round(o.X + layer.Transform.X), (int)Math.Round(o.Y + layer.Transform.Y));
            }
            else
            {
                source = target;
                var v = strokeToLayer.MapVector(o.X, o.Y);
                offset = new SKPointI((int)Math.Round(v.X), (int)Math.Round(v.Y));
            }
        }
        var color = mode == BrushMode.Paint ? Foreground : SKColors.Black;
        stroke = new BrushStroke(target, Brush, mode, color, scale)
        {
            Selection = document.Selection, ToDocument = matrix, CloneSource = source, CloneOffset = offset
        };
        strokeLayer = layer;
        strokeOriginal = target;
        strokeMode = mode;
        SetTarget(layer, stroke.Working);
        if (lineFromLast && lastStrokeEnd is { } from) stroke.AddPoint(strokeToLayer.MapPoint(from));
        ContinueStroke(point);
        if (lineFromLast) Invalidate(AffectedArea(layer));
        return true;
    }

    public void ContinueStroke(SKPoint point)
    {
        if (stroke == null || strokeLayer == null) return;
        var changed = stroke.AddPoint(strokeToLayer.MapPoint(point));
        lastStrokeEnd = point;
        if (changed.IsEmpty) return;
        var area = Geometry.RoundOut(stroke.ToDocument.MapRect(SKRect.Create(changed.Left, changed.Top, changed.Width, changed.Height)));
        area.Inflate(1, 1);
        // A clipping base or a folder/adjustment mask changes more than its own rectangle's worth of layers, but never
        // outside the rectangle itself, so the dab's area is all that needs re-rendering.
        Invalidate(area);
    }

    public void EndStroke()
    {
        if (stroke == null || strokeLayer == null) return;
        var layer = strokeLayer;
        if (stroke.Touched.IsEmpty)
        {
            SetTarget(layer, strokeOriginal!);
            CloseStroke();
            Cancel();
            return;
        }
        if (strokeMode == BrushMode.Heal)
        {
            using var mask = stroke.CoverageMask();
            var healed = Inpaint.Fill(strokeOriginal!, mask);
            Pixels.Invalidate(stroke.Working);
            SetTarget(layer, healed);
            Invalidate(AffectedArea(layer));
        }
        CloseStroke();
        Commit();
        LayersChanged?.Invoke();
    }

    public void CancelStroke()
    {
        if (stroke == null) return;
        CloseStroke();
        Cancel();
    }

    private void CloseStroke()
    {
        if (stroke?.CloneSource != null && !ReferenceEquals(stroke.CloneSource, strokeOriginal)) stroke.CloneSource.Dispose();
        stroke?.Dispose();
        stroke = null;
        strokeLayer = null;
        strokeOriginal = null;
    }
}
