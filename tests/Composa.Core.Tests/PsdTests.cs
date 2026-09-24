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

    private static PsdImport LoadText(PsdWriter.TypeTool type, int width = 300, int height = 200)
    {
        var writer = new PsdWriter { Width = width, Height = height };
        writer.Layers.Add(new PsdWriterLayer { Name = "Headline", Image = Solid(30, 10, SKColors.Black), Left = 40, Top = 30 }.With("TySh", type.Build()));
        return PsdImport.Load(writer.Build());
    }

    [Fact]
    public void Simple_photoshop_text_stays_editable_with_its_style_and_place()
    {
        var installed = EditorSession.FontFamilies.FirstOrDefault(f => f.Contains("Sans", StringComparison.OrdinalIgnoreCase)) ?? EditorSession.FontFamilies[0];
        var import = LoadText(new PsdWriter.TypeTool
        {
            Text = "Hello\rworld", Font = installed.Replace(" ", "") + "-BoldItalic", FontSize = 24, Color = new SKColor(20, 40, 200),
            Justification = 1, Tracking = 100, Leading = 30, Tx = 40, Ty = 50
        });
        var layer = Assert.Single(import.Layers);
        var text = layer.Text;
        Assert.NotNull(text);
        Assert.Equal("Hello\nworld", text!.Text);                           // Photoshop's return becomes a line break.
        Assert.Equal(installed, text.FontFamily);
        Assert.True(text.Bold && text.Italic);
        Assert.Equal(24, text.Size);
        Assert.Equal(0xFF1428C8u, text.Color);
        Assert.Equal(TextAlignment.Right, text.Alignment);
        Assert.Equal(2.4, text.Tracking, 3);                               // 100/1000 em at 24 px.
        Assert.Equal(30, text.Leading);
        Assert.DoesNotContain(import.Conversions, c => c.Message.Contains("retyped") || c.Message.Contains("installed"));
        // Right-aligned point text hangs from its first baseline at (tx, ty): the first line ends there.
        var layout = new Composa.Text.TextLayout(text);
        var first = layout.Lines[0];
        Assert.Equal(40, layer.Transform.X + first.X + first.VisibleWidth, 0.5);
        Assert.Equal(50, layer.Transform.Y + first.Baseline, 0.5);
        Assert.Equal((0d, false), (layer.Transform.Rotation, layer.Transform.FlipVertical));
        // The glyphs are drawn in the text's color: some pixel along the first line's middle is a solid blue.
        var band = Enumerable.Range((int)first.X, (int)first.VisibleWidth).Select(x => layer.Pixels!.GetPixel(x, (int)(first.Baseline - 6)));
        Assert.Contains(band, p => p.Alpha > 200 && p.Blue > 150 && p.Red < 60);
    }

    [Fact]
    public void Text_the_model_cannot_hold_stays_pixels_and_the_rest_is_reported()
    {
        foreach (var type in new[] { new PsdWriter.TypeTool { Vertical = true }, new PsdWriter.TypeTool { Xy = 0.4 }, new PsdWriter.TypeTool { Xx = 2, Yy = 1 } })
        {
            var import = LoadText(type);
            var layer = Assert.Single(import.Layers);
            Assert.Null(layer.Text);
            AssertColor(SKColors.Black, layer.Pixels!.GetPixel(0, 0));
            Assert.Contains(import.Conversions, c => c.Message.Contains("retyped"));
        }
        var noted = LoadText(new PsdWriter.TypeTool { Warp = true, FauxBold = true, Justification = 3, SecondSize = 40, Font = "NoSuchFace-Regular" });
        Assert.NotNull(Assert.Single(noted.Layers).Text);
        var messages = noted.Conversions.Select(c => c.Message).ToList();
        Assert.Contains(PsdText.WarpNote, messages);
        Assert.Contains(PsdText.FauxNote, messages);
        Assert.Contains(PsdText.JustifyNote, messages);
        Assert.Contains(PsdText.FirstStyleNote, messages);
        Assert.Contains(messages, m => m.Contains("NoSuchFace") && m.Contains("installed"));
        Assert.Equal(TextAlignment.Left, noted.Layers[0].Text!.Alignment);
        // Without engine data the words still come across, at 12 points.
        var bare = LoadText(new PsdWriter.TypeTool { Text = "Plain", NoEngine = true });
        Assert.Equal(("Plain", 12d), (bare.Layers[0].Text!.Text, bare.Layers[0].Text!.Size));
    }

    [Fact]
    public void Scaled_rotated_and_boxed_photoshop_text_keeps_its_placement()
    {
        // A 30° turn at twice the size: cos 30 = 0.866, sin 30 = 0.5, scale 2.
        double cos = Math.Cos(Math.PI / 6) * 2, sin = Math.Sin(Math.PI / 6) * 2;
        var turned = LoadText(new PsdWriter.TypeTool { FontSize = 24, Xx = cos, Xy = -sin, Yx = sin, Yy = cos, Tx = 100, Ty = 80 });
        var layer = Assert.Single(turned.Layers);
        Assert.Equal(48, layer.Text!.Size);                                 // Engine points times the matrix scale.
        Assert.Equal(30, layer.Transform.Rotation, 1);
        // The baseline start lands on (tx, ty) after the turn.
        var layout = new Composa.Text.TextLayout(layer.Text);
        var anchor = layer.Matrix.MapPoint(layout.Lines[0].X, layout.Lines[0].Baseline);
        Assert.Equal(100, anchor.X, 0.6);
        Assert.Equal(80, anchor.Y, 0.6);

        var flipped = LoadText(new PsdWriter.TypeTool { Yy = -1 });
        Assert.True(Assert.Single(flipped.Layers).Transform.FlipVertical);

        var boxed = LoadText(new PsdWriter.TypeTool { Text = "A paragraph that wraps inside its frame", Bounds = new SKRect(0, 0, 120, 60), GlyphBounds = new SKRect(0, 0, 100, 20), Tx = 30, Ty = 40 });
        var paragraph = Assert.Single(boxed.Layers);
        Assert.True(paragraph.Text!.IsBox);
        Assert.Equal(120 + 2 * Composa.Text.TextLayout.Padding, paragraph.Text.BoxWidth!.Value, 0.5);
        Assert.Equal(60 + 2 * Composa.Text.TextLayout.Padding, paragraph.Text.BoxHeight!.Value, 0.5);
        Assert.Equal((30 - Composa.Text.TextLayout.Padding, 40 - Composa.Text.TextLayout.Padding), (paragraph.Transform.X, paragraph.Transform.Y));
        Assert.True(new Composa.Text.TextLayout(paragraph.Text).Lines.Count > 1, "the frame wraps the sentence");
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
        Assert.Contains("format version", Assert.Throws<PsdException>(() => PsdImport.Load(new PsdWriter { Version = 3 }.Build())).Message);
        Assert.Contains("8-bit RGB", Assert.Throws<PsdException>(() => PsdImport.Load(new PsdWriter { Mode = 4 }.Build())).Message);
        Assert.Contains("8-bit RGB", Assert.Throws<PsdException>(() => PsdImport.Load(new PsdWriter { Depth = 16 }.Build())).Message);
        Assert.Contains("larger", Assert.Throws<PsdException>(() => PsdImport.Load(new PsdWriter { Width = 40_000, Height = 10 }.Build())).Message);
        Assert.Contains("larger", Assert.Throws<PsdException>(() => PsdImport.Load(Plain(), pixelBudget: 50)).Message);
        Assert.Contains("not a Photoshop", Assert.Throws<PsdException>(() => PsdImport.Load("hello world, not a psd"u8.ToArray())).Message);
        Assert.Throws<PsdException>(() => PsdImport.Load(Plain()[..(Plain().Length / 2)]));      // Cut through the layer data.
        Assert.False(PsdImport.IsPsd("PNG\r\n"u8.ToArray()));
        Assert.True(PsdImport.IsPsd(Plain()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Large_document_files_read_through_their_widened_lengths(int compression)
    {
        // A PSB is a PSD with 8-byte section, layer-info and channel lengths, 4-byte PackBits row counts, and 8-byte
        // lengths for a fixed set of additional-info keys; everything else is unchanged.
        using var gradient = Gradient(37, 23);
        using var hole = Pixels.NewMask(10, 10);
        var writer = new PsdWriter { Version = 2, Width = 60, Height = 40, Compression = compression, Resolution = 144 };
        writer.Layers.Add(new PsdWriterLayer { Name = "Background", Image = Solid(60, 40, SKColors.Red) });
        writer.Layers.Add(new PsdWriterLayer { Name = "</Layer group>", EmptyWidth = 0 }.With("lsct", PsdWriter.Section(3)));
        writer.Layers.Add(new PsdWriterLayer { Name = "Picture", Image = gradient, Left = 2, Top = 3, Mask = hole, MaskLeft = 12, MaskTop = 13 }
            .With("Lr16", new byte[6]).With("luni", PsdWriter.Unicode("Large picture")));
        writer.Layers.Add(new PsdWriterLayer { Name = "Folder", Blend = "pass" }.With("lsct", PsdWriter.Section(1)));
        var import = Load(writer);
        Assert.Equal((60, 40, 144d), (import.Width, import.Height, import.Resolution));
        Assert.Equal(["Background", "Folder"], import.Layers.Select(l => l.Name));
        var picture = Assert.Single(import.Layers[1].Children);
        Assert.Equal("Large picture", picture.Name);                        // The key after the 8-byte-length block still reads.
        Assert.Equal(gradient.Bytes, picture.Pixels!.Bytes);
        Assert.Equal((2d, 3d), (picture.Transform.X, picture.Transform.Y));
        Assert.Equal(0, picture.Mask!.GetPixel(15, 15).Alpha);
        Assert.Equal(255, picture.Mask.GetPixel(0, 0).Alpha);
        Assert.Empty(import.Conversions);
        Assert.Contains(".psb", Composa.IO.ImageFiles.ImportExtensions);
    }

    [Fact]
    public void A_flattened_file_becomes_one_layer_from_the_merged_image()
    {
        using var gradient = Gradient(30, 20);
        foreach (var (compression, version) in new[] { (0, 1), (1, 1), (0, 2), (1, 2) })
        {
            var writer = new PsdWriter { Width = 30, Height = 20, Composite = gradient, Compression = compression, Version = (ushort)version };
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
