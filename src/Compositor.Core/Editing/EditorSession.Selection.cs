using Compositor.Model;
using Compositor.Rendering;
using Compositor.Selections;
using SkiaSharp;

namespace Compositor.Editing;

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

    /// <summary>Magic Wand at a document point, sampling the active layer or the whole image.</summary>
    public void SelectWand(int x, int y, SelectionMode mode = SelectionMode.Replace)
    {
        if (x < 0 || y < 0 || x >= document.Width || y >= document.Height) return;
        SKBitmap source;
        var owned = false;
        if (SampleAllLayers || ActiveLayer is not { Pixels: not null } layer) source = Composite();
        else
        {
            var copy = layer.Clone();
            copy.Visible = true; copy.Opacity = 1; copy.Blend = BlendMode.Normal; copy.Clipped = false; copy.Mask = null;
            source = DocumentRenderer.RenderLayers(document, [copy], document.Bounds);
            owned = true;
        }
        var shape = MagicWand.Select(source, x, y, WandTolerance, WandContiguous);
        if (owned) source.Dispose();
        Select(shape, mode, "Magic Wand");
    }
}
