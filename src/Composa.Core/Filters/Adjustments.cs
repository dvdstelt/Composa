using System.Text.Json.Serialization;
using SkiaSharp;

namespace Composa.Filters;

public enum AdjustmentKind { HueSaturation, Levels, Curves, Exposure, GradientMap, Grain, Invert, BrightnessContrast, BlackAndWhite, ColorBalance }

/// <summary>
/// A color adjustment. The same settings drive a destructive Image menu command and a live adjustment layer.
/// Implementations change straight (unpremultiplied) RGB and leave alpha alone.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HueSaturationAdjustment), "hueSaturation")]
[JsonDerivedType(typeof(LevelsAdjustment), "levels")]
[JsonDerivedType(typeof(CurvesAdjustment), "curves")]
[JsonDerivedType(typeof(ExposureAdjustment), "exposure")]
[JsonDerivedType(typeof(GradientMapAdjustment), "gradientMap")]
[JsonDerivedType(typeof(GrainAdjustment), "grain")]
[JsonDerivedType(typeof(InvertAdjustment), "invert")]
[JsonDerivedType(typeof(BrightnessContrastAdjustment), "brightnessContrast")]
[JsonDerivedType(typeof(BlackAndWhiteAdjustment), "blackAndWhite")]
[JsonDerivedType(typeof(ColorBalanceAdjustment), "colorBalance")]
public abstract record Adjustment
{
    [JsonIgnore] public abstract AdjustmentKind Kind { get; }
    [JsonIgnore] public abstract string DisplayName { get; }
    [JsonIgnore] public virtual bool IsIdentity => false;

    /// <summary>Builds the per-pixel operation. <paramref name="originX"/>/<paramref name="originY"/> locate the buffer in the document.</summary>
    internal abstract PixelOp CreateOp();

    /// <summary>A variant for reduced views, where each pixel stands for <paramref name="step"/> document pixels; null when the normal one fits.</summary>
    internal virtual PixelOp? CreateOp(double step) => null;

    /// <summary>Adjusts RGBA8888 premultiplied pixels in place.</summary>
    public void Apply(SKBitmap bitmap, int originX = 0, int originY = 0) => Apply(bitmap, new SKRectI(0, 0, bitmap.Width, bitmap.Height), originX, originY);

    /// <summary>
    /// Adjusts an area of the bitmap. The bitmap's pixel (0,0) sits at document point (<paramref name="originX"/>,
    /// <paramref name="originY"/>) and neighbouring pixels are <paramref name="step"/> document pixels apart, so
    /// position-dependent adjustments (grain) keep their pattern fixed to the document at any zoom.
    /// </summary>
    public unsafe void Apply(SKBitmap bitmap, SKRectI area, double originX = 0, double originY = 0, double step = 1, bool parallel = true)
    {
        if (IsIdentity) return;
        area = Model.Geometry.Intersect(area, new SKRectI(0, 0, bitmap.Width, bitmap.Height));
        if (area.IsEmpty) return;
        // Settings are immutable, so the per-pixel operation (often a lookup table) is built once per instance.
        var op = step > 1.01 && CreateOp(step) is { } reduced ? reduced : Ops.GetValue(this, static adjustment => adjustment.CreateOp());
        var pixels = (byte*)bitmap.GetPixels();
        var stride = bitmap.RowBytes;
        // Fully optimized from the first call: tiered JIT would otherwise run this loop unoptimized for the first renders.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
        void Row(int y)
        {
            var row = pixels + (long)y * stride + area.Left * 4;
            for (var x = area.Left; x < area.Right; x++, row += 4)
            {
                int a = row[3];
                if (a == 0) continue;
                int r, g, b;
                if (a == 255) { r = row[0]; g = row[1]; b = row[2]; }
                else
                {
                    r = Math.Min(255, (row[0] * 255 + a / 2) / a);
                    g = Math.Min(255, (row[1] * 255 + a / 2) / a);
                    b = Math.Min(255, (row[2] * 255 + a / 2) / a);
                }
                op.Invoke(ref r, ref g, ref b, (int)Math.Floor(originX + x * step), (int)Math.Floor(originY + y * step));
                if (a == 255) { row[0] = (byte)r; row[1] = (byte)g; row[2] = (byte)b; }
                else
                {
                    row[0] = (byte)((r * a + 127) / 255);
                    row[1] = (byte)((g * a + 127) / 255);
                    row[2] = (byte)((b * a + 127) / 255);
                }
            }
        }
        if (parallel && (long)area.Width * area.Height > 200_000) Parallel.For(area.Top, area.Bottom, Row);
        else for (var y = area.Top; y < area.Bottom; y++) Row(y);
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Adjustment, PixelOp> Ops = new();

    /// <summary>Value equality including array contents, which record equality compares by reference.</summary>
    public bool ContentEquals(Adjustment other) =>
        System.Text.Json.JsonSerializer.Serialize(this) == System.Text.Json.JsonSerializer.Serialize(other);

    public static Adjustment Create(AdjustmentKind kind) => kind switch
    {
        AdjustmentKind.HueSaturation => new HueSaturationAdjustment(),
        AdjustmentKind.Levels => new LevelsAdjustment(),
        AdjustmentKind.Curves => new CurvesAdjustment(),
        AdjustmentKind.Exposure => new ExposureAdjustment(),
        AdjustmentKind.GradientMap => new GradientMapAdjustment(),
        AdjustmentKind.Grain => new GrainAdjustment { Seed = (uint)Random.Shared.Next() },
        AdjustmentKind.Invert => new InvertAdjustment(),
        AdjustmentKind.BlackAndWhite => new BlackAndWhiteAdjustment(),
        AdjustmentKind.ColorBalance => new ColorBalanceAdjustment(),
        _ => new BrightnessContrastAdjustment()
    };
}

internal delegate void PixelOp(ref int r, ref int g, ref int b, int docX, int docY);

internal static class Lut
{
    public static PixelOp Op(byte[] red, byte[] green, byte[] blue) =>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)] (ref int r, ref int g, ref int b, int _, int _) => { r = red[r]; g = green[g]; b = blue[b]; };

    public static byte ToByte(double unit) => (byte)Math.Clamp(Math.Round(unit * 255), 0, 255);
}

public sealed record InvertAdjustment : Adjustment
{
    public override AdjustmentKind Kind => AdjustmentKind.Invert;
    public override string DisplayName => "Invert";
    internal override PixelOp CreateOp() => (ref int r, ref int g, ref int b, int _, int _) => { r = 255 - r; g = 255 - g; b = 255 - b; };
}

public sealed record BrightnessContrastAdjustment : Adjustment
{
    /// <summary>-100…100.</summary>
    public double Brightness { get; init; }
    /// <summary>-100…100.</summary>
    public double Contrast { get; init; }
    public override AdjustmentKind Kind => AdjustmentKind.BrightnessContrast;
    public override string DisplayName => "Brightness/Contrast";
    public override bool IsIdentity => Brightness == 0 && Contrast == 0;
    internal override PixelOp CreateOp()
    {
        var table = new byte[256];
        var c = Math.Clamp(Contrast, -100, 100) / 100;
        var slope = c >= 0 ? 1 / Math.Max(0.004, 1 - c) : 1 + c;
        var lift = Math.Clamp(Brightness, -100, 100) / 100 * 0.5;
        for (var i = 0; i < 256; i++) table[i] = Lut.ToByte((i / 255.0 - 0.5) * slope + 0.5 + lift);
        return Lut.Op(table, table, table);
    }
}

/// <summary>One channel's Levels: input black and white points, midtone gamma, and output range.</summary>
public sealed record LevelsRange
{
    public double InputBlack { get; init; }
    public double InputWhite { get; init; } = 255;
    /// <summary>0.1…9.99; above 1 brightens the midtones.</summary>
    public double Gamma { get; init; } = 1;
    public double OutputBlack { get; init; }
    public double OutputWhite { get; init; } = 255;

    [JsonIgnore] public bool IsIdentity => InputBlack == 0 && InputWhite == 255 && Gamma == 1 && OutputBlack == 0 && OutputWhite == 255;

    public double Map(double value)
    {
        var span = Math.Max(1, InputWhite - InputBlack);
        var t = Math.Clamp((value - InputBlack) / span, 0, 1);
        t = Math.Pow(t, 1 / Math.Clamp(Gamma, 0.1, 9.99));
        return OutputBlack + (OutputWhite - OutputBlack) * t;
    }
}

public sealed record LevelsAdjustment : Adjustment
{
    /// <summary>Composite RGB, then red, green and blue.</summary>
    public LevelsRange[] Ranges { get; init; } = [new(), new(), new(), new()];
    public override AdjustmentKind Kind => AdjustmentKind.Levels;
    public override string DisplayName => "Levels";
    public override bool IsIdentity => Ranges.All(r => r.IsIdentity);

    public LevelsAdjustment WithRange(int channel, LevelsRange range)
    {
        var ranges = (LevelsRange[])Ranges.Clone();
        ranges[channel] = range;
        return this with { Ranges = ranges };
    }

    internal override PixelOp CreateOp()
    {
        var tables = new byte[3][];
        for (var c = 0; c < 3; c++)
        {
            tables[c] = new byte[256];
            for (var i = 0; i < 256; i++) tables[c][i] = (byte)Math.Clamp(Math.Round(Ranges[0].Map(Ranges[c + 1].Map(i))), 0, 255);
        }
        return Lut.Op(tables[0], tables[1], tables[2]);
    }

    /// <summary>Stretches each channel to its own 0.1% clipped extremes, like Photoshop's Auto.</summary>
    public static LevelsAdjustment Auto(Histogram histogram)
    {
        var result = new LevelsAdjustment();
        for (var c = 0; c < 3; c++)
        {
            var bins = histogram.Channel(c);
            long total = bins.Sum(v => (long)v);
            if (total == 0) continue;
            var clip = Math.Max(1, total / 1000);
            int low = 0, high = 255;
            for (long sum = 0; low < 255; low++) { sum += bins[low]; if (sum > clip) break; }
            for (long sum = 0; high > 0; high--) { sum += bins[high]; if (sum > clip) break; }
            if (high - low < 2) continue;
            result = result.WithRange(c + 1, new LevelsRange { InputBlack = low, InputWhite = high });
        }
        return result;
    }
}

public readonly record struct CurvePoint(double X, double Y);

public sealed record CurvesAdjustment : Adjustment
{
    /// <summary>Composite RGB, then red, green and blue; each sorted by X from 0 to 255.</summary>
    public CurvePoint[][] Channels { get; init; } = [Line(), Line(), Line(), Line()];
    public override AdjustmentKind Kind => AdjustmentKind.Curves;
    public override string DisplayName => "Curves";
    public override bool IsIdentity => Channels.All(c => c.All(p => p.X == p.Y));

    private static CurvePoint[] Line() => [new(0, 0), new(255, 255)];

    public CurvesAdjustment WithChannel(int channel, IEnumerable<CurvePoint> points)
    {
        var channels = (CurvePoint[][])Channels.Clone();
        channels[channel] = Normalize(points);
        return this with { Channels = channels };
    }

    private static CurvePoint[] Normalize(IEnumerable<CurvePoint> points)
    {
        var sorted = points.Select(p => new CurvePoint(Math.Clamp(p.X, 0, 255), Math.Clamp(p.Y, 0, 255))).OrderBy(p => p.X).ToList();
        for (var i = sorted.Count - 1; i > 0; i--) if (sorted[i].X - sorted[i - 1].X < 1) sorted.RemoveAt(i);
        if (sorted.Count < 2) return Line();
        return sorted.ToArray();
    }

    /// <summary>Shape-preserving cubic Hermite interpolation, so the curve never overshoots its handles.</summary>
    public double Value(double x, int channel)
    {
        var p = Channels[channel];
        if (x <= p[0].X) return p[0].Y;
        if (x >= p[^1].X) return p[^1].Y;
        var i = 0;
        while (i < p.Length - 2 && p[i + 1].X <= x) i++;
        double Secant(int j) => (p[j + 1].Y - p[j].Y) / (p[j + 1].X - p[j].X);
        double Slope(int j)
        {
            if (j == 0) return Secant(0);
            if (j == p.Length - 1) return Secant(p.Length - 2);
            double a = Secant(j - 1), b = Secant(j);
            return a * b <= 0 ? 0 : 2 / (1 / a + 1 / b);
        }
        var h = p[i + 1].X - p[i].X;
        var t = Math.Clamp((x - p[i].X) / h, 0, 1);
        double t2 = t * t, t3 = t2 * t;
        var y = (2 * t3 - 3 * t2 + 1) * p[i].Y + (t3 - 2 * t2 + t) * h * Slope(i)
              + (-2 * t3 + 3 * t2) * p[i + 1].Y + (t3 - t2) * h * Slope(i + 1);
        return Math.Clamp(y, 0, 255);
    }

    internal override PixelOp CreateOp()
    {
        var tables = new byte[3][];
        for (var c = 0; c < 3; c++)
        {
            tables[c] = new byte[256];
            for (var i = 0; i < 256; i++) tables[c][i] = (byte)Math.Round(Value(Value(i, c + 1), 0));
        }
        return Lut.Op(tables[0], tables[1], tables[2]);
    }
}

/// <summary>Exposure in stops scales linear light, offset shifts it, then gamma bends the result.</summary>
public sealed record ExposureAdjustment : Adjustment
{
    public double Exposure { get; init; }
    public double Offset { get; init; }
    public double Gamma { get; init; } = 1;
    public override AdjustmentKind Kind => AdjustmentKind.Exposure;
    public override string DisplayName => "Exposure";
    public override bool IsIdentity => Exposure == 0 && Offset == 0 && Gamma == 1;

    internal override PixelOp CreateOp()
    {
        var scale = Math.Pow(2, Math.Clamp(Exposure, -20, 20));
        var gamma = Math.Clamp(Gamma, 0.01, 9.99);
        var table = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            var encoded = i / 255.0;
            var linear = encoded <= 0.04045 ? encoded / 12.92 : Math.Pow((encoded + 0.055) / 1.055, 2.4);
            linear = Math.Pow(Math.Max(0, linear * scale + Offset), 1 / gamma);
            table[i] = Lut.ToByte(linear <= 0.0031308 ? linear * 12.92 : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055);
        }
        return Lut.Op(table, table, table);
    }
}

/// <summary>Each pixel's brightness picks a color between the shadow and highlight colors.</summary>
public sealed record GradientMapAdjustment : Adjustment
{
    public uint Shadows { get; init; } = 0xFF000000;
    public uint Highlights { get; init; } = 0xFFFFFFFF;
    public bool Reversed { get; init; }
    public override AdjustmentKind Kind => AdjustmentKind.GradientMap;
    public override string DisplayName => "Gradient Map";

    internal override PixelOp CreateOp()
    {
        var dark = new SKColor(Reversed ? Highlights : Shadows);
        var light = new SKColor(Reversed ? Shadows : Highlights);
        byte[] red = new byte[256], green = new byte[256], blue = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            var t = i / 255.0;
            red[i] = (byte)Math.Round(dark.Red + (light.Red - dark.Red) * t);
            green[i] = (byte)Math.Round(dark.Green + (light.Green - dark.Green) * t);
            blue[i] = (byte)Math.Round(dark.Blue + (light.Blue - dark.Blue) * t);
        }
        return [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)] (ref int r, ref int g, ref int b, int _, int _) =>
        {
            var luma = (r * 54 + g * 183 + b * 19) >> 8;
            r = red[luma]; g = green[luma]; b = blue[luma];
        };
    }
}

/// <summary>Film grain: brightness noise, strongest in the midtones, fixed in document space by its seed.</summary>
public sealed record GrainAdjustment : Adjustment
{
    public double Amount { get; init; } = 25;
    public double Size { get; init; } = 1.5;
    public double Roughness { get; init; } = 50;
    public uint Seed { get; init; }
    public override AdjustmentKind Kind => AdjustmentKind.Grain;
    public override string DisplayName => "Grain";
    public override bool IsIdentity => Amount <= 0;

    internal static float Hash(int x, int y, uint seed)
    {
        unchecked
        {
            var h = (uint)x * 0x85EBCA6Bu ^ (uint)y * 0xC2B2AE35u ^ seed * 0x27D4EB2Fu;
            h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12; h *= 0x297A2D39u; h ^= h >> 15;
            return (h & 0xFFFFFF) / (float)0xFFFFFF * 2 - 1;
        }
    }

    private static float Smooth(float x, float y, uint seed)
    {
        int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
        float fx = x - x0, fy = y - y0;
        fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy);
        var top = Hash(x0, y0, seed) + (Hash(x0 + 1, y0, seed) - Hash(x0, y0, seed)) * fx;
        var bottom = Hash(x0, y0 + 1, seed) + (Hash(x0 + 1, y0 + 1, seed) - Hash(x0, y0 + 1, seed)) * fx;
        return top + (bottom - top) * fy;
    }

    // Zoomed out, each screen pixel averages many grains, which evens the noise out; one sample at full strength
    // would make the picture look far grainier than it exports.
    internal override PixelOp? CreateOp(double step) => (this with { Amount = Amount * Math.Min(1, Math.Max(Size, 1) / step) }).CreateOp();

    internal override PixelOp CreateOp()
    {
        var amount = (float)(Math.Clamp(Amount, 0, 100) / 100 * 96);
        var size = (float)Math.Clamp(Size, 0.5, 20);
        var rough = (float)(Math.Clamp(Roughness, 0, 100) / 100);
        var seed = Seed;
        return [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)] (ref int r, ref int g, ref int b, int x, int y) =>
        {
            var noise = Smooth(x / size, y / size, seed) * (1 - rough) * 1.6f + Hash(x, y, seed ^ 0x9E3779B9u) * rough;
            var luma = (r * 54 + g * 183 + b * 19) / 65280f;
            var weight = 4 * luma * (1 - luma) * 0.75f + 0.25f;
            var delta = (int)MathF.Round(noise * amount * weight);
            r = Math.Clamp(r + delta, 0, 255); g = Math.Clamp(g + delta, 0, 255); b = Math.Clamp(b + delta, 0, 255);
        };
    }
}

/// <summary>
/// Black &amp; White as Photoshop's is: not a desaturation, but a choice of how bright each family of colors becomes in
/// gray. Reds at 40% and yellows at 60% is why a default conversion keeps skin and foliage apart where a plain
/// luminance flattens them. A color is min(r,g,b) of gray, plus the secondary between its two brightest channels,
/// plus the primary of its brightest, so the six weights apply exactly as Photoshop's do.
/// </summary>
public sealed record BlackAndWhiteAdjustment : Adjustment
{
    public const double MinWeight = -200, MaxWeight = 300;
    /// <summary>Photoshop's defaults, in percent.</summary>
    public double Reds { get; init; } = 40;
    public double Yellows { get; init; } = 60;
    public double Greens { get; init; } = 40;
    public double Cyans { get; init; } = 60;
    public double Blues { get; init; } = 20;
    public double Magentas { get; init; } = 80;
    /// <summary>Colors the result while keeping its tones, for a sepia or a cyanotype.</summary>
    public bool Tint { get; init; }
    /// <summary>0…360.</summary>
    public double TintHue { get; init; } = 40;
    /// <summary>0…100.</summary>
    public double TintSaturation { get; init; } = 20;
    public override AdjustmentKind Kind => AdjustmentKind.BlackAndWhite;
    public override string DisplayName => "Black & White";

    /// <summary>The weights in the order red, yellow, green, cyan, blue, magenta.</summary>
    [JsonIgnore] public double[] Weights => [Reds, Yellows, Greens, Cyans, Blues, Magentas];

    public BlackAndWhiteAdjustment WithWeight(int index, double weight) => index switch
    {
        0 => this with { Reds = weight }, 1 => this with { Yellows = weight }, 2 => this with { Greens = weight },
        3 => this with { Cyans = weight }, 4 => this with { Blues = weight }, _ => this with { Magentas = weight }
    };

    /// <summary>The gray a straight 0…1 color becomes under these weights, before any tint.</summary>
    public static float Gray(float r, float g, float b, ReadOnlySpan<float> weights)
    {
        float max = MathF.Max(r, MathF.Max(g, b)), min = MathF.Min(r, MathF.Min(g, b));
        var mid = r + g + b - max - min;
        int primary, secondary;
        if (max == r) { primary = 0; secondary = g >= b ? 1 : 5; }
        else if (max == g) { primary = 2; secondary = r >= b ? 1 : 3; }
        else { primary = 4; secondary = g >= r ? 3 : 5; }
        return Math.Clamp(min + (mid - min) * weights[secondary] + (max - mid) * weights[primary], 0, 1);
    }

    internal override PixelOp CreateOp()
    {
        var weights = Weights.Select(w => (float)(Math.Clamp(w, MinWeight, MaxWeight) / 100)).ToArray();
        var tint = Tint && TintSaturation > 0;
        var hue = ((TintHue % 360) + 360) % 360;
        var saturation = Math.Clamp(TintSaturation, 0, 100) / 100;
        return [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)] (ref int r, ref int g, ref int b, int _, int _) =>
        {
            var gray = Gray(r / 255f, g / 255f, b / 255f, weights);
            if (!tint) { r = g = b = Lut.ToByte(gray); return; }
            // The gray becomes the lightness of a color at the chosen hue.
            ColorMath.HslToRgb(hue, saturation, gray, out var tr, out var tg, out var tb);
            r = Lut.ToByte(tr); g = Lut.ToByte(tg); b = Lut.ToByte(tb);
        };
    }
}

/// <summary>
/// Color Balance: shifts color towards one end of each opposing pair, separately for shadows, midtones and
/// highlights. Preserve Luminosity puts each pixel's brightness back afterwards, so a warm cast doesn't also lighten
/// the picture.
/// </summary>
public sealed record ColorBalanceAdjustment : Adjustment
{
    public const double MinShift = -100, MaxShift = 100;
    /// <summary>Cyan/red, magenta/green and yellow/blue for the shadows, -100…100.</summary>
    public double[] Shadows { get; init; } = new double[3];
    public double[] Midtones { get; init; } = new double[3];
    public double[] Highlights { get; init; } = new double[3];
    public bool PreserveLuminosity { get; init; } = true;
    public override AdjustmentKind Kind => AdjustmentKind.ColorBalance;
    public override string DisplayName => "Color Balance";
    public override bool IsIdentity => Shadows.All(v => v == 0) && Midtones.All(v => v == 0) && Highlights.All(v => v == 0);

    /// <summary>Range 0 shadows, 1 midtones, 2 highlights; channel 0 cyan/red, 1 magenta/green, 2 yellow/blue.</summary>
    public ColorBalanceAdjustment WithShift(int range, int channel, double value)
    {
        var values = (double[])(range == 0 ? Shadows : range == 1 ? Midtones : Highlights).Clone();
        values[channel] = value;
        return range switch { 0 => this with { Shadows = values }, 1 => this with { Midtones = values }, _ => this with { Highlights = values } };
    }

    public double Shift(int range, int channel) => (range == 0 ? Shadows : range == 1 ? Midtones : Highlights)[channel];

    /// <summary>
    /// How much a tone belongs to the shadows, midtones and highlights: three overlapping ramps that sum to about one
    /// across the range, so a shift fades in and out rather than banding at a threshold.
    /// </summary>
    internal static void TonalWeights(float v, out float shadow, out float mid, out float highlight)
    {
        const float a = 0.25f, b = 0.333f, scale = 0.7f;
        shadow = Math.Clamp((v - b) / -a + 0.5f, 0, 1) * scale;
        highlight = Math.Clamp((v + b - 1) / a + 0.5f, 0, 1) * scale;
        mid = Math.Clamp((v - b) / a + 0.5f, 0, 1) * Math.Clamp((v + b - 1) / -a + 0.5f, 0, 1) * scale;
    }

    internal override PixelOp CreateOp()
    {
        float[] Unit(double[] values) => values.Select(v => (float)(Math.Clamp(v, MinShift, MaxShift) / 100)).ToArray();
        float[] shadows = Unit(Shadows), midtones = Unit(Midtones), highlights = Unit(Highlights);
        var preserve = PreserveLuminosity;
        return [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)] (ref int r, ref int g, ref int b, int _, int _) =>
        {
            Span<float> c = [r / 255f, g / 255f, b / 255f];
            var before = 0.299f * c[0] + 0.587f * c[1] + 0.114f * c[2];
            for (var i = 0; i < 3; i++)
            {
                TonalWeights(c[i], out var s, out var m, out var h);
                c[i] = Math.Clamp(c[i] + shadows[i] * s + midtones[i] * m + highlights[i] * h, 0, 1);
            }
            if (preserve)
            {
                var after = 0.299f * c[0] + 0.587f * c[1] + 0.114f * c[2];
                if (after > 0.0001f)
                {
                    var ratio = before / after;
                    for (var i = 0; i < 3; i++) c[i] = Math.Clamp(c[i] * ratio, 0, 1);
                }
            }
            r = Lut.ToByte(c[0]); g = Lut.ToByte(c[1]); b = Lut.ToByte(c[2]);
        };
    }
}

public enum HueRange { Master, Reds, Yellows, Greens, Cyans, Blues, Magentas }

public readonly record struct HslShift(double Hue, double Saturation, double Lightness)
{
    [JsonIgnore] public bool IsZero => Hue == 0 && Saturation == 0 && Lightness == 0;
}

public sealed record HueSaturationAdjustment : Adjustment
{
    /// <summary>Indexed by <see cref="HueRange"/>: hue -180…180, saturation and lightness -100…100.</summary>
    public HslShift[] Shifts { get; init; } = new HslShift[7];
    public bool Colorize { get; init; }
    public override AdjustmentKind Kind => AdjustmentKind.HueSaturation;
    public override string DisplayName => "Hue/Saturation";
    public override bool IsIdentity => !Colorize && Shifts.All(s => s.IsZero);

    public HueSaturationAdjustment WithShift(HueRange range, HslShift shift)
    {
        var shifts = (HslShift[])Shifts.Clone();
        shifts[(int)range] = shift;
        return this with { Shifts = shifts };
    }

    /// <summary>How strongly a hue (degrees) belongs to a color range: full within 15° of its center, fading out by 45°.</summary>
    internal static double RangeWeight(HueRange range, double hue)
    {
        var center = ((int)range - 1) * 60.0;
        var distance = Math.Abs(((hue - center) % 360 + 540) % 360 - 180);
        return distance <= 15 ? 1 : distance >= 45 ? 0 : (45 - distance) / 30;
    }

    internal override PixelOp CreateOp()
    {
        var shifts = Shifts;
        var colorize = Colorize;
        var anyRange = shifts.Skip(1).Any(s => !s.IsZero);
        return [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)] (ref int r, ref int g, ref int b, int _, int _) =>
        {
            ColorMath.RgbToHsl(r / 255.0, g / 255.0, b / 255.0, out var h, out var s, out var l);
            var master = shifts[0];
            if (colorize)
            {
                h = ((master.Hue % 360) + 360) % 360;
                s = master.Saturation >= 0 ? 0.25 + master.Saturation / 100 * 0.75 : 0.25 * (1 + master.Saturation / 100);
            }
            else
            {
                if (anyRange && s > 0)
                {
                    for (var i = 1; i < 7; i++)
                    {
                        if (shifts[i].IsZero) continue;
                        var weight = RangeWeight((HueRange)i, h) * Math.Min(1, s * 4);
                        if (weight <= 0) continue;
                        h += shifts[i].Hue * weight;
                        s = ApplySaturation(s, shifts[i].Saturation * weight);
                        l = ApplyLightness(l, shifts[i].Lightness * weight);
                    }
                }
                h = ((h + master.Hue) % 360 + 360) % 360;
                s = ApplySaturation(s, master.Saturation);
            }
            l = ApplyLightness(l, master.Lightness);
            ColorMath.HslToRgb(h, s, l, out var nr, out var ng, out var nb);
            r = Lut.ToByte(nr); g = Lut.ToByte(ng); b = Lut.ToByte(nb);
        };
    }

    private static double ApplySaturation(double s, double amount) =>
        Math.Clamp(amount >= 0 ? s * (1 + amount / 100 * 2) : s * (1 + amount / 100), 0, 1);

    private static double ApplyLightness(double l, double amount) =>
        amount >= 0 ? l + (1 - l) * amount / 100 : l * (1 + amount / 100);
}

public static class ColorMath
{
    public static void RgbToHsl(double r, double g, double b, out double h, out double s, out double l)
    {
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        l = (max + min) / 2;
        var d = max - min;
        if (d < 1e-9) { h = 0; s = 0; return; }
        s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
        else if (max == g) h = (b - r) / d + 2;
        else h = (r - g) / d + 4;
        h *= 60;
    }

    public static void HslToRgb(double h, double s, double l, out double r, out double g, out double b)
    {
        if (s <= 0) { r = g = b = l; return; }
        var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;
        r = Channel(p, q, h / 360 + 1.0 / 3);
        g = Channel(p, q, h / 360);
        b = Channel(p, q, h / 360 - 1.0 / 3);
    }

    private static double Channel(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6) return p + (q - p) * 6 * t;
        if (t < 0.5) return q;
        if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
        return p;
    }

    public static void RgbToHsv(double r, double g, double b, out double h, out double s, out double v)
    {
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        v = max;
        var d = max - min;
        s = max <= 0 ? 0 : d / max;
        if (d < 1e-9) { h = 0; return; }
        if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
        else if (max == g) h = (b - r) / d + 2;
        else h = (r - g) / d + 4;
        h *= 60;
    }

    public static void HsvToRgb(double h, double s, double v, out double r, out double g, out double b)
    {
        h = ((h % 360) + 360) % 360 / 60;
        var i = (int)Math.Floor(h);
        var f = h - i;
        double p = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f));
        (r, g, b) = i switch { 0 => (v, t, p), 1 => (q, v, p), 2 => (p, v, t), 3 => (p, q, v), 4 => (t, p, v), _ => (v, p, q) };
    }
}

public sealed class Histogram
{
    private readonly int[][] bins = [new int[256], new int[256], new int[256], new int[256]];
    /// <summary>0–2 are red, green and blue; 3 is luminance.</summary>
    public int[] Channel(int index) => bins[index];

    public static unsafe Histogram Of(SKBitmap bitmap, SKBitmap? selection = null, int originX = 0, int originY = 0)
    {
        var result = new Histogram();
        var pixels = (byte*)bitmap.GetPixels();
        var step = Math.Max(1, (int)Math.Sqrt((double)bitmap.Width * bitmap.Height / 1_000_000));
        for (var y = 0; y < bitmap.Height; y += step)
        {
            var row = pixels + (long)y * bitmap.RowBytes;
            for (var x = 0; x < bitmap.Width; x += step)
            {
                var p = row + x * 4;
                int a = p[3];
                if (a < 8) continue;
                int r = Math.Min(255, p[0] * 255 / a), g = Math.Min(255, p[1] * 255 / a), b = Math.Min(255, p[2] * 255 / a);
                result.bins[0][r]++; result.bins[1][g]++; result.bins[2][b]++;
                result.bins[3][(r * 54 + g * 183 + b * 19) >> 8]++;
            }
        }
        return result;
    }
}
