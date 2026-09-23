using System.Text.Json.Serialization;

namespace Composa.Filters;

public enum CameraRawWhiteBalance { Custom, Auto }

/// <summary>Glow's three looks. Warmth tints Diffusion and Bloom from cool to warm; Halation's fringe stays red.</summary>
public enum CameraRawGlowStyle { Diffusion, Bloom, Halation }

/// <summary>Post-crop vignette. Highlight Priority is the style whose Highlights slider protects bright pixels.</summary>
public enum CameraRawVignetteStyle { HighlightPriority, ColorPriority, PaintOverlay }

/// <summary>The panel's groups, each of which can be switched off without clearing its sliders.</summary>
public enum CameraRawGroup { Light, Color, Effects, Curve, Mixer, Grading, Detail, Optics, Calibration }

/// <summary>Parametric regions and point curves. Amounts are -100…100; curve points use 0…1 on both axes.</summary>
public sealed record CameraRawCurve
{
    public double Shadows { get; init; }
    public double Darks { get; init; }
    public double Lights { get; init; }
    public double Highlights { get; init; }
    /// <summary>Dividers, 0…100, kept in order: where each parametric slider hands off to the next.</summary>
    public double ShadowSplit { get; init; } = 25;
    public double DarkSplit { get; init; } = 50;
    public double LightSplit { get; init; } = 75;
    public CurvePoint[] Rgb { get; init; } = Linear;
    public CurvePoint[] Red { get; init; } = Linear;
    public CurvePoint[] Green { get; init; } = Linear;
    public CurvePoint[] Blue { get; init; } = Linear;
    /// <summary>How much the composite curve also changes saturation, -100…100. 0 keeps it to brightness.</summary>
    public double RefineSaturation { get; init; }

    public static readonly CurvePoint[] Linear = [new(0, 0), new(1, 1)];

    public static bool IsLinear(CurvePoint[] points) => points.Length == 2 && points[0] == new CurvePoint(0, 0) && points[1] == new CurvePoint(1, 1);

    [JsonIgnore] public bool Adjusts => Shadows != 0 || Darks != 0 || Lights != 0 || Highlights != 0 || RefineSaturation != 0
        || !IsLinear(Rgb) || !IsLinear(Red) || !IsLinear(Green) || !IsLinear(Blue);

    /// <summary>Lifts or lowers the region a tone belongs to. Dividers are fractions of the tonal range.</summary>
    public double Parametric(double tone)
    {
        double shadow = ShadowSplit / 100, dark = DarkSplit / 100, light = LightSplit / 100;
        var (amount, low, high) = tone < shadow ? (Shadows, 0, shadow) : tone < dark ? (Darks, shadow, dark) : tone < light ? (Lights, dark, light) : (Highlights, light, 1);
        var span = Math.Max(0.02, high - low);
        var weight = 1 - Math.Abs(tone - (low + high) / 2) / (span / 2);
        return Math.Clamp(tone + amount / 100 * Math.Max(0, weight) * 0.22, 0, 1);
    }

    /// <summary>The 256-entry table of the parametric sliders followed by the RGB point curve.</summary>
    public float[] LumaTable() => Enumerable.Range(0, 256).Select(i => (float)Point(Parametric(i / 255.0), Rgb)).ToArray();

    public float[] ChannelTable(CurvePoint[] points) => Enumerable.Range(0, 256).Select(i => (float)Point(i / 255.0, points)).ToArray();

    private static double Point(double x, CurvePoint[] points)
    {
        if (points.Length < 2) return x;
        var curve = new CurvesAdjustment().WithChannel(0, points.Select(p => new CurvePoint(p.X * 255, p.Y * 255)));
        return curve.Value(x * 255, 0) / 255;
    }

    public CameraRawCurve Normalized()
    {
        var shadowSplit = CameraRawSettings.Clamp(ShadowSplit, 5, 90, 25);
        var darkSplit = CameraRawSettings.Clamp(DarkSplit, shadowSplit + 2, 95, 50);
        return this with
        {
            Shadows = CameraRawSettings.Clamp(Shadows, -100, 100, 0), Darks = CameraRawSettings.Clamp(Darks, -100, 100, 0),
            Lights = CameraRawSettings.Clamp(Lights, -100, 100, 0), Highlights = CameraRawSettings.Clamp(Highlights, -100, 100, 0),
            RefineSaturation = CameraRawSettings.Clamp(RefineSaturation, -100, 100, 0),
            ShadowSplit = shadowSplit, DarkSplit = darkSplit, LightSplit = CameraRawSettings.Clamp(LightSplit, darkSplit + 2, 98, 75),
            Rgb = Repair(Rgb), Red = Repair(Red), Green = Repair(Green), Blue = Repair(Blue)
        };
    }

    private static CurvePoint[] Repair(CurvePoint[] points)
    {
        var sorted = points.Where(p => double.IsFinite(p.X) && double.IsFinite(p.Y)).OrderBy(p => p.X).ToList();
        if (sorted.Count < 2) return Linear;
        var kept = new List<CurvePoint> { new(0, Math.Clamp(sorted[0].Y, 0, 1)) };
        foreach (var point in sorted.Skip(1).Take(sorted.Count - 2))
        {
            var x = Math.Clamp(point.X, 0.01, 0.99);
            if (x > kept[^1].X + 0.01) kept.Add(new CurvePoint(x, Math.Clamp(point.Y, 0, 1)));
        }
        kept.Add(new CurvePoint(1, Math.Clamp(sorted[^1].Y, 0, 1)));
        return kept.ToArray();
    }
}

/// <summary>Eight color families, each with hue, saturation and luminance shifts of -100…100.</summary>
public sealed record CameraRawMixer
{
    public static readonly string[] Names = ["Reds", "Oranges", "Yellows", "Greens", "Aquas", "Blues", "Purples", "Magentas"];
    public static readonly double[] Centers = [0, 30, 60, 120, 180, 240, 270, 300];
    public double[] Hue { get; init; } = new double[8];
    public double[] Saturation { get; init; } = new double[8];
    public double[] Luminance { get; init; } = new double[8];

    [JsonIgnore] public bool Adjusts => Hue.Any(v => v != 0) || Saturation.Any(v => v != 0) || Luminance.Any(v => v != 0);

    public CameraRawMixer With(int tab, int family, double value)
    {
        var values = (double[])(tab == 0 ? Hue : tab == 1 ? Saturation : Luminance).Clone();
        values[family] = value;
        return tab switch { 0 => this with { Hue = values }, 1 => this with { Saturation = values }, _ => this with { Luminance = values } };
    }

    public double Get(int tab, int family) => (tab == 0 ? Hue : tab == 1 ? Saturation : Luminance)[family];

    /// <summary>The 24 values the pixel loop reads: hue, saturation and luminance for the eight families, -1…1.</summary>
    public float[] Floats() => Hue.Concat(Saturation).Concat(Luminance).Select(v => (float)(v / 100)).ToArray();

    public CameraRawMixer Normalized() => this with
    {
        Hue = Fix(Hue), Saturation = Fix(Saturation), Luminance = Fix(Luminance)
    };

    private static double[] Fix(double[] values)
    {
        var result = new double[8];
        for (var i = 0; i < 8 && i < values.Length; i++) result[i] = CameraRawSettings.Clamp(values[i], -100, 100, 0);
        return result;
    }
}

/// <summary>One color wheel: a hue around the wheel, how strongly it tints, and a brightness change.</summary>
public sealed record CameraRawWheel
{
    /// <summary>0…360.</summary>
    public double Hue { get; init; }
    /// <summary>0…100.</summary>
    public double Saturation { get; init; }
    /// <summary>-100…100.</summary>
    public double Luminance { get; init; }
    [JsonIgnore] public bool Adjusts => Saturation != 0 || Luminance != 0;
    public CameraRawWheel Normalized() => new()
    {
        Hue = CameraRawSettings.Clamp(Hue, 0, 360, 0), Saturation = CameraRawSettings.Clamp(Saturation, 0, 100, 0), Luminance = CameraRawSettings.Clamp(Luminance, -100, 100, 0)
    };
}

/// <summary>Four color wheels plus how the three tonal wheels overlap and which end they favor.</summary>
public sealed record CameraRawGrading
{
    public CameraRawWheel Shadows { get; init; } = new();
    public CameraRawWheel Midtones { get; init; } = new();
    public CameraRawWheel Highlights { get; init; } = new();
    public CameraRawWheel Global { get; init; } = new();
    /// <summary>0…100. Higher values let the three tonal wheels overlap more.</summary>
    public double Blending { get; init; } = 50;
    /// <summary>-100…100. Negative favors shadows, positive favors highlights.</summary>
    public double Balance { get; init; }

    public static readonly string[] Names = ["Shadows", "Midtones", "Highlights", "Global"];
    [JsonIgnore] public CameraRawWheel[] Wheels => [Shadows, Midtones, Highlights, Global];
    [JsonIgnore] public bool Adjusts => Wheels.Any(w => w.Adjusts);

    public CameraRawGrading WithWheel(int index, CameraRawWheel wheel) => index switch
    {
        0 => this with { Shadows = wheel }, 1 => this with { Midtones = wheel }, 2 => this with { Highlights = wheel }, _ => this with { Global = wheel }
    };

    /// <summary>Twelve values: hue in turns, saturation 0…1 and luminance -1…1 for each wheel.</summary>
    public float[] Floats() => Wheels.SelectMany(w => new[] { (float)(w.Hue / 360), (float)(w.Saturation / 100), (float)(w.Luminance / 100) }).ToArray();

    public CameraRawGrading Normalized() => this with
    {
        Shadows = Shadows.Normalized(), Midtones = Midtones.Normalized(), Highlights = Highlights.Normalized(), Global = Global.Normalized(),
        Blending = CameraRawSettings.Clamp(Blending, 0, 100, 50), Balance = CameraRawSettings.Clamp(Balance, -100, 100, 0)
    };
}

/// <summary>Sharpening and manual noise reduction. Amount is 0…150; the rest use 0…100.</summary>
public sealed record CameraRawDetail
{
    public double SharpenAmount { get; init; }
    public double SharpenRadius { get; init; } = 10;
    public double SharpenDetail { get; init; } = 25;
    public double SharpenMasking { get; init; }
    public double NoiseLuminance { get; init; }
    public double NoiseLuminanceDetail { get; init; } = 50;
    public double NoiseLuminanceContrast { get; init; }
    public double NoiseColor { get; init; }
    public double NoiseColorDetail { get; init; } = 50;
    public double NoiseColorSmoothness { get; init; } = 50;

    [JsonIgnore] public bool Adjusts => SharpenAmount != 0 || NoiseLuminance != 0 || NoiseColor != 0;

    public CameraRawDetail Normalized() => new()
    {
        SharpenAmount = CameraRawSettings.Clamp(SharpenAmount, 0, 150, 0), SharpenRadius = CameraRawSettings.Clamp(SharpenRadius, 0, 100, 10),
        SharpenDetail = CameraRawSettings.Clamp(SharpenDetail, 0, 100, 25), SharpenMasking = CameraRawSettings.Clamp(SharpenMasking, 0, 100, 0),
        NoiseLuminance = CameraRawSettings.Clamp(NoiseLuminance, 0, 100, 0), NoiseLuminanceDetail = CameraRawSettings.Clamp(NoiseLuminanceDetail, 0, 100, 50),
        NoiseLuminanceContrast = CameraRawSettings.Clamp(NoiseLuminanceContrast, 0, 100, 0), NoiseColor = CameraRawSettings.Clamp(NoiseColor, 0, 100, 0),
        NoiseColorDetail = CameraRawSettings.Clamp(NoiseColorDetail, 0, 100, 50), NoiseColorSmoothness = CameraRawSettings.Clamp(NoiseColorSmoothness, 0, 100, 50)
    };
}

/// <summary>
/// Chromatic aberration, manual distortion, defringe and lens-vignetting correction. A rendered layer carries no lens
/// metadata, so the profile switch only scales a generic correction.
/// </summary>
public sealed record CameraRawOptics
{
    public bool RemoveChromaticAberration { get; init; }
    public bool EnableLensProfile { get; init; }
    public double ProfileDistortion { get; init; } = 100;
    public double ProfileVignetting { get; init; } = 100;
    /// <summary>-100…100, the Lens Correction filter's sign convention.</summary>
    public double Distortion { get; init; }
    public double PurpleAmount { get; init; }
    /// <summary>Degrees on the color wheel, 0…360; the low handle stays below the high one.</summary>
    public double PurpleHueLow { get; init; } = 270;
    public double PurpleHueHigh { get; init; } = 310;
    public double GreenAmount { get; init; }
    public double GreenHueLow { get; init; } = 60;
    public double GreenHueHigh { get; init; } = 120;
    /// <summary>Brightens the corners to counter lens falloff, -100…100; the midpoint is 0…100.</summary>
    public double VignetteAmount { get; init; }
    public double VignetteMidpoint { get; init; } = 50;

    [JsonIgnore] public bool Adjusts => RemoveChromaticAberration || EnableLensProfile || Distortion != 0 || PurpleAmount != 0 || GreenAmount != 0 || VignetteAmount != 0;

    /// <summary>The combined distortion handed to the Lens Correction warp, -1…1 and beyond with a profile.</summary>
    public double DistortionK => Distortion / 100 + (EnableLensProfile ? ProfileDistortion / 100 : 0);

    public CameraRawOptics Normalized()
    {
        var result = this with
        {
            ProfileDistortion = CameraRawSettings.Clamp(ProfileDistortion, 0, 100, 100), ProfileVignetting = CameraRawSettings.Clamp(ProfileVignetting, 0, 100, 100),
            Distortion = CameraRawSettings.Clamp(Distortion, -100, 100, 0), PurpleAmount = CameraRawSettings.Clamp(PurpleAmount, 0, 100, 0),
            GreenAmount = CameraRawSettings.Clamp(GreenAmount, 0, 100, 0), VignetteAmount = CameraRawSettings.Clamp(VignetteAmount, -100, 100, 0),
            VignetteMidpoint = CameraRawSettings.Clamp(VignetteMidpoint, 0, 100, 50),
            PurpleHueLow = CameraRawSettings.Clamp(PurpleHueLow, 0, 360, 270), PurpleHueHigh = CameraRawSettings.Clamp(PurpleHueHigh, 0, 360, 310),
            GreenHueLow = CameraRawSettings.Clamp(GreenHueLow, 0, 360, 60), GreenHueHigh = CameraRawSettings.Clamp(GreenHueHigh, 0, 360, 120)
        };
        if (result.PurpleHueLow > result.PurpleHueHigh) result = result with { PurpleHueLow = result.PurpleHueHigh, PurpleHueHigh = result.PurpleHueLow };
        if (result.GreenHueLow > result.GreenHueHigh) result = result with { GreenHueLow = result.GreenHueHigh, GreenHueHigh = result.GreenHueLow };
        return result;
    }
}

/// <summary>Camera calibration before the main grade. Shifts are -100…100; the process version scales how far they reach.</summary>
public sealed record CameraRawCalibration
{
    /// <summary>1…6. Earlier versions move the sliders about half as far as Version 6, the current process.</summary>
    public int Process { get; init; } = 6;
    public double ShadowTint { get; init; }
    public double RedHue { get; init; }
    public double RedSaturation { get; init; }
    public double GreenHue { get; init; }
    public double GreenSaturation { get; init; }
    public double BlueHue { get; init; }
    public double BlueSaturation { get; init; }

    [JsonIgnore] public bool Adjusts => ShadowTint != 0 || RedHue != 0 || RedSaturation != 0 || GreenHue != 0 || GreenSaturation != 0 || BlueHue != 0 || BlueSaturation != 0;

    public static string ProcessSummary(int process) => process switch
    {
        1 => "Earliest response. Hue, saturation and shadow tint move about half as far as Version 6.",
        2 => "A little stronger than Version 1. The sliders below still fall well short of the current look.",
        3 => "Firmer color than Version 2. Primary shifts stay gentler than the current process.",
        4 => "The 2012 response. Calibration reaches most of the strength used by Version 6.",
        5 => "Close to the current process, with slightly softer primary and shadow shifts.",
        _ => "Current default. The calibration sliders below apply at full strength."
    };

    public CameraRawCalibration Normalized() => new()
    {
        Process = Math.Clamp(Process, 1, 6), ShadowTint = CameraRawSettings.Clamp(ShadowTint, -100, 100, 0),
        RedHue = CameraRawSettings.Clamp(RedHue, -100, 100, 0), RedSaturation = CameraRawSettings.Clamp(RedSaturation, -100, 100, 0),
        GreenHue = CameraRawSettings.Clamp(GreenHue, -100, 100, 0), GreenSaturation = CameraRawSettings.Clamp(GreenSaturation, -100, 100, 0),
        BlueHue = CameraRawSettings.Clamp(BlueHue, -100, 100, 0), BlueSaturation = CameraRawSettings.Clamp(BlueSaturation, -100, 100, 0)
    };
}

/// <summary>Camera Raw Filter settings. The defaults leave the image unchanged.</summary>
public sealed record CameraRawSettings
{
    /// <summary>Share of a full warm/cool swing applied to red and blue, so the eyedropper inverts the same gains the pixel loop multiplies.</summary>
    public const double TemperatureGain = 0.35;
    /// <summary>Magenta/green swing shared by red and blue.</summary>
    public const double TintRedBlue = 0.15;
    /// <summary>Magenta/green swing on green, opposite the other two channels.</summary>
    public const double TintGreen = 0.30;

    public CameraRawWhiteBalance WhiteBalance { get; init; }
    /// <summary>Relative cool-to-warm, -100…100. Positive is warmer.</summary>
    public double Temperature { get; init; }
    /// <summary>Green-to-magenta, -100…100. Positive is magenta.</summary>
    public double Tint { get; init; }
    /// <summary>Stops of linear light, -5…5.</summary>
    public double Exposure { get; init; }
    public double Contrast { get; init; }
    public double Highlights { get; init; }
    public double Shadows { get; init; }
    public double Whites { get; init; }
    public double Blacks { get; init; }
    public double Vibrance { get; init; }
    public double Saturation { get; init; }
    /// <summary>Local contrast, -100…100. Texture is the finer band; Clarity the broader one.</summary>
    public double Texture { get; init; }
    public double Clarity { get; init; }
    /// <summary>-100…100. Positive deepens contrast and saturation; negative lifts shadows and fades color.</summary>
    public double Dehaze { get; init; }
    /// <summary>0…100. Range, spread and warmth do nothing while this stays at zero.</summary>
    public double Glow { get; init; }
    public CameraRawGlowStyle GlowStyle { get; init; }
    public double GlowRange { get; init; }
    public double GlowSpread { get; init; }
    public double GlowWarmth { get; init; }
    /// <summary>-100…100. Negative darkens the edges, positive lightens them; the center is left alone.</summary>
    public double VignetteAmount { get; init; }
    public CameraRawVignetteStyle VignetteStyle { get; init; }
    public double VignetteMidpoint { get; init; } = 50;
    public double VignetteRoundness { get; init; }
    public double VignetteFeather { get; init; } = 50;
    /// <summary>Used only while the vignette darkens, and only for Highlight Priority.</summary>
    public double VignetteHighlights { get; init; }
    /// <summary>0…100. Zero adds no grain; Size is mapped onto the Grain adjustment's pixel scale.</summary>
    public double GrainAmount { get; init; }
    public double GrainSize { get; init; } = 25;
    public double GrainRoughness { get; init; } = 50;
    public CameraRawCurve Curve { get; init; } = new();
    public CameraRawMixer Mixer { get; init; } = new();
    public CameraRawGrading Grading { get; init; } = new();
    public CameraRawDetail Detail { get; init; } = new();
    public CameraRawOptics Optics { get; init; } = new();
    public CameraRawCalibration Calibration { get; init; } = new();

    [JsonIgnore] public bool AdjustsLight => Exposure != 0 || Contrast != 0 || Highlights != 0 || Shadows != 0 || Whites != 0 || Blacks != 0;
    [JsonIgnore] public bool AdjustsColor => Temperature != 0 || Tint != 0 || Vibrance != 0 || Saturation != 0;
    [JsonIgnore] public bool AdjustsEffects => Texture != 0 || Clarity != 0 || Dehaze != 0 || Glow != 0 || VignetteAmount != 0 || GrainAmount != 0;

    public bool Adjusts(CameraRawGroup group) => group switch
    {
        CameraRawGroup.Light => AdjustsLight,
        CameraRawGroup.Color => AdjustsColor,
        CameraRawGroup.Effects => AdjustsEffects,
        CameraRawGroup.Curve => Curve.Adjusts,
        CameraRawGroup.Mixer => Mixer.Adjusts,
        CameraRawGroup.Grading => Grading.Adjusts,
        CameraRawGroup.Detail => Detail.Adjusts,
        CameraRawGroup.Optics => Optics.Adjusts,
        _ => Calibration.Adjusts
    };

    [JsonIgnore] public bool IsIdentity => Enum.GetValues<CameraRawGroup>().All(group => !Adjusts(group));

    /// <summary>The grade with a group's eye turned off: that group's amounts become their defaults and the rest stay.</summary>
    public CameraRawSettings Without(CameraRawGroup group) => group switch
    {
        CameraRawGroup.Light => this with { Exposure = 0, Contrast = 0, Highlights = 0, Shadows = 0, Whites = 0, Blacks = 0 },
        CameraRawGroup.Color => this with { Temperature = 0, Tint = 0, Vibrance = 0, Saturation = 0 },
        CameraRawGroup.Effects => this with { Texture = 0, Clarity = 0, Dehaze = 0, Glow = 0, VignetteAmount = 0, GrainAmount = 0 },
        CameraRawGroup.Curve => this with { Curve = new CameraRawCurve() },
        CameraRawGroup.Mixer => this with { Mixer = new CameraRawMixer() },
        CameraRawGroup.Grading => this with { Grading = new CameraRawGrading() },
        CameraRawGroup.Detail => this with { Detail = new CameraRawDetail() },
        CameraRawGroup.Optics => this with { Optics = new CameraRawOptics() },
        _ => this with { Calibration = new CameraRawCalibration() }
    };

    /// <summary>Camera Raw's 0…100 grain size in the pixel scale the Grain adjustment uses.</summary>
    [JsonIgnore] public double GrainKernelSize => 0.5 + GrainSize / 100 * 19.5;

    /// <summary>Channel multipliers for Temperature and Tint. Neutral is 1, 1, 1.</summary>
    [JsonIgnore] public (double Red, double Green, double Blue) Gains
    {
        get
        {
            double warm = Temperature / 100, magenta = Tint / 100;
            return (1 + TemperatureGain * warm + TintRedBlue * magenta, 1 - TintGreen * magenta, 1 - TemperatureGain * warm + TintRedBlue * magenta);
        }
    }

    public CameraRawSettings Normalized() => this with
    {
        Exposure = Clamp(Exposure, -5, 5, 0), Contrast = Clamp(Contrast, -100, 100, 0), Highlights = Clamp(Highlights, -100, 100, 0),
        Shadows = Clamp(Shadows, -100, 100, 0), Whites = Clamp(Whites, -100, 100, 0), Blacks = Clamp(Blacks, -100, 100, 0),
        Temperature = Clamp(Temperature, -100, 100, 0), Tint = Clamp(Tint, -100, 100, 0), Vibrance = Clamp(Vibrance, -100, 100, 0),
        Saturation = Clamp(Saturation, -100, 100, 0), Texture = Clamp(Texture, -100, 100, 0), Clarity = Clamp(Clarity, -100, 100, 0),
        Dehaze = Clamp(Dehaze, -100, 100, 0), Glow = Clamp(Glow, 0, 100, 0), GlowRange = Clamp(GlowRange, -100, 100, 0),
        GlowSpread = Clamp(GlowSpread, -100, 100, 0), GlowWarmth = Clamp(GlowWarmth, -100, 100, 0),
        VignetteAmount = Clamp(VignetteAmount, -100, 100, 0), VignetteMidpoint = Clamp(VignetteMidpoint, 0, 100, 50),
        VignetteRoundness = Clamp(VignetteRoundness, -100, 100, 0), VignetteFeather = Clamp(VignetteFeather, 0, 100, 50),
        VignetteHighlights = Clamp(VignetteHighlights, 0, 100, 0), GrainAmount = Clamp(GrainAmount, 0, 100, 0),
        GrainSize = Clamp(GrainSize, 0, 100, 25), GrainRoughness = Clamp(GrainRoughness, 0, 100, 50),
        Curve = Curve.Normalized(), Mixer = Mixer.Normalized(), Grading = Grading.Normalized(), Detail = Detail.Normalized(),
        Optics = Optics.Normalized(), Calibration = Calibration.Normalized()
    };

    internal static double Clamp(double value, double min, double max, double fallback) => double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

    /// <summary>
    /// The Temperature and Tint that bring one linear-light color to neutral, using the same gains the pixel loop
    /// multiplies. Null when a channel is missing or the cast cannot be expressed on those two axes.
    /// </summary>
    public static (double Temperature, double Tint)? Neutralize(double linearRed, double linearGreen, double linearBlue)
    {
        if (linearRed <= 1e-4 || linearGreen <= 1e-4 || linearBlue <= 1e-4) return null;
        double a1 = TemperatureGain * linearRed, b1 = TintRedBlue * linearRed + TintGreen * linearGreen, c1 = linearGreen - linearRed;
        double a2 = -TemperatureGain * linearBlue, b2 = TintRedBlue * linearBlue + TintGreen * linearGreen, c2 = linearGreen - linearBlue;
        var determinant = a1 * b2 - a2 * b1;
        if (Math.Abs(determinant) < 1e-8) return null;
        double warm = (c1 * b2 - c2 * b1) / determinant, magenta = (a1 * c2 - a2 * c1) / determinant;
        if (!double.IsFinite(warm) || !double.IsFinite(magenta)) return null;
        return (warm * 100, magenta * 100);
    }

    /// <summary>The same for a straight sRGB color, 0…1 per channel.</summary>
    public static (double Temperature, double Tint)? NeutralizeSrgb(double red, double green, double blue) =>
        Neutralize(CameraRawPixels.SrgbToLinear(red), CameraRawPixels.SrgbToLinear(green), CameraRawPixels.SrgbToLinear(blue));
}
