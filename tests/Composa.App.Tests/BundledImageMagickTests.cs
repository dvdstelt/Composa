using Composa.Core.Tests;
using Composa.IO;
using SkiaSharp;
using Magick = ImageMagick;

namespace Composa.App.Tests;

/// <summary>
/// The ImageMagick that Windows and macOS builds carry. Developer builds bundle it on every platform,
/// so these run on Linux too, against the Linux native library from the same package.
/// </summary>
public class BundledImageMagickTests
{
    private static BundledImageMagick Load() => BundledImageMagick.TryLoad() ?? throw new Xunit.Sdk.XunitException("The bundled ImageMagick did not load.");

    /// <summary>
    /// The formats bundling was decided for. They depend on delegates compiled into the native library
    /// (libheif, libde265, libraw, libtiff), so a package update that dropped one would fail here
    /// rather than on a user's photo.
    /// </summary>
    [Theory]
    [InlineData("HEIC")]
    [InlineData("HEIF")]
    [InlineData("AVIF")]
    [InlineData("TIFF")]
    [InlineData("DNG")]
    [InlineData("CR2")]
    [InlineData("CR3")]
    [InlineData("NEF")]
    [InlineData("ARW")]
    [InlineData("RAF")]
    public void Reads_the_formats_skia_cannot(string format)
    {
        Load();
        Assert.Contains(Magick.MagickNET.SupportedFormats, f => f.Format.ToString().Equals(format, StringComparison.OrdinalIgnoreCase) && f.SupportsReading);
    }

    [Fact]
    public void Converts_the_first_frame_to_png_and_to_a_16_bit_pam()
    {
        var magick = Load();
        var folder = Directory.CreateTempSubdirectory("composa-tests-").FullName;
        try
        {
            // Two pages, so reading only the first is what is being checked: the second is blue.
            var tiff = Path.Combine(folder, "pages.tiff");
            using (var pages = new Magick.MagickImageCollection())
            {
                pages.Add(new Magick.MagickImage(Magick.MagickColors.Red, 12, 8));
                pages.Add(new Magick.MagickImage(Magick.MagickColors.Blue, 12, 8));
                pages.Write(tiff);
            }

            using var decoded = SKBitmap.Decode(magick.Convert(tiff, ImageMagickOutput.Png));
            Assert.Equal((12, 8), (decoded.Width, decoded.Height));
            Assert.Equal(SKColors.Red, decoded.GetPixel(3, 3));

            var raw = RawImage.ParsePam(magick.Convert(tiff, ImageMagickOutput.Pam16));
            Assert.Equal((12, 8), (raw.Width, raw.Height));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    /// <summary>AVIF is the one of these formats the library can also write, so it is the one decoded for real rather than only listed.</summary>
    [Fact]
    public void Decodes_an_avif_file()
    {
        var magick = Load();
        var path = Path.Combine(Path.GetTempPath(), "composa-" + Guid.NewGuid().ToString("N") + ".avif");
        try
        {
            using (var image = new Magick.MagickImage(Magick.MagickColors.Orange, 40, 30)) image.Write(path);
            using var decoded = SKBitmap.Decode(magick.Convert(path, ImageMagickOutput.Png));
            Assert.Equal((40, 30), (decoded.Width, decoded.Height));
        }
        finally { TempFiles.Delete(path); }
    }

    /// <summary>SVG import draws through the bundled library's librsvg, at the resolution that gives the size asked for.</summary>
    [Fact]
    public void Rasterizes_an_svg_at_the_scale_asked_for()
    {
        var magick = Load();
        Assert.Contains(Magick.MagickNET.SupportedFormats, f => f.Format == Magick.MagickFormat.Svg && f.SupportsReading);
        var path = Path.Combine(Path.GetTempPath(), "composa-" + Guid.NewGuid().ToString("N") + ".svg");
        File.WriteAllText(path, "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"50\"><circle cx=\"50\" cy=\"25\" r=\"20\" fill=\"#ff0000\"/></svg>");
        try
        {
            using var declared = SKBitmap.Decode(magick.Rasterize(path, 1));
            Assert.Equal((100, 50), (declared.Width, declared.Height));
            Assert.Equal(0, declared.GetPixel(2, 2).Alpha);
            using var doubled = SKBitmap.Decode(magick.Rasterize(path, 2));
            Assert.Equal((200, 100), (doubled.Width, doubled.Height));
            Assert.Equal(SKColors.Red, doubled.GetPixel(100, 50));
        }
        finally { TempFiles.Delete(path); }
    }

    [Fact]
    public void A_file_it_cannot_read_is_invalid_data_naming_the_file()
    {
        var magick = Load();
        var path = Path.Combine(Path.GetTempPath(), "composa-" + Guid.NewGuid().ToString("N") + ".cr2");
        File.WriteAllBytes(path, [0, 1, 2, 3]);
        try
        {
            var error = Assert.Throws<InvalidDataException>(() => magick.Convert(path, ImageMagickOutput.Pam16));
            Assert.Contains(Path.GetFileName(path), error.Message);
        }
        finally { TempFiles.Delete(path); }
    }
}
