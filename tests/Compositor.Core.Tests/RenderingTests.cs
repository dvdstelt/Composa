using Compositor.Filters;
using Compositor.Model;
using Compositor.Rendering;
using SkiaSharp;
using static Compositor.Core.Tests.TestImages;

namespace Compositor.Core.Tests;

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
