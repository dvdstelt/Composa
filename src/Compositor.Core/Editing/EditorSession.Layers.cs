using Compositor.Filters;
using Compositor.Model;
using Compositor.Rendering;
using SkiaSharp;

namespace Compositor.Editing;

public sealed partial class EditorSession
{
    public bool IsLocked(Layer layer) => false;

    public void SelectLayer(Guid id, bool extend = false, bool range = false)
    {
        if (document.Find(id) == null) return;
        SelectedEffect = null;
        if (TextEdit != null && textLayer?.Id != id) FinishText();
        if (range && document.ActiveLayerId is { } anchor)
        {
            var order = document.AllLayers().Select(l => l.Id).ToList();
            int from = order.IndexOf(anchor), to = order.IndexOf(id);
            if (from > to) (from, to) = (to, from);
            for (var i = from; i <= to; i++) document.SelectedLayerIds.Add(order[i]);
            document.ActiveLayerId = id;
        }
        else if (extend)
        {
            if (!document.SelectedLayerIds.Add(id) && document.SelectedLayerIds.Count > 1)
            {
                document.SelectedLayerIds.Remove(id);
                if (document.ActiveLayerId == id) document.ActiveLayerId = document.SelectedLayerIds.First();
            }
            else document.ActiveLayerId = id;
        }
        else document.SetActive(id);
        if (ActiveLayer?.Mask == null) EditingMask = false;
        LayersChanged?.Invoke();
    }

    /// <summary>The selected layers without any whose ancestor is also selected, bottom to top.</summary>
    public List<Layer> SelectedRoots()
    {
        var selected = document.SelectedLayerIds;
        bool AncestorSelected(Layer layer)
        {
            for (var parent = document.ParentOf(layer.Id); parent != null; parent = document.ParentOf(parent.Id))
                if (selected.Contains(parent.Id)) return true;
            return false;
        }
        return document.AllLayers().Where(l => selected.Contains(l.Id) && !AncestorSelected(l)).ToList();
    }

    public Layer AddBlankLayer()
    {
        var layer = Layer.Raster(document.UniqueName("Layer"), Pixels.NewColor(document.Width, document.Height));
        Apply("New Layer", () => document.InsertAboveActive(layer));
        LayersChanged?.Invoke();
        return layer;
    }

    /// <summary>Adds decoded image pixels as a new layer, centered (or at a drop point) and scaled down to fit the canvas.</summary>
    public Layer AddImageLayer(string name, SKBitmap pixels, SKPoint? center = null, bool fit = true)
    {
        var layer = Layer.Raster(name, pixels);
        double scale = 1;
        if (fit) scale = Math.Min(1, Math.Min((double)document.Width / pixels.Width, (double)document.Height / pixels.Height));
        double w = pixels.Width * scale, h = pixels.Height * scale;
        var c = center ?? new SKPoint(document.Width / 2f, document.Height / 2f);
        layer.Transform = new LayerTransform { Width = w, Height = h, X = Math.Round(c.X - w / 2), Y = Math.Round(c.Y - h / 2) };
        Apply("Add Image", () => document.InsertAboveActive(layer));
        Invalidate(AffectedArea(layer));
        LayersChanged?.Invoke();
        return layer;
    }

    /// <param name="commit">False leaves the edit open, so a settings dialog can still <see cref="Cancel"/> the whole layer.</param>
    public Layer AddAdjustmentLayer(Adjustment adjustment, bool commit = true)
    {
        var layer = Layer.ForAdjustment(adjustment);
        layer.Name = document.UniqueName(adjustment.DisplayName);
        Begin("New Adjustment Layer");
        document.InsertAboveActive(layer);
        if (document.Selection != null) layer.Mask = Pixels.Clone(document.Selection);
        if (commit) Commit();
        InvalidateAll();
        LayersChanged?.Invoke();
        return layer;
    }

    /// <summary>Changes a live adjustment; wrap a slider drag in Begin/Commit so it undoes as one step.</summary>
    public void SetAdjustment(Layer layer, Adjustment adjustment)
    {
        layer.Adjustment = adjustment;
        InvalidateAll();
    }

    public void DeleteSelectedLayers()
    {
        var roots = SelectedRoots();
        if (roots.Count == 0) return;
        Apply(roots.Count > 1 ? "Delete Layers" : "Delete Layer", () =>
        {
            Guid? next = null;
            foreach (var layer in roots)
            {
                var siblings = document.SiblingsOf(layer.Id)!;
                var index = siblings.IndexOf(layer);
                siblings.RemoveAt(index);
                // Layers clipped to a deleted base are released.
                if (!layer.Clipped) for (var i = index; i < siblings.Count && siblings[i].Clipped; i++) siblings[i].Clipped = false;
                next = siblings.Count > 0 ? siblings[Math.Clamp(index - 1, 0, siblings.Count - 1)].Id : document.ParentOf(layer.Id)?.Id;
            }
            document.SetActive(next ?? document.Layers.LastOrDefault()?.Id);
        });
        EditingMask = false;
        InvalidateAll();
        LayersChanged?.Invoke();
    }

    public void DuplicateSelectedLayers()
    {
        var roots = SelectedRoots();
        if (roots.Count == 0) return;
        Apply("Duplicate Layer", () =>
        {
            Layer? last = null;
            foreach (var layer in roots)
            {
                var copy = layer.Clone(newIds: true);
                copy.Name = layer.Name + " copy";
                var siblings = document.SiblingsOf(layer.Id)!;
                siblings.Insert(siblings.IndexOf(layer) + 1, copy);
                last = copy;
            }
            document.SetActive(last!.Id);
        });
        InvalidateAll();
        LayersChanged?.Invoke();
    }

    public void SetVisible(Layer layer, bool visible, bool undoable = true)
    {
        if (layer.Visible == visible) return;
        if (undoable) Apply(visible ? "Show Layer" : "Hide Layer", () => layer.Visible = visible);
        else layer.Visible = visible;
        InvalidateAll();
        LayersChanged?.Invoke();
    }

    public void SetOpacity(Layer layer, double opacity)
    {
        layer.Opacity = Math.Clamp(opacity, 0, 1);
        Invalidate(AffectedArea(layer));
    }

    public void SetBlend(Layer layer, BlendMode blend)
    {
        if (layer.Blend == blend) return;
        Apply("Blend Mode", () => layer.Blend = blend);
        Invalidate(AffectedArea(layer));
        LayersChanged?.Invoke();
    }

    public void Rename(Layer layer, string name)
    {
        name = name.Trim();
        if (name.Length == 0 || name == layer.Name) return;
        Apply("Rename Layer", () => layer.Name = name);
        LayersChanged?.Invoke();
    }

    public bool CanClip(Layer layer)
    {
        var siblings = document.SiblingsOf(layer.Id);
        return siblings != null && (layer.Clipped || siblings.IndexOf(layer) > 0);
    }

    public void ToggleClippingMask(Layer layer)
    {
        if (!CanClip(layer)) return;
        Apply(layer.Clipped ? "Release Clipping Mask" : "Create Clipping Mask", () => layer.Clipped = !layer.Clipped);
        InvalidateAll();
        LayersChanged?.Invoke();
    }

    public void GroupSelectedLayers()
    {
        var roots = SelectedRoots();
        if (roots.Count == 0) return;
        Apply("Group Layers", () =>
        {
            var top = roots[^1];
            var siblings = document.SiblingsOf(top.Id)!;
            var group = Layer.Group(document.UniqueName("Folder"));
            siblings.Insert(siblings.IndexOf(top) + 1, group);
            foreach (var layer in roots)
            {
                document.SiblingsOf(layer.Id)!.Remove(layer);
                group.Children.Add(layer);
            }
            // A clipped layer needs its base beneath it. Grouped away from the base, the folder takes over the clipping.
            if (group.Children[0].Clipped)
            {
                foreach (var child in group.Children.TakeWhile(c => c.Clipped).ToList()) child.Clipped = false;
                group.Clipped = siblings.IndexOf(group) > 0;
            }
            foreach (var list in document.AllLayers().Select(l => l.Children).Append(document.Layers))
                if (list.Count > 0 && list[0].Clipped) list[0].Clipped = false;
            document.SetActive(group.Id);
        });
        InvalidateAll();
        LayersChanged?.Invoke();
    }

    public void Ungroup(Layer group)
    {
        if (!group.IsGroup) return;
        Apply("Ungroup Layers", () =>
        {
            var siblings = document.SiblingsOf(group.Id)!;
            var index = siblings.IndexOf(group);
            siblings.RemoveAt(index);
            siblings.InsertRange(index, group.Children);
            document.SetActive(group.Children.LastOrDefault()?.Id ?? siblings.LastOrDefault()?.Id);
        });
        InvalidateAll();
        LayersChanged?.Invoke();
    }

    /// <summary>Moves layers next to <paramref name="target"/>: above it, below it, or into it when it is a folder.</summary>
    public void MoveLayers(IReadOnlyList<Layer> layers, Layer? target, LayerDrop drop)
    {
        layers = layers.Where(l => target == null || (l.Id != target.Id && !Document.Flatten(l.Children).Any(c => c.Id == target.Id))).ToList();
        if (layers.Count == 0) return;
        Apply("Reorder Layers", () =>
        {
            foreach (var layer in layers) document.SiblingsOf(layer.Id)!.Remove(layer);
            if (target == null) document.Layers.AddRange(layers);
            else if (drop == LayerDrop.Into && target.IsGroup) target.Children.AddRange(layers);
            else
            {
                var siblings = document.SiblingsOf(target.Id)!;
                var index = siblings.IndexOf(target) + (drop == LayerDrop.Above ? 1 : 0);
                siblings.InsertRange(index, layers);
            }
            // A clipped layer needs a base beneath it.
            foreach (var list in document.AllLayers().Select(l => l.Children).Append(document.Layers))
                if (list.Count > 0 && list[0].Clipped) list[0].Clipped = false;
        });
        InvalidateAll();
        LayersChanged?.Invoke();
    }

    /// <summary>Moves the active layer one step up (+1) or down (-1) among its siblings.</summary>
    public void MoveActiveLayer(int direction)
    {
        if (ActiveLayer is not { } layer) return;
        var siblings = document.SiblingsOf(layer.Id)!;
        var index = siblings.IndexOf(layer);
        var target = index + direction;
        if (target < 0 || target >= siblings.Count) return;
        Apply("Reorder Layers", () =>
        {
            siblings.RemoveAt(index);
            siblings.Insert(target, layer);
            if (siblings[0].Clipped) siblings[0].Clipped = false;
        });
        InvalidateAll();
        LayersChanged?.Invoke();
    }

    public string MergeTitle
    {
        get
        {
            var roots = SelectedRoots();
            if (roots.Count > 1) return "Merge Layers";
            return roots.Count == 1 && roots[0].IsGroup ? "Merge Group" : "Merge Down";
        }
    }

    public bool CanMerge
    {
        get
        {
            var roots = SelectedRoots();
            if (roots.Count > 1) return true;
            if (roots.Count == 0) return false;
            if (roots[0].IsGroup) return roots[0].Children.Count > 0;
            var siblings = document.SiblingsOf(roots[0].Id)!;
            var index = siblings.IndexOf(roots[0]);
            // Merging into a hidden layer would silently throw that layer's pixels away.
            return index > 0 && !siblings[index - 1].IsAdjustment && siblings[index - 1].Visible && roots[0].Visible;
        }
    }

    /// <summary>Ctrl+E: merges the selected layers, the active folder, or the active layer into the one below.</summary>
    public void MergeLayers()
    {
        if (!CanMerge) return;
        var roots = SelectedRoots();
        var title = MergeTitle;
        if (roots.Count == 1 && !roots[0].IsGroup)
        {
            var siblings = document.SiblingsOf(roots[0].Id)!;
            roots.Insert(0, siblings[siblings.IndexOf(roots[0]) - 1]);
        }
        Apply(title, () =>
        {
            var bottom = roots[0];
            var area = document.Bounds;
            foreach (var layer in roots.SelectMany(r => Document.Flatten([r])).Where(l => l.Pixels != null))
                area = Geometry.Union(area, Geometry.RoundOut(layer.VisibleBounds));
            area = Geometry.Intersect(area, new SKRectI(-Document.MaxSide, -Document.MaxSide, 2 * Document.MaxSide, 2 * Document.MaxSide));

            // The merged layer keeps the bottom layer's blend mode and opacity, so those are left out of the render.
            var single = roots.Count == 1;
            var rendered = roots.Select((layer, i) =>
            {
                var copy = layer.Clone();
                copy.Visible = true;
                if (i == 0 || single) { copy.Blend = BlendMode.Normal; copy.Opacity = 1; copy.Clipped = false; }
                // When the bottom layer is itself clipped, its real base lies outside the merge: the layers above it
                // were clipped to that base too, not to each other.
                if (bottom.Clipped) copy.Clipped = false;
                return copy;
            }).Where((copy, i) => roots[i].Visible).ToList();
            var pixels = DocumentRenderer.RenderLayers(document, rendered, area);

            var merged = Layer.Raster(roots[^1].IsGroup || roots.Count > 2 ? roots[^1].Name : bottom.Name, pixels, area.Left, area.Top);
            merged.Blend = bottom.Blend;
            merged.Opacity = bottom.Opacity;
            merged.Clipped = bottom.Clipped;
            merged.Visible = true;
            var target = document.SiblingsOf(roots[^1].Id)!;
            target.Insert(target.IndexOf(roots[^1]) + 1, merged);
            foreach (var layer in roots) document.SiblingsOf(layer.Id)!.Remove(layer);
            document.SetActive(merged.Id);
            TrimToContent(merged);
        });
        EditingMask = false;
        InvalidateAll();
        LayersChanged?.Invoke();
    }

    /// <summary>Flattens everything into a single layer.</summary>
    public void FlattenImage()
    {
        Apply("Flatten Image", () =>
        {
            var merged = Layer.Raster("Background", DocumentRenderer.Flatten(document));
            document.Layers.Clear();
            document.Layers.Add(merged);
            document.SetActive(merged.Id);
        });
        EditingMask = false;
        InvalidateAll();
        LayersChanged?.Invoke();
    }

    /// <summary>Crops a pure-translation layer's bitmap to its non-transparent pixels, so merges don't hoard memory.</summary>
    private static unsafe void TrimToContent(Layer layer)
    {
        if (layer.Pixels is not { } pixels || !layer.Transform.IsPureTranslation(pixels.Width, pixels.Height)) return;
        int left = pixels.Width, top = pixels.Height, right = -1, bottom = -1;
        var data = (byte*)pixels.GetPixels();
        for (var y = 0; y < pixels.Height; y++)
        {
            var row = data + (long)y * pixels.RowBytes;
            int first = -1, last = -1;
            for (var x = 0; x < pixels.Width; x++) if (row[x * 4 + 3] != 0) { if (first < 0) first = x; last = x; }
            if (first < 0) continue;
            left = Math.Min(left, first); right = Math.Max(right, last);
            if (top > y) top = y;
            bottom = y;
        }
        if (right < 0 || (left == 0 && top == 0 && right == pixels.Width - 1 && bottom == pixels.Height - 1)) return;
        var trimmed = Pixels.NewColor(right - left + 1, bottom - top + 1);
        using (var canvas = new SKCanvas(trimmed)) canvas.DrawBitmap(pixels, -left, -top);
        layer.Pixels = trimmed;
        layer.Transform = LayerTransform.Identity(trimmed.Width, trimmed.Height) with { X = layer.Transform.X + left, Y = layer.Transform.Y + top };
    }

    // ---- Layer masks --------------------------------------------------------------------------------------------

    /// <summary>Adds a mask revealing everything, or only the current selection when there is one.</summary>
    public void AddMask(Layer layer, bool hideAll = false)
    {
        if (layer.Mask != null) return;
        Apply("Add Layer Mask", () =>
        {
            if (layer.Pixels == null)
                layer.Mask = document.Selection != null ? Pixels.Clone(document.Selection) : Pixels.NewMask(document.Width, document.Height, hideAll ? (byte)0 : (byte)255);
            else if (document.Selection != null) layer.Mask = SelectionInLayerSpace(layer)!;
            else layer.Mask = Pixels.NewMask(layer.Pixels.Width, layer.Pixels.Height, hideAll ? (byte)0 : (byte)255);
            layer.MaskEnabled = true;
        });
        EditingMask = true;
        Invalidate(AffectedArea(layer));
        LayersChanged?.Invoke();
    }

    public void DeleteMask(Layer layer)
    {
        if (layer.Mask == null) return;
        Apply("Delete Layer Mask", () => layer.Mask = null);
        EditingMask = false;
        Invalidate(AffectedArea(layer));
        LayersChanged?.Invoke();
    }

    /// <summary>Burns the mask into the layer's alpha and removes it.</summary>
    public void ApplyMask(Layer layer)
    {
        if (layer.Mask == null || layer.Pixels == null) return;
        Apply("Apply Layer Mask", () =>
        {
            var pixels = Pixels.Clone(layer.Pixels);
            using (var canvas = new SKCanvas(pixels))
            using (var paint = new SKPaint { BlendMode = SKBlendMode.DstIn })
                canvas.DrawBitmap(layer.Mask, new SKRect(0, 0, pixels.Width, pixels.Height), paint);
            layer.Pixels = pixels;
            layer.Mask = null;
        });
        EditingMask = false;
        Invalidate(AffectedArea(layer));
        LayersChanged?.Invoke();
    }

    public void SetMaskEnabled(Layer layer, bool enabled)
    {
        if (layer.Mask == null || layer.MaskEnabled == enabled) return;
        Apply(enabled ? "Enable Layer Mask" : "Disable Layer Mask", () => layer.MaskEnabled = enabled);
        Invalidate(AffectedArea(layer));
        LayersChanged?.Invoke();
    }

    /// <summary>The document selection resampled into a layer's own pixel grid.</summary>
    public SKBitmap? SelectionInLayerSpace(Layer layer)
    {
        if (document.Selection == null || layer.Pixels == null) return null;
        var mask = Pixels.NewMask(layer.Pixels.Width, layer.Pixels.Height);
        if (!layer.Matrix.TryInvert(out var inverse)) return mask;
        using var canvas = new SKCanvas(mask);
        canvas.SetMatrix(in inverse);
        canvas.DrawImage(Pixels.ImageOf(document.Selection), 0, 0, DocumentRenderer.SamplingFor(inverse));
        return mask;
    }
}

public enum LayerDrop { Above, Below, Into }
