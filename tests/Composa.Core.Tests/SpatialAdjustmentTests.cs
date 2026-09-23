using Composa.Editing;
using Composa.Filters;
using Composa.IO;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;
using static Composa.Core.Tests.TestImages;

namespace Composa.Core.Tests;

/// <summary>Adjustment layers that look at their neighbours: Gaussian Blur, Motion Blur and Add Noise.</summary>
public class SpatialAdjustmentTests
{
    /// <summary>A canvas split down the middle into black and white, with a sharp edge to blur.</summary>
    private static EditorSession Halves()
    {
        var session = EditorSession.NewCanvas(240, 160, SKColors.White);
        session.AddImageLayer("Dark", Solid(120, 160, SKColors.Black), new SKPoint(60, 80));
        return session;
    }

    private static int MaxDifference(SKBitmap a, SKBitmap b)
    {
        var bytesA = a.Bytes;
        var bytesB = b.Bytes;
        var worst = 0;
        for (var i = 0; i < bytesA.Length; i++) worst = Math.Max(worst, Math.Abs(bytesA[i] - bytesB[i]));
        return worst;
    }

    [Fact]
    public void A_blur_layer_softens_what_lies_beneath_and_a_partial_render_matches_the_whole()
    {
        var session = Halves();
        session.AddAdjustmentLayer(new GaussianBlurAdjustment { Radius = 8 });
        using var whole = session.Flatten();
        var edge = whole.GetPixel(120, 80);
        Assert.InRange(edge.Red, 100, 155);                                // Mid gray where black met white.
        AssertColor(SKColors.Black, whole.GetPixel(60, 80));               // Far from the edge nothing changes.
        AssertColor(SKColors.White, whole.GetPixel(200, 80));
        AssertColor(SKColors.White, whole.GetPixel(239, 0), 3);            // Edges are continued, not faded into transparency.
        Assert.Equal(255, whole.GetPixel(239, 159).Alpha);

        // Rendered in pieces, as the canvas view and the dirty-rectangle preview do, the picture must come out the same.
        using var pieces = Pixels.NewColor(240, 160);
        DocumentRenderer.Render(session.Document, pieces, new SKRectI(0, 0, 240, 70));
        DocumentRenderer.Render(session.Document, pieces, new SKRectI(0, 70, 130, 160));
        DocumentRenderer.Render(session.Document, pieces, new SKRectI(130, 70, 240, 160));
        Assert.InRange(MaxDifference(whole, pieces), 0, 2);

        // At half scale the blur covers the same amount of picture.
        using var reduced = Pixels.NewColor(120, 80);
        DocumentRenderer.Render(session.Document, reduced, new SKRectI(0, 0, 120, 80), new RenderView(0.5f, SKPoint.Empty));
        Assert.InRange(reduced.GetPixel(60, 40).Red, 100, 155);
        AssertColor(SKColors.Black, reduced.GetPixel(30, 40), 3);
    }

    [Fact]
    public void Motion_blur_smears_along_its_angle_only()
    {
        var session = Halves();
        session.AddAdjustmentLayer(new MotionBlurAdjustment { Angle = 0, Distance = 40 });
        using var flat = session.Flatten();
        Assert.InRange(flat.GetPixel(120, 80).Red, 90, 165);               // Across the vertical edge: smeared.
        Assert.True(flat.GetPixel(135, 80).Red < 250 && flat.GetPixel(105, 80).Red > 5);
        session.Undo();
        session.AddAdjustmentLayer(new MotionBlurAdjustment { Angle = 90, Distance = 40 });
        using var vertical = session.Flatten();
        AssertColor(SKColors.White, vertical.GetPixel(121, 80));           // Along the edge: nothing to smear.
        AssertColor(SKColors.Black, vertical.GetPixel(118, 80));
    }

    [Fact]
    public void Noise_is_fixed_to_the_document_and_keeps_the_layer_sizes()
    {
        var session = EditorSession.NewCanvas(120, 80, new SKColor(128, 128, 128));
        session.AddAdjustmentLayer(new AddNoiseAdjustment { Amount = 40, Monochromatic = true, Seed = 7 });
        using var whole = session.Flatten();
        var a = whole.GetPixel(10, 10);
        var b = whole.GetPixel(11, 10);
        Assert.True(a.Red == a.Green && a.Green == a.Blue, "monochromatic noise moves every channel together");
        Assert.NotEqual(a, b);
        using var pieces = Pixels.NewColor(120, 80);
        DocumentRenderer.Render(session.Document, pieces, new SKRectI(0, 0, 60, 80));
        DocumentRenderer.Render(session.Document, pieces, new SKRectI(60, 0, 120, 80));
        Assert.Equal(0, MaxDifference(whole, pieces));                     // The same grain whichever piece is drawn.
        session.Undo();
        session.AddAdjustmentLayer(new AddNoiseAdjustment { Amount = 40, Gaussian = true, Seed = 7 });
        using var color = session.Flatten();
        var c = color.GetPixel(10, 10);
        Assert.False(c.Red == c.Green && c.Green == c.Blue, "color noise moves the channels apart");
        Assert.Equal(0, DocumentRenderer.SamplingMargin(session.Document));  // Noise needs no halo.
    }

    [Fact]
    public void The_new_layers_round_trip_through_project_files_and_report_their_margins()
    {
        var session = Halves();
        session.AddAdjustmentLayer(new GaussianBlurAdjustment { Radius = 6 });
        session.AddAdjustmentLayer(new MotionBlurAdjustment { Angle = 30, Distance = 24 });
        session.AddAdjustmentLayer(new AddNoiseAdjustment { Amount = 12.5, Gaussian = true, Monochromatic = true, Seed = 99 });
        Assert.Equal(6 * 3 + 2, DocumentRenderer.SamplingMargin(session.Document));
        using var stream = new MemoryStream();
        ProjectFile.Write(session.Document, stream);
        stream.Position = 0;
        var loaded = ProjectFile.Read(stream);
        var adjustments = loaded.Layers.Skip(2).Select(l => l.Adjustment!).ToList();
        Assert.Equal(6, Assert.IsType<GaussianBlurAdjustment>(adjustments[0]).Radius);
        Assert.Equal((30d, 24d), (Assert.IsType<MotionBlurAdjustment>(adjustments[1]).Angle, ((MotionBlurAdjustment)adjustments[1]).Distance));
        var noise = Assert.IsType<AddNoiseAdjustment>(adjustments[2]);
        Assert.Equal((12.5, true, true, 99u), (noise.Amount, noise.Gaussian, noise.Monochromatic, noise.Seed));
        using var expected = DocumentRenderer.Flatten(session.Document);
        using var actual = DocumentRenderer.Flatten(loaded);
        Assert.Equal(expected.Bytes, actual.Bytes);
        // A change under the blur is felt as far as the blur reaches.
        var dark = session.Document.Layers[1];
        Assert.True(session.AffectedArea(dark).Left <= dark.Bounds.Left - 20);
    }
}
