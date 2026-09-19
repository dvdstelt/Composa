using Compositor.Model;
using Compositor.Rendering;
using SkiaSharp;

namespace Compositor.Filters;

public enum FilterKind { GaussianBlur, MotionBlur, AddNoise, LensCorrection, Sharpen, RemoveBackground }

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
    /// <summary>Lens distortion, -100 (pincushion) … 100 (barrel correction).</summary>
    public double Distortion { get; init; }
    /// <summary>Vignette, -100 (darken corners) … 100 (lighten corners).</summary>
    public double Vignette { get; init; }
    public uint Seed { get; init; } = 1;

    public static string DisplayName(FilterKind kind) => kind switch
    {
        FilterKind.GaussianBlur => "Gaussian Blur",
        FilterKind.MotionBlur => "Motion Blur",
        FilterKind.AddNoise => "Add Noise",
        FilterKind.LensCorrection => "Lens Correction",
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
            case FilterKind.GaussianBlur:
            {
                var sigma = (float)Math.Max(0.1, settings.Radius);
                var pad = (int)Math.Ceiling(sigma * 3);
                using var filter = SKImageFilter.CreateBlur(sigma, sigma);
                return (Draw(source, pad, pad, filter), pad, pad);
            }
            case FilterKind.MotionBlur:
                return MotionBlur(source, settings.Radius, settings.Angle);
            case FilterKind.Sharpen:
                return (Sharpen(source, settings.Radius, settings.Amount / 100 * 2), 0, 0);
            case FilterKind.AddNoise:
                return (AddNoise(source, settings.Amount, settings.Monochrome, settings.Seed), 0, 0);
            case FilterKind.LensCorrection:
                return (LensCorrection(source, settings.Distortion / 100, settings.Vignette / 100), 0, 0);
            case FilterKind.RemoveBackground:
                return (RemoveBackground(source, settings.Amount), 0, 0);
            default:
                return (source.Copy(), 0, 0);
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

    private static (SKBitmap, int, int) MotionBlur(SKBitmap source, double distance, double angle)
    {
        var length = Math.Max(1, (int)Math.Round(distance));
        double radians = angle * Math.PI / 180, dx = Math.Cos(radians), dy = -Math.Sin(radians);
        int padX = (int)Math.Ceiling(Math.Abs(dx) * length / 2) + 1, padY = (int)Math.Ceiling(Math.Abs(dy) * length / 2) + 1;
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
                    if (sx < 0 || sy < 0 || sx >= sw || sy >= sh) continue;
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
        var result = source.Copy();
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

    private static SKBitmap AddNoise(SKBitmap source, double amount, bool monochrome, uint seed)
    {
        var result = source.Copy();
        var dst = (byte*)result.GetPixels();
        var strength = (float)(Math.Clamp(amount, 0, 100) / 100 * 128);
        Parallel.For(0, source.Height, y =>
        {
            var row = dst + (long)y * result.RowBytes;
            for (var x = 0; x < source.Width; x++)
            {
                var p = row + x * 4;
                int a = p[3];
                if (a == 0) continue;
                var shared = GrainAdjustment.Hash(x, y, seed) * strength;
                for (var c = 0; c < 3; c++)
                {
                    var n = monochrome ? shared : GrainAdjustment.Hash(x, y, seed + (uint)c * 7919u) * strength;
                    p[c] = (byte)Math.Clamp(p[c] + n * a / 255f + 0.5f, 0, a);
                }
            }
        });
        return result;
    }

    private static SKBitmap LensCorrection(SKBitmap source, double distortion, double vignette)
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
                var gain = (float)(1 + vignette * r2);
                for (var c = 0; c < 4; c++)
                {
                    float Sample(int px, int py) => px < 0 || py < 0 || px >= w || py >= h ? 0 : src[(long)py * source.RowBytes + px * 4 + c];
                    var value = (Sample(x0, y0) * (1 - tx) + Sample(x0 + 1, y0) * tx) * (1 - ty) + (Sample(x0, y0 + 1) * (1 - tx) + Sample(x0 + 1, y0 + 1) * tx) * ty;
                    o[c] = (byte)Math.Clamp(c < 3 ? value * gain + 0.5f : value + 0.5f, 0, 255);
                }
                for (var c = 0; c < 3; c++) if (o[c] > o[3]) o[c] = o[3];
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
        int w = source.Width, h = source.Height;
        var tol = (int)Math.Clamp(tolerance / 100 * 160, 2, 200);
        using var background = Pixels.NewMask(w, h);
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
        using var soft = Selections.SelectionMask.Feather(background, 2);
        var result = source.Copy();
        using var canvas = new SKCanvas(result);
        using var paint = new SKPaint { BlendMode = SKBlendMode.DstOut };
        canvas.DrawBitmap(soft, 0, 0, paint);
        return result;
    }
}
