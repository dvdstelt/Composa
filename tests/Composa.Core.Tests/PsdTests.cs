using Composa.Editing;
using Composa.Filters;
using Composa.IO.Psd;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;
using static Composa.Core.Tests.TestImages;

namespace Composa.Core.Tests;

public class PsdTests
{
    private static PsdImport Load(PsdWriter writer) => PsdImport.Load(writer.Build());

    [Theory]
    [InlineData("lbrn", BlendMode.LinearBurn)]
    [InlineData("lddg", BlendMode.LinearDodge)]
    [InlineData("vLit", BlendMode.VividLight)]
    [InlineData("lLit", BlendMode.LinearLight)]
    [InlineData("pLit", BlendMode.PinLight)]
    [InlineData("hMix", BlendMode.HardMix)]
    [InlineData("fsub", BlendMode.Subtract)]
    [InlineData("fdiv", BlendMode.Divide)]
    [InlineData("hLit", BlendMode.HardLight)]
    public void Photoshops_remaining_blend_modes_keep_their_mode(string key, BlendMode expected)
    {
        var writer = new PsdWriter { Width = 10, Height = 10 };
        writer.Layers.Add(new PsdWriterLayer { Name = "Background", Image = Solid(10, 10, SKColors.Red) });
        writer.Layers.Add(new PsdWriterLayer { Name = "Blended", Image = Solid(10, 10, SKColors.Blue), Blend = key });
        var import = Load(writer);
        Assert.Equal(expected, import.Layers[1].Blend);
        Assert.Empty(import.Conversions);
    }

    [Fact]
    public void Layers_folders_names_visibility_opacity_and_blend_modes_come_across()
    {
        var writer = new PsdWriter { Width = 100, Height = 80, Resolution = 300 };
        writer.Layers.Add(new PsdWriterLayer { Name = "Background", Image = Solid(100, 80, SKColors.Red) });
        writer.Layers.Add(new PsdWriterLayer { Name = "</Layer group>", EmptyWidth = 0 }.With("lsct", PsdWriter.Section(3)));
        writer.Layers.Add(new PsdWriterLayer { Name = "Child", Image = Solid(20, 20, SKColors.Blue), Left = 10, Top = 10, Opacity = 128, Blend = "mul " }.With("iOpa", PsdWriter.FillOpacity(128)));
        writer.Layers.Add(new PsdWriterLayer { Name = "Folder", Opacity = 200, Blend = "pass" }.With("lsct", PsdWriter.Section(1)));
        writer.Layers.Add(new PsdWriterLayer { Name = "Old name", Image = Solid(5, 5, SKColors.Green), Left = 50, Top = 50, Hidden = true, Blend = "diss" }.With("luni", PsdWriter.Unicode("Ünïcode ✓")));

        var import = Load(writer);
        Assert.Equal((100, 80, 300d), (import.Width, import.Height, import.Resolution));
        Assert.Equal(["Background", "Folder", "Ünïcode ✓"], import.Layers.Select(l => l.Name));
        var folder = import.Layers[1];
        Assert.True(folder.IsGroup);
        Assert.Equal(200 / 255.0, folder.Opacity, 3);
        var child = Assert.Single(folder.Children);
        Assert.Equal("Child", child.Name);
        Assert.Equal(0.25, child.Opacity, 2);                              // Opacity and fill multiply.
        Assert.Equal(BlendMode.Multiply, child.Blend);
        Assert.Equal((10d, 10d, 20d, 20d), (child.Transform.X, child.Transform.Y, child.Transform.Width, child.Transform.Height));
        AssertColor(SKColors.Blue, child.Pixels!.GetPixel(0, 0));
        var top = import.Layers[2];
        Assert.False(top.Visible);
        Assert.Equal(BlendMode.Normal, top.Blend);
        var conversion = Assert.Single(import.Conversions);
        Assert.Equal("Ünïcode ✓", conversion.LayerName);
        Assert.Contains("diss", conversion.Message);

        var document = import.ToDocument();
        Assert.Equal(top.Id, document.ActiveLayerId);
        using var flat = DocumentRenderer.Flatten(document);
        AssertColor(SKColors.Red, flat.GetPixel(90, 70));
        var blended = flat.GetPixel(15, 15);                                 // Blue multiplied over red at a quarter of 78%: darker red.
        Assert.True(blended.Red < 255 && blended.Red > 150 && blended.Blue == 0, blended.ToString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Every_channel_compression_decodes_to_the_same_pixels(int compression)
    {
        using var gradient = Gradient(37, 23);
        var writer = new PsdWriter { Width = 40, Height = 30, Compression = compression };
        writer.Layers.Add(new PsdWriterLayer { Name = "Picture", Image = gradient, Left = 2, Top = 3 });
        var layer = Assert.Single(Load(writer).Layers);
        Assert.Equal(gradient.Bytes, layer.Pixels!.Bytes);
    }

    [Fact]
    public void Masks_sit_where_photoshop_put_them_with_the_default_value_outside()
    {
        var writer = new PsdWriter { Width = 100, Height = 80 };
        using var hole = Pixels.NewMask(10, 10);
        writer.Layers.Add(new PsdWriterLayer { Name = "Masked", Image = Solid(40, 40, SKColors.Red), Left = 10, Top = 10, Mask = hole, MaskLeft = 20, MaskTop = 20, MaskDefault = 255 });
        writer.Layers.Add(new PsdWriterLayer { Name = "Off", Image = Solid(10, 10, SKColors.Red), Mask = Pixels.NewMask(10, 10, 255), MaskDisabled = true, MaskUnlinked = true });
        writer.Layers.Add(new PsdWriterLayer { Name = "</Layer group>" }.With("lsct", PsdWriter.Section(3)));
        writer.Layers.Add(new PsdWriterLayer { Name = "Inside", Image = Solid(10, 10, SKColors.Blue) });
        writer.Layers.Add(new PsdWriterLayer { Name = "Folder", Mask = Pixels.NewMask(30, 30), MaskLeft = 5, MaskTop = 5, MaskDefault = 255 }.With("lsct", PsdWriter.Section(1)));
        writer.Layers.Add(new PsdWriterLayer { Name = "Rendered", Image = Solid(10, 10, SKColors.Red), Mask = Pixels.NewMask(10, 10), MaskFromRender = true });

        var import = Load(writer);
        var masked = import.Layers[0];
        Assert.Equal((40, 40), (masked.Mask!.Width, masked.Mask.Height));  // The mask shares the layer's pixel grid.
        Assert.Equal(255, masked.Mask.GetPixel(5, 5).Alpha);                // Outside the mask's own rectangle: the default.
        Assert.Equal(0, masked.Mask.GetPixel(15, 15).Alpha);                // Inside it: the plane, placed at (20,20) - (10,10).
        Assert.True(masked.MaskEnabled);
        var off = import.Layers[1];
        Assert.False(off.MaskEnabled);
        Assert.Contains(import.Conversions, c => c.LayerName == "Off" && c.Message.Contains("unlinked"));
        var folder = import.Layers[2];
        Assert.Equal((100, 80), (folder.Mask!.Width, folder.Mask.Height));  // Folder masks share the document's grid.
        Assert.Equal(0, folder.Mask.GetPixel(10, 10).Alpha);
        Assert.Equal(255, folder.Mask.GetPixel(50, 50).Alpha);
        Assert.Null(import.Layers[3].Mask);                                 // A mask rendered from other data is not the user's.
    }

    [Fact]
    public void Clipping_follows_the_sibling_below_and_is_skipped_over_unsupported_bases()
    {
        var writer = new PsdWriter { Width = 50, Height = 50 };
        writer.Layers.Add(new PsdWriterLayer { Name = "Base", Image = Solid(20, 20, SKColors.Red) });
        writer.Layers.Add(new PsdWriterLayer { Name = "Clipped", Image = Solid(50, 50, SKColors.Blue), Clipping = true });
        writer.Layers.Add(new PsdWriterLayer { Name = "Also clipped", Image = Solid(50, 50, SKColors.Green), Clipping = true });
        writer.Layers.Add(new PsdWriterLayer { Name = "</Layer group>" }.With("lsct", PsdWriter.Section(3)));
        writer.Layers.Add(new PsdWriterLayer { Name = "Inside", Image = Solid(10, 10, SKColors.Blue) });
        writer.Layers.Add(new PsdWriterLayer { Name = "Folder" }.With("lsct", PsdWriter.Section(1)));
        writer.Layers.Add(new PsdWriterLayer { Name = "Over folder", Image = Solid(10, 10, SKColors.Blue), Clipping = true });

        var import = Load(writer);
        Assert.True(import.Layers[1].Clipped);
        Assert.True(import.Layers[2].Clipped);
        Assert.False(import.Layers[4].Clipped);
        Assert.Contains(import.Conversions, c => c.LayerName == "Over folder" && c.Message.Contains("clipping was skipped"));
        using var flat = DocumentRenderer.Flatten(import.ToDocument());
        AssertColor(SKColors.Green, flat.GetPixel(10, 10));                 // Clipped to the base.
        Assert.Equal(0, flat.GetPixel(40, 40).Alpha);                       // Nothing outside it.
    }

    [Fact]
    public void Text_smart_objects_and_effects_become_pixels_with_a_report()
    {
        var writer = new PsdWriter { Width = 50, Height = 50 };
        writer.Layers.Add(new PsdWriterLayer { Name = "Headline", Image = Solid(30, 10, SKColors.Black), Left = 5, Top = 5 }.With("TySh", new byte[16]));
        writer.Layers.Add(new PsdWriterLayer { Name = "Logo", Image = Solid(10, 10, SKColors.Red) }.With("SoLd", new byte[16]));
        writer.Layers.Add(new PsdWriterLayer { Name = "Glowing", Image = Solid(10, 10, SKColors.Red), Opacity = 255 }.With("lfx2", new byte[16]).With("iOpa", PsdWriter.FillOpacity(128)));
        writer.Layers.Add(new PsdWriterLayer { Name = "Pattern" }.With("PtFl", new byte[16]));
        var import = Load(writer);
        Assert.Equal(4, import.Layers.Count);
        Assert.Null(import.Layers[0].Text);
        AssertColor(SKColors.Black, import.Layers[0].Pixels!.GetPixel(0, 0));
        Assert.Contains(import.Conversions, c => c.LayerName == "Headline" && c.Message.Contains("retyped"));
        Assert.Contains(import.Conversions, c => c.LayerName == "Logo" && c.Message.Contains("smart object"));
        Assert.Contains(import.Conversions, c => c.LayerName == "Glowing" && c.Message.Contains("effects"));
        Assert.Equal(1, import.Layers[2].Opacity);                          // Fill opacity belongs to the effects that were dropped.
        Assert.Contains(import.Conversions, c => c.LayerName == "Pattern" && c.Message.Contains("imported empty"));
        Assert.Equal((50, 50), (import.Layers[3].Pixels!.Width, import.Layers[3].Pixels.Height));
    }

    [Fact]
    public void Solid_fill_rectangles_and_ellipses_stay_live_shapes()
    {
        var writer = new PsdWriter { Width = 100, Height = 80 };
        writer.Layers.Add(new PsdWriterLayer { Name = "Rectangle", Image = Solid(50, 30, SKColors.Red), Left = 10, Top = 10 }
            .With("SoCo", PsdWriter.SolidColor(SKColors.Red)).With("vogk", PsdWriter.Origination(1, new SKRect(10, 10, 60, 40))).With("vmsk", PsdWriter.VectorMask(100, 80, new SKPoint(10, 10), new SKPoint(60, 10), new SKPoint(60, 40), new SKPoint(10, 40))));
        writer.Layers.Add(new PsdWriterLayer { Name = "Ellipse", Image = Solid(20, 20, SKColors.Blue), Left = 70, Top = 50 }
            .With("SoCo", PsdWriter.SolidColor(SKColors.Blue)).With("vogk", PsdWriter.Origination(5, new SKRect(70, 50, 90, 70)))
            .With("vstk", PsdWriter.StrokeSettings(fill: true, stroke: true, 3, SKColors.Black)));
        writer.Layers.Add(new PsdWriterLayer { Name = "Rounded", Image = Solid(40, 20, SKColors.Green) }
            .With("SoCo", PsdWriter.SolidColor(SKColors.Green)).With("vogk", PsdWriter.Origination(2, new SKRect(0, 0, 40, 20), radius: 6, radiiFirst: true)));
        writer.Layers.Add(new PsdWriterLayer { Name = "Corners" }
            .With("SoCo", PsdWriter.SolidColor(SKColors.Yellow)).With("vmsk", PsdWriter.VectorMask(100, 80, new SKPoint(20, 20), new SKPoint(40, 20), new SKPoint(40, 60), new SKPoint(20, 60))));
        writer.Layers.Add(new PsdWriterLayer { Name = "Color Fill" }.With("SoCo", PsdWriter.SolidColor(SKColors.Magenta)));

        var import = Load(writer);
        var rectangle = import.Layers[0];
        Assert.Equal(new ShapeStyle(ShapeKind.Rectangle, 0xFFFF0000, 0), rectangle.Shape);
        Assert.Equal((10d, 10d, 50d, 30d), (rectangle.Transform.X, rectangle.Transform.Y, rectangle.Transform.Width, rectangle.Transform.Height));
        AssertColor(SKColors.Red, rectangle.Pixels!.GetPixel(25, 15));
        var ellipse = import.Layers[1];
        Assert.Equal(ShapeKind.Ellipse, ellipse.Shape!.Kind);
        Assert.Equal(0xFF0000FFu, ellipse.Shape.Fill);
        Assert.Equal(0, ellipse.Pixels!.GetPixel(0, 0).Alpha);              // An ellipse leaves its corners clear.
        Assert.Contains(import.Conversions, c => c.LayerName == "Ellipse" && c.Message.Contains("stroke"));
        var rounded = import.Layers[2];
        Assert.Equal(ShapeKind.RoundedRectangle, rounded.Shape!.Kind);
        Assert.Equal(6, rounded.Shape.CornerRadius);
        var corners = import.Layers[3];                                     // Four sharp corners without an origination record.
        Assert.Equal(ShapeKind.Rectangle, corners.Shape!.Kind);
        Assert.Equal((20d, 20d, 20d, 40d), (corners.Transform.X, corners.Transform.Y, corners.Transform.Width, corners.Transform.Height));
        var fill = import.Layers[4];                                        // A fill with no path covers the canvas.
        Assert.Equal(ShapeKind.Rectangle, fill.Shape!.Kind);
        Assert.Equal((100d, 80d), (fill.Transform.Width, fill.Transform.Height));
        AssertColor(SKColors.Magenta, fill.Pixels!.GetPixel(50, 40));
        Assert.DoesNotContain(import.Conversions, c => c.LayerName is "Rectangle" or "Rounded" or "Corners" or "Color Fill");
    }

    [Fact]
    public void Other_paths_are_drawn_into_pixels_once()
    {
        var writer = new PsdWriter { Width = 100, Height = 100 };
        writer.Layers.Add(new PsdWriterLayer { Name = "Triangle" }
            .With("SoCo", PsdWriter.SolidColor(SKColors.Green)).With("vmsk", PsdWriter.VectorMask(100, 100, new SKPoint(10, 90), new SKPoint(50, 10), new SKPoint(90, 90))));
        writer.Layers.Add(new PsdWriterLayer { Name = "Outline" }
            .With("vmsk", PsdWriter.VectorMask(100, 100, new SKPoint(10, 10), new SKPoint(60, 10), new SKPoint(60, 60), new SKPoint(10, 60)))
            .With("vstk", PsdWriter.StrokeSettings(fill: false, stroke: true, 4, SKColors.Blue)));
        var import = Load(writer);
        var triangle = import.Layers[0];
        Assert.Null(triangle.Shape);
        Assert.Contains(import.Conversions, c => c.LayerName == "Triangle" && c.Message.Contains("rasterized"));
        var pixels = triangle.Pixels!;
        Assert.Equal((10d, 10d), (triangle.Transform.X, triangle.Transform.Y));
        AssertColor(SKColors.Green, pixels.GetPixel(40, 70));               // Inside the triangle.
        Assert.Equal(0, pixels.GetPixel(2, 2).Alpha);                       // Its top-left corner is empty.
        var outline = import.Layers[1];
        Assert.Null(outline.Shape);                                          // No fill, so not a live shape: a stroked path instead.
        using var flat = DocumentRenderer.Flatten(import.ToDocument());
        AssertColor(SKColors.Blue, flat.GetPixel(10, 35));
        Assert.Equal(0, flat.GetPixel(35, 35).Alpha);
    }

    [Fact]
    public void Adjustment_layers_map_onto_their_equivalents_or_are_skipped()
    {
        var writer = new PsdWriter { Width = 20, Height = 20 };
        writer.Layers.Add(new PsdWriterLayer { Name = "Photo", Image = Solid(20, 20, SKColors.Gray) });
        writer.Layers.Add(new PsdWriterLayer { Name = "Levels" }.With("levl", PsdWriter.Levels([(10, 240, 0, 255, 150), (0, 255, 0, 255, 100), (0, 255, 0, 255, 100), (0, 255, 0, 255, 100)])));
        writer.Layers.Add(new PsdWriterLayer { Name = "Curves" }.With("curv", PsdWriter.Curves((0, [(0, 0), (128, 160), (255, 255)]), (2, [(0, 10), (255, 255)]))));
        writer.Layers.Add(new PsdWriterLayer { Name = "Hue" }.With("hue2", PsdWriter.HueSaturation(false, (0, 0, 0), (10, 20, -5), (30, 0, 0))));
        writer.Layers.Add(new PsdWriterLayer { Name = "Tint" }.With("hue2", PsdWriter.HueSaturation(true, (200, 40, 0), (0, 0, 0))));
        writer.Layers.Add(new PsdWriterLayer { Name = "Invert" }.With("nvrt", []));
        writer.Layers.Add(new PsdWriterLayer { Name = "Bright" }.With("brit", PsdWriter.BrightnessContrast(30, -20)));
        writer.Layers.Add(new PsdWriterLayer { Name = "Exposure" }.With("expA", PsdWriter.Exposure(1.5f, 0.1f, 0.8f)));
        writer.Layers.Add(new PsdWriterLayer { Name = "Balance" }.With("blnc", PsdWriter.ColorBalance([(10, 0, -20), (0, 5, 0), (-30, 0, 40)], false)));
        writer.Layers.Add(new PsdWriterLayer { Name = "Mono" }.With("blwh", PsdWriter.BlackWhite(70, 60, 40, 60, 20, 80)));
        writer.Layers.Add(new PsdWriterLayer { Name = "Sepia" }.With("blwh", PsdWriter.BlackWhite(40, 60, 40, 60, 20, 80, new SKColor(255, 128, 0))));
        writer.Layers.Add(new PsdWriterLayer { Name = "Threshold" }.With("thrs", new byte[2]));

        var import = Load(writer);
        Assert.Equal(["Photo", "Levels", "Curves", "Hue", "Tint", "Invert", "Bright", "Exposure", "Balance", "Mono", "Sepia"], import.Layers.Select(l => l.Name));
        var levels = Assert.IsType<LevelsAdjustment>(import.Layers[1].Adjustment);
        Assert.Equal((10d, 240d, 1.5), (levels.Ranges[0].InputBlack, levels.Ranges[0].InputWhite, levels.Ranges[0].Gamma));
        var curves = Assert.IsType<CurvesAdjustment>(import.Layers[2].Adjustment);
        Assert.Equal([new CurvePoint(0, 0), new CurvePoint(128, 160), new CurvePoint(255, 255)], curves.Channels[0]);
        Assert.Equal(new CurvePoint(0, 10), curves.Channels[2][0]);
        var hue = Assert.IsType<HueSaturationAdjustment>(import.Layers[3].Adjustment);
        Assert.Equal(new HslShift(10, 20, -5), hue.Shifts[(int)HueRange.Master]);
        Assert.Equal(new HslShift(30, 0, 0), hue.Shifts[(int)HueRange.Reds]);
        var tint = Assert.IsType<HueSaturationAdjustment>(import.Layers[4].Adjustment);
        Assert.True(tint.Colorize);
        Assert.Equal(new HslShift(180, 40, 0), tint.Shifts[(int)HueRange.Master]);
        Assert.IsType<InvertAdjustment>(import.Layers[5].Adjustment);
        var bright = Assert.IsType<BrightnessContrastAdjustment>(import.Layers[6].Adjustment);
        Assert.Equal((20d, -20d), (bright.Brightness, bright.Contrast));
        var exposure = Assert.IsType<ExposureAdjustment>(import.Layers[7].Adjustment);
        Assert.Equal(1.5, exposure.Exposure, 3);
        var balance = Assert.IsType<ColorBalanceAdjustment>(import.Layers[8].Adjustment);
        Assert.Equal([10d, 0d, -20d], balance.Shadows);
        Assert.Equal([0d, 5d, 0d], balance.Midtones);
        Assert.Equal([-30d, 0d, 40d], balance.Highlights);
        Assert.False(balance.PreserveLuminosity);
        var mono = Assert.IsType<BlackAndWhiteAdjustment>(import.Layers[9].Adjustment);
        Assert.Equal((70d, 80d, false), (mono.Reds, mono.Magentas, mono.Tint));
        var sepia = Assert.IsType<BlackAndWhiteAdjustment>(import.Layers[10].Adjustment);
        Assert.True(sepia.Tint);
        Assert.Equal(30, sepia.TintHue, 0);
        Assert.Equal(100, sepia.TintSaturation);
        Assert.DoesNotContain(import.Conversions, c => c.LayerName is "Balance" or "Mono");
        Assert.Contains(import.Conversions, c => c.LayerName == "Sepia" && c.Message.Contains("may not match"));
        Assert.Contains(import.Conversions, c => c.LayerName == "Threshold" && c.Message.Contains("skipped"));
        Assert.Contains(import.Conversions, c => c.LayerName == "Levels" && c.Message.Contains("may not match"));
        Assert.DoesNotContain(import.Conversions, c => c.LayerName == "Invert");
    }

    [Fact]
    public void Unsupported_and_damaged_files_are_refused_with_a_reason()
    {
        byte[] Plain() { var w = new PsdWriter { Width = 10, Height = 10 }; w.Layers.Add(new PsdWriterLayer { Image = Solid(10, 10, SKColors.Red) }); return w.Build(); }
        Assert.Contains("psb", Assert.Throws<PsdException>(() => PsdImport.Load(new PsdWriter { Version = 2 }.Build())).Message);
        Assert.Contains("8-bit RGB", Assert.Throws<PsdException>(() => PsdImport.Load(new PsdWriter { Mode = 4 }.Build())).Message);
        Assert.Contains("8-bit RGB", Assert.Throws<PsdException>(() => PsdImport.Load(new PsdWriter { Depth = 16 }.Build())).Message);
        Assert.Contains("larger", Assert.Throws<PsdException>(() => PsdImport.Load(new PsdWriter { Width = 40_000, Height = 10 }.Build())).Message);
        Assert.Contains("larger", Assert.Throws<PsdException>(() => PsdImport.Load(Plain(), pixelBudget: 50)).Message);
        Assert.Contains("not a Photoshop", Assert.Throws<PsdException>(() => PsdImport.Load("hello world, not a psd"u8.ToArray())).Message);
        Assert.Throws<PsdException>(() => PsdImport.Load(Plain()[..(Plain().Length / 2)]));      // Cut through the layer data.
        Assert.False(PsdImport.IsPsd("PNG\r\n"u8.ToArray()));
        Assert.True(PsdImport.IsPsd(Plain()));
    }

    [Fact]
    public void A_flattened_file_becomes_one_layer_from_the_merged_image()
    {
        using var gradient = Gradient(30, 20);
        foreach (var compression in new[] { 0, 1 })
        {
            var writer = new PsdWriter { Width = 30, Height = 20, Composite = gradient, Compression = compression };
            var import = Load(writer);
            var layer = Assert.Single(import.Layers);
            Assert.Equal("Background", layer.Name);
            Assert.Equal(gradient.Bytes, layer.Pixels!.Bytes);
            Assert.Empty(import.Conversions);
        }
    }

    [Fact]
    public void Opening_makes_a_document_and_placing_wraps_the_layers_in_a_folder_as_one_step()
    {
        var writer = new PsdWriter { Width = 60, Height = 40 };
        writer.Layers.Add(new PsdWriterLayer { Name = "Back", Image = Solid(60, 40, SKColors.Red) });
        writer.Layers.Add(new PsdWriterLayer { Name = "Dot", Image = Solid(10, 10, SKColors.Blue), Left = 25, Top = 15 });
        var bytes = writer.Build();

        var opened = EditorSession.OpenPhotoshop(PsdImport.Load(bytes), "poster");
        Assert.Equal("poster", opened.Title);
        Assert.Equal((60, 40), (opened.Document.Width, opened.Document.Height));
        Assert.Equal("Dot", opened.ActiveLayer!.Name);
        Assert.False(opened.IsModified);

        var session = EditorSession.NewCanvas(200, 200, SKColors.White);
        var folder = session.PlacePhotoshop(PsdImport.Load(bytes), "poster", new SKPoint(150, 150));
        Assert.Equal("Import Photoshop File", session.History.UndoName);
        Assert.Equal(folder.Id, session.ActiveLayer!.Id);
        Assert.Equal(["Back", "Dot"], folder.Children.Select(l => l.Name));
        Assert.Equal((120d, 130d), (folder.Children[0].Transform.X, folder.Children[0].Transform.Y));   // Centered on the drop point.
        Assert.Equal((145d, 145d), (folder.Children[1].Transform.X, folder.Children[1].Transform.Y));
        AssertColor(SKColors.Blue, session.Composite().GetPixel(150, 150));
        session.Undo();
        Assert.Single(session.Document.Layers);
        AssertColor(SKColors.White, session.Composite().GetPixel(150, 150));

        var cancelled = PsdImport.Load(bytes);
        cancelled.Discard();
        Assert.Empty(cancelled.Layers);
    }
}
