using Compositor.Rendering;
using SkiaSharp;

namespace Compositor.IO;

public enum ExportFormat { Png, Jpeg, Webp }

public static class ImageFiles
{
    public static readonly string[] ImportExtensions = [".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".ico", ".heic", ".heif", ".avif", ".tif", ".tiff"];

    public const long MaxPixels = 100_000_000;

    /// <summary>Decodes an image file to RGBA premultiplied pixels, upright according to its EXIF orientation.</summary>
    public static SKBitmap Load(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Load(stream, Path.GetFileName(path));
        }
        catch (InvalidDataException) when (ConvertWithImageMagick(path) is { } converted)
        {
            // Skia has no HEIC, AVIF or TIFF decoder; ImageMagick, when installed, fills the gap.
            using var stream = new MemoryStream(converted);
            return Load(stream, Path.GetFileName(path));
        }
    }

    private static byte[]? ConvertWithImageMagick(string path)
    {
        foreach (var tool in new[] { "magick", "convert" })
        {
            try
            {
                var start = new System.Diagnostics.ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                start.ArgumentList.Add(path + "[0]");
                start.ArgumentList.Add("-auto-orient");
                start.ArgumentList.Add("png:-");
                using var process = System.Diagnostics.Process.Start(start);
                if (process == null) continue;
                using var output = new MemoryStream();
                var errors = process.StandardError.ReadToEndAsync();
                process.StandardOutput.BaseStream.CopyTo(output);
                process.WaitForExit(60_000);
                _ = errors.Result;
                if (process.ExitCode == 0 && output.Length > 0) return output.ToArray();
            }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
            {
                // The tool is not installed or could not run; try the next one, then report the original decode error.
            }
        }
        return null;
    }

    public static SKBitmap Load(Stream stream, string name = "image")
    {
        using var codec = SKCodec.Create(stream, out var status)
            ?? throw new InvalidDataException($"{name} could not be read ({status}). Supported formats are PNG, JPEG, WebP, BMP and GIF; HEIC, AVIF and TIFF open when ImageMagick is installed.");
        var info = codec.Info;
        if ((long)info.Width * info.Height > MaxPixels || info.Width > Model.Document.MaxSide || info.Height > Model.Document.MaxSide)
            throw new InvalidDataException($"{name} is larger than the supported 100 megapixels.");
        var bitmap = new SKBitmap(Pixels.ColorInfo(info.Width, info.Height));
        var result = codec.GetPixels(bitmap.Info, bitmap.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            bitmap.Dispose();
            throw new InvalidDataException($"{name} could not be decoded ({result}).");
        }
        return Upright(bitmap, codec.EncodedOrigin);
    }

    private static SKBitmap Upright(SKBitmap bitmap, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default) return bitmap;
        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        int w = swap ? bitmap.Height : bitmap.Width, h = swap ? bitmap.Width : bitmap.Height;
        var result = Pixels.NewColor(w, h);
        using (var canvas = new SKCanvas(result))
        {
            var matrix = origin switch
            {
                SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),
                SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
                SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),
                SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
                SKEncodedOrigin.RightTop => new SKMatrix(0, -1, w, 1, 0, 0, 0, 0, 1),
                SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, w, -1, 0, h, 0, 0, 1),
                SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, h, 0, 0, 1),
                _ => SKMatrix.Identity
            };
            canvas.SetMatrix(in matrix);
            canvas.DrawBitmap(bitmap, 0, 0);
        }
        bitmap.Dispose();
        return result;
    }

    public static byte[] Encode(SKBitmap bitmap, ExportFormat format, int quality = 90, SKColor? matte = null)
    {
        var source = bitmap;
        SKBitmap? flattened = null;
        if (format == ExportFormat.Jpeg)
        {
            // JPEG has no transparency: composite over the matte (white by default).
            flattened = new SKBitmap(new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Rgba8888, SKAlphaType.Opaque));
            using var canvas = new SKCanvas(flattened);
            canvas.Clear(matte ?? SKColors.White);
            canvas.DrawBitmap(bitmap, 0, 0);
            source = flattened;
        }
        try
        {
            using var data = source.Encode(format switch
            {
                ExportFormat.Jpeg => SKEncodedImageFormat.Jpeg,
                ExportFormat.Webp => SKEncodedImageFormat.Webp,
                _ => SKEncodedImageFormat.Png
            }, Math.Clamp(quality, 1, 100)) ?? throw new IOException("The image could not be encoded.");
            return data.ToArray();
        }
        finally { flattened?.Dispose(); }
    }

    public static void Save(SKBitmap bitmap, string path, ExportFormat format, int quality = 90)
    {
        var bytes = Encode(bitmap, format, quality);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }

    public static ExportFormat FormatFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => ExportFormat.Jpeg,
        ".webp" => ExportFormat.Webp,
        _ => ExportFormat.Png
    };
}
