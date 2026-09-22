using Composa.IO;

namespace Composa.Core.Tests;

public class ImageMagickTests
{
    /// <summary>
    /// The property that matters, and the one Windows broke: whatever is resolved must actually be
    /// ImageMagick. There, "convert" resolves to the FAT-to-NTFS volume converter in System32, which
    /// exists on every machine and answers to the name while being an entirely different program.
    /// </summary>
    [Fact]
    public void Whatever_is_resolved_identifies_itself_as_imagemagick()
    {
        if (!ImageMagick.IsAvailable) return; // Not installed here; nothing to check.

        using var process = ImageMagick.Start("-version");
        Assert.NotNull(process);
        var output = process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(10_000));
        Assert.Contains("ImageMagick", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Availability_and_starting_agree()
    {
        using var process = ImageMagick.Start("-version");
        Assert.Equal(ImageMagick.IsAvailable, process != null);
    }

    /// <summary>A RAW file must report what is missing, not whatever an unrelated program printed.</summary>
    [Fact]
    public void A_raw_file_without_imagemagick_says_imagemagick_is_what_is_missing()
    {
        if (ImageMagick.IsAvailable) return; // Covered by the decoding test when it is installed.

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
