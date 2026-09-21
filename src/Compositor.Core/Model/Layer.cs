using Compositor.Filters;
using SkiaSharp;

namespace Compositor.Model;

public enum LayerKind { Raster, Group, Adjustment }

public enum ShapeKind { Rectangle, RoundedRectangle, Ellipse }

/// <summary>A live shape: redrawn at full sharpness whenever its layer is scaled.</summary>
public sealed record ShapeStyle(ShapeKind Kind, uint Fill, double CornerRadius);

public enum TextAlignment { Left, Center, Right }

/// <summary>Live text: kept as characters and redrawn sharp whenever it is edited or its layer is scaled.</summary>
public sealed record TextStyle
{
    public string Text { get; init; } = "";
    public string FontFamily { get; init; } = "Inter";
    /// <summary>Font size in document pixels.</summary>
    public double Size { get; init; } = 72;
    public uint Color { get; init; } = 0xFF000000;
    public bool Bold { get; init; }
    public bool Italic { get; init; }
    public TextAlignment Alignment { get; init; }
    /// <summary>Line height as a multiple of the font's own spacing.</summary>
    public double LineSpacing { get; init; } = 1;
}

/// <summary>
/// A node in the layer tree. Bitmaps are treated as immutable once a layer has been committed to the document:
/// every edit swaps in a new bitmap, so history snapshots can share pixels freely.
/// </summary>
public sealed class Layer
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = "Layer";
    public LayerKind Kind { get; init; }
    public bool Visible { get; set; } = true;
    public double Opacity { get; set; } = 1;
    public BlendMode Blend { get; set; } = BlendMode.Normal;
    /// <summary>RGBA8888 premultiplied source pixels; null for groups and adjustments.</summary>
    public SKBitmap? Pixels { get; set; }
    public LayerTransform Transform { get; set; } = new();
    /// <summary>Alpha8 coverage (white reveals). For raster layers it matches <see cref="Pixels"/>; otherwise the document.</summary>
    public SKBitmap? Mask { get; set; }
    public bool MaskEnabled { get; set; } = true;
    /// <summary>A clipping mask: this layer only shows where the nearest unclipped sibling below it has coverage.</summary>
    public bool Clipped { get; set; }
    public Adjustment? Adjustment { get; set; }
    public ShapeStyle? Shape { get; set; }
    public TextStyle? Text { get; set; }
    /// <summary>Stroke, shadows and overlay drawn around the pixels; null when the layer has none.</summary>
    public LayerEffects? Effects { get; set; }
    /// <summary>Live layers (shapes and text) are regenerated from their settings; they take pixel edits only once rasterized.</summary>
    public bool IsLive => Shape != null || Text != null;
    /// <summary>Bottom-to-top children of a group.</summary>
    public List<Layer> Children { get; init; } = [];
    public bool Collapsed { get; set; }

    public bool IsGroup => Kind == LayerKind.Group;
    public bool IsAdjustment => Kind == LayerKind.Adjustment;
    public bool HasPixels => Pixels != null;

    public SKMatrix Matrix => Pixels == null ? SKMatrix.Identity : Transform.Matrix(Pixels.Width, Pixels.Height);

    /// <summary>Document-space bounds of the layer's pixels; empty for groups and adjustments.</summary>
    public SKRect Bounds => Pixels == null ? SKRect.Empty : Transform.Bounds(Pixels.Width, Pixels.Height);

    /// <summary>How far the layer's effects reach beyond its pixels, in layer pixels.</summary>
    public int EffectMargin => Pixels != null && Effects != null ? Effects.Margin() : 0;

    /// <summary>The bounds of everything the layer draws: its pixels plus the room its effects take around them.</summary>
    public SKRect VisibleBounds
    {
        get
        {
            if (Pixels == null) return SKRect.Empty;
            var margin = EffectMargin;
            if (margin == 0) return Bounds;
            return Matrix.MapRect(new SKRect(-margin, -margin, Pixels.Width + margin, Pixels.Height + margin));
        }
    }

    /// <summary>A structural copy sharing the (immutable) bitmaps.</summary>
    public Layer Clone(bool newIds = false)
    {
        var copy = new Layer
        {
            Id = newIds ? Guid.NewGuid() : Id, Name = Name, Kind = Kind, Visible = Visible, Opacity = Opacity, Blend = Blend,
            Pixels = Pixels, Transform = Transform, Mask = Mask, MaskEnabled = MaskEnabled, Clipped = Clipped,
            Adjustment = Adjustment, Shape = Shape, Text = Text, Effects = Effects, Collapsed = Collapsed
        };
        foreach (var child in Children) copy.Children.Add(child.Clone(newIds));
        return copy;
    }

    public static Layer Raster(string name, SKBitmap pixels, double x = 0, double y = 0) => new()
    {
        Name = name, Kind = LayerKind.Raster, Pixels = pixels,
        Transform = LayerTransform.Identity(pixels.Width, pixels.Height) with { X = x, Y = y }
    };

    public static Layer Group(string name) => new() { Name = name, Kind = LayerKind.Group };

    public static Layer ForAdjustment(Adjustment adjustment) => new()
    {
        Name = adjustment.DisplayName, Kind = LayerKind.Adjustment, Adjustment = adjustment
    };
}
