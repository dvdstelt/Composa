using System.Diagnostics;
using System.Text;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.IO;

/// <summary>
/// What to spend a RAW file's latitude on before it becomes an 8-bit layer. The camera's own rendering is the starting
/// point; every value is a departure from it, so Reset has somewhere to go back to.
/// </summary>
public sealed record RawDevelopSettings
{
    /// <summary>Stops of exposure either side of what the camera recorded, -3…3.</summary>
    public double Exposure { get; init; }
    /// <summary>White balance away from the camera's reading: -100 cooler to 100 warmer.</summary>
    public double Temperature { get; init; }
    /// <summary>Green to magenta balance away from the camera's reading, -100…100.</summary>
    public double Tint { get; init; }

    public bool IsAsShot => Exposure == 0 && Temperature == 0 && Tint == 0;
}

/// <summary>
/// A decoded camera frame at 16 bits per channel, sRGB-encoded, kept until the develop settings are chosen. Sixteen bits
/// hold more of the sensor's range than a layer can, so darkening a bright frame recovers tones an 8-bit decode would
/// have already clipped.
/// </summary>
public sealed class RawImage
{
    public int Width { get; }
    public int Height { get; }
    /// <summary>Interleaved RGB, row by row.</summary>
    public ushort[] Samples { get; }

    public RawImage(int width, int height, ushort[] samples)
    {
        if (width <= 0 || height <= 0 || samples.Length != (long)width * height * 3) throw new ArgumentException("The sample count does not match the size.");
        Width = width;
        Height = height;
        Samples = samples;
    }

    /// <summary>
    /// Reads a Netpbm PAM image (<c>P7</c>) with RGB or RGB plus alpha tuples at 8 or 16 bits, which is what ImageMagick
    /// is asked to write: a header of plain words followed by big-endian samples.
    /// </summary>
    public static RawImage ParsePam(byte[] data)
    {
        var header = Encoding.ASCII.GetString(data, 0, Math.Min(data.Length, 512));
        if (!header.StartsWith("P7", StringComparison.Ordinal)) throw new InvalidDataException("The decoded frame was not in the expected format.");
        var end = header.IndexOf("ENDHDR\n", StringComparison.Ordinal);
        if (end < 0) throw new InvalidDataException("The decoded frame was not in the expected format.");
        int width = 0, height = 0, depth = 0, maxValue = 0;
        foreach (var line in header[..end].Split('\n'))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            switch (parts[0])
            {
                case "WIDTH": width = int.Parse(parts[1]); break;
                case "HEIGHT": height = int.Parse(parts[1]); break;
                case "DEPTH": depth = int.Parse(parts[1]); break;
                case "MAXVAL": maxValue = int.Parse(parts[1]); break;
            }
        }
        if (width <= 0 || height <= 0 || depth is not (3 or 4) || maxValue is not (255 or 65535)) throw new InvalidDataException("The decoded frame was not in the expected format.");
        if ((long)width * height > ImageFiles.MaxPixels || width > Model.Document.MaxSide || height > Model.Document.MaxSide)
            throw new InvalidDataException("The image is larger than the supported 100 megapixels.");
        var bytesPerSample = maxValue == 65535 ? 2 : 1;
        var offset = end + "ENDHDR\n".Length;
        if (data.Length - offset < (long)width * height * depth * bytesPerSample) throw new InvalidDataException("The decoded frame was cut short.");
        var samples = new ushort[(long)width * height * 3];
        var read = offset;
        for (long pixel = 0, i = 0; pixel < (long)width * height; pixel++)
        {
            for (var channel = 0; channel < depth; channel++)
            {
                var value = bytesPerSample == 2 ? (ushort)(data[read] << 8 | data[read + 1]) : (ushort)(data[read] * 257);
                read += bytesPerSample;
                if (channel < 3) samples[i++] = value;
            }
        }
        return new RawImage(width, height, samples);
    }

    /// <summary>
    /// The frame as 8-bit premultiplied pixels with the settings applied: exposure scales linear light, temperature and
    /// tint weigh the channels against each other. <paramref name="maxSide"/> above zero reduces the result to fit,
    /// sampling the frame rather than filtering it, for a preview that keeps up with a slider.
    /// </summary>
    public unsafe SKBitmap Develop(RawDevelopSettings settings, int maxSide = 0)
    {
        var step = maxSide > 0 ? Math.Max(1, (int)Math.Ceiling(Math.Max(Width, Height) / (double)maxSide)) : 1;
        int width = (Width + step - 1) / step, height = (Height + step - 1) / step;
        var tables = Tables(settings);
        var result = Pixels.NewColor(width, height);
        var target = (byte*)result.GetPixels();
        var stride = result.RowBytes;
        var samples = Samples;
        Parallel.For(0, height, y =>
        {
            var row = target + (long)y * stride;
            var source = (long)y * step * Width * 3;
            for (var x = 0; x < width; x++, row += 4, source += step * 3)
            {
                row[0] = tables[0][samples[source]];
                row[1] = tables[1][samples[source + 1]];
                row[2] = tables[2][samples[source + 2]];
                row[3] = 255;
            }
        });
        return result;
    }

    /// <summary>One 16-bit to 8-bit table per channel: decode sRGB, scale in linear light, encode again.</summary>
    private static byte[][] Tables(RawDevelopSettings settings)
    {
        var exposure = Math.Pow(2, Math.Clamp(settings.Exposure, -3, 3));
        var warmth = Math.Clamp(settings.Temperature, -100, 100) / 100;
        var tint = Math.Clamp(settings.Tint, -100, 100) / 100;
        // Warmer means more red and less blue; a magenta tint takes green away, a green one adds it.
        double[] gains = [exposure * (1 + 0.35 * warmth), exposure * (1 - 0.25 * tint), exposure * (1 - 0.35 * warmth)];
        var tables = new byte[3][];
        for (var channel = 0; channel < 3; channel++)
        {
            var table = tables[channel] = new byte[65536];
            var gain = gains[channel];
            for (var i = 0; i < 65536; i++)
            {
                var encoded = i / 65535.0;
                var linear = encoded <= 0.04045 ? encoded / 12.92 : Math.Pow((encoded + 0.055) / 1.055, 2.4);
                linear = Math.Min(1, linear * gain);
                var output = linear <= 0.0031308 ? linear * 12.92 : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055;
                table[i] = (byte)Math.Clamp(Math.Round(output * 255), 0, 255);
            }
        }
        return tables;
    }
}

/// <summary>
/// Camera RAW files, decoded by ImageMagick's LibRaw delegate when ImageMagick is installed. The decode keeps 16 bits
/// per channel so that the develop step has range to work with; the macOS app uses Apple's RAW pipeline, which has no
/// Linux equivalent, so exposure and white balance are applied here to the decoded frame rather than to the sensor data.
/// </summary>
public static class RawImporter
{
    /// <summary>The extensions LibRaw reads: Canon, Nikon, Sony, Fujifilm, Olympus, Panasonic, Pentax, Leica, Hasselblad, Phase One, Samsung and DNG.</summary>
    public static readonly string[] Extensions =
    [
        ".dng", ".cr2", ".cr3", ".crw", ".nef", ".nrw", ".arw", ".srf", ".sr2", ".raf", ".orf", ".rw2", ".pef", ".dcr", ".kdc",
        ".raw", ".rwl", ".3fr", ".fff", ".erf", ".mef", ".mos", ".mrw", ".x3f", ".iiq", ".srw"
    ];

    public static bool IsRaw(string path) => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Decodes the frame at the camera's own white balance and exposure. Seconds of work: call it off the UI thread.</summary>
    public static RawImage Decode(string path)
    {
        using var process = ImageMagick.Start(path + "[0]", "-auto-orient", "-depth", "16", "pam:-")
            ?? throw new InvalidDataException($"{Path.GetFileName(path)} is a camera RAW file. Opening one needs ImageMagick (with its LibRaw delegate), which is not installed.");
        using var output = new MemoryStream();
        var errors = process.StandardError.ReadToEndAsync();
        process.StandardOutput.BaseStream.CopyTo(output);
        if (!process.WaitForExit(180_000)) { process.Kill(); throw new InvalidDataException($"{Path.GetFileName(path)} took too long to decode."); }
        if (process.ExitCode != 0 || output.Length == 0)
            throw new InvalidDataException($"{Path.GetFileName(path)} could not be decoded. {FirstLine(errors.Result)}".Trim());
        return RawImage.ParsePam(output.ToArray());
    }

    private static string FirstLine(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
}
