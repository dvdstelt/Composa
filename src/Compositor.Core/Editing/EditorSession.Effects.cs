using Compositor.Model;
using SkiaSharp;

namespace Compositor.Editing;

public sealed partial class EditorSession
{
    /// <summary>The effect row highlighted in the Layers panel, if any. Cleared whenever the layer selection changes.</summary>
    public (Guid LayerId, LayerEffectKind Kind)? SelectedEffect { get; set; }

    /// <summary>Effects sit on pixel layers (including live shapes and text); folders and adjustments have nothing to draw them around.</summary>
    public static bool CanHaveEffects(Layer layer) => layer.Pixels != null;

    /// <summary>
    /// Adds an effect with its default settings and selects it. A new stroke or overlay takes the background color:
    /// the foreground is usually what the layer is painted in. Leaves the edit open when <paramref name="commit"/> is false.
    /// </summary>
    public bool AddEffect(Layer layer, LayerEffectKind kind, bool commit = true)
    {
        if (!CanHaveEffects(layer)) return false;
        var effects = layer.Effects ?? LayerEffects.Empty;
        if (effects.Contains(kind)) { SelectedEffect = (layer.Id, kind); return true; }
        var background = (uint)Background | 0xFF000000;
        effects = kind switch
        {
            LayerEffectKind.Stroke => effects with { Stroke = new StrokeEffect { Color = background } },
            LayerEffectKind.DropShadow => effects with { Shadow = new ShadowEffect() },
            LayerEffectKind.ColorOverlay => effects with { ColorOverlay = new ColorOverlayEffect { Color = background } },
            _ => effects with { InnerShadow = new ShadowEffect { Distance = 10, Blur = 10 } }
        };
        Begin("Add " + LayerEffects.DisplayName(kind));
        SetEffects(layer, effects);
        if (commit) Commit();
        SelectedEffect = (layer.Id, kind);
        LayersChanged?.Invoke();
        return true;
    }

    /// <summary>Changes a layer's effects live; wrap a dialog's slider drags in Begin/Commit so they undo as one step.</summary>
    public void SetEffects(Layer layer, LayerEffects? effects)
    {
        if (!CanHaveEffects(layer)) return;
        var before = AffectedArea(layer);
        layer.Effects = effects == null || effects.IsEmpty ? null : effects.Clamped();
        Invalidate(Geometry.Union(before, AffectedArea(layer)));
    }

    public void ToggleEffect(Layer layer, LayerEffectKind kind)
    {
        if (layer.Effects is not { } effects || !effects.Contains(kind)) return;
        var enabled = effects.IsEnabled(kind);
        Apply((enabled ? "Hide " : "Show ") + LayerEffects.DisplayName(kind), () => SetEffects(layer, effects.WithEnabled(kind, !enabled)));
        LayersChanged?.Invoke();
    }

    public void RemoveEffect(Layer layer, LayerEffectKind kind)
    {
        if (layer.Effects is not { } effects || !effects.Contains(kind)) return;
        Apply("Remove " + LayerEffects.DisplayName(kind), () => SetEffects(layer, effects.Without(kind)));
        if (SelectedEffect is { } selected && selected.LayerId == layer.Id && selected.Kind == kind) SelectedEffect = null;
        LayersChanged?.Invoke();
    }

    /// <summary>Delete with an effect row highlighted removes that effect. Returns false when none is highlighted.</summary>
    public bool RemoveSelectedEffect()
    {
        if (SelectedEffect is not { } selected || document.Find(selected.LayerId) is not { } layer) { SelectedEffect = null; return false; }
        RemoveEffect(layer, selected.Kind);
        return true;
    }

    public bool CanCopyEffect(LayerEffectKind kind, Layer from, Layer to) =>
        from.Id != to.Id && from.Effects?.Contains(kind) == true && CanHaveEffects(to);

    /// <summary>Alt-dragging an effect row onto another layer gives that layer a copy of the effect.</summary>
    public void CopyEffect(LayerEffectKind kind, Layer from, Layer to)
    {
        if (!CanCopyEffect(kind, from, to)) return;
        Apply("Copy " + LayerEffects.DisplayName(kind), () => SetEffects(to, (to.Effects ?? LayerEffects.Empty).WithFrom(kind, from.Effects!)));
        SelectedEffect = (to.Id, kind);
        LayersChanged?.Invoke();
    }
}
