using Composa.Model;
using SkiaSharp;

namespace Composa.IO;

/// <summary>
/// SVG files, drawn once into pixels by ImageMagick's SVG renderer (librsvg in every build that carries one). Skia
/// has no SVG parser of its own, so this is the same road HEIC and TIFF take. What comes in is an ordinary image
/// layer: it does not stay vector.
/// </summary>
public static class SvgImporter
{
    public static readonly string[] Extensions = [".svg"];

    public static bool IsSvg(string path) => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The file rendered to fit inside <paramref name="fitting"/> (the canvas) when given, otherwise at the size it
    /// declares. Fitting happens as the file is drawn, not by scaling pixels afterwards, so a small icon placed on a
    /// large canvas comes in sharp. Tens to hundreds of milliseconds: call it off the UI thread.
    /// </summary>
    public static SKBitmap Render(string path, SKSizeI? fitting = null, long remainingPixels = long.MaxValue)
    {
        var name = Path.GetFileName(path);
        var magick = ImageMagick.Current ?? throw new InvalidDataException($"{name} is an SVG file. Opening one needs ImageMagick, which is not installed.");
        // Drawn at its declared size first: that is what the file says it is, and it is the answer when there is nothing to fit.
        var declared = ImageFiles.Load(new MemoryStream(magick.Rasterize(path, 1)), name);
        try
        {
            var scale = fitting is { } fit ? Math.Min((double)fit.Width / declared.Width, (double)fit.Height / declared.Height) : 1;
            int width = Math.Max(1, (int)Math.Round(declared.Width * scale)), height = Math.Max(1, (int)Math.Round(declared.Height * scale));
            if (!DocumentLimits.FitsSurface(width, height) || (long)width * height > remainingPixels)
                throw new InvalidDataException($"{name} would be larger than the supported {DocumentLimits.MaxSide:N0} pixels a side and {DocumentLimits.MaxSurfaceMegapixels} megapixels, or than the document has room for.");
            if (Math.Abs(scale - 1) < 0.005) return declared;
            var fitted = ImageFiles.Load(new MemoryStream(magick.Rasterize(path, scale)), name);
            declared.Dispose();
            return fitted;
        }
        catch
        {
            declared.Dispose();
            throw;
        }
    }
}
