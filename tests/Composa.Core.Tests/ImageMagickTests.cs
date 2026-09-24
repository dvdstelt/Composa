using Composa.IO;
using SkiaSharp;

namespace Composa.Core.Tests;

/// <summary>
/// The command-line path, which is what Linux uses. The bundled library that Windows and macOS use is
/// covered by the app tests, since the app is what references it.
/// </summary>
public class ImageMagickTests
{
    /// <summary>
    /// The property that matters, and the one Windows broke: whatever is found must actually be
    /// ImageMagick. There, "convert" resolves to the FAT-to-NTFS volume converter in System32, which
    /// exists on every machine and answers to the name while being an entirely different program.
    /// </summary>
    [Fact]
    public void Whatever_is_found_identifies_itself_as_imagemagick()
    {
        if (ImageMagickTool.Find() is not { } tool) return; // Not installed here; nothing to check.

        using var process = tool.Start("-version");
        Assert.NotNull(process);
        var output = process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(10_000));
        Assert.Contains("ImageMagick", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_tool_converts_the_first_frame_to_png_and_to_a_16_bit_pam()
    {
        if (ImageMagickTool.Find() is not { } tool) return;
        var folder = Directory.CreateTempSubdirectory("composa-tests-").FullName;
        try
        {
            var png = Path.Combine(folder, "source.png");
            var tiff = Path.Combine(folder, "source.tiff");
            using (var red = TestImages.Solid(12, 8, SKColors.Red)) ImageFiles.Save(red, png, ExportFormat.Png);
            using (var convert = tool.Start(png, tiff)) convert!.WaitForExit();

            using var decoded = SKBitmap.Decode(tool.Convert(tiff, ImageMagickOutput.Png));
            Assert.Equal((12, 8), (decoded.Width, decoded.Height));
            TestImages.AssertColor(SKColors.Red, decoded.GetPixel(3, 3));

            var raw = RawImage.ParsePam(tool.Convert(tiff, ImageMagickOutput.Pam16));
            Assert.Equal((12, 8), (raw.Width, raw.Height));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    /// <summary>An SVG is drawn at the size it declares, or larger by the scale asked for, over a transparent background.</summary>
    [Fact]
    public void The_tool_rasterizes_an_svg_at_its_declared_size_and_scaled()
    {
        if (ImageMagickTool.Find() is not { } tool) return;
        var path = Path.Combine(Path.GetTempPath(), "composa-" + Guid.NewGuid().ToString("N") + ".svg");
        File.WriteAllText(path, "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"50\"><circle cx=\"50\" cy=\"25\" r=\"20\" fill=\"#ff0000\"/></svg>");
        try
        {
            using var declared = SKBitmap.Decode(tool.Rasterize(path, 1));
            Assert.Equal((100, 50), (declared.Width, declared.Height));
            Assert.Equal(0, declared.GetPixel(2, 2).Alpha);
            TestImages.AssertColor(SKColors.Red, declared.GetPixel(50, 25));
            using var doubled = SKBitmap.Decode(tool.Rasterize(path, 2.5));
            Assert.Equal((250, 125), (doubled.Width, doubled.Height));
            TestImages.AssertColor(SKColors.Red, doubled.GetPixel(125, 62));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void An_svg_is_fitted_to_the_canvas_as_it_is_drawn_or_kept_at_its_declared_size()
    {
        Assert.True(SvgImporter.IsSvg("icon.SVG"));
        Assert.False(SvgImporter.IsSvg("icon.png"));
        Assert.Contains(".svg", ImageFiles.ImportExtensions);
        var path = Path.Combine(Path.GetTempPath(), "composa-" + Guid.NewGuid().ToString("N") + ".svg");
        File.WriteAllText(path, "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"50\"><circle cx=\"50\" cy=\"25\" r=\"20\" fill=\"#ff0000\"/></svg>");
        try
        {
            if (!ImageMagick.IsAvailable)
            {
                Assert.Contains("ImageMagick", Assert.Throws<InvalidDataException>(() => SvgImporter.Render(path)).Message);
                return;
            }
            using var declared = SvgImporter.Render(path);
            Assert.Equal((100, 50), (declared.Width, declared.Height));
            using var fitted = SvgImporter.Render(path, new SKSizeI(400, 400));
            Assert.Equal((400, 200), (fitted.Width, fitted.Height));       // Sharp at the fitted size, not enlarged from 100 x 50.
            TestImages.AssertColor(SKColors.Red, fitted.GetPixel(200, 100));
            Assert.Equal(0, fitted.GetPixel(4, 4).Alpha);
            using var shrunk = SvgImporter.Render(path, new SKSizeI(20, 40));
            Assert.Equal((20, 10), (shrunk.Width, shrunk.Height));
            Assert.Contains("room", Assert.Throws<InvalidDataException>(() => SvgImporter.Render(path, null, remainingPixels: 100)).Message);
        }
        finally { File.Delete(path); }
    }

    /// <summary>A file ImageMagick cannot read says so in the exception the callers already handle, rather than in an empty result.</summary>
    [Fact]
    public void A_file_the_tool_cannot_read_is_invalid_data()
    {
        if (ImageMagickTool.Find() is not { } tool) return;
        var path = Path.Combine(Path.GetTempPath(), "composa-" + Guid.NewGuid().ToString("N") + ".tiff");
        File.WriteAllBytes(path, [0, 1, 2, 3]);
        try
        {
            var error = Assert.Throws<InvalidDataException>(() => tool.Convert(path, ImageMagickOutput.Png));
            Assert.Contains(Path.GetFileName(path), error.Message);
        }
        finally { File.Delete(path); }
    }

    /// <summary>A RAW file must report what is missing, not whatever an unrelated program printed.</summary>
    [Fact]
    public void A_raw_file_without_imagemagick_says_imagemagick_is_what_is_missing()
    {
        if (ImageMagick.IsAvailable) return; // Covered by the decoding tests when it is installed.

        var path = Path.Combine(Path.GetTempPath(), "composa-" + Guid.NewGuid().ToString("N") + ".cr2");
        File.WriteAllBytes(path, [0, 1, 2, 3]);
        try
        {
            var error = Assert.Throws<InvalidDataException>(() => RawImporter.Decode(path));
            Assert.Contains("ImageMagick", error.Message);
        }
        finally { File.Delete(path); }
    }
}
