using System.Runtime.CompilerServices;
using Composa.Model;
using SkiaSharp;

namespace Composa.Rendering;

/// <summary>
/// The blend modes Skia cannot draw, composited per pixel: Linear Burn, Linear Dodge, Vivid Light, Linear Light,
/// Pin Light, Hard Mix, Subtract and Divide. Each is a separable function B(backdrop, source) on straight color,
/// combined the way the PDF compositing model (and Photoshop) does: the blended color where both layers cover the
/// pixel, the plain source where only it does, and the backdrop where only it does.
/// </summary>
public static class SeparableBlend
{
    /// <summary>Composites <paramref name="source"/> onto <paramref name="destination"/> in place; both premultiplied RGBA8888 of the same size.</summary>
    public static unsafe void Composite(SKBitmap destination, SKBitmap source, BlendMode mode, double opacity)
    {
        if (destination.Width != source.Width || destination.Height != source.Height) throw new ArgumentException("The bitmaps must be the same size.");
        var blend = FunctionFor(mode);
        var scale = (float)Math.Clamp(opacity, 0, 1);
        var dst = (byte*)destination.GetPixels();
        var src = (byte*)source.GetPixels();
        int dstStride = destination.RowBytes, srcStride = source.RowBytes, width = destination.Width;
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void Row(int y)
        {
            var d = dst + (long)y * dstStride;
            var s = src + (long)y * srcStride;
            for (var x = 0; x < width; x++, d += 4, s += 4)
            {
                var sa = s[3] / 255f * scale;
                if (sa <= 0) continue;
                var da = d[3] / 255f;
                // Straight colors on both sides; premultiplied source scaled by the layer opacity.
                float sr = s[0] / 255f * scale, sg = s[1] / 255f * scale, sb = s[2] / 255f * scale;
                float dr = d[0] / 255f, dg = d[1] / 255f, db = d[2] / 255f;
                float cs0 = sr / sa, cs1 = sg / sa, cs2 = sb / sa;
                float cb0 = 0, cb1 = 0, cb2 = 0;
                if (da > 0) { cb0 = dr / da; cb1 = dg / da; cb2 = db / da; }
                var both = sa * da;
                var r = sr * (1 - da) + dr * (1 - sa) + both * blend(Math.Min(1, cb0), Math.Min(1, cs0));
                var g = sg * (1 - da) + dg * (1 - sa) + both * blend(Math.Min(1, cb1), Math.Min(1, cs1));
                var b = sb * (1 - da) + db * (1 - sa) + both * blend(Math.Min(1, cb2), Math.Min(1, cs2));
                var a = sa + da - both;
                d[3] = ToByte(a);
                d[0] = (byte)Math.Min(d[3], ToByte(r));
                d[1] = (byte)Math.Min(d[3], ToByte(g));
                d[2] = (byte)Math.Min(d[3], ToByte(b));
            }
        }
        if ((long)width * destination.Height > 200_000) Parallel.For(0, destination.Height, Row);
        else for (var y = 0; y < destination.Height; y++) Row(y);
    }

    private static byte ToByte(float unit) => (byte)Math.Clamp(MathF.Round(unit * 255), 0, 255);

    /// <summary>The separable blend function B(backdrop, source) on straight 0…1 channel values.</summary>
    public static Func<float, float, float> FunctionFor(BlendMode mode) => mode switch
    {
        BlendMode.LinearBurn => static (cb, cs) => Math.Max(0, cb + cs - 1),
        BlendMode.LinearDodge => static (cb, cs) => Math.Min(1, cb + cs),
        BlendMode.VividLight => static (cb, cs) => VividLight(cb, cs),
        BlendMode.LinearLight => static (cb, cs) => Math.Clamp(cb + 2 * cs - 1, 0, 1),
        BlendMode.PinLight => static (cb, cs) => cs < 0.5f ? Math.Min(cb, 2 * cs) : Math.Max(cb, 2 * cs - 1),
        // Vivid Light thresholded at half, which works out to whether the two channels sum past one.
        BlendMode.HardMix => static (cb, cs) => cb + cs < 1 ? 0 : 1,
        BlendMode.Subtract => static (cb, cs) => Math.Max(0, cb - cs),
        BlendMode.Divide => static (cb, cs) => cb <= 0 ? 0 : cs <= 0 ? 1 : Math.Min(1, cb / cs),
        _ => throw new ArgumentException($"{mode} is drawn by Skia, not here.", nameof(mode))
    };

    /// <summary>Color Burn for a dark source, Color Dodge for a light one, each at double strength.</summary>
    private static float VividLight(float cb, float cs)
    {
        if (cs <= 0.5f)
        {
            var burn = 2 * cs;
            return burn <= 0 ? (cb >= 1 ? 1 : 0) : 1 - Math.Min(1, (1 - cb) / burn);
        }
        var dodge = 2 * cs - 1;
        return dodge >= 1 ? (cb <= 0 ? 0 : 1) : Math.Min(1, cb / (1 - dodge));
    }
}
