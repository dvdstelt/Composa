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
