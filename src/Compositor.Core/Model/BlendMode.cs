using SkiaSharp;

namespace Compositor.Model;

public enum BlendMode
{
    Normal, Multiply, Screen, Overlay, Darken, Lighten, Difference, ColorDodge, ColorBurn,
    SoftLight, HardLight, Exclusion, Hue, Saturation, Color, Luminosity
}

public static class BlendModeExtensions
{
    public static SKBlendMode ToSkia(this BlendMode mode) => mode switch
    {
        BlendMode.Multiply => SKBlendMode.Multiply,
        BlendMode.Screen => SKBlendMode.Screen,
        BlendMode.Overlay => SKBlendMode.Overlay,
        BlendMode.Darken => SKBlendMode.Darken,
        BlendMode.Lighten => SKBlendMode.Lighten,
        BlendMode.Difference => SKBlendMode.Difference,
        BlendMode.ColorDodge => SKBlendMode.ColorDodge,
        BlendMode.ColorBurn => SKBlendMode.ColorBurn,
        BlendMode.SoftLight => SKBlendMode.SoftLight,
        BlendMode.HardLight => SKBlendMode.HardLight,
        BlendMode.Exclusion => SKBlendMode.Exclusion,
        BlendMode.Hue => SKBlendMode.Hue,
        BlendMode.Saturation => SKBlendMode.Saturation,
        BlendMode.Color => SKBlendMode.Color,
        BlendMode.Luminosity => SKBlendMode.Luminosity,
        _ => SKBlendMode.SrcOver
    };

    public static string DisplayName(this BlendMode mode) => mode switch
    {
        BlendMode.ColorDodge => "Color Dodge",
        BlendMode.ColorBurn => "Color Burn",
        BlendMode.SoftLight => "Soft Light",
        BlendMode.HardLight => "Hard Light",
        _ => mode.ToString()
    };
}
