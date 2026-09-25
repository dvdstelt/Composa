using Avalonia.Media;

namespace Composa.App.Controls;

/// <summary>
/// The colors a <see cref="SliderField"/> shows along its track when the value is a color or moves one: left to right, evenly
/// spaced. They match the macOS app's Camera Raw tracks so a slider reads the same in both.
/// </summary>
public static class SliderTracks
{
    /// <summary>Blue to yellow, as white balance temperature.</summary>
    public static readonly IReadOnlyList<Color> Temperature = [Rgb(0.22, 0.46, 0.95), Rgb(0.98, 0.82, 0.18)];
    /// <summary>Green to mauve, as white balance tint.</summary>
    public static readonly IReadOnlyList<Color> Tint = [Rgb(0.28, 0.70, 0.34), Rgb(0.70, 0.40, 0.64)];
    /// <summary>Gray to a strong color, for vibrance and saturation.</summary>
    public static readonly IReadOnlyList<Color> Chroma = [Rgb(0.62, 0.62, 0.64), Rgb(0.86, 0.18, 0.20)];
    /// <summary>Black to white, for lightness.</summary>
    public static readonly IReadOnlyList<Color> Lightness = [Colors.Black, Colors.White];
    /// <summary>Color Balance's Cyan / Red.</summary>
    public static readonly IReadOnlyList<Color> CyanRed = [Rgb(0.10, 0.72, 0.80), Rgb(0.86, 0.18, 0.20)];
    /// <summary>Color Balance's Magenta / Green.</summary>
    public static readonly IReadOnlyList<Color> MagentaGreen = [Rgb(0.80, 0.22, 0.70), Rgb(0.24, 0.70, 0.30)];
    /// <summary>Color Balance's Yellow / Blue.</summary>
    public static readonly IReadOnlyList<Color> YellowBlue = [Rgb(0.95, 0.82, 0.18), Rgb(0.22, 0.40, 0.92)];

    /// <summary>Neighbouring hues around a color family's centre, 50 degrees either way.</summary>
    public static IReadOnlyList<Color> Hue(double degrees) => [Hsv(degrees - 50, 0.85, 0.9), Hsv(degrees + 50, 0.85, 0.9)];

    /// <summary>Gray to that family's own color.</summary>
    public static IReadOnlyList<Color> Saturation(double degrees) => [Rgb(0.55, 0.55, 0.56), Hsv(degrees, 0.9, 0.9)];

    /// <summary>Dark to light in that family's hue.</summary>
    public static IReadOnlyList<Color> Luminance(double degrees) => [Hsv(degrees, 0.55, 0.18), Hsv(degrees, 0.35, 0.95)];

    /// <summary>The whole hue circle with that hue in the middle, so a hue shift of zero sits on the color it shifts.</summary>
    public static IReadOnlyList<Color> Spectrum(double centre) => Enumerable.Range(0, 13).Select(i => Hsv(centre - 180 + i * 30, 0.85, 0.9)).ToList();

    private static Color Rgb(double r, double g, double b) => Color.FromRgb((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));

    private static Color Hsv(double degrees, double saturation, double value) => new HsvColor(1, ((degrees % 360) + 360) % 360, saturation, value).ToRgb();
}
