using Composa.Filters;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;
using static Composa.Core.Tests.TestImages;

namespace Composa.Core.Tests;

public class RenderingTests
{
    private static Document TwoLayers(SKColor bottom, SKColor top, out Layer topLayer)
    {
        var document = new Document(8, 8);
        document.Layers.Add(Layer.Raster("bottom", Solid(8, 8, bottom)));
        topLayer = Layer.Raster("top", Solid(8, 8, top));
        document.Layers.Add(topLayer);
        return document;
    }

    [Fact]
    public void Normal_blend_with_opacity_mixes_layers()
    {
        var document = TwoLayers(SKColors.Black, SKColors.White, out var top);
        top.Opacity = 0.5;
        using var flat = DocumentRenderer.Flatten(document);
        AssertColor(new SKColor(128, 128, 128), flat.GetPixel(4, 4));
    }

    [Fact]
    public void Multiply_darkens()
    {
        var document = TwoLayers(new SKColor(200, 100, 50), new SKColor(128, 128, 128), out var top);
        top.Blend = BlendMode.Multiply;
        using var flat = DocumentRenderer.Flatten(document);
        AssertColor(new SKColor(100, 50, 25), flat.GetPixel(1, 1));
    }

    [Theory]
    [InlineData(BlendMode.LinearBurn)]
    [InlineData(BlendMode.LinearDodge)]
    [InlineData(BlendMode.VividLight)]
    [InlineData(BlendMode.LinearLight)]
    [InlineData(BlendMode.PinLight)]
    [InlineData(BlendMode.HardMix)]
    [InlineData(BlendMode.Subtract)]
    [InlineData(BlendMode.Divide)]
    public void Photoshop_only_blend_modes_follow_their_formula_and_the_layer_opacity(BlendMode mode)
    {
        var backdrop = new SKColor(200, 100, 50);
        var source = new SKColor(90, 180, 240);
        var document = TwoLayers(backdrop, source, out var top);
        top.Blend = mode;
        top.Opacity = 0.6;
        var blend = SeparableBlend.FunctionFor(mode);
        byte Expect(byte cb, byte cs) => (byte)Math.Round((cb / 255f * 0.4f + 0.6f * blend(cb / 255f, cs / 255f)) * 255);
        using var flat = DocumentRenderer.Flatten(document);
        AssertColor(new SKColor(Expect(200, 90), Expect(100, 180), Expect(50, 240)), flat.GetPixel(3, 3));
    }

    [Fact]
    public void Photoshop_only_blend_modes_give_known_results()
    {
        SKColor Blend(BlendMode mode, SKColor bottom, SKColor top)
        {
            var document = TwoLayers(bottom, top, out var layer);
            layer.Blend = mode;
            using var flat = DocumentRenderer.Flatten(document);
            return flat.GetPixel(0, 0);
        }
        AssertColor(new SKColor(200, 255, 0), Blend(BlendMode.LinearDodge, new SKColor(100, 200, 0), new SKColor(100, 100, 0)));
        AssertColor(new SKColor(100, 0, 0), Blend(BlendMode.Subtract, new SKColor(200, 100, 0), new SKColor(100, 200, 0)));
        AssertColor(new SKColor(128, 255, 0), Blend(BlendMode.Divide, new SKColor(100, 200, 0), new SKColor(200, 100, 0)));
        AssertColor(new SKColor(0, 255, 255), Blend(BlendMode.HardMix, new SKColor(100, 200, 128), new SKColor(100, 100, 128)));
        AssertColor(new SKColor(45, 0, 0), Blend(BlendMode.LinearBurn, new SKColor(200, 100, 0), new SKColor(100, 100, 255)));
    }

    [Fact]
    public void Photoshop_only_blend_modes_over_transparency_show_the_source_and_keep_soft_edges()
    {
        // Where nothing lies beneath, the layer shows as it is: Divide over an empty canvas is not white.
        var document = new Document(8, 8);
        var top = Layer.Raster("top", Solid(8, 8, new SKColor(90, 180, 240)));
        top.Blend = BlendMode.Divide;
        top.Opacity = 0.5;
        document.Layers.Add(top);
        using var flat = DocumentRenderer.Flatten(document);
        var pixel = flat.GetPixel(2, 2);
        Assert.InRange(pixel.Alpha, 127, 129);
        AssertColor(new SKColor(90, 180, 240, 128), pixel, 3);

        // A half-covered backdrop: the union of the coverages, with the blend only where both cover.
        var half = TwoLayers(new SKColor(200, 100, 50, 128), SKColors.White, out var over);
        over.Blend = BlendMode.LinearBurn;
        using var mixed = DocumentRenderer.Flatten(half);
        Assert.Equal(255, mixed.GetPixel(1, 1).Alpha);
        AssertColor(new SKColor(228, 178, 153), mixed.GetPixel(1, 1), 3); // white where the backdrop is missing, the backdrop's own color where it is
    }

    [Fact]
    public void Hidden_layers_and_hidden_folders_do_not_render()
    {
        var document = TwoLayers(SKColors.Red, SKColors.Blue, out var top);
        top.Visible = false;
        using (var flat = DocumentRenderer.Flatten(document)) AssertColor(SKColors.Red, flat.GetPixel(0, 0));

        top.Visible = true;
        var folder = Layer.Group("folder");
        document.Layers.Remove(top);
        folder.Children.Add(top);
        folder.Visible = false;
        document.Layers.Add(folder);
        using (var flat = DocumentRenderer.Flatten(document)) AssertColor(SKColors.Red, flat.GetPixel(0, 0));
    }

    [Fact]
    public void Layer_mask_hides_black_areas()
    {
        var document = TwoLayers(SKColors.Red, SKColors.Blue, out var top);
        var mask = Pixels.NewMask(8, 8, 255);
        for (var y = 0; y < 8; y++) for (var x = 0; x < 4; x++) mask.SetPixel(x, y, new SKColor(0, 0, 0, 0));
        top.Mask = mask;
        using var flat = DocumentRenderer.Flatten(document);
        AssertColor(SKColors.Red, flat.GetPixel(1, 4));
        AssertColor(SKColors.Blue, flat.GetPixel(6, 4));

        top.MaskEnabled = false;
        using var unmasked = DocumentRenderer.Flatten(document);
        AssertColor(SKColors.Blue, unmasked.GetPixel(1, 4));
    }

    [Fact]
    public void Clipped_layer_shows_only_over_its_base()
    {
        var document = new Document(8, 8);
        document.Layers.Add(Layer.Raster("background", Solid(8, 8, SKColors.White)));
        document.Layers.Add(Layer.Raster("base", Solid(4, 8, SKColors.Red)));
        var clipped = Layer.Raster("clipped", Solid(8, 8, SKColors.Blue));
        clipped.Clipped = true;
        document.Layers.Add(clipped);
        using var flat = DocumentRenderer.Flatten(document);
        AssertColor(SKColors.Blue, flat.GetPixel(2, 2));
        AssertColor(SKColors.White, flat.GetPixel(6, 2));
    }

    [Fact]
    public void Adjustment_layer_changes_everything_below_and_respects_its_mask()
    {
        var document = TwoLayers(SKColors.Black, new SKColor(10, 200, 30), out _);
        var invert = Layer.ForAdjustment(new InvertAdjustment());
        var mask = Pixels.NewMask(8, 8, 255);
        for (var y = 0; y < 8; y++) for (var x = 4; x < 8; x++) mask.SetPixel(x, y, new SKColor(0, 0, 0, 0));
        invert.Mask = mask;
        document.Layers.Add(invert);
        using var flat = DocumentRenderer.Flatten(document);
        AssertColor(new SKColor(245, 55, 225), flat.GetPixel(1, 1));
        AssertColor(new SKColor(10, 200, 30), flat.GetPixel(6, 1));
    }

    [Fact]
    public void Adjustment_layer_leaves_transparency_alone()
    {
        var document = new Document(8, 8);
        document.Layers.Add(Layer.Raster("half", Solid(8, 8, new SKColor(255, 0, 0, 128))));
        document.Layers.Add(Layer.ForAdjustment(new InvertAdjustment()) );
        document.Layers[1].Opacity = 0.5;
        using var flat = DocumentRenderer.Flatten(document);
        Assert.InRange(flat.GetPixel(3, 3).Alpha, 126, 130);
    }

    [Fact]
    public void Transformed_layer_lands_where_its_transform_says()
    {
        var document = new Document(20, 20);
        var layer = Layer.Raster("small", Solid(2, 2, SKColors.Green));
        layer.Transform = new LayerTransform { X = 10, Y = 4, Width = 8, Height = 8 };
        document.Layers.Add(layer);
        using var flat = DocumentRenderer.Flatten(document);
        Assert.Equal(0, flat.GetPixel(8, 8).Alpha);
        AssertColor(SKColors.Green, flat.GetPixel(14, 8));
        Assert.Equal(new SKRect(10, 4, 18, 12), layer.Bounds);
    }

    [Fact]
    public void Partial_render_matches_full_render()
    {
        var document = TwoLayers(SKColors.Red, SKColors.Blue, out var top);
        top.Opacity = 0.3;
        using var full = DocumentRenderer.Flatten(document);
        using var partial = Pixels.NewColor(8, 8);
        DocumentRenderer.Render(document, partial, new SKRectI(2, 2, 6, 6));
        Assert.Equal(full.GetPixel(3, 3), partial.GetPixel(3, 3));
        Assert.Equal(0, partial.GetPixel(0, 0).Alpha);
    }

    [Fact]
    public void Rect_to_quad_maps_the_corners()
    {
        SKPoint[] quad = [new(1, 2), new(11, 0), new(14, 9), new(-2, 12)];
        var matrix = Geometry.RectToQuad(10, 10, quad);
        SKPoint[] corners = [new(0, 0), new(10, 0), new(10, 10), new(0, 10)];
        for (var i = 0; i < 4; i++)
        {
            var mapped = matrix.MapPoint(corners[i]);
            Assert.Equal(quad[i].X, mapped.X, 3);
            Assert.Equal(quad[i].Y, mapped.Y, 3);
        }
    }
}

public class ViewRenderingTests
{
    [Fact]
    public void A_view_at_full_scale_matches_the_same_region_of_the_flattened_document()
    {
        var session = Editing.EditorSession.NewCanvas(300, 200, SKColors.White);
        var photo = session.AddImageLayer("photo", TestImages.Gradient(120, 90), new SKPoint(140, 100));
        photo.Transform = photo.Transform with { Rotation = 20 };
        session.AddMask(photo);
        session.AddAdjustmentLayer(new GrainAdjustment { Amount = 40, Seed = 7 });
        session.AddAdjustmentLayer(new HueSaturationAdjustment().WithShift(HueRange.Master, new HslShift(60, 10, 0)));
        session.Document.Layers[^1].Opacity = 0.6;

        using var flat = session.Flatten();
        using var view = Pixels.NewColor(100, 80);
        session.RenderView(view, new SKRectI(0, 0, 100, 80), new RenderView(1, new SKPoint(90, 60)));
        // Interior pixels are identical; along the rotated layer's antialiased edge Skia's coverage may differ by a
        // few levels because the view's translation is folded into the matrix.
        var differing = 0;
        for (var y = 0; y < 80; y++)
        for (var x = 0; x < 100; x++)
        {
            SKColor a = flat.GetPixel(x + 90, y + 60), b = view.GetPixel(x, y);
            if (Math.Abs(a.Red - b.Red) > 2 || Math.Abs(a.Green - b.Green) > 2 || Math.Abs(a.Blue - b.Blue) > 2) differing++;
        }
        Assert.True(differing < 8000 * 0.02, $"{differing} pixels differ");
    }

    [Fact]
    public void A_reduced_view_approximates_the_flattened_document_and_uses_the_pyramid()
    {
        var session = Editing.EditorSession.NewCanvas(1600, 1200, SKColors.White);
        session.AddImageLayer("photo", TestImages.Solid(800, 600, SKColors.Red), new SKPoint(800, 600));
        using var view = Pixels.NewColor(200, 150);
        session.RenderView(view, new SKRectI(0, 0, 200, 150), new RenderView(0.125f, SKPoint.Empty));
        TestImages.AssertColor(SKColors.Red, view.GetPixel(100, 75));
        TestImages.AssertColor(SKColors.White, view.GetPixel(10, 10));
        TestImages.AssertColor(SKColors.White, view.GetPixel(190, 140));

        // Re-rendering only a dirty rectangle leaves the rest of the view untouched.
        view.Erase(SKColors.Lime);
        session.RenderView(view, new SKRectI(90, 60, 110, 90), new RenderView(0.125f, SKPoint.Empty));
        TestImages.AssertColor(SKColors.Red, view.GetPixel(100, 75));
        TestImages.AssertColor(SKColors.Lime, view.GetPixel(10, 10));
    }
}

public class CompositingQaTests
{
    [Fact]
    public void A_layer_clipped_to_a_semi_transparent_base_takes_the_bases_alpha_once()
    {
        var document = new Document(20, 20);
        document.Layers.Add(Layer.Raster("base", TestImages.Solid(20, 20, new SKColor(255, 0, 0, 128))));
        var top = Layer.Raster("top", TestImages.Solid(20, 20, SKColors.Blue));
        top.Clipped = true;
        document.Layers.Add(top);
        using var flat = DocumentRenderer.Flatten(document);
        TestImages.AssertColor(new SKColor(0, 0, 255, 128), flat.GetPixel(5, 5), 3);
    }

    [Fact]
    public void A_clipped_adjustment_on_a_semi_transparent_base_keeps_its_alpha()
    {
        var document = new Document(20, 20);
        document.Layers.Add(Layer.Raster("white", TestImages.Solid(20, 20, SKColors.White)));
        document.Layers.Add(Layer.Raster("base", TestImages.Solid(20, 20, new SKColor(200, 100, 50, 128))));
        var invert = Layer.ForAdjustment(new InvertAdjustment());
        invert.Clipped = true;
        document.Layers.Add(invert);
        using var flat = DocumentRenderer.Flatten(document);
        TestImages.AssertColor(new SKColor(155, 205, 230), flat.GetPixel(5, 5), 3);
    }

    [Fact]
    public void Adjustments_inside_a_folder_keep_working_when_the_folder_has_opacity_or_a_mask()
    {
        var document = new Document(20, 20);
        document.Layers.Add(Layer.Raster("photo", TestImages.Solid(20, 20, new SKColor(200, 100, 50))));
        var folder = Layer.Group("folder");
        folder.Children.Add(Layer.ForAdjustment(new InvertAdjustment()));
        document.Layers.Add(folder);
        folder.Opacity = 0.5;
        using (var flat = DocumentRenderer.Flatten(document)) TestImages.AssertColor(new SKColor(128, 128, 128), flat.GetPixel(5, 5), 3);

        folder.Opacity = 1;
        folder.Mask = Pixels.NewMask(20, 20, 255);
        for (var y = 0; y < 20; y++) for (var x = 10; x < 20; x++) folder.Mask.SetPixel(x, y, new SKColor(0, 0, 0, 0));
        using var masked = DocumentRenderer.Flatten(document);
        TestImages.AssertColor(new SKColor(55, 155, 205), masked.GetPixel(5, 5), 3);
        TestImages.AssertColor(new SKColor(200, 100, 50), masked.GetPixel(15, 5), 3);
    }

    [Fact]
    public void A_folder_with_opacity_composites_ordinary_layers_as_before()
    {
        var document = new Document(20, 20);
        document.Layers.Add(Layer.Raster("white", TestImages.Solid(20, 20, SKColors.White)));
        var folder = Layer.Group("folder");
        folder.Children.Add(Layer.Raster("black", TestImages.Solid(10, 20, SKColors.Black)));
        folder.Opacity = 0.5;
        document.Layers.Add(folder);
        using var flat = DocumentRenderer.Flatten(document);
        TestImages.AssertColor(new SKColor(128, 128, 128), flat.GetPixel(5, 5), 3);
        TestImages.AssertColor(SKColors.White, flat.GetPixel(15, 5), 1);
    }
}
