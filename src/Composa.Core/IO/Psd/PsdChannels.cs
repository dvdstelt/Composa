using Composa.Rendering;
using SkiaSharp;

namespace Composa.IO.Psd;

/// <summary>
/// Unpacks channel planes as the Photoshop file format stores them: raw, PackBits run lengths (the usual), or
/// zlib with and without per-row prediction. Written from Adobe's published Photoshop File Formats Specification.
/// </summary>
internal static class PsdChannels
{
    public const int Raw = 0, Rle = 1, Zip = 2, ZipWithPrediction = 3;

    /// <summary>One plane of <paramref name="width"/> × <paramref name="height"/> bytes from one channel's data.</summary>
    public static byte[] Decode(int compression, int width, int height, ReadOnlySpan<byte> data)
    {
        var expected = (long)width * height;
        if (expected == 0) return [];
        switch (compression)
        {
            case Raw:
                if (data.Length < expected) throw PsdException.Truncated();
                return data[..(int)expected].ToArray();
            case Rle:
            {
                var cursor = new PsdCursor(data);
                var counts = new int[height];
                for (var row = 0; row < height; row++) counts[row] = cursor.U16();
                var plane = new byte[expected];
                UnpackRows(data[cursor.Offset..], counts, width, height, plane, 0);
                return plane;
            }
            case Zip:
            case ZipWithPrediction:
            {
                var plane = Inflate(data, expected);
                if (compression == ZipWithPrediction)
                    for (var row = 0; row < height; row++)
                    {
                        var start = row * width;
                        for (var x = 1; x < width; x++) plane[start + x] = (byte)(plane[start + x] + plane[start + x - 1]);
                    }
                return plane;
            }
            default:
                throw new PsdException("This Photoshop file uses a layer compression method that isn't supported.");
        }
    }

    /// <summary>PackBits rows into <paramref name="plane"/> from <paramref name="planeOffset"/>; returns the bytes consumed.</summary>
    public static int UnpackRows(ReadOnlySpan<byte> data, ReadOnlySpan<int> counts, int width, int height, byte[] plane, long planeOffset)
    {
        var offset = 0;
        for (var row = 0; row < height; row++)
        {
            var end = offset + counts[row];
            if (end > data.Length) throw PsdException.Truncated();
            var target = planeOffset + (long)row * width;
            var written = 0;
            while (written < width)
            {
                if (offset >= end) throw PsdException.Truncated();
                var n = (sbyte)data[offset++];
                if (n >= 0)
                {
                    var count = n + 1;
                    if (written + count > width || offset + count > end) throw PsdException.Truncated();
                    data.Slice(offset, count).CopyTo(plane.AsSpan((int)(target + written), count));
                    offset += count;
                    written += count;
                }
                else if (n != -128)
                {
                    var count = 1 - n;
                    if (written + count > width || offset >= end) throw PsdException.Truncated();
                    plane.AsSpan((int)(target + written), count).Fill(data[offset++]);
                    written += count;
                }
            }
            offset = end;
        }
        return offset;
    }

    private static byte[] Inflate(ReadOnlySpan<byte> data, long expected)
    {
        var plane = new byte[expected];
        try
        {
            using var input = new MemoryStream(data.ToArray());
            using var zlib = new System.IO.Compression.ZLibStream(input, System.IO.Compression.CompressionMode.Decompress);
            var read = 0;
            while (read < plane.Length)
            {
                var got = zlib.Read(plane, read, plane.Length - read);
                if (got <= 0) break;
                read += got;
            }
            if (read < plane.Length) throw PsdException.Truncated();
        }
        catch (InvalidDataException) { throw PsdException.Truncated(); }
        return plane;
    }

    /// <summary>Premultiplied RGBA pixels from straight planes; a missing alpha means opaque.</summary>
    public static SKBitmap ColorImage(int width, int height, byte[]? red, byte[]? green, byte[]? blue, byte[]? alpha)
    {
        var count = (long)width * height;
        if ((red != null && red.Length < count) || (green != null && green.Length < count) || (blue != null && blue.Length < count) || (alpha != null && alpha.Length < count))
            throw PsdException.Truncated();
        var bitmap = Pixels.NewColor(width, height);
        var pixels = bitmap.GetPixelSpan();
        for (long i = 0, p = 0; i < count; i++, p += 4)
        {
            var a = alpha == null ? (byte)255 : alpha[i];
            pixels[(int)p] = Premultiply(red == null ? (byte)0 : red[i], a);
            pixels[(int)p + 1] = Premultiply(green == null ? (byte)0 : green[i], a);
            pixels[(int)p + 2] = Premultiply(blue == null ? (byte)0 : blue[i], a);
            pixels[(int)p + 3] = a;
        }
        Pixels.Invalidate(bitmap);
        return bitmap;
    }

    private static byte Premultiply(byte value, byte alpha) => alpha == 255 ? value : (byte)((value * alpha + 127) / 255);

    /// <summary>An Alpha8 mask from one gray plane.</summary>
    public static SKBitmap MaskImage(int width, int height, byte[] gray)
    {
        if (gray.Length < (long)width * height) throw PsdException.Truncated();
        var mask = Pixels.NewMask(width, height);
        var target = mask.GetPixelSpan();
        for (var y = 0; y < height; y++) gray.AsSpan(y * width, width).CopyTo(target.Slice(y * mask.RowBytes, width));
        Pixels.Invalidate(mask);
        return mask;
    }
}
