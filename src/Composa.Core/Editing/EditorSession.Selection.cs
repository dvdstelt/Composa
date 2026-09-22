using Composa.Model;
using Composa.Rendering;
using Composa.Selections;
using SkiaSharp;

namespace Composa.Editing;

public sealed partial class EditorSession
{
    public SKBitmap? Selection => document.Selection;

    private void SetSelection(string name, SKBitmap? selection)
    {
        if (selection == null && document.Selection == null) return;
        Apply(name, () => document.Selection = selection);
        SelectionChanged?.Invoke();
    }

    /// <summary>Replaces the selection without an undo step, for live previews while dragging a marquee.</summary>
    public void PreviewSelection(SKBitmap? selection)
    {
        document.Selection = selection;
        SelectionChanged?.Invoke();
    }

    public void Select(SKBitmap shape, SelectionMode mode, string name = "Select") =>
        SetSelection(name, SelectionMask.Combine(document.Selection, shape, mode));

    public void SelectRect(SKRect rect, SelectionMode mode = SelectionMode.Replace) =>
        Select(SelectionMask.FromRect(document.Width, document.Height, rect, (float)Feather), mode, "Rectangular Marquee");

    public void SelectEllipse(SKRect rect, SelectionMode mode = SelectionMode.Replace) =>
        Select(SelectionMask.FromEllipse(document.Width, document.Height, rect, (float)Feather), mode, "Elliptical Marquee");

    public void SelectPolygon(IReadOnlyList<SKPoint> points, SelectionMode mode = SelectionMode.Replace) =>
        Select(SelectionMask.FromPolygon(document.Width, document.Height, points, (float)Feather), mode, "Lasso");

    public void SelectAll() => SetSelection("Select All", SelectionMask.All(document.Width, document.Height));
    public void Deselect() => SetSelection("Deselect", null);

    public void InvertSelection()
    {
        if (document.Selection == null) { SelectAll(); return; }
        var inverted = SelectionMask.Invert(document.Selection);
        SetSelection("Select Inverse", SelectionMask.IsEmpty(inverted) ? null : inverted);
    }

    public void ExpandSelection(int pixels)
    {
        if (document.Selection != null && pixels > 0) SetSelection("Expand Selection", SelectionMask.Expand(document.Selection, pixels));
    }

    public void ContractSelection(int pixels)
    {
        if (document.Selection != null && pixels > 0) SetSelection("Contract Selection", SelectionMask.Contract(document.Selection, pixels));
    }

    public void FeatherSelection(float radius)
    {
        if (document.Selection != null && radius > 0) SetSelection("Feather Selection", SelectionMask.Feather(document.Selection, radius));
    }

    public void MoveSelection(int dx, int dy)
    {
        if (document.Selection != null && (dx != 0 || dy != 0)) SetSelection("Move Selection", SelectionMask.Translate(document.Selection, dx, dy));
    }

    public void SelectLayerPixels(Layer layer, SelectionMode mode = SelectionMode.Replace)
    {
        if (SelectionMask.FromLayer(document, layer, fromMask: false) is { } shape) Select(shape, mode, "Load Selection");
    }

    public void SelectLayerMask(Layer layer, SelectionMode mode = SelectionMode.Replace)
    {
        if (SelectionMask.FromLayer(document, layer, fromMask: true) is { } shape) Select(shape, mode, "Load Selection");
    }

    /// <summary>
    /// What selection-from-image tools read, at document size: every visible layer as shown on the canvas, or just the
    /// active layer's own pixels (without its mask). The bitmap is owned by the caller when <c>Owned</c> is true.
    /// </summary>
    private (SKBitmap Source, bool Owned) SelectionSample()
    {
        if (SampleAllLayers || ActiveLayer is not { Pixels: not null } layer) return (Composite(), false);
        var copy = layer.Clone();
        copy.Visible = true; copy.Opacity = 1; copy.Blend = BlendMode.Normal; copy.Clipped = false; copy.Mask = null; copy.Effects = null;
        return (DocumentRenderer.RenderLayers(document, [copy], document.Bounds), true);
    }

    /// <summary>Magic Wand at a document point, sampling the active layer or the whole image.</summary>
    public void SelectWand(int x, int y, SelectionMode mode = SelectionMode.Replace)
    {
        if (x < 0 || y < 0 || x >= document.Width || y >= document.Height) return;
        var (source, owned) = SelectionSample();
        var shape = MagicWand.Select(source, x, y, WandTolerance, WandContiguous);
        if (owned) source.Dispose();
        Select(shape, mode, "Magic Wand");
    }

    /// <summary>The Magic tool's Object mode: selects the object under the point. Landing on the backdrop deselects (in Replace mode).</summary>
    public void SelectObject(int x, int y, SelectionMode mode = SelectionMode.Replace)
    {
        if (x < 0 || y < 0 || x >= document.Width || y >= document.Height) return;
        var (source, owned) = SelectionSample();
        var shape = ObjectSelection.Select(source, x, y, ObjectEdgeOffset);
        if (owned) source.Dispose();
        if (shape == null) { if (mode == SelectionMode.Replace) Deselect(); return; }
        Select(shape, mode, "Object Selection");
    }

    /// <summary>Select > Subject: everything in the picture that is not the plain backdrop around it.</summary>
    public bool SelectSubject(SelectionMode mode = SelectionMode.Replace)
    {
        var shape = ObjectSelection.Subject(Composite());
        if (shape == null) return false;
        Select(shape, mode, "Select Subject");
        return true;
    }
}
