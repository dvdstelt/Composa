using Composa.Editing;
using Composa.Filters;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;
using static Composa.Core.Tests.TestImages;

namespace Composa.Core.Tests;

/// <summary>Vignette, Bloom / Glow and Tonal Contrast.</summary>
public class FinishingFilterTests
{
    private static SKBitmap Apply(FilterKind kind, SKBitmap source, FilterSettings settings) => ImageFilters.Run(source, settings with { Kind = kind }).Result;

    private static bool AllOpaque(SKBitmap bitmap)
    {
        var bytes = bitmap.Bytes;
        for (var i = 3; i < bytes.Length; i += 4) if (bytes[i] != 255) return false;
        return true;
    }

    [Fact]
    public void Vignette_darkens_the_corners_while_keeping_the_center_and_the_alpha()
    {
        using var source = Solid(41, 41, new SKColor(204, 204, 204));
        using var result = Apply(FilterKind.Vignette, source, new FilterSettings { VignetteAmount = 80 });
        var corner = result.GetPixel(0, 0);
        var center = result.GetPixel(20, 20);
        Assert.True(center.Red > corner.Red + 50, $"corner {corner} should be well darker than center {center}");
        Assert.InRange(center.Red, 202, 206);
        Assert.True(AllOpaque(result));
    }

    [Fact]
    public void Vignette_blends_the_chosen_color_only_at_the_edges()
    {
        using var source = Solid(41, 41, new SKColor(128, 128, 128));
        using var result = Apply(FilterKind.Vignette, source, new FilterSettings { VignetteAmount = 100, VignetteHighlights = 0, VignetteColor = 0xFFFF0000 });
        var corner = result.GetPixel(0, 0);
        var center = result.GetPixel(20, 20);
        Assert.True(corner.Red > corner.Green + 80, $"corner {corner} should be red");
        Assert.InRange(Math.Abs(center.Red - center.Green), 0, 2);
        Assert.InRange(center.Red, 126, 130);
        Assert.True(AllOpaque(result));
        // Roundness follows the frame's corners when negative and a circle when positive: at -100 the middle of an edge
        // is as far out as a corner; at 100 a falloff starting far out reaches the corners but not the edges' middles.
        using var square = Apply(FilterKind.Vignette, source, new FilterSettings { VignetteAmount = 100, VignetteHighlights = 0, VignetteColor = 0xFFFF0000, VignetteRoundness = -100, VignetteMidpoint = 90, VignetteFeather = 5 });
        Assert.InRange(Math.Abs(square.GetPixel(20, 0).Red - square.GetPixel(0, 0).Red), 0, 3);
        Assert.True(square.GetPixel(0, 0).Red > 200);
        using var round = Apply(FilterKind.Vignette, source, new FilterSettings { VignetteAmount = 100, VignetteHighlights = 0, VignetteColor = 0xFFFF0000, VignetteRoundness = 100, VignetteMidpoint = 90, VignetteFeather = 5 });
        Assert.True(round.GetPixel(0, 0).Red > round.GetPixel(20, 0).Red + 20);
    }

    [Fact]
    public void Vignette_on_an_empty_layer_paints_across_the_canvas()
    {
        var session = EditorSession.NewCanvas(120, 80, SKColors.White);
        session.AddBlankLayer();
        session.ApplyFilter(new FilterSettings { Kind = FilterKind.Vignette, VignetteAmount = 100, VignetteFeather = 40, VignetteHighlights = 0 });
        var layer = session.ActiveLayer!;
        Assert.Equal((120, 80), (layer.Pixels!.Width, layer.Pixels.Height));
        Assert.True(layer.Pixels.GetPixel(0, 0).Alpha > 200, "the corners take the color on");
        Assert.Equal(0, layer.Pixels.GetPixel(60, 40).Alpha);             // The middle stays clear.
        var flat = session.Composite();
        Assert.True(flat.GetPixel(0, 0).Red < 60);                          // Black over white at the corner.
        AssertColor(SKColors.White, flat.GetPixel(60, 40));
        Assert.Equal("Vignette", session.History.UndoName);
        // On a layer with pixels only those pixels change and none appear.
        var photo = EditorSession.NewCanvas(120, 80, SKColors.White);
        photo.AddImageLayer("Box", Solid(40, 40, new SKColor(128, 128, 128)), new SKPoint(60, 40));
        photo.ApplyFilter(new FilterSettings { Kind = FilterKind.Vignette, VignetteAmount = 100, VignetteHighlights = 0 });
        var box = photo.ActiveLayer!.Pixels!;
        Assert.Equal((40, 40), (box.Width, box.Height));
        Assert.True(AllOpaque(box));
        Assert.True(box.GetPixel(0, 0).Red < box.GetPixel(20, 20).Red);
    }

    [Fact]
    public void Bloom_spreads_light_from_bright_pixels_and_grows_a_floating_layer()
    {
        using var source = Solid(65, 65, SKColors.Black);
        using (var canvas = new SKCanvas(source))
        using (var paint = new SKPaint { Color = SKColors.White })
            canvas.DrawRect(30, 30, 5, 5, paint);
        var settings = new FilterSettings { BloomAmount = 100, BloomRadius = 12, ClampEdges = true };
        using var result = Apply(FilterKind.BloomGlow, source, settings);
        Assert.Equal(65, result.Width);                                     // A layer filling the canvas has nothing to spread into.
        var nearLight = result.GetPixel(40, 32);
        var corner = result.GetPixel(2, 2);
        Assert.True(nearLight.Red > corner.Red && nearLight.Red > 0);
        Assert.True(AllOpaque(result));

        using var isolated = Pixels.NewColor(65, 65);
        using (var canvas = new SKCanvas(isolated))
        using (var paint = new SKPaint { Color = SKColors.White })
            canvas.DrawRect(30, 30, 5, 5, paint);
        var (spread, growX, growY) = ImageFilters.Run(isolated, settings with { Kind = FilterKind.BloomGlow, ClampEdges = false });
        Assert.True(growX > 0 && growY > 0);
        Assert.True(spread.GetPixel(40 + growX, 32 + growY).Alpha > 0, "bloom must remain visible beyond a transparent layer's bright pixels");
        spread.Dispose();
    }

    [Fact]
    public void Tonal_contrast_increases_midtone_detail_without_changing_the_alpha()
    {
        using var source = Pixels.NewColor(64, 16);
        using (var canvas = new SKCanvas(source))
            for (var stripe = 0; stripe < 8; stripe++)
            {
                var gray = (byte)(stripe % 2 == 0 ? 102 : 153);
                using var paint = new SKPaint { Color = new SKColor(gray, gray, gray) };
                canvas.DrawRect(stripe * 8, 0, 8, 16, paint);
            }
        using var result = Apply(FilterKind.TonalContrast, source, new FilterSettings { TonalAmount = 100, TonalShadows = 0, TonalMidtones = 100, TonalHighlights = 0, TonalRadius = 6 });
        Assert.True(result.GetPixel(5, 8).Red < source.GetPixel(5, 8).Red);      // The dark stripe gets darker near its edge...
        Assert.True(result.GetPixel(13, 8).Red > source.GetPixel(13, 8).Red);    // ...and the light one lighter.
        Assert.True(AllOpaque(result));
    }

    [Fact]
    public void Each_filter_commits_as_one_undo_step_and_keeps_layer_effects_and_placement()
    {
        foreach (var kind in new[] { FilterKind.Vignette, FilterKind.BloomGlow, FilterKind.TonalContrast })
        {
            var session = EditorSession.NewCanvas(48, 48);
            var layer = session.AddImageLayer("Sample", Solid(24, 24, new SKColor(180, 180, 180)), new SKPoint(24, 24));
            session.SetEffects(layer, new LayerEffects { Stroke = new StrokeEffect { Size = 3 } });
            var undoCount = 0;
            for (var probe = session; probe.CanUndo; ) { undoCount++; break; }
            var name = session.History.UndoName;
            session.ApplyFilter(new FilterSettings { Kind = kind });
            Assert.Equal(FilterSettings.DisplayName(kind), session.History.UndoName);
            Assert.NotEqual(name, session.History.UndoName);
            Assert.Equal(3, session.ActiveLayer!.Effects!.Stroke!.Size);
            // The picture stays where it was: a growing filter shifts the layer by as much as it grows.
            Assert.Equal(24, session.ActiveLayer.Bounds.MidX, 0.5);
            Assert.Equal(24, session.ActiveLayer.Bounds.MidY, 0.5);
            session.Undo();
            Assert.Equal(name, session.History.UndoName);
            _ = undoCount;
        }
        Assert.True(new FilterSettings { Kind = FilterKind.Vignette, VignetteAmount = 0 }.IsIdentity);
        Assert.True(new FilterSettings { Kind = FilterKind.TonalContrast, TonalShadows = 0, TonalMidtones = 0, TonalHighlights = 0 }.IsIdentity);
        Assert.False(new FilterSettings { Kind = FilterKind.BloomGlow }.IsIdentity);
    }
}
