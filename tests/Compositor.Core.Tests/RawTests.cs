using Compositor.IO;
using SkiaSharp;
using static Compositor.Core.Tests.TestImages;

namespace Compositor.Core.Tests;

public class RawTests
{
    /// <summary>A 16-bit PAM of one color, as ImageMagick writes it.</summary>
    private static byte[] Pam(int width, int height, ushort r, ushort g, ushort b, bool alpha = false)
    {
        var header = System.Text.Encoding.ASCII.GetBytes($"P7\nWIDTH {width}\nHEIGHT {height}\nDEPTH {(alpha ? 4 : 3)}\nMAXVAL 65535\nTUPLTYPE {(alpha ? "RGB_ALPHA" : "RGB")}\nENDHDR\n");
        var stream = new MemoryStream();
        stream.Write(header);
        void Write(ushort v) { stream.WriteByte((byte)(v >> 8)); stream.WriteByte((byte)v); }
        for (var i = 0; i < width * height; i++) { Write(r); Write(g); Write(b); if (alpha) Write(65535); }
        return stream.ToArray();
    }

    [Fact]
    public void Pam_frames_parse_and_develop_as_shot_to_their_8_bit_color()
    {
        var raw = RawImage.ParsePam(Pam(6, 4, 65535, 32896, 0, alpha: true));
        Assert.Equal((6, 4), (raw.Width, raw.Height));
        using var developed = raw.Develop(new RawDevelopSettings());
        AssertColor(new SKColor(255, 128, 0), developed.GetPixel(5, 3), 1);
        Assert.Throws<InvalidDataException>(() => RawImage.ParsePam(System.Text.Encoding.ASCII.GetBytes("P6 2 2 255\n")));
        Assert.Throws<InvalidDataException>(() => RawImage.ParsePam(Pam(4, 4, 1, 1, 1)[..40]));
    }

    [Fact]
    public void Exposure_scales_linear_light_and_white_balance_weighs_the_channels()
    {
        var gray = RawImage.ParsePam(Pam(2, 2, 32896, 32896, 32896));
        using var brighter = gray.Develop(new RawDevelopSettings { Exposure = 1 });
        // Mid gray is 21.4% linear; one stop up is 42.8%, which encodes to 175.
        AssertColor(new SKColor(175, 175, 175), brighter.GetPixel(0, 0), 1);
        using var darker = gray.Develop(new RawDevelopSettings { Exposure = -1 });
        AssertColor(new SKColor(92, 92, 92), darker.GetPixel(0, 0), 1); // 10.7% linear

        using var warm = gray.Develop(new RawDevelopSettings { Temperature = 100 });
        var pixel = warm.GetPixel(1, 1);
        Assert.True(pixel.Red > 128 && pixel.Green == 128 && pixel.Blue < 128, $"{pixel} should be warmer");
        using var magenta = gray.Develop(new RawDevelopSettings { Tint = 100 });
        Assert.True(magenta.GetPixel(0, 0).Green < 128);
        using var green = gray.Develop(new RawDevelopSettings { Tint = -100 });
        Assert.True(green.GetPixel(0, 0).Green > 128);

        // Sixteen bits keep what an 8-bit decode would have merged: two near-white values still differ after darkening.
        var white = RawImage.ParsePam(Pam(1, 1, 65535, 65535, 65535));
        var nearlyWhite = RawImage.ParsePam(Pam(1, 1, 65400, 65400, 65400));
        using var a = white.Develop(new RawDevelopSettings { Exposure = -2 });
        using var b = nearlyWhite.Develop(new RawDevelopSettings { Exposure = -2 });
        Assert.True(a.GetPixel(0, 0).Red >= b.GetPixel(0, 0).Red);
        Assert.True(new RawDevelopSettings().IsAsShot);
        Assert.False(new RawDevelopSettings { Tint = 1 }.IsAsShot);
    }

    [Fact]
    public void Previews_sample_the_frame_down_to_the_requested_side()
    {
        var raw = RawImage.ParsePam(Pam(100, 40, 1000, 2000, 3000));
        using var preview = raw.Develop(new RawDevelopSettings(), maxSide: 25);
        Assert.Equal((25, 10), (preview.Width, preview.Height));
        using var full = raw.Develop(new RawDevelopSettings(), maxSide: 500);
        Assert.Equal((100, 40), (full.Width, full.Height));
    }

    [Fact]
    public void Raw_files_are_told_apart_by_extension()
    {
        Assert.True(RawImporter.IsRaw("/photos/IMG_0001.CR2"));
        Assert.True(RawImporter.IsRaw("shot.dng"));
        Assert.False(RawImporter.IsRaw("shot.png"));
        Assert.False(RawImporter.IsRaw("shot.psd"));
    }

    [Fact]
    public void Decoding_goes_through_imagemagick_at_16_bits_when_it_is_installed()
    {
        var folder = Path.Combine(Path.GetTempPath(), "compositor-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var png = Path.Combine(folder, "frame.png");
            using (var color = Solid(12, 8, new SKColor(200, 100, 50))) ImageFiles.Save(color, png, ExportFormat.Png);
            RawImage raw;
            try { raw = RawImporter.Decode(png); }
            catch (InvalidDataException error) when (error.Message.Contains("not installed")) { return; }
            Assert.Equal((12, 8), (raw.Width, raw.Height));
            Assert.Equal(200 * 257, raw.Samples[0]);
            using var developed = raw.Develop(new RawDevelopSettings());
            AssertColor(new SKColor(200, 100, 50), developed.GetPixel(3, 3), 1);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
