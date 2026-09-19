using System.Runtime.CompilerServices;
using Compositor.Model;
using SkiaSharp;

namespace Compositor.Rendering;

/// <summary>Pixel formats and helpers shared by everything that touches bitmaps.</summary>
public static class Pixels
{
    public static SKImageInfo ColorInfo(int width, int height) => new(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
    public static SKImageInfo MaskInfo(int width, int height) => new(width, height, SKColorType.Alpha8, SKAlphaType.Premul);

    public static SKBitmap NewColor(int width, int height)
    {
        var bitmap = new SKBitmap(ColorInfo(Math.Max(1, width), Math.Max(1, height)));
        Fill(bitmap, 0);
        return bitmap;
    }

    public static SKBitmap NewMask(int width, int height, byte fill = 0)
    {
        var bitmap = new SKBitmap(MaskInfo(Math.Max(1, width), Math.Max(1, height)));
        Fill(bitmap, fill);
        return bitmap;
    }

    private const int ChunkRows = 256;

    private static unsafe void Fill(SKBitmap bitmap, byte value)
    {
        var pixels = (byte*)bitmap.GetPixels();
        long stride = bitmap.RowBytes, height = bitmap.Height;
        if (stride * height < 4_000_000) { new Span<byte>(pixels, (int)(stride * height)).Fill(value); return; }
        Parallel.For(0, (int)((height + ChunkRows - 1) / ChunkRows), chunk =>
        {
            var rows = Math.Min(ChunkRows, height - (long)chunk * ChunkRows);
            new Span<byte>(pixels + chunk * ChunkRows * stride, (int)(rows * stride)).Fill(value);
        });
    }

    /// <summary>An independent copy of a bitmap. Far faster than <c>SKBitmap.Copy</c>, which converts pixel by pixel.</summary>
    public static unsafe SKBitmap Clone(SKBitmap source)
    {
        var copy = new SKBitmap(source.Info);
        byte* from = (byte*)source.GetPixels(), to = (byte*)copy.GetPixels();
        long height = source.Height;
        if (source.RowBytes != copy.RowBytes)
        {
            for (var y = 0; y < height; y++) Buffer.MemoryCopy(from + y * (long)source.RowBytes, to + y * (long)copy.RowBytes, copy.RowBytes, Math.Min(source.RowBytes, copy.RowBytes));
            return copy;
        }
        long stride = source.RowBytes;
        if (stride * height < 4_000_000) { Buffer.MemoryCopy(from, to, stride * height, stride * height); return copy; }
        Parallel.For(0, (int)((height + ChunkRows - 1) / ChunkRows), chunk =>
        {
            var offset = chunk * ChunkRows * stride;
            var bytes = Math.Min(ChunkRows, height - (long)chunk * ChunkRows) * stride;
            Buffer.MemoryCopy(from + offset, to + offset, bytes, bytes);
        });
        return copy;
    }

    // ---- Cached images and reduction pyramids -------------------------------------------------------------------

    /// <summary>Level 0 wraps the bitmap itself without copying; each further level is half the size of the one before.</summary>
    private sealed class Pyramid : IDisposable
    {
        public readonly List<SKBitmap> Bitmaps = [];
        public readonly List<SKImage> Images = [];

        public void Dispose()
        {
            foreach (var image in Images) image.Dispose();
            foreach (var bitmap in Bitmaps.Skip(1)) bitmap.Dispose();
        }
    }

    private static readonly ConditionalWeakTable<SKBitmap, Pyramid> Pyramids = new();
    private static readonly HashSet<SKBitmap> Live = new(ReferenceEqualityComparer.Instance);

    /// <summary>A no-copy image over the bitmap's pixels. Call <see cref="Invalidate"/> after changing them.</summary>
    public static SKImage ImageOf(SKBitmap bitmap)
    {
        var level = 0;
        return Level(bitmap, ref level);
    }

    /// <summary>The image for a reduction level (0 is full size), clamped to the smallest level worth having.</summary>
    internal static SKImage Level(SKBitmap bitmap, ref int level)
    {
        lock (bitmap)
        {
            if (!Pyramids.TryGetValue(bitmap, out var pyramid))
            {
                pyramid = new Pyramid();
                pyramid.Bitmaps.Add(bitmap);
                pyramid.Images.Add(SKImage.FromPixels(bitmap.PeekPixels()));
                Pyramids.Add(bitmap, pyramid);
            }
            level = Math.Max(0, level);
            while (pyramid.Bitmaps.Count <= level)
            {
                var previous = pyramid.Bitmaps[^1];
                if (previous.Width <= 64 || previous.Height <= 64) break;
                var half = Halve(previous);
                pyramid.Bitmaps.Add(half);
                pyramid.Images.Add(SKImage.FromPixels(half.PeekPixels()));
            }
            level = Math.Min(level, pyramid.Images.Count - 1);
            return pyramid.Images[level];
        }
    }

    /// <summary>Raised when a bitmap's pixels changed in place, so other caches keyed on it (thumbnails) can drop it too.</summary>
    public static event Action<SKBitmap>? Invalidated;

    /// <summary>Drops everything cached for a bitmap whose pixels changed.</summary>
    public static void Invalidate(SKBitmap bitmap)
    {
        Invalidated?.Invoke(bitmap);
        lock (bitmap)
        {
            if (!Pyramids.TryGetValue(bitmap, out var pyramid)) return;
            Pyramids.Remove(bitmap);
            pyramid.Dispose();
        }
    }

    /// <summary>Marks a bitmap as changing continuously (a stroke in progress): it is drawn without a pyramid until released.</summary>
    public static void SetLive(SKBitmap bitmap, bool live)
    {
        lock (Live) { if (live) Live.Add(bitmap); else Live.Remove(bitmap); }
    }

    internal static bool IsLive(SKBitmap bitmap)
    {
        lock (Live) return Live.Count > 0 && Live.Contains(bitmap);
    }

    /// <summary>Builds the pyramids a reduced render will read, once, before the render fans out over threads.</summary>
    internal static void PrepareLevels(Document document, float scale)
    {
        if (scale >= 0.5f) return;
        foreach (var layer in document.AllLayers())
        {
            foreach (var bitmap in new[] { layer.Pixels, layer.Mask })
            {
                if (bitmap == null || IsLive(bitmap)) continue;
                var layerScale = scale * (layer.Pixels != null ? Math.Sqrt(Math.Abs(layer.Transform.Width * layer.Transform.Height) / Math.Max(1.0, (double)layer.Pixels.Width * layer.Pixels.Height)) : 1);
                var level = layerScale >= 0.5 ? 0 : (int)Math.Floor(Math.Log2(1 / layerScale));
                if (level > 0) Level(bitmap, ref level);
            }
        }
    }

    /// <summary>A half-size copy averaging each 2×2 block. Premultiplied pixels average correctly as they are.</summary>
    private static unsafe SKBitmap Halve(SKBitmap source)
    {
        int width = Math.Max(1, source.Width / 2), height = Math.Max(1, source.Height / 2), bpp = source.BytesPerPixel;
        var half = new SKBitmap(source.Info.WithSize(width, height));
        byte* from = (byte*)source.GetPixels(), to = (byte*)half.GetPixels();
        int fromStride = source.RowBytes, toStride = half.RowBytes;
        int lastX = source.Width - 1, lastY = source.Height - 1;
        Parallel.For(0, height, y =>
        {
            byte* top = from + (long)Math.Min(y * 2, lastY) * fromStride, bottom = from + (long)Math.Min(y * 2 + 1, lastY) * fromStride;
            var row = to + (long)y * toStride;
            for (var x = 0; x < width; x++)
            {
                int a = Math.Min(x * 2, lastX) * bpp, b = Math.Min(x * 2 + 1, lastX) * bpp;
                for (var c = 0; c < bpp; c++) row[x * bpp + c] = (byte)((top[a + c] + top[b + c] + bottom[a + c] + bottom[b + c] + 2) >> 2);
            }
        });
        return half;
    }
}
