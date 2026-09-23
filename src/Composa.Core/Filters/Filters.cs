using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Filters;

public enum FilterKind { GaussianBlur, MotionBlur, AddNoise, Sharpen, Vignette, BloomGlow, TonalContrast, LensCorrection, CameraRaw, RemoveBackground }

/// <summary>Settings for the destructive Filter menu commands. Unused values are ignored by each kind.</summary>
public sealed record FilterSettings
{
    public FilterKind Kind { get; init; }
    /// <summary>Blur radius, motion distance, or sharpen radius, in pixels.</summary>
    public double Radius { get; init; } = 8;
    /// <summary>Motion blur direction in degrees.</summary>
    public double Angle { get; init; }
    /// <summary>Noise amount, sharpen strength, or background tolerance: 0…100.</summary>
    public double Amount { get; init; } = 20;
    public bool Monochrome { get; init; } = true;
    /// <summary>Add Noise: a bell-shaped spread instead of an even one.</summary>
    public bool Gaussian { get; init; }
    /// <summary>Lens distortion, -100 (pincushion) … 100 (barrel correction).</summary>
    public double Distortion { get; init; }
    /// <summary>Vignette: how far the edges take on the color, 0…100.</summary>
    public double VignetteAmount { get; init; } = 35;
    public uint VignetteColor { get; init; } = 0xFF000000;
    /// <summary>Vignette: where the falloff starts, 0…100 from the middle outward.</summary>
    public double VignetteMidpoint { get; init; } = 50;
    /// <summary>Vignette: -100 follows the frame's corners, 100 is a circle.</summary>
    public double VignetteRoundness { get; init; } = 100;
    /// <summary>Vignette: how soft the falloff is, 0…100.</summary>
    public double VignetteFeather { get; init; } = 60;
    /// <summary>Vignette: how much bright pixels near the edge are spared, 0…100.</summary>
    public double VignetteHighlights { get; init; } = 25;
    /// <summary>
    /// Vignette on an empty layer: the frame it shapes itself to, in the layer's pixels, and whether it paints
    /// transparent pixels too. Set by the session: the canvas for a layer without pixels of its own, otherwise the
    /// layer's own bounds, which it only recolors.
    /// </summary>
    public SKRect? VignetteFrame { get; init; }
    public bool VignetteFillsClear { get; init; }
    /// <summary>Bloom / Glow: strength, 0…100, and blur radius in layer pixels.</summary>
    public double BloomAmount { get; init; } = 40;
    public double BloomRadius { get; init; } = 24;
    /// <summary>Tonal Contrast: overall strength, the detail radius in layer pixels, and how much each range gets, -100…100.</summary>
    public double TonalAmount { get; init; } = 50;
    public double TonalRadius { get; init; } = 16;
    public double TonalShadows { get; init; } = 40;
    public double TonalMidtones { get; init; } = 60;
    public double TonalHighlights { get; init; } = 30;
    /// <summary>The Camera Raw Filter's whole grade, and the layer's pixels per document pixel so its radii match the picture.</summary>
    public CameraRawSettings CameraRaw { get; init; } = new();
    public double CameraRawScale { get; init; } = 1;
    public uint Seed { get; init; } = 1;
    /// <summary>Set for layers that fill the canvas: a blur then continues their edge colors instead of fading into transparency.</summary>
    public bool ClampEdges { get; init; }

    /// <summary>True when the filter would change nothing, so OK can close as Cancel does, without an undo step.</summary>
    public bool IsIdentity => Kind switch
    {
        FilterKind.Vignette => VignetteAmount <= 0,
        FilterKind.BloomGlow => BloomAmount <= 0,
        FilterKind.TonalContrast => TonalAmount <= 0 || (TonalShadows == 0 && TonalMidtones == 0 && TonalHighlights == 0),
        FilterKind.LensCorrection => Distortion == 0,
        FilterKind.CameraRaw => CameraRaw.IsIdentity,
        FilterKind.Sharpen or FilterKind.AddNoise => Amount <= 0,
        _ => false
    };

    /// <summary>The filters that push pixels past a layer's edges, so the layer grows to hold them.</summary>
    public bool Spreads => Kind is FilterKind.GaussianBlur or FilterKind.MotionBlur or FilterKind.BloomGlow;

    public static string DisplayName(FilterKind kind) => kind switch
    {
        FilterKind.GaussianBlur => "Gaussian Blur",
        FilterKind.MotionBlur => "Motion Blur",
        FilterKind.AddNoise => "Add Noise",
        FilterKind.BloomGlow => "Bloom / Glow",
        FilterKind.TonalContrast => "Tonal Contrast",
        FilterKind.LensCorrection => "Lens Correction",
        FilterKind.CameraRaw => "Camera Raw Filter",
        FilterKind.RemoveBackground => "Remove Background",
        _ => kind.ToString()
    };
}

public static unsafe class ImageFilters
{
    /// <summary>
    /// Runs a filter over RGBA premultiplied pixels and returns the new bitmap with how far it grew on the left and
    /// top: blurs spread past the layer's edges, so their result is larger than the source.
    /// </summary>
    public static (SKBitmap Result, int GrowX, int GrowY) Run(SKBitmap source, FilterSettings settings)
    {
        switch (settings.Kind)
        {
            case FilterKind.GaussianBlur when settings.ClampEdges && HasOpaqueBorder(source):
            {
                // A picture that is solid right up to its edges (a photo, a filled background) blurs as if its edge
                // colors continued outwards. Spreading it into transparency would leave a see-through frame.
                var sigma = (float)Math.Max(0.1, settings.Radius);
                using var filter = SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp);
                var result = Pixels.NewColor(source.Width, source.Height);
                using var canvas = new SKCanvas(result);
                using var paint = new SKPaint { ImageFilter = filter, BlendMode = SKBlendMode.Src };
                canvas.DrawBitmap(source, 0, 0, paint);
                return (result, 0, 0);
            }
            case FilterKind.GaussianBlur:
            {
                var sigma = (float)Math.Max(0.1, settings.Radius);
                var pad = (int)Math.Ceiling(sigma * 3);
                using var filter = SKImageFilter.CreateBlur(sigma, sigma);
                return (Draw(source, pad, pad, filter), pad, pad);
            }
            case FilterKind.MotionBlur:
                return MotionBlur(source, settings.Radius, settings.Angle, settings.ClampEdges && HasOpaqueBorder(source));
            case FilterKind.Sharpen:
                return (Sharpen(source, settings.Radius, settings.Amount / 100 * 2), 0, 0);
            case FilterKind.AddNoise:
                return (AddNoise(source, settings.Amount, settings.Gaussian, settings.Monochrome, settings.Seed), 0, 0);
            case FilterKind.LensCorrection:
                return (LensCorrection(source, settings.Distortion / 100), 0, 0);
            case FilterKind.Vignette:
                return (Vignette(source, settings), 0, 0);
            case FilterKind.BloomGlow:
                return Bloom(source, settings.BloomAmount / 50, settings.BloomRadius, settings.ClampEdges && HasOpaqueBorder(source));
            case FilterKind.TonalContrast:
                return (TonalContrast(source, settings), 0, 0);
            case FilterKind.CameraRaw:
                return (CameraRawPixels.Apply(source, settings.CameraRaw, settings.CameraRawScale, settings.Seed), 0, 0);
            case FilterKind.RemoveBackground:
                return (RemoveBackground(source, settings.Amount), 0, 0);
            default:
                return (Pixels.Clone(source), 0, 0);
        }
    }

    private static SKBitmap Draw(SKBitmap source, int padX, int padY, SKImageFilter filter)
    {
        var result = Pixels.NewColor(source.Width + padX * 2, source.Height + padY * 2);
        using var canvas = new SKCanvas(result);
        using var paint = new SKPaint { ImageFilter = filter };
        canvas.DrawBitmap(source, padX, padY, paint);
        return result;
    }

    /// <summary>True when every pixel along the four edges is fully opaque.</summary>
    private static bool HasOpaqueBorder(SKBitmap source)
    {
        var pixels = (byte*)source.GetPixels();
        int w = source.Width, h = source.Height, stride = source.RowBytes;
        for (var x = 0; x < w; x++)
            if (pixels[x * 4 + 3] != 255 || pixels[(long)(h - 1) * stride + x * 4 + 3] != 255) return false;
        for (var y = 0; y < h; y++)
            if (pixels[(long)y * stride + 3] != 255 || pixels[(long)y * stride + (w - 1) * 4 + 3] != 255) return false;
        return true;
    }

    /// <summary>Softens a bitmap in place with a Gaussian of <paramref name="sigma"/>, its edges continued outward so nothing fades into transparency.</summary>
    public static void BlurInPlace(SKBitmap bitmap, float sigma)
    {
        sigma = Math.Max(0.1f, sigma);
        using var filter = SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp);
        using var source = Pixels.Clone(bitmap);
        using var canvas = new SKCanvas(bitmap);
        using var paint = new SKPaint { ImageFilter = filter, BlendMode = SKBlendMode.Src };
        canvas.DrawBitmap(source, 0, 0, paint);
        canvas.Flush();
        Pixels.Invalidate(bitmap);
    }

    /// <summary>
    /// Seeded noise for a document pixel and channel, -1…1: even across the range, or bell-shaped (the mean of three
    /// draws, widened back to the same spread) when <paramref name="gaussian"/>.
    /// </summary>
    public static float Noise(int x, int y, uint seed, int channel, bool gaussian)
    {
        var key = unchecked(seed + (uint)channel * 0x9E3779B9u);
        var n = GrainAdjustment.Hash(x, y, key);
        if (!gaussian) return n;
        var sum = n + GrainAdjustment.Hash(x, y, unchecked(key + 0x7F4A7C15u)) + GrainAdjustment.Hash(x, y, unchecked(key + 0x3C6EF372u));
        return Math.Clamp(sum / 3 * 1.7f, -1, 1);
    }

    internal static (SKBitmap Result, int GrowX, int GrowY) MotionBlur(SKBitmap source, double distance, double angle, bool clamp)
    {
        var length = Math.Max(1, (int)Math.Round(distance));
        double radians = angle * Math.PI / 180, dx = Math.Cos(radians), dy = -Math.Sin(radians);
        int padX = clamp ? 0 : (int)Math.Ceiling(Math.Abs(dx) * length / 2) + 1, padY = clamp ? 0 : (int)Math.Ceiling(Math.Abs(dy) * length / 2) + 1;
        int w = source.Width + padX * 2, h = source.Height + padY * 2;
        var result = Pixels.NewColor(w, h);
        var src = (byte*)source.GetPixels();
        var dst = (byte*)result.GetPixels();
        int srcStride = source.RowBytes, dstStride = result.RowBytes, sw = source.Width, sh = source.Height;
        var samples = Math.Min(length, 96);
        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
            {
                float r = 0, g = 0, b = 0, a = 0;
                for (var i = 0; i < samples; i++)
                {
                    var t = samples == 1 ? 0 : (i / (double)(samples - 1) - 0.5) * length;
                    int sx = (int)Math.Round(x - padX + dx * t), sy = (int)Math.Round(y - padY + dy * t);
                    if (clamp) { sx = Math.Clamp(sx, 0, sw - 1); sy = Math.Clamp(sy, 0, sh - 1); }
                    else if (sx < 0 || sy < 0 || sx >= sw || sy >= sh) continue;
                    var p = src + (long)sy * srcStride + sx * 4;
                    r += p[0]; g += p[1]; b += p[2]; a += p[3];
                }
                var o = dst + (long)y * dstStride + x * 4;
                o[0] = (byte)(r / samples + 0.5f); o[1] = (byte)(g / samples + 0.5f);
                o[2] = (byte)(b / samples + 0.5f); o[3] = (byte)(a / samples + 0.5f);
            }
        });
        return (result, padX, padY);
    }

    private static SKBitmap Sharpen(SKBitmap source, double radius, double strength)
    {
        var sigma = (float)Math.Max(0.3, radius / 2);
        using var filter = SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp);
        using var blurred = new SKBitmap(source.Info);
        using (var canvas = new SKCanvas(blurred))
        using (var paint = new SKPaint { ImageFilter = filter, BlendMode = SKBlendMode.Src })
            canvas.DrawBitmap(source, 0, 0, paint);
        var result = Pixels.Clone(source);
        byte* dst = (byte*)result.GetPixels(), soft = (byte*)blurred.GetPixels();
        var s = (float)strength;
        Parallel.For(0, source.Height, y =>
        {
            var row = dst + (long)y * result.RowBytes;
            var blur = soft + (long)y * blurred.RowBytes;
            for (var x = 0; x < source.Width * 4; x += 4)
            {
                int a = row[x + 3];
                for (var c = 0; c < 3; c++) row[x + c] = (byte)Math.Clamp(row[x + c] + (row[x + c] - blur[x + c]) * s + 0.5f, 0, a);
            }
        });
        return result;
    }

    private static SKBitmap AddNoise(SKBitmap source, double amount, bool gaussian, bool monochrome, uint seed)
    {
        var result = Pixels.Clone(source);
        var dst = (byte*)result.GetPixels();
        var spread = (float)(Math.Clamp(amount, 0, AddNoiseAdjustment.MaxAmount) / 100 * 127.5);
        Parallel.For(0, source.Height, y =>
        {
            var row = dst + (long)y * result.RowBytes;
            for (var x = 0; x < source.Width; x++)
            {
                var p = row + x * 4;
                int a = p[3];
                if (a == 0) continue;
                var shared = Noise(x, y, seed, 0, gaussian) * spread;
                for (var c = 0; c < 3; c++)
                {
                    var n = monochrome ? shared : Noise(x, y, seed, c, gaussian) * spread;
                    p[c] = (byte)Math.Clamp(p[c] + n * a / 255f + 0.5f, 0, a);
                }
            }
        });
        return result;
    }

    /// <summary>The Lens Correction warp on its own, for the Camera Raw Filter's Optics: <paramref name="distortion"/> is -1…1 as the filter's slider gives it.</summary>
    internal static SKBitmap Distort(SKBitmap source, double distortion) => LensCorrection(source, distortion);

    private static SKBitmap LensCorrection(SKBitmap source, double distortion)
    {
        int w = source.Width, h = source.Height;
        var result = Pixels.NewColor(w, h);
        byte* src = (byte*)source.GetPixels(), dst = (byte*)result.GetPixels();
        double cx = w / 2.0, cy = h / 2.0, norm = Math.Sqrt(cx * cx + cy * cy);
        var k = distortion * 0.5;
        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
            {
                double nx = (x + 0.5 - cx) / norm, ny = (y + 0.5 - cy) / norm, r2 = nx * nx + ny * ny;
                var factor = 1 + k * r2;
                double sx = cx + nx * factor * norm - 0.5, sy = cy + ny * factor * norm - 0.5;
                var o = dst + (long)y * result.RowBytes + x * 4;
                int x0 = (int)Math.Floor(sx), y0 = (int)Math.Floor(sy);
                if (x0 < -1 || y0 < -1 || x0 >= w || y0 >= h) continue;
                float tx = (float)(sx - x0), ty = (float)(sy - y0);
                for (var c = 0; c < 4; c++)
                {
                    float Sample(int px, int py) => px < 0 || py < 0 || px >= w || py >= h ? 0 : src[(long)py * source.RowBytes + px * 4 + c];
                    var value = (Sample(x0, y0) * (1 - tx) + Sample(x0 + 1, y0) * tx) * (1 - ty) + (Sample(x0, y0 + 1) * (1 - tx) + Sample(x0 + 1, y0 + 1) * tx) * ty;
                    o[c] = (byte)Math.Clamp(value + 0.5f, 0, 255);
                }
                for (var c = 0; c < 3; c++) if (o[c] > o[3]) o[c] = o[3];
            }
        });
        return result;
    }

    // ---- Finishing filters --------------------------------------------------------------------------------------

    private static double Clamp01(double value) => value < 0 ? 0 : value > 1 ? 1 : value;
    private static double Rec709(double r, double g, double b) => 0.2126 * r + 0.7152 * g + 0.0722 * b;

    /// <summary>The vignette's strength at a point of a frame: 0 at its middle, 1 past its edges, shaped between a circle and the frame's corners.</summary>
    internal static double VignetteMaskAt(double px, double py, double width, double height, double midpoint, double roundness, double feather)
    {
        double nx = px / width * 2 - 1, ny = py / height * 2 - 1;
        var square = Math.Max(Math.Abs(nx), Math.Abs(ny));
        var circle = Math.Sqrt(nx * nx + ny * ny) / Math.Sqrt(2);
        var shape = (1 - roundness / 100) * 0.5;
        var distance = circle + (square - circle) * shape;
        var start = midpoint / 100 * 0.85;
        var soft = Math.Max(0.05, feather / 100);
        var t = Clamp01((distance - start) / soft);
        return t * t * (3 - 2 * t);
    }

    /// <summary>
    /// Blends the edges toward a color with Camera Raw's falloff shape and Highlight Priority. With <c>VignetteFillsClear</c>
    /// it paints transparent pixels too, shaped to the frame; without, it recolors only the pixels that are there.
    /// </summary>
    private static SKBitmap Vignette(SKBitmap source, FilterSettings settings)
    {
        var result = Pixels.Clone(source);
        if (settings.VignetteAmount <= 0) return result;
        var frame = settings.VignetteFrame ?? new SKRect(0, 0, source.Width, source.Height);
        if (frame.Width <= 0 || frame.Height <= 0) return result;
        var fills = settings.VignetteFillsClear;
        var strength = Clamp01(settings.VignetteAmount / 100);
        var color = new SKColor(settings.VignetteColor);
        double red = color.Red / 255.0, green = color.Green / 255.0, blue = color.Blue / 255.0;
        double midpoint = Math.Clamp(settings.VignetteMidpoint, 0, 100), roundness = Math.Clamp(settings.VignetteRoundness, -100, 100);
        double feather = Math.Clamp(settings.VignetteFeather, 0, 100), highlights = Math.Clamp(settings.VignetteHighlights, 0, 100) / 100;
        var dst = (byte*)result.GetPixels();
        Parallel.For(0, source.Height, y =>
        {
            var row = dst + (long)y * result.RowBytes;
            for (var x = 0; x < source.Width; x++)
            {
                var p = row + x * 4;
                if (p[3] == 0 && !fills) continue;
                var mask = VignetteMaskAt(x + 0.5 - frame.Left, y + 0.5 - frame.Top, frame.Width, frame.Height, midpoint, roundness, feather);
                if (mask <= 0) continue;
                var alpha = p[3] / 255.0;
                double r = 0, g = 0, b = 0, bright = 0;
                if (p[3] > 0)
                {
                    r = Math.Min(1, p[0] / (double)p[3]); g = Math.Min(1, p[1] / (double)p[3]); b = Math.Min(1, p[2] / (double)p[3]);
                    bright = Clamp01((Rec709(r, g, b) - 0.45) / 0.55);
                }
                var effect = strength * mask * (1 - highlights * bright);
                if (!fills)
                {
                    // Only the pixels that are there change color; their coverage stays as it was.
                    WritePremultiplied(p, r + (red - r) * effect, g + (green - g) * effect, b + (blue - b) * effect, p[3]);
                    continue;
                }
                // The color painted over the pixel at `effect`: an opaque pixel moves toward it, a clear one takes it on.
                var outAlpha = alpha + effect * (1 - alpha);
                if (outAlpha <= 0) continue;
                r = (red * effect + r * alpha * (1 - effect)) / outAlpha;
                g = (green * effect + g * alpha * (1 - effect)) / outAlpha;
                b = (blue * effect + b * alpha * (1 - effect)) / outAlpha;
                p[3] = (byte)Math.Min(255, Math.Round(outAlpha * 255));
                WritePremultiplied(p, r, g, b, p[3]);
            }
        });
        return result;
    }

    private static void WritePremultiplied(byte* p, double r, double g, double b, double alpha)
    {
        p[0] = (byte)Math.Min(alpha, Math.Max(0, Math.Round(r * alpha)));
        p[1] = (byte)Math.Min(alpha, Math.Max(0, Math.Round(g * alpha)));
        p[2] = (byte)Math.Min(alpha, Math.Max(0, Math.Round(b * alpha)));
    }

    /// <summary>
    /// A glow spreading from the bright parts: the picture's lights (everything above a soft knee at 40% brightness)
    /// softened by the radius and added back on top, colors adding and coverage uniting. It reaches past the layer's
    /// edges, so the result grows like a blur's, unless the layer fills the canvas.
    /// </summary>
    private static (SKBitmap, int, int) Bloom(SKBitmap source, double intensity, double radius, bool clamp)
    {
        var sigma = (float)Math.Max(0.5, radius);
        var pad = clamp ? 0 : (int)Math.Ceiling(sigma * 3);
        int w = source.Width + pad * 2, h = source.Height + pad * 2;
        // The lights alone: each pixel scaled by how far its brightness rises above the knee, so gray areas shed nothing.
        using var lights = Pixels.Clone(source);
        var lit = (byte*)lights.GetPixels();
        Parallel.For(0, source.Height, y =>
        {
            var row = lit + (long)y * lights.RowBytes;
            for (var x = 0; x < source.Width; x++)
            {
                var p = row + x * 4;
                int a = p[3];
                if (a == 0) continue;
                var weight = Clamp01((Rec709(p[0] / (double)a, p[1] / (double)a, p[2] / (double)a) - 0.4) / 0.6);
                for (var c = 0; c < 4; c++) p[c] = (byte)Math.Round(p[c] * weight);
            }
        });
        using var glow = Pixels.NewColor(w, h);
        using (var filter = SKImageFilter.CreateBlur(sigma, sigma, clamp ? SKShaderTileMode.Clamp : SKShaderTileMode.Decal))
        using (var canvas = new SKCanvas(glow))
        using (var paint = new SKPaint { ImageFilter = filter, BlendMode = SKBlendMode.Src })
            canvas.DrawBitmap(lights, pad, pad, paint);
        var result = Pixels.NewColor(w, h);
        using (var canvas = new SKCanvas(result)) canvas.DrawBitmap(source, pad, pad);
        byte* dst = (byte*)result.GetPixels(), soft = (byte*)glow.GetPixels();
        var gain = Math.Max(0, intensity);
        Parallel.For(0, h, y =>
        {
            var row = dst + (long)y * result.RowBytes;
            var shed = soft + (long)y * glow.RowBytes;
            for (var x = 0; x < w; x++)
            {
                byte* p = row + x * 4, q = shed + x * 4;
                if (q[3] == 0) continue;
                var glowAlpha = (int)Math.Min(255, Math.Round(q[3] * gain));
                if (glowAlpha == 0) continue;
                int a = p[3];
                var outAlpha = a + glowAlpha - a * glowAlpha / 255;
                for (var c = 0; c < 3; c++) p[c] = (byte)Math.Min(outAlpha, p[c] + Math.Round(q[c] * gain));
                p[3] = (byte)outAlpha;
            }
        });
        return (result, pad, pad);
    }

    private static double TonalSmooth(double low, double high, double value)
    {
        var t = Clamp01((value - low) / (high - low));
        return t * t * (3 - 2 * t);
    }

    /// <summary>Local contrast: each pixel's brightness is pushed away from its surroundings, by an amount set per tonal range.</summary>
    private static SKBitmap TonalContrast(SKBitmap source, FilterSettings settings)
    {
        var result = Pixels.Clone(source);
        var amount = Math.Clamp(settings.TonalAmount, 0, 100);
        double shadows = Math.Clamp(settings.TonalShadows, -100, 100), midtones = Math.Clamp(settings.TonalMidtones, -100, 100), highlights = Math.Clamp(settings.TonalHighlights, -100, 100);
        if (amount <= 0 || (shadows == 0 && midtones == 0 && highlights == 0)) return result;
        using var blurred = Pixels.Clone(source);
        BlurInPlace(blurred, (float)Math.Max(0.5, settings.TonalRadius));
        var strength = amount / 50;
        byte* dst = (byte*)result.GetPixels(), soft = (byte*)blurred.GetPixels();
        Parallel.For(0, source.Height, y =>
        {
            var row = dst + (long)y * result.RowBytes;
            var baseRow = soft + (long)y * blurred.RowBytes;
            for (var x = 0; x < source.Width; x++)
            {
                byte* p = row + x * 4, q = baseRow + x * 4;
                int a = p[3];
                if (a == 0 || q[3] == 0) continue;
                double r = Math.Min(1, p[0] / (double)a), g = Math.Min(1, p[1] / (double)a), b = Math.Min(1, p[2] / (double)a);
                var lum = Rec709(r, g, b);
                var baseLum = Rec709(Math.Min(1, q[0] / (double)q[3]), Math.Min(1, q[1] / (double)q[3]), Math.Min(1, q[2] / (double)q[3]));
                var shadowWeight = 1 - TonalSmooth(0.15, 0.5, baseLum);
                var highlightWeight = TonalSmooth(0.5, 0.85, baseLum);
                var midtoneWeight = 1 - shadowWeight - highlightWeight;
                var weight = (shadows * shadowWeight + midtones * midtoneWeight + highlights * highlightWeight) / 100;
                var delta = 0.18 * Math.Tanh((lum - baseLum) * 6) * weight * strength * (4 * lum * (1 - lum));
                WritePremultiplied(p, Clamp01(r + delta), Clamp01(g + delta), Clamp01(b + delta), a);
            }
        });
        return result;
    }

    /// <summary>
    /// Clears the background that touches the image's edges: everything connected to the border whose color is
    /// close to the border's own colors. It suits product shots and portraits on plain backdrops; it is not a
    /// subject-detection model.
    /// </summary>
    private static SKBitmap RemoveBackground(SKBitmap source, double tolerance)
    {
        using var background = Backdrop(source, (int)Math.Clamp(tolerance / 100 * 160, 2, 200));
        using var soft = Selections.SelectionMask.Feather(background, 2);
        var result = Pixels.Clone(source);
        using var canvas = new SKCanvas(result);
        using var paint = new SKPaint { BlendMode = SKBlendMode.DstOut };
        canvas.DrawBitmap(soft, 0, 0, paint);
        return result;
    }

    /// <summary>
    /// The plain backdrop: every pixel connected to the image's edges whose color is within <paramref name="tol"/>
    /// (0 to 255) of the edge color it grew from. White where the backdrop is.
    /// </summary>
    public static SKBitmap Backdrop(SKBitmap source, int tol)
    {
        int w = source.Width, h = source.Height;
        var background = Pixels.NewMask(w, h);
        var seeds = new List<(int, int)>();
        var stepX = Math.Max(1, w / 24);
        var stepY = Math.Max(1, h / 24);
        for (var x = 0; x < w; x += stepX) { seeds.Add((x, 0)); seeds.Add((x, h - 1)); }
        for (var y = 0; y < h; y += stepY) { seeds.Add((0, y)); seeds.Add((w - 1, y)); }
        var bg = (byte*)background.GetPixels();
        foreach (var (x, y) in seeds)
        {
            if (bg[(long)y * background.RowBytes + x] != 0) continue;
            using var region = Selections.MagicWand.Select(source, x, y, tol, contiguous: true, smooth: false);
            var r = (byte*)region.GetPixels();
            for (var i = 0L; i < (long)h * region.RowBytes; i++) if (r[i] != 0) bg[i] = 255;
        }
        return background;
    }
}
