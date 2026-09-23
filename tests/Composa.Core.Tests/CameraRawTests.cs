using Composa.Editing;
using Composa.Filters;
using Composa.Rendering;
using SkiaSharp;
using static Composa.Core.Tests.TestImages;

namespace Composa.Core.Tests;

public class CameraRawTests
{
    private static SKColor Graded(SKColor color, CameraRawSettings settings)
    {
        using var source = Solid(4, 4, color);
        using var result = CameraRawPixels.Apply(source, settings);
        return result.GetPixel(1, 1);
    }

    private static readonly SKColor Gray = new(128, 128, 128);

    private static double Saturation(SKColor c)
    {
        ColorMath.RgbToHsv(c.Red / 255.0, c.Green / 255.0, c.Blue / 255.0, out _, out var s, out _);
        return s;
    }

    [Fact]
    public void The_defaults_change_nothing_and_a_group_can_be_switched_off()
    {
        var settings = new CameraRawSettings();
        Assert.True(settings.IsIdentity);
        using var source = Gradient(32, 32);
        using var same = CameraRawPixels.Apply(source, settings);
        Assert.Equal(source.Bytes, same.Bytes);
        var graded = settings with { Exposure = 1, Vibrance = 20, Texture = 30, Detail = new CameraRawDetail { SharpenAmount = 50 } };
        Assert.False(graded.IsIdentity);
        Assert.True(graded.Adjusts(CameraRawGroup.Light) && graded.Adjusts(CameraRawGroup.Color) && graded.Adjusts(CameraRawGroup.Effects) && graded.Adjusts(CameraRawGroup.Detail));
        Assert.False(graded.Adjusts(CameraRawGroup.Curve));
        var lightless = graded.Without(CameraRawGroup.Light);
        Assert.False(lightless.Adjusts(CameraRawGroup.Light));
        Assert.Equal(20, lightless.Vibrance);                               // Other groups keep their sliders.
        Assert.True(graded.Without(CameraRawGroup.Light).Without(CameraRawGroup.Color).Without(CameraRawGroup.Effects).Without(CameraRawGroup.Detail).IsIdentity);
    }

    [Fact]
    public void Light_sliders_move_the_tones_they_name()
    {
        Assert.True(Graded(Gray, new CameraRawSettings { Exposure = 1 }).Red > 160);
        Assert.True(Graded(Gray, new CameraRawSettings { Exposure = -1 }).Red < 100);
        Assert.Equal(255, Graded(Gray, new CameraRawSettings { Exposure = 1 }).Alpha);
        var dark = new SKColor(40, 40, 40);
        var bright = new SKColor(220, 220, 220);
        Assert.True(Graded(dark, new CameraRawSettings { Contrast = 80 }).Red < 40 && Graded(bright, new CameraRawSettings { Contrast = 80 }).Red > 220);
        Assert.True(Graded(bright, new CameraRawSettings { Highlights = -100 }).Red < 200);
        AssertColor(dark, Graded(dark, new CameraRawSettings { Highlights = -100 }), 3);           // Highlights leave the shadows alone.
        Assert.True(Graded(dark, new CameraRawSettings { Shadows = 100 }).Red > 60);
        AssertColor(bright, Graded(bright, new CameraRawSettings { Shadows = 100 }), 3);
        Assert.True(Graded(bright, new CameraRawSettings { Whites = 100 }).Red > 240);
        Assert.True(Graded(dark, new CameraRawSettings { Blacks = -100 }).Red < 20);
    }

    [Fact]
    public void Color_sliders_warm_tint_and_saturate_and_auto_balance_neutralizes_a_cast()
    {
        var warm = Graded(Gray, new CameraRawSettings { Temperature = 60 });
        Assert.True(warm.Red > warm.Blue + 20, $"{warm} should be warmer");
        var magenta = Graded(Gray, new CameraRawSettings { Tint = 60 });
        Assert.True(magenta.Green < magenta.Red && magenta.Green < magenta.Blue, $"{magenta} should be magenta");
        var muted = new SKColor(140, 120, 110);
        Assert.True(Saturation(Graded(muted, new CameraRawSettings { Vibrance = 80 })) > Saturation(muted));
        Assert.True(Saturation(Graded(muted, new CameraRawSettings { Saturation = 60 })) > Saturation(muted));
        var flat = Graded(new SKColor(200, 60, 60), new CameraRawSettings { Saturation = -100 });
        Assert.True(Math.Abs(flat.Red - flat.Blue) <= 2, $"{flat} should be gray");

        // A gray given a cast, read back by the gray-world balance, comes out neutral again.
        var cast = Graded(Gray, new CameraRawSettings { Temperature = 45, Tint = -30 });
        using var tinted = Solid(8, 8, cast);
        var solved = CameraRawPixels.AutoBalance(tinted);
        Assert.NotNull(solved);
        var balanced = Graded(cast, new CameraRawSettings { Temperature = solved.Value.Temperature, Tint = solved.Value.Tint });
        Assert.True(Math.Abs(balanced.Red - balanced.Green) <= 3 && Math.Abs(balanced.Green - balanced.Blue) <= 3, $"{balanced} should be neutral");
        // The eyedropper does the same for one pixel's straight color.
        var picked = CameraRawSettings.NeutralizeSrgb(cast.Red / 255.0, cast.Green / 255.0, cast.Blue / 255.0);
        Assert.NotNull(picked);
        Assert.InRange(picked.Value.Temperature, solved.Value.Temperature - 2, solved.Value.Temperature + 2);
    }

    [Fact]
    public void Curve_mixer_and_grading_reach_their_own_tones_and_hues()
    {
        var dark = new SKColor(40, 40, 40);
        Assert.True(Graded(dark, new CameraRawSettings { Curve = new CameraRawCurve { Shadows = 100 } }).Red > 45);
        AssertColor(dark, Graded(dark, new CameraRawSettings { Curve = new CameraRawCurve { Highlights = 100 } }), 2);
        var lifted = Graded(Gray, new CameraRawSettings { Curve = new CameraRawCurve { Rgb = [new(0, 0), new(0.5, 0.75), new(1, 1)] } });
        Assert.True(lifted.Red > 170, $"{lifted} should follow the point curve");
        var redder = Graded(Gray, new CameraRawSettings { Curve = new CameraRawCurve { Red = [new(0, 0.2), new(1, 1)] } });
        Assert.True(redder.Red > redder.Green + 10);

        var red = new SKColor(200, 60, 60);
        var blue = new SKColor(60, 60, 200);
        var shifted = Graded(red, new CameraRawSettings { Mixer = new CameraRawMixer().With(0, 0, 100) });
        ColorMath.RgbToHsv(shifted.Red / 255.0, shifted.Green / 255.0, shifted.Blue / 255.0, out var hue, out _, out _);
        Assert.InRange(hue, 15, 45);                                        // Reds turned toward orange...
        AssertColor(blue, Graded(blue, new CameraRawSettings { Mixer = new CameraRawMixer().With(0, 0, 100) }), 3); // ...blues untouched.
        // A pure red also belongs a quarter to the Oranges, whose untouched slider tempers the Reds' -100 a little.
        Assert.True(Saturation(Graded(red, new CameraRawSettings { Mixer = new CameraRawMixer().With(1, 0, -100) })) < Saturation(red) * 0.35);

        var graded = Graded(dark, new CameraRawSettings { Grading = new CameraRawGrading { Shadows = new CameraRawWheel { Hue = 210, Saturation = 100 } } });
        Assert.True(graded.Blue > graded.Red + 10, $"{graded} shadows should be cooled");
        var brightGraded = Graded(new SKColor(230, 230, 230), new CameraRawSettings { Grading = new CameraRawGrading { Shadows = new CameraRawWheel { Hue = 210, Saturation = 100 }, Blending = 0 } });
        Assert.True(Math.Abs(brightGraded.Blue - brightGraded.Red) <= 6, $"{brightGraded} highlights stay clear of the shadow wheel");
    }

    [Fact]
    public void Effects_optics_detail_and_calibration_do_what_they_say()
    {
        // Clarity and Texture add local contrast to a soft edge; Dehaze deepens; a vignette darkens the corners only.
        using var edge = Pixels.NewColor(64, 64);
        for (var y = 0; y < 64; y++) for (var x = 0; x < 64; x++) edge.SetPixel(x, y, new SKColor((byte)(x < 32 ? 90 : 160), (byte)(x < 32 ? 90 : 160), (byte)(x < 32 ? 90 : 160)));
        using (var clear = CameraRawPixels.Apply(edge, new CameraRawSettings { Clarity = 100, Texture = 100 }))
            Assert.True(clear.GetPixel(33, 32).Red - clear.GetPixel(30, 32).Red > 160 - 90 + 10);
        var hazy = Graded(new SKColor(100, 100, 110), new CameraRawSettings { Dehaze = 80 });   // Haze sits in the lower tones, which Dehaze deepens.
        Assert.True(hazy.Red < 80, $"{hazy} should be deeper");
        Assert.True(Graded(new SKColor(100, 100, 110), new CameraRawSettings { Dehaze = -80 }).Red > 120, "negative Dehaze adds haze");
        using (var vignetted = CameraRawPixels.Apply(edge, new CameraRawSettings { VignetteAmount = -100, VignetteHighlights = 0 }))
        {
            Assert.True(vignetted.GetPixel(0, 0).Red < 60);
            Assert.InRange(vignetted.GetPixel(30, 32).Red, 86, 92);
        }
        using (var glowing = CameraRawPixels.Apply(edge, new CameraRawSettings { Glow = 100, GlowRange = -100, GlowSpread = 100 }))
            Assert.True(glowing.GetPixel(28, 32).Red > 90, "light from the bright half spills into the dark one");
        using (var grainy = CameraRawPixels.Apply(edge, new CameraRawSettings { GrainAmount = 60 }, seed: 3))
            Assert.NotEqual(grainy.GetPixel(10, 10), grainy.GetPixel(11, 10));

        // Sharpening steepens the edge; luminance noise reduction evens out speckles.
        using (var sharp = CameraRawPixels.Apply(edge, new CameraRawSettings { Detail = new CameraRawDetail { SharpenAmount = 150, SharpenRadius = 50 } }))
            Assert.True(sharp.GetPixel(31, 32).Red < 90 && sharp.GetPixel(32, 32).Red > 160);
        using var speckled = Solid(64, 64, Gray);
        new AddNoiseAdjustment { Amount = 40, Monochromatic = true, Seed = 5 }.Apply(speckled);
        using (var smooth = CameraRawPixels.Apply(speckled, new CameraRawSettings { Detail = new CameraRawDetail { NoiseLuminance = 100, NoiseLuminanceDetail = 0 } }))
        {
            double Spread(SKBitmap b) { double t = 0; for (var i = 8; i < 56; i++) t += Math.Abs(b.GetPixel(i, 32).Red - 128); return t; }
            Assert.True(Spread(smooth) < Spread(speckled) * 0.6);
        }

        // Defringe drains purple, the lens vignette correction lifts the corners, calibration turns the reds.
        var fringe = new SKColor(160, 60, 200);
        Assert.True(Saturation(Graded(fringe, new CameraRawSettings { Optics = new CameraRawOptics { PurpleAmount = 100 } })) < Saturation(fringe) * 0.6);
        AssertColor(new SKColor(200, 60, 60), Graded(new SKColor(200, 60, 60), new CameraRawSettings { Optics = new CameraRawOptics { PurpleAmount = 100 } }), 2);
        using (var lifted = CameraRawPixels.Apply(edge, new CameraRawSettings { Optics = new CameraRawOptics { VignetteAmount = 100 } }))
            Assert.True(lifted.GetPixel(0, 0).Red > 120);
        var turned = Graded(new SKColor(200, 60, 60), new CameraRawSettings { Calibration = new CameraRawCalibration { RedHue = 100 } });
        Assert.True(turned.Green > 70, $"{turned} should have turned toward orange");
        var gentle = Graded(new SKColor(200, 60, 60), new CameraRawSettings { Calibration = new CameraRawCalibration { RedHue = 100, Process = 1 } });
        Assert.True(gentle.Green < turned.Green, "an earlier process moves the sliders less far");
    }

    [Fact]
    public void The_filter_runs_from_the_session_as_one_undo_step_and_scales_with_the_layer()
    {
        var session = EditorSession.NewCanvas(60, 60, SKColors.White);
        session.AddImageLayer("Photo", Solid(30, 30, Gray), new SKPoint(30, 30));
        session.ApplyFilter(new FilterSettings { Kind = FilterKind.CameraRaw, CameraRaw = new CameraRawSettings { Exposure = 1 } });
        Assert.Equal("Camera Raw Filter", session.History.UndoName);
        Assert.True(session.Composite().GetPixel(30, 30).Red > 160);
        Assert.True(new FilterSettings { Kind = FilterKind.CameraRaw }.IsIdentity);
        Assert.False(new FilterSettings { Kind = FilterKind.CameraRaw, CameraRaw = new CameraRawSettings { Tint = 5 } }.IsIdentity);
        Assert.Equal(0.5 + 19.5 * 0.25, new CameraRawSettings().GrainKernelSize, 6);
        // Out-of-range values are brought back before they reach the pixels.
        var wild = new CameraRawSettings { Exposure = 40, Contrast = double.NaN, Curve = new CameraRawCurve { ShadowSplit = 80, DarkSplit = 10 } }.Normalized();
        Assert.Equal((5d, 0d), (wild.Exposure, wild.Contrast));
        Assert.True(wild.Curve.ShadowSplit < wild.Curve.DarkSplit && wild.Curve.DarkSplit < wild.Curve.LightSplit);
    }
}
