using Composa.Model;
using Composa.Painting;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Editing;

public enum Tool { Move, Marquee, Lasso, Wand, Crop, Brush, SpotHealing, CloneStamp, Smear, Gradient, Shape, Text, Eyedropper, Hand, Zoom }

public enum MarqueeKind { Rectangle, Ellipse }
public enum LassoKind { Freehand, Polygonal }
/// <summary>The Magic tool's modes: Wand selects pixels of a similar color, Object traces the object under the click.</summary>
public enum WandMode { Wand, Object }
public enum SmearMode { Liquify, Blur, Smudge, Dodge, Burn }

/// <summary>
/// One open project: the document, its undo history, the flattened preview, and every editing command. It has no
/// UI dependencies, so the whole editor can be driven from tests.
/// </summary>
public sealed partial class EditorSession
{
    private Document document;
    private Document? pendingBefore;
    private string pendingName = "";
    private SKBitmap? composite;
    private SKRectI dirty;

    public EditorSession(Document document)
    {
        this.document = document;
        dirty = document.Bounds;
    }

    public static EditorSession NewCanvas(int width, int height, SKColor? background = null)
    {
        var document = new Document(width, height);
        var pixels = Pixels.NewColor(document.Width, document.Height);
        if (background is { } color) pixels.Erase(color);
        var layer = Layer.Raster(background == null ? "Layer 1" : "Background", pixels);
        document.Layers.Add(layer);
        document.SetActive(layer.Id);
        return new EditorSession(document);
    }

    public Document Document => document;
    public History History { get; } = new();
    public string? FilePath { get; set; }
    public bool IsModified { get; private set; }
    /// <summary>Counts every change to the document, so autosave can tell whether anything happened since its last copy.</summary>
    public int Revision { get; private set; }
    /// <summary>The name shown for a project that has not been saved yet.</summary>
    public string? SuggestedName { get; set; }
    public string Title => FilePath != null ? Path.GetFileNameWithoutExtension(FilePath) : SuggestedName ?? "Untitled";

    // Tool state shared with the UI.
    private Tool tool = Tool.Move;
    /// <summary>The current tool. Leaving the Type tool finishes any text being typed.</summary>
    public Tool Tool
    {
        get => tool;
        set
        {
            if (value != Tool.Text && TextEdit != null) FinishText();
            tool = value;
        }
    }
    public SKColor Foreground { get; set; } = SKColors.Black;
    public SKColor Background { get; set; } = SKColors.White;
    public BrushSettings Brush { get; set; } = new();
    public bool EraserMode { get; set; }
    public SmearMode SmearMode { get; set; } = SmearMode.Blur;
    public MarqueeKind MarqueeKind { get; set; }
    public LassoKind LassoKind { get; set; }
    public double Feather { get; set; }
    public int WandTolerance { get; set; } = 32;
    public bool WandContiguous { get; set; } = true;
    public WandMode WandMode { get; set; }
    /// <summary>Object mode: positive values tighten the detected outline inward, negative ones loosen it, in pixels.</summary>
    public int ObjectEdgeOffset { get; set; }
    public bool SampleAllLayers { get; set; }
    public bool CloneAligned { get; set; } = true;
    /// <summary>Pixels the selection bar's Expand, Contract and Feather buttons work by.</summary>
    public int SelectionExpandAmount { get; set; } = 1;
    public int SelectionContractAmount { get; set; } = 1;
    public int SelectionFeatherAmount { get; set; } = 2;
    /// <summary>The crop bar's ratio: one of <see cref="CropRatios"/>. Free lets the box take any shape.</summary>
    public string CropRatio { get; set; } = "Free";
    public static readonly string[] CropRatios = ["Free", "Original", "1:1", "4:3", "3:4", "16:9", "9:16"];
    /// <summary>Width over height for the chosen crop ratio, or null for Free.</summary>
    public double? CropAspect => CropRatio switch
    {
        "Original" => (double)document.Width / document.Height,
        "1:1" => 1,
        "4:3" => 4 / 3.0,
        "3:4" => 3 / 4.0,
        "16:9" => 16 / 9.0,
        "9:16" => 9 / 16.0,
        _ => null
    };
    public ShapeKind ShapeKind { get; set; } = ShapeKind.Rectangle;
    public double ShapeCornerRadius { get; set; } = 24;
    /// <summary>A Line shape's thickness in document pixels.</summary>
    public double ShapeLineWidth { get; set; } = 4;
    /// <summary>The settings new text starts with; the color follows the foreground color.</summary>
    public TextStyle TextDefaults { get; set; } = new();
    public bool GradientToTransparent { get; set; }
    public bool GradientRadial { get; set; }
    public double GradientOpacity { get; set; } = 1;
    /// <summary>When the active layer has a mask: paint, fill and filter the mask instead of the pixels.</summary>
    public bool EditingMask { get; set; }
    public Guid? SoloLayerId { get; set; }

    /// <summary>Raised after pixels changed; the rectangle is in document space (null means everything, including size).</summary>
    public event Action<SKRectI?>? CanvasChanged;
    /// <summary>Raised when layers were added, removed, reordered or had their properties changed.</summary>
    public event Action? LayersChanged;
    public event Action? SelectionChanged;
    public event Action? HistoryChanged;
    /// <summary>Raised when an edit could not be carried out, with a message for the user.</summary>
    public event Action<string>? Problem;

    public Layer? ActiveLayer => document.ActiveLayer;
    public bool IsEditingMask => EditingMask && ActiveLayer?.Mask != null;
    public bool HasPendingEdit => pendingBefore != null;

    // ---- Undo ---------------------------------------------------------------------------------------------------

    /// <summary>Starts an edit that may be previewed live and later committed or cancelled.</summary>
    public void Begin(string name)
    {
        FinishInteraction();
        pendingBefore = document.Clone();
        pendingName = name;
    }

    /// <summary>
    /// Completes whatever live edit is still open. A new edit must never start while a stroke, preview, transform or
    /// pixel move is in flight: those keep replacing (and disposing) bitmaps, which must not end up in a snapshot.
    /// </summary>
    private void FinishInteraction()
    {
        if (TextEdit != null) FinishText();
        else if (stroke != null) EndStroke();
        else if (floatLayer != null) EndMovePixels(keep: true);
        else if (previewLayer != null) CommitPreview();
        else if (Transform != null) CommitTransform();
        else if (pendingBefore != null) Commit();
    }

    /// <summary>True while a drag, preview or other uncommitted edit is open.</summary>
    public bool IsInteracting => pendingBefore != null;

    public void Commit()
    {
        if (pendingBefore == null) return;
        History.Push(pendingName, pendingBefore);
        pendingBefore = null;
        IsModified = true;
        Revision++;
        HistoryChanged?.Invoke();
    }

    public void Cancel()
    {
        if (pendingBefore == null) return;
        Restore(pendingBefore);
        pendingBefore = null;
    }

    /// <summary>Runs a complete, undoable edit.</summary>
    public void Apply(string name, Action edit)
    {
        Begin(name);
        try { edit(); }
        catch { Cancel(); throw; }
        Commit();
    }

    public bool CanUndo => History.CanUndo && pendingBefore == null;
    public bool CanRedo => History.CanRedo && pendingBefore == null;

    public void Undo()
    {
        if (!CanUndo) return;
        if (History.Undo(document.Clone()) is { } state) Restore(state);
        IsModified = true;
        Revision++;
        HistoryChanged?.Invoke();
    }

    public void Redo()
    {
        if (!CanRedo) return;
        if (History.Redo(document.Clone()) is { } state) Restore(state);
        IsModified = true;
        Revision++;
        HistoryChanged?.Invoke();
    }

    private void Restore(Document state)
    {
        var resized = state.Width != document.Width || state.Height != document.Height;
        document = state;
        if (resized) composite = null;
        InvalidateAll();
        LayersChanged?.Invoke();
        SelectionChanged?.Invoke();
    }

    /// <summary>Marks a document that did not come from a saved file (a recovered copy) as needing a save.</summary>
    public void MarkModified()
    {
        IsModified = true;
        Revision++;
        HistoryChanged?.Invoke();
    }

    public void MarkSaved(string path)
    {
        FilePath = path;
        IsModified = false;
        HistoryChanged?.Invoke();
    }

    // ---- Flattened preview --------------------------------------------------------------------------------------

    public void Invalidate(SKRectI area)
    {
        dirty = Geometry.Union(dirty, Geometry.Intersect(area, document.Bounds));
        CanvasChanged?.Invoke(area);
    }

    public void Invalidate(SKRect area) => Invalidate(Geometry.RoundOut(area));

    public void InvalidateAll()
    {
        dirty = document.Bounds;
        CanvasChanged?.Invoke(null);
    }

    public void NotifyLayersChanged() => LayersChanged?.Invoke();

    /// <summary>The flattened document, brought up to date. Call from the UI thread.</summary>
    public SKBitmap Composite()
    {
        if (composite == null || composite.Width != document.Width || composite.Height != document.Height)
        {
            composite = Pixels.NewColor(document.Width, document.Height);
            dirty = document.Bounds;
        }
        if (!dirty.IsEmpty)
        {
            DocumentRenderer.Render(document, composite, dirty, CurrentOptions());
            dirty = SKRectI.Empty;
        }
        return composite;
    }

    private RenderOptions SoloOptions(Guid solo)
    {
        // Showing one layer alone: its ancestors and descendants stay, everything else renders hidden.
        var overrides = new Dictionary<Guid, Layer>();
        var keep = new HashSet<Guid> { solo };
        for (var parent = document.ParentOf(solo); parent != null; parent = document.ParentOf(parent.Id)) keep.Add(parent.Id);
        if (document.Find(solo) is { } target) foreach (var child in Document.Flatten(target.Children)) keep.Add(child.Id);
        foreach (var layer in document.AllLayers())
        {
            if (keep.Contains(layer.Id)) continue;
            var hidden = layer.Clone();
            hidden.Visible = false;
            overrides[layer.Id] = hidden;
        }
        return new RenderOptions { Overrides = overrides };
    }

    /// <summary>
    /// Renders what a view shows straight from the layers, at the view's own resolution. The canvas uses this rather
    /// than <see cref="Composite"/>, so its cost follows the screen size and not the document size.
    /// </summary>
    public void RenderView(SKBitmap target, SKRectI area, RenderView view) =>
        DocumentRenderer.Render(document, target, area, view, CurrentOptions());

    private RenderOptions? CurrentOptions()
    {
        // Solo ends by itself when its layer is deleted, merged away or undone out of existence.
        if (SoloLayerId is { } id && document.Find(id) == null) SoloLayerId = null;
        return SoloLayerId is { } solo ? SoloOptions(solo) : null;
    }

    /// <summary>A fresh flattened copy, independent of the preview, for export and Copy Merged.</summary>
    public SKBitmap Flatten() => DocumentRenderer.Flatten(document);

    /// <summary>The document-space area a layer affects when it changes.</summary>
    public SKRectI AffectedArea(Layer layer)
    {
        if (layer.Pixels == null) return document.Bounds;
        var bounds = Geometry.RoundOut(layer.VisibleBounds);
        // A blur adjustment above spreads the change as far as it samples.
        var margin = 2 + (int)Math.Ceiling(DocumentRenderer.SamplingMargin(document));
        bounds.Inflate(margin, margin);
        // A clipping base also changes what its clipped layers show.
        var siblings = document.SiblingsOf(layer.Id);
        if (siblings != null)
        {
            var index = siblings.IndexOf(layer);
            for (var i = index + 1; i < siblings.Count && siblings[i].Clipped; i++)
                bounds = siblings[i].Pixels == null ? document.Bounds : Geometry.Union(bounds, Geometry.RoundOut(siblings[i].VisibleBounds));
        }
        return bounds;
    }

    public SKColor SampleColor(int x, int y, bool allLayers = true)
    {
        if (x < 0 || y < 0 || x >= document.Width || y >= document.Height) return Foreground;
        var color = Composite().GetPixel(x, y);
        return color.Alpha == 0 ? Foreground : color.WithAlpha(255);
    }

    public void SwapColors() => (Foreground, Background) = (Background, Foreground);

    /// <summary>
    /// Tab steps the current tool through its own modes, the setting at the left of its options bar. Tools without
    /// modes (Move, Crop, Text, Eyedropper, Hand, Zoom) ignore it.
    /// </summary>
    public void CycleToolMode()
    {
        static T Next<T>(T value) where T : struct, Enum { var all = Enum.GetValues<T>(); return all[(Array.IndexOf(all, value) + 1) % all.Length]; }
        switch (Tool)
        {
            case Tool.Marquee: MarqueeKind = Next(MarqueeKind); break;
            case Tool.Lasso: LassoKind = Next(LassoKind); break;
            case Tool.Wand: WandMode = Next(WandMode); break;
            case Tool.Shape: ShapeKind = Next(ShapeKind); break;
            case Tool.Brush: EraserMode = !EraserMode; break;
            case Tool.Smear: SmearMode = Next(SmearMode); break;
            case Tool.SpotHealing or Tool.CloneStamp: SampleAllLayers = !SampleAllLayers; break;
            case Tool.Gradient: GradientRadial = !GradientRadial; break;
        }
    }

    public void ResetColors()
    {
        Foreground = SKColors.Black;
        Background = SKColors.White;
    }
}
