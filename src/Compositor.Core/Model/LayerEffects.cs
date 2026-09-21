using System.Text.Json.Serialization;

namespace Compositor.Model;

public enum LayerEffectKind { Stroke, DropShadow, ColorOverlay, InnerShadow }

/// <summary>A line drawn around what the layer shows, outside its edge or inside it.</summary>
public sealed record StrokeEffect
{
    public const double MaxSize = 500;
    public bool Enabled { get; init; } = true;
    /// <summary>Width in layer pixels.</summary>
    public double Size { get; init; } = 4;
    public uint Color { get; init; } = 0xFF000000;
    public double Opacity { get; init; } = 1;
    public bool Inside { get; init; }

    public StrokeEffect Clamped() => this with
    {
        Size = double.IsFinite(Size) ? Math.Clamp(Size, 0, MaxSize) : 4,
        Opacity = double.IsFinite(Opacity) ? Math.Clamp(Opacity, 0, 1) : 1,
        Color = Color | 0xFF000000
    };
}

/// <summary>The layer's shape repeated behind it, offset and softened.</summary>
public sealed record ShadowEffect
{
    public bool Enabled { get; init; } = true;
    /// <summary>Where the light comes from, in degrees counterclockwise from the right, as Photoshop's dial is: 90 is from straight above, which drops the shadow straight down.</summary>
    public double Angle { get; init; } = 90;
    public double Distance { get; init; } = 20;
    public double Blur { get; init; } = 20;
    public uint Color { get; init; } = 0xFF000000;
    public double Opacity { get; init; } = 0.5;

    /// <summary>Where the shadow sits, in layer pixels (y grows downward, as the layer's own pixels do).</summary>
    [JsonIgnore]
    public (double X, double Y) Offset
    {
        get
        {
            var radians = Angle * Math.PI / 180;
            return (-Math.Cos(radians) * Distance, Math.Sin(radians) * Distance);
        }
    }

    public ShadowEffect Clamped() => this with
    {
        Angle = double.IsFinite(Angle) ? Math.Clamp(Angle, -360, 360) : 90,
        Distance = double.IsFinite(Distance) ? Math.Clamp(Distance, 0, 5000) : 20,
        Blur = double.IsFinite(Blur) ? Math.Clamp(Blur, 0, 500) : 20,
        Opacity = double.IsFinite(Opacity) ? Math.Clamp(Opacity, 0, 1) : 0.5,
        Color = Color | 0xFF000000
    };
}

/// <summary>A flat color over everything the layer shows.</summary>
public sealed record ColorOverlayEffect
{
    public bool Enabled { get; init; } = true;
    public uint Color { get; init; } = 0xFF000000;
    public double Opacity { get; init; } = 1;

    public ColorOverlayEffect Clamped() => this with { Opacity = double.IsFinite(Opacity) ? Math.Clamp(Opacity, 0, 1) : 1, Color = Color | 0xFF000000 };
}

/// <summary>
/// What a layer draws around itself. Kept with the layer, so it follows every edit and can be changed or removed at
/// any time; the pixels themselves are never touched. A null member means the effect is absent; a disabled one keeps
/// its settings and stays listed under the layer.
/// </summary>
public sealed record LayerEffects
{
    public StrokeEffect? Stroke { get; init; }
    public ShadowEffect? Shadow { get; init; }
    public ColorOverlayEffect? ColorOverlay { get; init; }
    /// <summary>A shadow cast inside the layer's own edges, as though it were cut out of what is behind it. Shares the shadow's settings.</summary>
    public ShadowEffect? InnerShadow { get; init; }

    public static readonly LayerEffects Empty = new();

    [JsonIgnore] public bool IsEmpty => Stroke == null && Shadow == null && ColorOverlay == null && InnerShadow == null;

    /// <summary>The effects present, in the order they are listed under a layer.</summary>
    [JsonIgnore] public IEnumerable<LayerEffectKind> Kinds => Enum.GetValues<LayerEffectKind>().Where(Contains);

    public bool Contains(LayerEffectKind kind) => kind switch
    {
        LayerEffectKind.Stroke => Stroke != null,
        LayerEffectKind.DropShadow => Shadow != null,
        LayerEffectKind.ColorOverlay => ColorOverlay != null,
        _ => InnerShadow != null
    };

    public bool IsEnabled(LayerEffectKind kind) => kind switch
    {
        LayerEffectKind.Stroke => Stroke?.Enabled == true,
        LayerEffectKind.DropShadow => Shadow?.Enabled == true,
        LayerEffectKind.ColorOverlay => ColorOverlay?.Enabled == true,
        _ => InnerShadow?.Enabled == true
    };

    public uint? ColorOf(LayerEffectKind kind) => kind switch
    {
        LayerEffectKind.Stroke => Stroke?.Color,
        LayerEffectKind.DropShadow => Shadow?.Color,
        LayerEffectKind.ColorOverlay => ColorOverlay?.Color,
        _ => InnerShadow?.Color
    };

    public LayerEffects WithColor(LayerEffectKind kind, uint color) => kind switch
    {
        LayerEffectKind.Stroke => this with { Stroke = Stroke == null ? null : Stroke with { Color = color } },
        LayerEffectKind.DropShadow => this with { Shadow = Shadow == null ? null : Shadow with { Color = color } },
        LayerEffectKind.ColorOverlay => this with { ColorOverlay = ColorOverlay == null ? null : ColorOverlay with { Color = color } },
        _ => this with { InnerShadow = InnerShadow == null ? null : InnerShadow with { Color = color } }
    };

    public LayerEffects WithEnabled(LayerEffectKind kind, bool enabled) => kind switch
    {
        LayerEffectKind.Stroke => this with { Stroke = Stroke == null ? null : Stroke with { Enabled = enabled } },
        LayerEffectKind.DropShadow => this with { Shadow = Shadow == null ? null : Shadow with { Enabled = enabled } },
        LayerEffectKind.ColorOverlay => this with { ColorOverlay = ColorOverlay == null ? null : ColorOverlay with { Enabled = enabled } },
        _ => this with { InnerShadow = InnerShadow == null ? null : InnerShadow with { Enabled = enabled } }
    };

    public LayerEffects Without(LayerEffectKind kind) => kind switch
    {
        LayerEffectKind.Stroke => this with { Stroke = null },
        LayerEffectKind.DropShadow => this with { Shadow = null },
        LayerEffectKind.ColorOverlay => this with { ColorOverlay = null },
        _ => this with { InnerShadow = null }
    };

    /// <summary>Takes one effect (present or absent) over from another set.</summary>
    public LayerEffects WithFrom(LayerEffectKind kind, LayerEffects source) => kind switch
    {
        LayerEffectKind.Stroke => this with { Stroke = source.Stroke },
        LayerEffectKind.DropShadow => this with { Shadow = source.Shadow },
        LayerEffectKind.ColorOverlay => this with { ColorOverlay = source.ColorOverlay },
        _ => this with { InnerShadow = source.InnerShadow }
    };

    /// <summary>Only the effects that are switched on, with every value inside its range.</summary>
    public LayerEffects Visible() => new()
    {
        Stroke = Stroke?.Enabled == true ? Stroke.Clamped() : null,
        Shadow = Shadow?.Enabled == true ? Shadow.Clamped() : null,
        ColorOverlay = ColorOverlay?.Enabled == true ? ColorOverlay.Clamped() : null,
        InnerShadow = InnerShadow?.Enabled == true ? InnerShadow.Clamped() : null
    };

    /// <summary>Every value inside its range, for settings read from a file.</summary>
    public LayerEffects Clamped() => new()
    {
        Stroke = Stroke?.Clamped(), Shadow = Shadow?.Clamped(), ColorOverlay = ColorOverlay?.Clamped(), InnerShadow = InnerShadow?.Clamped()
    };

    /// <summary>How far the visible effects reach beyond the layer's pixels, in layer pixels.</summary>
    public int Margin()
    {
        var visible = Visible();
        double margin = 0;
        if (visible.Stroke is { Inside: false } stroke && stroke.Opacity > 0) margin = Math.Max(margin, stroke.Size);
        if (visible.Shadow is { } shadow && shadow.Opacity > 0) margin = Math.Max(margin, shadow.Distance + shadow.Blur * 3);
        return visible.IsEmpty ? 0 : (int)Math.Ceiling(margin) + 2;
    }

    /// <summary>True when drawing the layer needs the effects renderer at all.</summary>
    [JsonIgnore] public bool HasVisible => !Visible().IsEmpty;

    public static string DisplayName(LayerEffectKind kind) => kind switch
    {
        LayerEffectKind.DropShadow => "Drop Shadow",
        LayerEffectKind.ColorOverlay => "Color Overlay",
        LayerEffectKind.InnerShadow => "Inner Shadow",
        _ => "Stroke"
    };
}
