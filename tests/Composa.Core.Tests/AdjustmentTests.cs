using Composa.Editing;
using Composa.Filters;
using Composa.IO;
using Composa.Model;
using SkiaSharp;
using static Composa.Core.Tests.TestImages;

namespace Composa.Core.Tests;

public class AdjustmentTests
{
    private static SKColor Adjusted(Adjustment adjustment, SKColor color)
    {
        using var bitmap = Solid(2, 2, color);
        adjustment.Apply(bitmap);
        return bitmap.GetPixel(0, 0);
    }

    [Fact]
    public void Black_and_white_weighs_each_color_family_as_photoshop_does()
    {
        var bw = new BlackAndWhiteAdjustment();
        AssertColor(new SKColor(102, 102, 102), Adjusted(bw, SKColors.Red));                 // reds 40%
        AssertColor(new SKColor(153, 153, 153), Adjusted(bw, SKColors.Yellow));              // yellows 60%
        AssertColor(new SKColor(51, 51, 51), Adjusted(bw, SKColors.Blue));                   // blues 20%
        AssertColor(new SKColor(128, 128, 128), Adjusted(bw, new SKColor(128, 128, 128)));   // gray stays put
        // Orange is red plus some yellow: the minimum is gray, the yellow part weighs 60%, the red remainder 40%.
        AssertColor(new SKColor(128, 128, 128), Adjusted(bw, new SKColor(255, 128, 0)));   // 0.502 × 0.6 + 0.498 × 0.4
        // A red weight of 100% makes pure red white; -100% makes it black.
        AssertColor(SKColors.White, Adjusted(bw with { Reds = 100 }, SKColors.Red));
        AssertColor(SKColors.Black, Adjusted(bw with { Reds = -100 }, SKColors.Red));
        Assert.Equal(bw with { Cyans = 10 }, bw.WithWeight(3, 10));
    }

    [Fact]
    public void Black_and_white_tint_colors_the_gray_at_its_lightness()
    {
        var sepia = new BlackAndWhiteAdjustment { Tint = true, TintHue = 40, TintSaturation = 20 };
        var pixel = Adjusted(sepia, new SKColor(128, 128, 128));
        Assert.True(pixel.Red > pixel.Green && pixel.Green > pixel.Blue, $"{pixel} should be warm");
        Assert.InRange((pixel.Red + pixel.Blue) / 2, 126, 130); // the lightness is the gray
        var none = new BlackAndWhiteAdjustment { Tint = true, TintSaturation = 0 };
        AssertColor(new SKColor(128, 128, 128), Adjusted(none, new SKColor(128, 128, 128)));
    }

    [Fact]
    public void Color_balance_shifts_the_tonal_range_it_is_told_to_and_can_keep_the_brightness()
    {
        var gray = new SKColor(128, 128, 128);
        var warmMidtones = new ColorBalanceAdjustment { PreserveLuminosity = false }.WithShift(1, 0, 100);
        // Mid gray belongs wholly to the midtones, which weigh 0.7: red climbs by 0.7 and clips.
        AssertColor(new SKColor(255, 128, 128), Adjusted(warmMidtones, gray));
        // The shadows do not reach mid gray at all.
        var warmShadows = new ColorBalanceAdjustment { PreserveLuminosity = false }.WithShift(0, 0, 100);
        AssertColor(gray, Adjusted(warmShadows, gray));
        AssertColor(new SKColor(198, 20, 20), Adjusted(warmShadows, new SKColor(20, 20, 20)), 3); // 0.078 + 0.7, the full shadow weight
        // Preserve Luminosity scales the result back to the original Rec. 601 luma.
        var kept = Adjusted(warmMidtones with { PreserveLuminosity = true }, gray);
        Assert.InRange(0.299 * kept.Red + 0.587 * kept.Green + 0.114 * kept.Blue, 125, 131);
        Assert.True(kept.Red > kept.Green);
        Assert.True(new ColorBalanceAdjustment().IsIdentity);
        Assert.Equal(-40, new ColorBalanceAdjustment().WithShift(2, 2, -40).Highlights[2]);
    }

    [Fact]
    public void Black_and_white_and_color_balance_round_trip_through_the_project_file()
    {
        var session = EditorSession.NewCanvas(10, 10, SKColors.White);
        session.AddAdjustmentLayer(new BlackAndWhiteAdjustment { Reds = 70, Tint = true, TintHue = 200, TintSaturation = 35 });
        session.AddAdjustmentLayer(new ColorBalanceAdjustment { PreserveLuminosity = false }.WithShift(0, 1, -30).WithShift(2, 2, 45));
        using var stream = new MemoryStream();
        ProjectFile.Write(session.Document, stream);
        stream.Position = 0;
        var loaded = ProjectFile.Read(stream);
        var adjustments = loaded.AllLayers().Where(l => l.IsAdjustment).Select(l => l.Adjustment!).ToList();
        var bw = Assert.IsType<BlackAndWhiteAdjustment>(adjustments[0]);
        Assert.Equal((70d, true, 200d, 35d), (bw.Reds, bw.Tint, bw.TintHue, bw.TintSaturation));
        var balance = Assert.IsType<ColorBalanceAdjustment>(adjustments[1]);
        Assert.Equal((-30d, 45d, false), (balance.Shadows[1], balance.Highlights[2], balance.PreserveLuminosity));
    }

    [Fact]
    public void Invert_adjustment_layer_needs_no_settings_and_inverts_what_lies_below()
    {
        var session = EditorSession.NewCanvas(4, 4, new SKColor(200, 100, 50));
        var layer = session.AddAdjustmentLayer(new InvertAdjustment());
        Assert.True(layer.IsAdjustment);
        using var flat = Composa.Rendering.DocumentRenderer.Flatten(session.Document);
        AssertColor(new SKColor(55, 155, 205), flat.GetPixel(1, 1));
    }

    [Fact]
    public void Grain_size_makes_larger_particles_even_when_rough()
    {
        static double NeighbourDifference(SKBitmap bitmap)
        {
            double total = 0;
            var count = 0;
            for (var y = 0; y < bitmap.Height; y++)
            for (var x = 1; x < bitmap.Width; x++) { total += Math.Abs(bitmap.GetPixel(x, y).Red - bitmap.GetPixel(x - 1, y).Red); count++; }
            return total / count;
        }
        using var small = Solid(64, 64, new SKColor(128, 128, 128));
        new GrainAdjustment { Amount = 70, Size = 1, Roughness = 70, Seed = 17 }.Apply(small);
        using var large = Solid(64, 64, new SKColor(128, 128, 128));
        new GrainAdjustment { Amount = 70, Size = 12, Roughness = 70, Seed = 17 }.Apply(large);
        Assert.True(NeighbourDifference(large) < NeighbourDifference(small) * 0.7, "larger grain should form visibly larger, more coherent particles");
    }
}
