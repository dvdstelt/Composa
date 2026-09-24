using SkiaSharp;

namespace Composa.Model;

/// <summary>The canvas, its layer tree and the current selection.</summary>
public sealed class Document
{
    public const int MaxSide = DocumentLimits.MaxSide;

    public int Width { get; set; }
    public int Height { get; set; }
    public double Resolution { get; set; } = 72;
    /// <summary>Root layers, bottom to top.</summary>
    public List<Layer> Layers { get; init; } = [];
    public Guid? ActiveLayerId { get; set; }
    public HashSet<Guid> SelectedLayerIds { get; init; } = [];
    /// <summary>Alpha8 selection coverage at document size, or null when nothing is selected.</summary>
    public SKBitmap? Selection { get; set; }
    /// <summary>User-placed alignment lines. Saved with the project; undo covers them.</summary>
    public List<Guide> Guides { get; init; } = [];

    public Document(int width, int height)
    {
        Width = Math.Clamp(width, 1, MaxSide);
        Height = Math.Clamp(height, 1, MaxSide);
    }

    public SKRectI Bounds => new(0, 0, Width, Height);

    /// <summary>The raster the document holds: every layer's pixels and mask, counted against <see cref="DocumentLimits.DocumentPixelBudget"/> when more is imported.</summary>
    public long RasterPixels() => AllLayers().Sum(l => (long)(l.Pixels?.Width ?? 0) * (l.Pixels?.Height ?? 0) + (long)(l.Mask?.Width ?? 0) * (l.Mask?.Height ?? 0));
    public Layer? ActiveLayer => ActiveLayerId is { } id ? Find(id) : null;

    public Document Clone()
    {
        var copy = new Document(Width, Height) { Resolution = Resolution, ActiveLayerId = ActiveLayerId, Selection = Selection };
        foreach (var layer in Layers) copy.Layers.Add(layer.Clone());
        foreach (var id in SelectedLayerIds) copy.SelectedLayerIds.Add(id);
        copy.Guides.AddRange(Guides);
        return copy;
    }

    /// <summary>Every layer, depth first, bottom to top.</summary>
    public IEnumerable<Layer> AllLayers() => Flatten(Layers);

    public static IEnumerable<Layer> Flatten(IEnumerable<Layer> layers)
    {
        foreach (var layer in layers)
        {
            foreach (var child in Flatten(layer.Children)) yield return child;
            yield return layer;
        }
    }

    public Layer? Find(Guid id) => AllLayers().FirstOrDefault(l => l.Id == id);

    public Layer? ParentOf(Guid id) => AllLayers().FirstOrDefault(l => l.Children.Any(c => c.Id == id));

    /// <summary>The list that directly holds the layer: a group's children or the root.</summary>
    public List<Layer>? SiblingsOf(Guid id)
    {
        if (Layers.Any(l => l.Id == id)) return Layers;
        return ParentOf(id)?.Children;
    }

    public bool IsEffectivelyVisible(Layer layer)
    {
        if (!layer.Visible) return false;
        for (var parent = ParentOf(layer.Id); parent != null; parent = ParentOf(parent.Id))
            if (!parent.Visible) return false;
        return true;
    }

    public void SetActive(Guid? id)
    {
        ActiveLayerId = id;
        SelectedLayerIds.Clear();
        if (id is { } value) SelectedLayerIds.Add(value);
    }

    /// <summary>Inserts above the active layer (inside its group), or on top when there is none.</summary>
    public void InsertAboveActive(Layer layer)
    {
        if (ActiveLayer is { } active)
        {
            if (active.IsGroup && !active.Collapsed) active.Children.Add(layer);
            else
            {
                var siblings = SiblingsOf(active.Id)!;
                siblings.Insert(siblings.IndexOf(active) + 1, layer);
            }
        }
        else Layers.Add(layer);
        SetActive(layer.Id);
    }

    public string UniqueName(string stem)
    {
        var names = AllLayers().Select(l => l.Name).ToHashSet();
        for (var i = 1; ; i++)
        {
            var candidate = $"{stem} {i}";
            if (!names.Contains(candidate)) return candidate;
        }
    }

    /// <summary>Approximate bytes held by distinct bitmaps, for budgeting history.</summary>
    public void CollectBitmaps(HashSet<SKBitmap> into)
    {
        foreach (var layer in AllLayers())
        {
            if (layer.Pixels != null) into.Add(layer.Pixels);
            if (layer.Mask != null) into.Add(layer.Mask);
        }
        if (Selection != null) into.Add(Selection);
    }
}
