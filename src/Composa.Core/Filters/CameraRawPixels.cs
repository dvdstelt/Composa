using Composa.Rendering;
using SkiaSharp;

namespace Composa.Filters;

/// <summary>
/// The Camera Raw Filter's pixel pipeline on premultiplied RGBA, in this order: calibration, white balance and the
/// Light and Color sliders, the curve, the color mixer and grading, the Effects (texture, clarity, dehaze, glow,
/// vignette, grain), optics, then detail (noise reduction and sharpening). Every stage reads straight color and writes
/// premultiplied bytes back, as the macOS app's does. <c>scale</c> is picture pixels per layer pixel, so radii given
/// in layer pixels land the same on a reduced preview.
/// </summary>
public static unsafe class CameraRawPixels
{
    /// <summary>A new bitmap with the grade applied; the source is left as it is.</summary>
    public static SKBitmap Apply(SKBitmap source, CameraRawSettings settings, double scale = 1, uint seed = 0)
    {
        var result = Pixels.Clone(source);
        var s = settings.Normalized();
        if (s.IsIdentity) return result;
        int width = result.Width, height = result.Height, stride = result.RowBytes;
        var pixels = (byte*)result.GetPixels();
        if (scale <= 0) scale = 1;
        if (s.Calibration.Adjusts) Calibration(pixels, width, height, stride, s.Calibration);
        if (s.AdjustsLight || s.AdjustsColor)
        {
            var gains = s.Gains;
            Basic(pixels, width, height, stride, gains.Red, gains.Green, gains.Blue, s.Exposure, s.Contrast, s.Highlights, s.Shadows, s.Whites, s.Blacks, s.Vibrance, s.Saturation);
        }
        if (s.Curve.Adjusts || s.Mixer.Adjusts || s.Grading.Adjusts) CurveColor(pixels, width, height, stride, s.Curve, s.Mixer, s.Grading);
        if (s.Texture != 0 || s.Clarity != 0 || s.Dehaze != 0 || s.Glow > 0 || s.VignetteAmount != 0)
            Effects(pixels, width, height, stride, s.Texture, s.Clarity, s.Dehaze, s.Glow, s.GlowStyle, s.GlowRange, s.GlowSpread, s.GlowWarmth,
                s.VignetteAmount, s.VignetteMidpoint, s.VignetteRoundness, s.VignetteFeather, s.VignetteHighlights, s.VignetteStyle, scale);
        if (s.GrainAmount > 0)
        {
            // The Grain adjustment's own field, at Camera Raw's size scale, fixed to the layer by the seed.
            var grain = new GrainAdjustment { Amount = s.GrainAmount, Size = s.GrainKernelSize, Roughness = s.GrainRoughness, Seed = seed };
            grain.Apply(result, new SKRectI(0, 0, width, height), 0, 0, 1 / scale);
        }
        if (s.Optics.Adjusts) Optics(result, s.Optics, scale);
        if (s.Detail.Adjusts) Detail(pixels, width, height, stride, s.Detail, scale);
        Pixels.Invalidate(result);
        return result;
    }

    /// <summary>Gray-world balance of the opaque pixels: the Temperature and Tint that make their average neutral. Null when there is nothing to read.</summary>
    public static (double Temperature, double Tint)? AutoBalance(SKBitmap image)
    {
        var pixels = (byte*)image.GetPixels();
        double red = 0, green = 0, blue = 0, count = 0;
        var step = Math.Max(1, (int)Math.Sqrt((double)image.Width * image.Height / 250_000));
        for (var y = 0; y < image.Height; y += step)
        {
            var row = pixels + (long)y * image.RowBytes;
            for (var x = 0; x < image.Width; x += step)
            {
                var p = row + x * 4;
                double alpha = p[3];
                if (alpha == 0) continue;
                red += SrgbToLinear(Math.Min(1, p[0] / alpha)); green += SrgbToLinear(Math.Min(1, p[1] / alpha)); blue += SrgbToLinear(Math.Min(1, p[2] / alpha));
                count++;
            }
        }
        return count == 0 ? null : CameraRawSettings.Neutralize(red / count, green / count, blue / count);
    }

    /// <summary>The straight color of one pixel, 0…1 per channel, or null where the layer is clear.</summary>
    public static (double Red, double Green, double Blue)? StraightColor(SKBitmap image, int x, int y)
    {
        if (x < 0 || y < 0 || x >= image.Width || y >= image.Height) return null;
        var p = (byte*)image.GetPixels() + (long)y * image.RowBytes + x * 4;
        if (p[3] == 0) return null;
        double alpha = p[3];
        return (Math.Min(1, p[0] / alpha), Math.Min(1, p[1] / alpha), Math.Min(1, p[2] / alpha));
    }

    // ---- Shared math --------------------------------------------------------------------------------------------

    private static double Clamp01(double value) => value < 0 ? 0 : value > 1 ? 1 : value;

    public static double SrgbToLinear(double encoded) => encoded <= 0.04045 ? encoded / 12.92 : Math.Pow((encoded + 0.055) / 1.055, 2.4);

    public static double LinearToSrgb(double linear) => linear <= 0 ? 0 : linear >= 1 ? 1 : linear <= 0.0031308 ? linear * 12.92 : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055;

    private static double Rec709(double r, double g, double b) => 0.2126 * r + 0.7152 * g + 0.0722 * b;

    /// <summary>Moves a color so its Rec. 709 luminance becomes <paramref name="target"/>, keeping the hue. Pure black cannot be scaled, so a lift paints neutral light.</summary>
    private static void ScaleLuminance(ref double r, ref double g, ref double b, double target)
    {
        target = Clamp01(target);
        var y = Rec709(r, g, b);
        if (Math.Abs(target - y) < 1e-8) return;
        if (y < 1e-8) { if (target > y) r = g = b = target; return; }
        var scale = target / y;
        r = Clamp01(r * scale); g = Clamp01(g * scale); b = Clamp01(b * scale);
    }

    private static void Read(byte* p, out double r, out double g, out double b)
    {
        double alpha = p[3];
        r = Math.Min(1, p[0] / alpha); g = Math.Min(1, p[1] / alpha); b = Math.Min(1, p[2] / alpha);
    }

    private static void Write(byte* p, double r, double g, double b)
    {
        double alpha = p[3];
        p[0] = (byte)Math.Min(alpha, Math.Max(0, Math.Round(r * alpha)));
        p[1] = (byte)Math.Min(alpha, Math.Max(0, Math.Round(g * alpha)));
        p[2] = (byte)Math.Min(alpha, Math.Max(0, Math.Round(b * alpha)));
    }

    /// <summary>Hue in turns (0…1), saturation and lightness as HSL has them.</summary>
    private static void RgbToHsl(double r, double g, double b, out double h, out double s, out double l)
    {
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        l = (max + min) * 0.5;
        var d = max - min;
        if (d < 1e-6) { h = 0; s = 0; return; }
        s = d / (1 - Math.Abs(2 * l - 1));
        if (max == r) h = ((g - b) / d) % 6;
        else if (max == g) h = (b - r) / d + 2;
        else h = (r - g) / d + 4;
        h /= 6;
        if (h < 0) h += 1;
    }

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6) return p + (q - p) * 6 * t;
        if (t < 0.5) return q;
        if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
        return p;
    }

    private static void HslToRgb(double h, double s, double l, out double r, out double g, out double b)
    {
        if (s <= 1e-6) { r = g = b = l; return; }
        var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;
        r = HueToRgb(p, q, h + 1.0 / 3); g = HueToRgb(p, q, h); b = HueToRgb(p, q, h - 1.0 / 3);
    }

    private static double HueDegrees(double r, double g, double b)
    {
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), chroma = max - min;
        if (chroma < 1e-6) return 0;
        var hue = max == r ? ((g - b) / chroma) % 6 : max == g ? (b - r) / chroma + 2 : (r - g) / chroma + 4;
        hue *= 60;
        return hue < 0 ? hue + 360 : hue;
    }

    private static void ForRows(int height, Action<int> row) => Parallel.For(0, height, row);

    private static int ClampedIndex(int index, int limit) => index < 0 ? 0 : index >= limit ? limit - 1 : index;

    /// <summary>Edge-clamped box blur of a float plane into another. Rows and columns run in parallel.</summary>
    private static void BoxBlur(float[] source, float[] destination, int width, int height, int radius)
    {
        if (radius < 1) { Array.Copy(source, destination, source.Length); return; }
        var window = radius * 2 + 1;
        var temp = new float[source.Length];
        ForRows(height, y =>
        {
            double sum = 0;
            for (var k = -radius; k <= radius; k++) sum += source[y * width + ClampedIndex(k, width)];
            for (var x = 0; x < width; x++)
            {
                temp[y * width + x] = (float)(sum / window);
                sum += source[y * width + ClampedIndex(x + radius + 1, width)];
                sum -= source[y * width + ClampedIndex(x - radius, width)];
            }
        });
        Parallel.For(0, width, x =>
        {
            double sum = 0;
            for (var k = -radius; k <= radius; k++) sum += temp[ClampedIndex(k, height) * width + x];
            for (var y = 0; y < height; y++)
            {
                destination[y * width + x] = (float)(sum / window);
                sum += temp[ClampedIndex(y + radius + 1, height) * width + x];
                sum -= temp[ClampedIndex(y - radius, height) * width + x];
            }
        });
    }

    private static int EffectsRadius(double radius, double scale) => (int)Math.Round(Math.Clamp(radius * (scale > 0 ? scale : 1), 1, 64));

    private static float[] LumaPlane(byte* pixels, int width, int height, int stride)
    {
        var luma = new float[width * height];
        ForRows(height, y =>
        {
            var row = pixels + (long)y * stride;
            for (var x = 0; x < width; x++)
            {
                var p = row + x * 4;
                if (p[3] == 0) { luma[y * width + x] = 0; continue; }
                Read(p, out var r, out var g, out var b);
                luma[y * width + x] = (float)Rec709(r, g, b);
            }
        });
        return luma;
    }

    // ---- Light and Color ----------------------------------------------------------------------------------------

    private static double ToneHighlights(double y, double amount)
    {
        var t = Clamp01((y - 0.5) / 0.5);
        var weight = t * t;
        return amount >= 0 ? Clamp01(y + amount * weight * (1 - y)) : Clamp01(y + amount * weight * (y - 0.5));
    }

    private static double ToneShadows(double y, double amount)
    {
        var t = Clamp01((0.5 - y) / 0.5);
        var weight = t * t;
        return amount >= 0 ? Clamp01(y + amount * weight * (0.5 - y)) : Clamp01(y + amount * weight * y);
    }

    /// <summary>The top quarter is the white point: +1 maps 0.875 to 1, -1 pulls everything above 0.75 down to 0.75.</summary>
    private static double ToneWhites(double y, double amount) => y <= 0.75 ? y : Clamp01(0.75 + (y - 0.75) * (1 + amount));

    /// <summary>The bottom quarter is the black point: negative amounts crush toward 0, positive ones lift toward 0.25.</summary>
    private static double ToneBlacks(double y, double amount) => y >= 0.25 ? y : Clamp01(0.25 + (y - 0.25) * (1 - amount));

    private static void VibranceAndSaturation(ref double r, ref double g, ref double b, double vibrance, double saturation)
    {
        var lum = Rec709(r, g, b);
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), chroma = max - min;
        var sat = max <= 1e-8 ? 0 : chroma / max;
        var hue = HueDegrees(r, g, b);
        double skin = 0;
        if (hue is >= 10 and <= 50)
        {
            skin = hue <= 30 ? (hue - 10) / 20 : (50 - hue) / 20;
            skin *= Clamp01((sat - 0.15) / 0.35);
        }
        var amount = vibrance * (1 - sat);
        if (vibrance > 0) amount *= 1 - 0.7 * skin;
        var factor = 1 + amount;
        r = Clamp01(lum + (r - lum) * factor); g = Clamp01(lum + (g - lum) * factor); b = Clamp01(lum + (b - lum) * factor);
        lum = Rec709(r, g, b);
        factor = 1 + saturation;
        r = Clamp01(lum + (r - lum) * factor); g = Clamp01(lum + (g - lum) * factor); b = Clamp01(lum + (b - lum) * factor);
    }

    /// <summary>White balance, exposure in stops of linear light, contrast about mid gray, highlights, shadows, whites, blacks, vibrance, saturation.</summary>
    internal static void Basic(byte* pixels, int width, int height, int stride, double redGain, double greenGain, double blueGain, double exposure, double contrast,
        double highlights, double shadows, double whites, double blacks, double vibrance, double saturation)
    {
        var light = Math.Pow(2, exposure);
        var contrastScale = 1 + contrast / 100;
        double highlightAmount = highlights / 100, shadowAmount = shadows / 100, whiteAmount = whites / 100, blackAmount = blacks / 100;
        double vibranceAmount = vibrance / 100, saturationAmount = saturation / 100;
        ForRows(height, y =>
        {
            var row = pixels + (long)y * stride;
            for (var x = 0; x < width; x++)
            {
                var p = row + x * 4;
                if (p[3] == 0) continue;
                Read(p, out var r, out var g, out var b);
                r = Clamp01(SrgbToLinear(r) * redGain * light); g = Clamp01(SrgbToLinear(g) * greenGain * light); b = Clamp01(SrgbToLinear(b) * blueGain * light);
                r = Clamp01(0.5 + (LinearToSrgb(r) - 0.5) * contrastScale);
                g = Clamp01(0.5 + (LinearToSrgb(g) - 0.5) * contrastScale);
                b = Clamp01(0.5 + (LinearToSrgb(b) - 0.5) * contrastScale);
                ScaleLuminance(ref r, ref g, ref b, ToneHighlights(Rec709(r, g, b), highlightAmount));
                ScaleLuminance(ref r, ref g, ref b, ToneShadows(Rec709(r, g, b), shadowAmount));
                ScaleLuminance(ref r, ref g, ref b, ToneWhites(Rec709(r, g, b), whiteAmount));
                ScaleLuminance(ref r, ref g, ref b, ToneBlacks(Rec709(r, g, b), blackAmount));
                VibranceAndSaturation(ref r, ref g, ref b, vibranceAmount, saturationAmount);
                Write(p, r, g, b);
            }
        });
    }

    // ---- Curve, Color Mixer and Color Grading -------------------------------------------------------------------

    private static double LutAt(float[] lut, double value)
    {
        var scaled = Clamp01(value) * 255;
        var lo = (int)scaled;
        var hi = lo < 255 ? lo + 1 : 255;
        return lut[lo] + (lut[hi] - lut[lo]) * (scaled - lo);
    }

    private static double CircularDistance(double a, double b)
    {
        var d = Math.Abs(a - b);
        return d > 0.5 ? 1 - d : d;
    }

    private static readonly double[] MixerCenters = CameraRawMixer.Centers.Select(c => c / 360).ToArray();

    internal static void CurveColor(byte* pixels, int width, int height, int stride, CameraRawCurve curve, CameraRawMixer mixer, CameraRawGrading grading)
    {
        var luma = curve.LumaTable();
        float[] redLut = curve.ChannelTable(curve.Red), greenLut = curve.ChannelTable(curve.Green), blueLut = curve.ChannelTable(curve.Blue);
        var refine = curve.RefineSaturation / 100;
        var mix = mixer.Floats();
        var grade = grading.Floats();
        double blending = grading.Blending / 100, balance = grading.Balance / 100;
        ForRows(height, y =>
        {
            var row = pixels + (long)y * stride;
            for (var x = 0; x < width; x++)
            {
                var p = row + x * 4;
                if (p[3] == 0) continue;
                Read(p, out var r, out var g, out var b);
                var tone = Rec709(r, g, b);
                var mapped = LutAt(luma, tone);
                ScaleLuminance(ref r, ref g, ref b, mapped);
                if (refine != 0 && tone > 1e-4)
                {
                    var factor = 1 + refine * (mapped / tone - 1);
                    var lum = Rec709(r, g, b);
                    r = Clamp01(lum + (r - lum) * factor); g = Clamp01(lum + (g - lum) * factor); b = Clamp01(lum + (b - lum) * factor);
                }
                r = LutAt(redLut, r); g = LutAt(greenLut, g); b = LutAt(blueLut, b);
                RgbToHsl(r, g, b, out var h, out var s, out var l);
                double hueDelta = 0, satDelta = 0, lumDelta = 0, weightSum = 0;
                for (var i = 0; i < 8; i++)
                {
                    var w = 1 - CircularDistance(h, MixerCenters[i]) / (40.0 / 360);
                    if (w <= 0) continue;
                    hueDelta += mix[i] * w * (30.0 / 360);
                    satDelta += mix[8 + i] * w;
                    lumDelta += mix[16 + i] * w * 0.25;
                    weightSum += w;
                }
                if (weightSum > 1) { hueDelta /= weightSum; satDelta /= weightSum; lumDelta /= weightSum; }
                h += hueDelta;
                if (h < 0) h += 1;
                if (h >= 1) h -= 1;
                s = Clamp01(s * (1 + satDelta));
                l = Clamp01(l + lumDelta);
                HslToRgb(h, s, l, out r, out g, out b);
                // Balance moves the crossover between the shadow and highlight wheels: toward highlights it moves down,
                // so more of the picture counts as highlight and the shadow wheel loses its hold.
                var split = 0.5 - balance * 0.2;
                var reach = 0.12 + blending * 0.38;
                var lumNow = Rec709(r, g, b);
                var shadowW = Clamp01((split + reach - lumNow) / Math.Max(0.05, reach * 2));
                var highlightW = Clamp01((lumNow - (split - reach)) / Math.Max(0.05, reach * 2));
                var midW = Clamp01(1 - Math.Abs(lumNow - split) / (0.35 + reach));
                var sum = shadowW + midW + highlightW;
                if (sum > 1e-4) { shadowW /= sum; midW /= sum; highlightW /= sum; }
                Span<double> weights = [shadowW, midW, highlightW, 1];
                for (var wheel = 0; wheel < 4; wheel++)
                {
                    double wh = grade[wheel * 3], ws = grade[wheel * 3 + 1], wl = grade[wheel * 3 + 2], w = weights[wheel];
                    if (w <= 0 || (ws <= 0 && wl == 0)) continue;
                    HslToRgb(wh, 1, 0.5, out var cr, out var cg, out var cb);
                    r = Clamp01(r + (cr - 0.5) * ws * w * 0.85); g = Clamp01(g + (cg - 0.5) * ws * w * 0.85); b = Clamp01(b + (cb - 0.5) * ws * w * 0.85);
                    if (wl != 0) ScaleLuminance(ref r, ref g, ref b, Clamp01(Rec709(r, g, b) + wl * 0.25 * w));
                }
                Write(p, r, g, b);
            }
        });
    }

    // ---- Effects ------------------------------------------------------------------------------------------------

    private static void Dehaze(ref double r, ref double g, ref double b, double amount)
    {
        var d = amount / 100;
        var y = Rec709(r, g, b);
        var contrast = 1 + 0.8 * d;
        var pivot = 0.45 - 0.1 * Math.Max(0, d);
        var y2 = Clamp01(pivot + (y - 0.45) * contrast);
        y2 = d < 0 ? Clamp01(y2 + -d * (1 - y2) * 0.45) : Clamp01(y2 - d * Math.Max(0, 0.4 - y2));
        ScaleLuminance(ref r, ref g, ref b, y2);
        y2 = Rec709(r, g, b);
        var sat = 1 + 0.7 * d;
        r = Clamp01(y2 + (r - y2) * sat); g = Clamp01(y2 + (g - y2) * sat); b = Clamp01(y2 + (b - y2) * sat);
    }

    private static void EffectsVignette(ref double r, ref double g, ref double b, int x, int y, int width, int height, double amount, double midpoint,
        double roundness, double feather, double highlights, CameraRawVignetteStyle style)
    {
        if (amount == 0) return;
        var mask = ImageFilters.VignetteMaskAt(x + 0.5, y + 0.5, width, height, midpoint, roundness, feather);
        var effect = amount / 100 * mask;
        // Highlight Priority eases a darkening vignette off bright pixels; the other styles do not.
        if (effect < 0 && style == CameraRawVignetteStyle.HighlightPriority)
            effect *= 1 - highlights / 100 * Clamp01((Rec709(r, g, b) - 0.45) / 0.55);
        if (effect < 0) { var factor = 1 + effect; r *= factor; g *= factor; b *= factor; }
        else if (effect > 0) { r += (1 - r) * effect; g += (1 - g) * effect; b += (1 - b) * effect; }
        if (style == CameraRawVignetteStyle.ColorPriority && mask > 0)
        {
            var lum = Rec709(r, g, b);
            var sat = 1 - 0.75 * mask * Math.Abs(amount / 100);
            r = Clamp01(lum + (r - lum) * sat); g = Clamp01(lum + (g - lum) * sat); b = Clamp01(lum + (b - lum) * sat);
        }
    }

    internal static void Effects(byte* pixels, int width, int height, int stride, double texture, double clarity, double dehaze, double glow, CameraRawGlowStyle glowStyle,
        double glowRange, double glowSpread, double glowWarmth, double vignetteAmount, double vignetteMidpoint, double vignetteRoundness, double vignetteFeather,
        double vignetteHighlights, CameraRawVignetteStyle vignetteStyle, double scale)
    {
        float[]? luma = null, fine = null, coarse = null, glowPlane = null;
        if (texture != 0 || clarity != 0 || glow > 0)
        {
            luma = LumaPlane(pixels, width, height, stride);
            if (texture != 0) { fine = new float[luma.Length]; BoxBlur(luma, fine, width, height, EffectsRadius(1, scale)); }
            if (clarity != 0) { coarse = new float[luma.Length]; BoxBlur(luma, coarse, width, height, EffectsRadius(4, scale)); }
            if (glow > 0)
            {
                var spread = glowSpread / 100;
                var widened = Math.Max(1, (glowStyle == CameraRawGlowStyle.Bloom ? 2.0 : 5.0) * (1 + spread));
                var threshold = (float)(0.55 + 0.4 * (glowRange / 100));
                var denominator = Math.Max(0.05f, 1 - threshold);
                var source = new float[luma.Length];
                for (var i = 0; i < luma.Length; i++) source[i] = Math.Clamp((luma[i] - threshold) / denominator, 0, 1);
                glowPlane = new float[luma.Length];
                BoxBlur(source, glowPlane, width, height, EffectsRadius(widened, scale));
            }
        }
        var warmth = glowWarmth / 100;
        double glowRed, glowGreen, glowBlue, glowGain;
        if (glowStyle == CameraRawGlowStyle.Halation)
        {
            // Halation's fringe is red. Warmth pushes it further that way, rather than toward yellow or blue.
            glowRed = 1; glowGreen = 0.35 - 0.3 * warmth; glowBlue = 0.2 - 0.2 * warmth; glowGain = 1;
        }
        else
        {
            glowRed = 0.75 + 0.25 * warmth; glowGreen = 0.6 + 0.2 * warmth; glowBlue = 0.75 - 0.6 * warmth;
            glowGain = glowStyle == CameraRawGlowStyle.Bloom ? 1.4 : 1;
        }
        ForRows(height, y =>
        {
            var row = pixels + (long)y * stride;
            for (var x = 0; x < width; x++)
            {
                var p = row + x * 4;
                if (p[3] == 0) continue;
                var index = y * width + x;
                Read(p, out var r, out var g, out var b);
                if (fine != null || coarse != null)
                {
                    var tone = Rec709(r, g, b);
                    double detail = 0;
                    if (fine != null) detail += texture / 100 * (tone - fine[index]);
                    if (coarse != null) detail += clarity / 100 * (tone - coarse[index]);
                    if (detail != 0) ScaleLuminance(ref r, ref g, ref b, Clamp01(tone + detail));
                }
                if (dehaze != 0) Dehaze(ref r, ref g, ref b, dehaze);
                if (glowPlane != null)
                {
                    var add = glowPlane[index] * (glow / 100) * glowGain;
                    r = Clamp01(r + add * glowRed); g = Clamp01(g + add * glowGreen); b = Clamp01(b + add * glowBlue);
                }
                EffectsVignette(ref r, ref g, ref b, x, y, width, height, vignetteAmount, vignetteMidpoint, vignetteRoundness, vignetteFeather, vignetteHighlights, vignetteStyle);
                Write(p, r, g, b);
            }
        });
    }

    // ---- Optics -------------------------------------------------------------------------------------------------

    private static bool HueInRange(double hue, double low, double high) => low <= high ? hue >= low && hue <= high : hue >= low || hue <= high;

    private static void Defringe(ref double r, ref double g, ref double b, CameraRawOptics optics)
    {
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), chroma = max - min;
        if (chroma < 1e-6) return;
        var hue = HueDegrees(r, g, b);
        var sat = chroma / max;
        double reduce = 0;
        if (optics.PurpleAmount > 0 && HueInRange(hue, optics.PurpleHueLow, optics.PurpleHueHigh)) reduce = Math.Max(reduce, optics.PurpleAmount / 100);
        if (optics.GreenAmount > 0 && HueInRange(hue, optics.GreenHueLow, optics.GreenHueHigh)) reduce = Math.Max(reduce, optics.GreenAmount / 100);
        if (reduce <= 0) return;
        var lum = Rec709(r, g, b);
        var factor = 1 - reduce * sat;
        r = Clamp01(lum + (r - lum) * factor); g = Clamp01(lum + (g - lum) * factor); b = Clamp01(lum + (b - lum) * factor);
    }

    /// <summary>Slides the red and blue channels apart radially, opposite to how a lens fringes them.</summary>
    private static void Chromatic(SKBitmap bitmap, double strength)
    {
        using var copy = Pixels.Clone(bitmap);
        int width = bitmap.Width, height = bitmap.Height, stride = bitmap.RowBytes;
        byte* pixels = (byte*)bitmap.GetPixels(), source = (byte*)copy.GetPixels();
        double cx = width * 0.5, cy = height * 0.5, maxR = Math.Sqrt(cx * cx + cy * cy);
        ForRows(height, y =>
        {
            var row = pixels + (long)y * stride;
            var from = source + (long)y * copy.RowBytes;
            for (var x = 0; x < width; x++)
            {
                var p = row + x * 4;
                if (p[3] == 0) continue;
                double dx = x + 0.5 - cx, dy = y + 0.5 - cy, radial = Math.Sqrt(dx * dx + dy * dy) / maxR;
                var shift = strength * radial * radial * 2.5;
                byte* pr = from + ClampedIndex((int)Math.Round(x - shift), width) * 4, pb = from + ClampedIndex((int)Math.Round(x + shift), width) * 4;
                double alpha = p[3];
                var g = Math.Min(1, from[x * 4 + 1] / alpha);
                var r = Math.Min(1, pr[0] / Math.Max(1.0, pr[3]));
                var b = Math.Min(1, pb[2] / Math.Max(1.0, pb[3]));
                Write(p, r, g, b);
            }
        });
    }

    private static void VignetteCorrect(ref double r, ref double g, ref double b, int x, int y, int width, int height, double amount, double midpoint)
    {
        if (amount == 0) return;
        double nx = (x + 0.5) / width * 2 - 1, ny = (y + 0.5) / height * 2 - 1;
        var distance = Math.Sqrt(nx * nx + ny * ny) / Math.Sqrt(2);
        var t = Clamp01((distance - midpoint / 100 * 0.85) / 0.35);
        var lift = amount / 100 * (t * t * (3 - 2 * t));
        if (lift > 0) { r = Clamp01(r + (1 - r) * lift); g = Clamp01(g + (1 - g) * lift); b = Clamp01(b + (1 - b) * lift); }
        else { var factor = 1 + lift; r *= factor; g *= factor; b *= factor; }
    }

    internal static void Optics(SKBitmap bitmap, CameraRawOptics optics, double scale)
    {
        if (optics.DistortionK != 0)
        {
            using var warped = ImageFilters.Distort(bitmap, optics.DistortionK);
            Pixels.CopyPixels(warped, bitmap);
        }
        if (optics.RemoveChromaticAberration) Chromatic(bitmap, 0.45);
        var vignette = optics.VignetteAmount + (optics.EnableLensProfile ? optics.ProfileVignetting / 100 * 35 : 0);
        if (optics.PurpleAmount == 0 && optics.GreenAmount == 0 && vignette == 0) return;
        int width = bitmap.Width, height = bitmap.Height, stride = bitmap.RowBytes;
        var pixels = (byte*)bitmap.GetPixels();
        ForRows(height, y =>
        {
            var row = pixels + (long)y * stride;
            for (var x = 0; x < width; x++)
            {
                var p = row + x * 4;
                if (p[3] == 0) continue;
                Read(p, out var r, out var g, out var b);
                Defringe(ref r, ref g, ref b, optics);
                VignetteCorrect(ref r, ref g, ref b, x, y, width, height, vignette, optics.VignetteMidpoint);
                Write(p, r, g, b);
            }
        });
    }

    // ---- Detail -------------------------------------------------------------------------------------------------

    private static double DetailRadius(double slider, double scale) => Math.Clamp((0.5 + slider / 100 * 2.5) * (scale > 0 ? scale : 1), 0.5, 64);

    /// <summary>How much a pixel's brightness differs from its eight neighbours <paramref name="radius"/> away.</summary>
    private static float EdgeAt(float[] luma, int width, int height, int x, int y, int radius)
    {
        if (radius < 1) radius = 1;
        var center = luma[y * width + x];
        float sum = 0;
        var count = 0;
        for (var dy = -radius; dy <= radius; dy += radius)
        for (var dx = -radius; dx <= radius; dx += radius)
        {
            if (dx == 0 && dy == 0) continue;
            int sx = x + dx, sy = y + dy;
            if (sx < 0 || sy < 0 || sx >= width || sy >= height) continue;
            sum += Math.Abs(luma[sy * width + sx] - center);
            count++;
        }
        return count == 0 ? 0 : sum / count;
    }

    internal static void Detail(byte* pixels, int width, int height, int stride, CameraRawDetail detail, double scale)
    {
        var luma = LumaPlane(pixels, width, height, stride);
        var work = new float[luma.Length];
        if (detail.NoiseLuminance > 0)
        {
            BoxBlur(luma, work, width, height, EffectsRadius(1 + detail.NoiseLuminance / 50, scale));
            double strength = detail.NoiseLuminance / 100, preserve = detail.NoiseLuminanceDetail / 100, contrast = detail.NoiseLuminanceContrast / 100;
            var smoothed = new float[luma.Length];
            ForRows(height, y =>
            {
                var row = pixels + (long)y * stride;
                for (var x = 0; x < width; x++)
                {
                    var p = row + x * 4;
                    if (p[3] == 0) continue;
                    var index = y * width + x;
                    var edge = EdgeAt(luma, width, height, x, y, 1);
                    var local = strength * (1 - preserve * Math.Min(1, edge * 6));
                    var target = (float)(luma[index] * (1 - local) + work[index] * local);
                    if (contrast != 0) target = (float)(target + contrast * 0.25 * (luma[index] - work[index]));
                    smoothed[index] = target;
                    Read(p, out var r, out var g, out var b);
                    ScaleLuminance(ref r, ref g, ref b, target);
                    Write(p, r, g, b);
                }
            });
            luma = smoothed;
        }
        if (detail.NoiseColor > 0)
        {
            var chroma = new float[luma.Length];
            ForRows(height, y =>
            {
                var row = pixels + (long)y * stride;
                for (var x = 0; x < width; x++)
                {
                    var p = row + x * 4;
                    if (p[3] == 0) continue;
                    Read(p, out var r, out var g, out var b);
                    RgbToHsl(r, g, b, out _, out var s, out _);
                    chroma[y * width + x] = (float)s;
                }
            });
            var chromaBlur = new float[luma.Length];
            BoxBlur(chroma, chromaBlur, width, height, EffectsRadius(1 + detail.NoiseColorSmoothness / 40, scale));
            double strength = detail.NoiseColor / 100, preserve = detail.NoiseColorDetail / 100;
            ForRows(height, y =>
            {
                var row = pixels + (long)y * stride;
                for (var x = 0; x < width; x++)
                {
                    var p = row + x * 4;
                    if (p[3] == 0) continue;
                    var index = y * width + x;
                    var edge = Math.Abs(chroma[index] - chromaBlur[index]);
                    var local = strength * (1 - preserve * Math.Min(1, edge * 4));
                    var sat = chroma[index] * (1 - local) + chromaBlur[index] * local;
                    Read(p, out var r, out var g, out var b);
                    RgbToHsl(r, g, b, out var h, out _, out var l);
                    HslToRgb(h, sat, l, out r, out g, out b);
                    Write(p, r, g, b);
                }
            });
        }
        if (detail.SharpenAmount > 0)
        {
            luma = LumaPlane(pixels, width, height, stride);
            var radius = EffectsRadius(DetailRadius(detail.SharpenRadius, scale), 1);
            BoxBlur(luma, work, width, height, radius);
            double amount = detail.SharpenAmount / 100, detailMix = detail.SharpenDetail / 100, threshold = detail.SharpenMasking / 100 * 0.35;
            ForRows(height, y =>
            {
                var row = pixels + (long)y * stride;
                for (var x = 0; x < width; x++)
                {
                    var p = row + x * 4;
                    if (p[3] == 0) continue;
                    var index = y * width + x;
                    var edge = EdgeAt(luma, width, height, x, y, radius);
                    var mask = Clamp01((edge * (0.5 + detailMix) - threshold) / Math.Max(0.04, 0.35 - threshold * 0.5));
                    var sharpened = Clamp01(luma[index] + (luma[index] - work[index]) * amount * mask * (0.5 + detailMix));
                    Read(p, out var r, out var g, out var b);
                    ScaleLuminance(ref r, ref g, ref b, sharpened);
                    Write(p, r, g, b);
                }
            });
        }
    }

    // ---- Calibration --------------------------------------------------------------------------------------------

    internal static void Calibration(byte* pixels, int width, int height, int stride, CameraRawCalibration calibration)
    {
        var versionScale = calibration.Process switch { <= 1 => 0.55, 2 => 0.65, 3 => 0.75, 4 => 0.85, 5 => 0.92, _ => 1.0 };
        var tint = calibration.ShadowTint / 100 * versionScale;
        double rh = calibration.RedHue / 100 * (15.0 / 360) * versionScale, rs = calibration.RedSaturation / 100 * 0.45 * versionScale;
        double gh = calibration.GreenHue / 100 * (15.0 / 360) * versionScale, gs = calibration.GreenSaturation / 100 * 0.45 * versionScale;
        double bh = calibration.BlueHue / 100 * (15.0 / 360) * versionScale, bs = calibration.BlueSaturation / 100 * 0.45 * versionScale;
        ForRows(height, y =>
        {
            var row = pixels + (long)y * stride;
            for (var x = 0; x < width; x++)
            {
                var p = row + x * 4;
                if (p[3] == 0) continue;
                Read(p, out var r, out var g, out var b);
                RgbToHsl(r, g, b, out var h, out var s, out var l);
                if (l < 0.35 && tint != 0)
                {
                    h += tint * 0.06;
                    if (h < 0) h += 1;
                    if (h >= 1) h -= 1;
                }
                double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                if (max - min > 1e-5)
                {
                    if (r >= g && r >= b) { h += rh; s = Clamp01(s * (1 + rs)); }
                    else if (g >= r && g >= b) { h += gh; s = Clamp01(s * (1 + gs)); }
                    else { h += bh; s = Clamp01(s * (1 + bs)); }
                    if (h < 0) h += 1;
                    if (h >= 1) h -= 1;
                }
                HslToRgb(h, s, l, out r, out g, out b);
                Write(p, r, g, b);
            }
        });
    }
}
