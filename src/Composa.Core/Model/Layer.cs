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

/// <summary>Letters painted in a color other than their style's own: a character range into the text (end exclusive) and its opaque color.</summary>
public sealed record TextColorRun(int Start, int Length, uint Color)
{
    [System.Text.Json.Serialization.JsonIgnore] public int End => Start + Length;
}

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
    /// <summary>
    /// Letters in a color other than <see cref="Color"/>, as character indices into <see cref="Text"/>, sorted and not
    /// overlapping. Null when the whole text is one color, which is what every style starts as.
    /// </summary>
    public IReadOnlyList<TextColorRun>? ColorRuns { get; init; }

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
            Color = Color | 0xFF000000,
            ColorRuns = ValidRuns(ColorRuns, text.Length)
        };
    }

    /// <summary>The runs as they are when they are sorted, disjoint and inside the text, with their colors opaque; otherwise none, since a damaged file cannot say which letter has which color.</summary>
    private static IReadOnlyList<TextColorRun>? ValidRuns(IReadOnlyList<TextColorRun>? runs, int length)
    {
        if (runs == null || runs.Count == 0) return null;
        var end = 0;
        foreach (var run in runs)
        {
            if (run.Start < end || run.Length <= 0 || run.Start > length - run.Length) return null;
            end = run.End;
        }
        return runs.All(r => (r.Color & 0xFF000000) == 0xFF000000) ? runs : runs.Select(r => r with { Color = r.Color | 0xFF000000 }).ToList();
    }

    /// <summary>The color of the character at an index: its run's, or the style's own.</summary>
    public uint ColorAt(int index)
    {
        if (ColorRuns != null)
            foreach (var run in ColorRuns)
                if (run.Start <= index && index < run.End) return run.Color;
        return Color;
    }

    /// <summary>
    /// Paints the characters from <paramref name="start"/> to <paramref name="end"/> (exclusive) in a color. An empty
    /// range, or one covering the whole text, recolors all of it and drops the runs.
    /// </summary>
    public TextStyle WithColor(uint color, int start, int end)
    {
        color |= 0xFF000000;
        var count = Text.Length;
        start = Math.Clamp(start, 0, count);
        end = Math.Clamp(end, start, count);
        if (start == end || (start == 0 && end == count)) return this with { Color = color, ColorRuns = null };
        var colors = UnitColors();
        for (var i = start; i < end; i++) colors[i] = color;
        return this with { ColorRuns = Runs(colors, Color) };
    }

    /// <summary>
    /// Keeps the colors on their letters when the characters from <paramref name="start"/> to <paramref name="end"/>
    /// are replaced by <paramref name="length"/> new ones, which take the color of the letter before them, as typing
    /// does. Call before <see cref="Text"/> changes.
    /// </summary>
    public TextStyle WithReplacedCharacters(int start, int end, int length)
    {
        if (ColorRuns == null) return this;
        var colors = UnitColors();
        start = Math.Clamp(start, 0, colors.Count);
        end = Math.Clamp(end, start, colors.Count);
        var inherited = start > 0 ? colors[start - 1] : end > start ? colors[start] : colors.Count > 0 ? colors[0] : Color;
        colors.RemoveRange(start, end - start);
        colors.InsertRange(start, Enumerable.Repeat(inherited, Math.Max(0, length)));
        return this with { ColorRuns = Runs(colors, Color) };
    }

    /// <summary>The style the next text takes: the same look, without this text's wording, box and per-letter colors.</summary>
    public TextStyle AsDefaults() => this with { Text = "", BoxWidth = null, BoxHeight = null, ColorRuns = null };

    private List<uint> UnitColors()
    {
        var colors = Enumerable.Repeat(Color, Text.Length).ToList();
        foreach (var run in ColorRuns ?? [])
            for (var i = Math.Max(0, run.Start); i < Math.Min(colors.Count, run.End); i++) colors[i] = run.Color;
        return colors;
    }

    private static IReadOnlyList<TextColorRun>? Runs(List<uint> colors, uint baseColor)
    {
        var runs = new List<TextColorRun>();
        for (var i = 0; i < colors.Count; i++)
        {
            if (colors[i] == baseColor) continue;
            if (runs.Count > 0 && runs[^1].End == i && runs[^1].Color == colors[i]) runs[^1] = runs[^1] with { Length = runs[^1].Length + 1 };
            else runs.Add(new TextColorRun(i, 1, colors[i]));
        }
        return runs.Count == 0 ? null : runs;
    }

    // The runs are a list, which a record compares by reference; styles are compared everywhere to tell a real change
    // from none, so equality is spelled out. A new property belongs in both members.
    public bool Equals(TextStyle? other) =>
        other is not null && Text == other.Text && FontFamily == other.FontFamily && Size == other.Size && Color == other.Color
        && Bold == other.Bold && Italic == other.Italic && Alignment == other.Alignment && Tracking == other.Tracking && Leading == other.Leading
        && BoxWidth == other.BoxWidth && BoxHeight == other.BoxHeight
        && (ColorRuns == null ? other.ColorRuns == null : other.ColorRuns != null && ColorRuns.SequenceEqual(other.ColorRuns));

    public override int GetHashCode() =>
        HashCode.Combine(Text, FontFamily, Size, Color, Bold, Italic, Alignment, HashCode.Combine(Tracking, Leading, BoxWidth, BoxHeight, ColorRuns?.Count ?? 0));

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
