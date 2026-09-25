using Composa.Filters;
using SkiaSharp;

namespace Composa.Model;

public enum LayerKind { Raster, Group, Adjustment }

public enum ShapeKind { Rectangle, RoundedRectangle, Ellipse, Line }

/// <summary>A live shape: redrawn at full sharpness whenever its layer is scaled.</summary>
public sealed record ShapeStyle(ShapeKind Kind, uint Fill, double CornerRadius)
{
    /// <summary>A line's thickness in layer pixels; other shapes ignore it.</summary>
    public double LineWidth { get; init; }
    /// <summary>A line's two ends as fractions of the layer's box (0 to 1), so a scaled line still runs between the same two places. Null runs corner to corner.</summary>
    public double? StartX { get; init; }
    public double? StartY { get; init; }
    public double? EndX { get; init; }
    public double? EndY { get; init; }

    public static string DisplayName(ShapeKind kind) => kind == ShapeKind.RoundedRectangle ? "Rounded Rectangle" : kind.ToString();
}

public enum TextAlignment { Left, Center, Right }

/// <summary>Live text: kept as characters and redrawn sharp whenever it is edited or its layer is scaled.</summary>
public sealed record TextStyle
{
    public const int MaxLength = 100_000;
    public const double MinBox = 16, MaxBox = DocumentLimits.MaxSide;

    public string Text { get; init; } = "";
    public string FontFamily { get; init; } = "Inter";
    /// <summary>Font size in layer pixels.</summary>
    public double Size { get; init; } = 72;
    public uint Color { get; init; } = 0xFF000000;
    public bool Bold { get; init; }
    public bool Italic { get; init; }
    public TextAlignment Alignment { get; init; }
    /// <summary>Extra space after every character, in layer pixels.</summary>
    public double Tracking { get; init; }
    /// <summary>Baseline to baseline, in layer pixels, as Photoshop's Leading is. 0 is Auto: 120% of the font size.</summary>
    public double Leading { get; init; }
    /// <summary>Fixed paragraph bounds in layer pixels; text wraps inside them. Null is point text, which is as big as what is typed.</summary>
    public double? BoxWidth { get; init; }
    public double? BoxHeight { get; init; }

    [System.Text.Json.Serialization.JsonIgnore] public double LineHeight => Leading > 0 ? Leading : Size * 1.2;
    [System.Text.Json.Serialization.JsonIgnore] public bool IsBox => BoxWidth != null && BoxHeight != null;

    /// <summary>Every value inside its range, so a damaged file or a wild drag cannot ask for an impossible layout.</summary>
    public TextStyle Clamped()
    {
        var text = Text.Length > MaxLength ? Text[..MaxLength] : Text;
        var box = BoxWidth is { } w && BoxHeight is { } h && double.IsFinite(w) && double.IsFinite(h);
        return this with
        {
            Text = text,
            Size = double.IsFinite(Size) ? Math.Clamp(Size, 1, 2000) : 72,
            Tracking = double.IsFinite(Tracking) ? Math.Clamp(Tracking, -100, 1000) : 0,
            Leading = double.IsFinite(Leading) ? Math.Clamp(Leading, 0, 5000) : 0,
            BoxWidth = box ? Math.Clamp(Math.Round(BoxWidth!.Value), MinBox, MaxBox) : null,
            BoxHeight = box ? Math.Clamp(Math.Round(BoxHeight!.Value), MinBox, MaxBox) : null,
            Color = Color | 0xFF000000
        };
    }

    /// <summary>The same text drawn <paramref name="factor"/> times as large: size, spacing and box together.</summary>
    public TextStyle Scaled(double factor) => Scaled(factor, factor);

    public TextStyle Scaled(double horizontal, double vertical) => (this with
    {
        Size = Size * vertical, Tracking = Tracking * horizontal, Leading = Leading * vertical,
        BoxWidth = BoxWidth * horizontal, BoxHeight = BoxHeight * vertical
    }).Clamped();

    /// <summary>A text layer's name: its first words on one line, so a paragraph never makes a Layers row taller.</summary>
    public string LayerName()
    {
        var flattened = string.Join(' ', Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flattened.Length == 0 ? "Text" : flattened.Length > 40 ? flattened[..40] : flattened;
    }
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
