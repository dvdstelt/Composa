using Compositor.Filters;

namespace Compositor.IO.Psd;

/// <summary>
/// Maps Photoshop adjustment layers onto this editor's adjustments where one exists with the same meaning. Read from
/// the adjustment layer blocks in Adobe's Photoshop File Formats Specification.
/// </summary>
internal static class PsdAdjustments
{
    /// <summary>Every key that marks an adjustment layer, supported here or not.</summary>
    public static readonly HashSet<string> Keys =
    [
        "levl", "curv", "hue2", "hue ", "expA", "grdm", "brit", "blnc", "nvrt", "thrs", "post", "mixr", "selc", "blwh", "phfl", "vibA", "clrL"
    ];

    public static bool IsAdjustment(Dictionary<string, byte[]> extra) => extra.Keys.Any(Keys.Contains);

    /// <summary>The matching adjustment, or null when Photoshop's has no equivalent here. <paramref name="approximate"/> says the values were mapped rather than copied.</summary>
    public static Adjustment? Parse(Dictionary<string, byte[]> extra, out bool approximate)
    {
        approximate = true;
        try
        {
            if (extra.TryGetValue("levl", out var levels)) return Levels(levels);
            if (extra.TryGetValue("curv", out var curves)) return Curves(curves);
            if (extra.TryGetValue("hue2", out var hue) || extra.TryGetValue("hue ", out hue)) return HueSaturation(hue);
            if (extra.TryGetValue("brit", out var brightness)) return BrightnessContrast(brightness);
            if (extra.TryGetValue("expA", out var exposure)) return Exposure(exposure);
            if (extra.ContainsKey("nvrt")) { approximate = false; return new InvertAdjustment(); }
            if (extra.TryGetValue("blnc", out var balance)) { approximate = false; return ColorBalance(balance); }
            if (extra.TryGetValue("blwh", out var blackWhite)) return BlackAndWhite(blackWhite, out approximate);
        }
        catch (PsdException) { }
        return null;
    }

    /// <summary>Version, then 29 records of input floor, input ceiling, output floor, output ceiling and gamma × 100; the first four are RGB, red, green and blue.</summary>
    private static Adjustment? Levels(byte[] data)
    {
        if (data.Length < 2 + 4 * 10) return null;
        var cursor = new PsdCursor(data);
        _ = cursor.U16();
        var ranges = new LevelsRange[4];
        for (var channel = 0; channel < 4; channel++)
        {
            double inputBlack = cursor.U16(), inputWhite = cursor.U16(), outputBlack = cursor.U16(), outputWhite = cursor.U16(), gamma = cursor.U16() / 100.0;
            ranges[channel] = new LevelsRange
            {
                InputBlack = Math.Clamp(inputBlack, 0, 255), InputWhite = Math.Clamp(inputWhite, 0, 255),
                OutputBlack = Math.Clamp(outputBlack, 0, 255), OutputWhite = Math.Clamp(outputWhite, 0, 255),
                Gamma = Math.Clamp(gamma <= 0 ? 1 : gamma, 0.1, 9.99)
            };
        }
        return new LevelsAdjustment { Ranges = ranges };
    }

    /// <summary>A padding byte, the version, a bitmask of the channels present, then each channel's points as output, input pairs.</summary>
    private static Adjustment? Curves(byte[] data)
    {
        if (data.Length < 7) return null;
        var cursor = new PsdCursor(data);
        _ = cursor.U8();
        var version = cursor.U16();
        if (version is not (1 or 4)) return null;
        var present = cursor.U32();
        var result = new CurvesAdjustment();
        for (var channel = 0; channel < 32; channel++)
        {
            if ((present & (1u << channel)) == 0) continue;
            var count = cursor.U16();
            if (count > 19) return null;
            var points = new List<CurvePoint>();
            for (var i = 0; i < count; i++)
            {
                double output = cursor.U16(), input = cursor.U16();
                points.Add(new CurvePoint(Math.Clamp(input, 0, 255), Math.Clamp(output, 0, 255)));
            }
            if (channel > 3 || points.Count < 2) continue;
            points.Sort((a, b) => a.X.CompareTo(b.X));
            if (points[0].X != 0) points.Insert(0, new CurvePoint(0, points[0].Y));
            if (points[^1].X != 255) points.Add(new CurvePoint(255, points[^1].Y));
            result = result.WithChannel(channel, points);
        }
        return result;
    }

    /// <summary>Version, colorize flag, the colorize hue, saturation and lightness, the master triple, then six ranges each with four range values and a triple.</summary>
    private static Adjustment? HueSaturation(byte[] data)
    {
        if (data.Length < 4 + 6 + 6) return null;
        var cursor = new PsdCursor(data);
        var version = cursor.U16();
        var colorize = cursor.U8() != 0;
        _ = cursor.U8();
        HslShift Triple(ref PsdCursor c) => new(Math.Clamp((int)c.I16(), -180, 180), Math.Clamp((int)c.I16(), -100, 100), Math.Clamp((int)c.I16(), -100, 100));
        var colorized = Triple(ref cursor);
        var master = Triple(ref cursor);
        var shifts = new HslShift[7];
        shifts[(int)HueRange.Master] = colorize ? colorized : master;
        if (version >= 2)
        {
            for (var range = 1; range < 7 && cursor.Remaining >= 14; range++)
            {
                cursor.Skip(8); // the range's four hue boundaries; the bands here have fixed widths
                shifts[range] = Triple(ref cursor);
            }
        }
        return new HueSaturationAdjustment { Shifts = shifts, Colorize = colorize };
    }

    /// <summary>Brightness and contrast as signed shorts, on Photoshop's -150…150 and -50…100 legacy scales, mapped to -100…100.</summary>
    private static Adjustment? BrightnessContrast(byte[] data)
    {
        if (data.Length < 4) return null;
        var cursor = new PsdCursor(data);
        double brightness = cursor.I16(), contrast = cursor.I16();
        return new BrightnessContrastAdjustment { Brightness = Math.Clamp(brightness / 1.5, -100, 100), Contrast = Math.Clamp(contrast, -100, 100) };
    }

    /// <summary>Nine signed shorts: cyan/red, magenta/green and yellow/blue for the shadows, midtones and highlights, then a preserve luminosity byte.</summary>
    private static Adjustment? ColorBalance(byte[] data)
    {
        if (data.Length < 19) return null;
        var cursor = new PsdCursor(data);
        double[] Triple(ref PsdCursor c) => [Math.Clamp((int)c.I16(), -100, 100), Math.Clamp((int)c.I16(), -100, 100), Math.Clamp((int)c.I16(), -100, 100)];
        var shadows = Triple(ref cursor);
        var midtones = Triple(ref cursor);
        var highlights = Triple(ref cursor);
        return new ColorBalanceAdjustment { Shadows = shadows, Midtones = midtones, Highlights = highlights, PreserveLuminosity = cursor.U8() != 0 };
    }

    /// <summary>
    /// A versioned descriptor: the six weights as longs under Photoshop's color keys, <c>useTint</c> and the tint as an
    /// RGB color. The tint color becomes a hue and saturation here, so it is marked approximate.
    /// </summary>
    private static Adjustment? BlackAndWhite(byte[] data, out bool approximate)
    {
        approximate = false;
        var items = PsdDescriptor.ReadVersioned(data);
        if (items == null) return null;
        double Weight(string key, double fallback) => Math.Clamp(PsdDescriptor.Number(items, key) ?? fallback, BlackAndWhiteAdjustment.MinWeight, BlackAndWhiteAdjustment.MaxWeight);
        var result = new BlackAndWhiteAdjustment
        {
            Reds = Weight("Rd  ", 40), Yellows = Weight("Yllw", 60), Greens = Weight("Grn ", 40),
            Cyans = Weight("Cyn ", 60), Blues = Weight("Bl  ", 20), Magentas = Weight("Mgnt", 80),
            Tint = PsdDescriptor.Flag(items, "useTint") ?? false
        };
        if (result.Tint && PsdDescriptor.Color(PsdDescriptor.Child(items, "tintColor")) is { } tint)
        {
            approximate = true;
            var color = new SkiaSharp.SKColor(tint);
            ColorMath.RgbToHsl(color.Red / 255.0, color.Green / 255.0, color.Blue / 255.0, out var hue, out var saturation, out _);
            result = result with { TintHue = hue, TintSaturation = Math.Round(saturation * 100) };
        }
        return result;
    }

    /// <summary>Version, then exposure, offset and gamma as 32-bit floats.</summary>
    private static Adjustment? Exposure(byte[] data)
    {
        if (data.Length < 14) return null;
        var cursor = new PsdCursor(data);
        _ = cursor.U16();
        double exposure = cursor.F32(), offset = cursor.F32(), gamma = cursor.F32();
        if (!double.IsFinite(exposure) || !double.IsFinite(offset) || !double.IsFinite(gamma)) return null;
        return new ExposureAdjustment { Exposure = Math.Clamp(exposure, -20, 20), Offset = Math.Clamp(offset, -0.5, 0.5), Gamma = Math.Clamp(gamma <= 0 ? 1 : gamma, 0.01, 9.99) };
    }
}
