using Composa.Editing;
using Composa.Filters;
using Composa.IO;
using Composa.Model;
using Composa.Painting;
using Composa.Rendering;
using Composa.Selections;
using SkiaSharp;
using static Composa.Core.Tests.TestImages;

namespace Composa.Core.Tests;

[Collection(ClipboardCollection.Name)]
public class EditingTests
{
    [Fact]
    public void Undo_and_redo_restore_pixels_and_structure()
    {
        var session = EditorSession.NewCanvas(16, 16, SKColors.White);
        session.AddBlankLayer();
        session.Fill(SKColors.Red);
        AssertColor(SKColors.Red, session.Composite().GetPixel(5, 5));
        session.Undo();
        AssertColor(SKColors.White, session.Composite().GetPixel(5, 5));
        session.Undo();
        Assert.Single(session.Document.Layers);
        session.Redo();
        session.Redo();
        Assert.Equal(2, session.Document.Layers.Count);
        AssertColor(SKColors.Red, session.Composite().GetPixel(5, 5));
        Assert.False(session.CanRedo);
    }

    [Fact]
    public void Fill_respects_the_selection()
    {
        var session = EditorSession.NewCanvas(16, 16, SKColors.White);
        session.SelectRect(new SKRect(0, 0, 8, 16));
        session.Fill(SKColors.Blue);
        AssertColor(SKColors.Blue, session.Composite().GetPixel(2, 2));
        AssertColor(SKColors.White, session.Composite().GetPixel(12, 2));
    }

    [Fact]
    public void Selection_modes_combine()
    {
        var session = EditorSession.NewCanvas(20, 20);
        session.SelectRect(new SKRect(0, 0, 10, 10));
        session.SelectRect(new SKRect(5, 5, 15, 15), SelectionMode.Add);
        Assert.Equal(new SKRectI(0, 0, 15, 15), SelectionMask.Bounds(session.Selection!));
        session.SelectRect(new SKRect(0, 0, 20, 8), SelectionMode.Subtract);
        Assert.Equal(new SKRectI(0, 8, 15, 15), SelectionMask.Bounds(session.Selection!));
        session.SelectRect(new SKRect(0, 0, 3, 20), SelectionMode.Intersect);
        Assert.Equal(new SKRectI(0, 8, 3, 10), SelectionMask.Bounds(session.Selection!));
        session.InvertSelection();
        Assert.Equal(0, session.Selection!.GetPixel(1, 9).Alpha);
        session.Deselect();
        Assert.Null(session.Selection);
        session.Undo();
        Assert.NotNull(session.Selection);
    }

    [Fact]
    public void Expand_contract_and_outline()
    {
        var session = EditorSession.NewCanvas(40, 40);
        session.SelectRect(new SKRect(10, 10, 30, 30));
        session.ExpandSelection(2);
        Assert.Equal(new SKRectI(8, 8, 32, 32), SelectionMask.Bounds(session.Selection!, 128));
        session.ContractSelection(4);
        Assert.Equal(new SKRectI(12, 12, 28, 28), SelectionMask.Bounds(session.Selection!, 128));
        using var outline = SelectionMask.Outline(session.Selection!);
        Assert.Equal(new SKRect(12, 12, 28, 28), outline.Bounds);
    }

    [Fact]
    public void Magic_wand_selects_the_connected_region()
    {
        var session = EditorSession.NewCanvas(30, 10, SKColors.White);
        session.SelectRect(new SKRect(10, 0, 12, 10));
        session.Fill(SKColors.Black);
        session.Deselect();
        session.WandTolerance = 10;
        session.SelectWand(2, 2);
        Assert.Equal(255, session.Selection!.GetPixel(5, 5).Alpha);
        Assert.Equal(0, session.Selection!.GetPixel(20, 5).Alpha);
        session.WandContiguous = false;
        session.SelectWand(2, 2);
        Assert.Equal(255, session.Selection!.GetPixel(20, 5).Alpha);
        Assert.Equal(0, session.Selection!.GetPixel(11, 5).Alpha);
    }

    [Fact]
    public void Brush_paints_with_stroke_level_opacity_and_undoes()
    {
        var session = EditorSession.NewCanvas(100, 100, SKColors.White);
        session.Tool = Tool.Brush;
        session.Foreground = SKColors.Black;
        session.Brush = new BrushSettings { Size = 20, Hardness = 1, Opacity = 0.5 };
        Assert.True(session.BeginStroke(new SKPoint(20, 50), out _));
        session.ContinueStroke(new SKPoint(80, 50));
        session.ContinueStroke(new SKPoint(20, 50)); // Going back over the same pixels must not darken them further.
        session.EndStroke();
        AssertColor(new SKColor(128, 128, 128), session.Composite().GetPixel(50, 50), 3);
        AssertColor(SKColors.White, session.Composite().GetPixel(50, 80));
        session.Undo();
        AssertColor(SKColors.White, session.Composite().GetPixel(50, 50));
    }

    [Fact]
    public void Smoothing_trails_the_pointer_and_catches_up_on_release()
    {
        var session = EditorSession.NewCanvas(200, 100, SKColors.White);
        session.Tool = Tool.Brush;
        session.Brush = new BrushSettings { Size = 10, Hardness = 1, Smoothing = 40 };
        Assert.True(session.BeginStroke(new SKPoint(50, 50), out _));
        // A jitter shorter than the string's length paints nothing at all.
        session.ContinueStroke(new SKPoint(60, 58));
        AssertColor(SKColors.Black, session.Composite().GetPixel(50, 50));
        AssertColor(SKColors.White, session.Composite().GetPixel(60, 58));
        // Pulled taut, the brush follows, staying a string's length behind the pointer.
        session.ContinueStroke(new SKPoint(150, 50));
        AssertColor(SKColors.Black, session.Composite().GetPixel(110, 50));
        AssertColor(SKColors.White, session.Composite().GetPixel(130, 50));
        // On release the stroke catches up to where the hand is.
        session.EndStroke();
        AssertColor(SKColors.Black, session.Composite().GetPixel(150, 50));
        AssertColor(SKColors.Black, session.Composite().GetPixel(130, 50));
        Assert.Equal("Brush", session.History.UndoName);

        // The length is in screen points: zoomed in twice, the string covers half as many document pixels.
        session.ViewZoom = 2;
        session.BeginStroke(new SKPoint(50, 80), out _);
        session.ContinueStroke(new SKPoint(100, 80));
        AssertColor(SKColors.Black, session.Composite().GetPixel(80, 80));
        AssertColor(SKColors.White, session.Composite().GetPixel(95, 80));
        session.CancelStroke();

        // Off, the brush follows the pointer exactly, as it always did.
        session.Brush = session.Brush with { Smoothing = 0 };
        session.BeginStroke(new SKPoint(50, 20), out _);
        session.ContinueStroke(new SKPoint(60, 20));
        AssertColor(SKColors.Black, session.Composite().GetPixel(60, 20));
        session.EndStroke();
    }

    [Fact]
    public void Eraser_clears_and_painting_stays_inside_the_selection()
    {
        var session = EditorSession.NewCanvas(60, 60, SKColors.Red);
        session.Tool = Tool.Brush;
        session.EraserMode = true;
        session.Brush = new BrushSettings { Size = 30, Hardness = 1 };
        session.SelectRect(new SKRect(0, 0, 30, 60));
        session.BeginStroke(new SKPoint(30, 30), out _);
        session.EndStroke();
        Assert.Equal(0, session.Composite().GetPixel(25, 30).Alpha);
        AssertColor(SKColors.Red, session.Composite().GetPixel(35, 30));
    }

    [Fact]
    public void Painting_beyond_a_small_layer_grows_it()
    {
        var session = EditorSession.NewCanvas(100, 100);
        var layer = session.AddImageLayer("small", Solid(10, 10, SKColors.Blue));
        session.Tool = Tool.Brush;
        session.Brush = new BrushSettings { Size = 10, Hardness = 1 };
        session.Foreground = SKColors.Lime;
        session.BeginStroke(new SKPoint(90, 90), out _);
        session.EndStroke();
        AssertColor(SKColors.Lime, session.Composite().GetPixel(90, 90));
        AssertColor(SKColors.Blue, session.Composite().GetPixel(50, 50));
        Assert.Equal(100, session.Document.Find(layer.Id)!.Pixels!.Width);
    }

    [Fact]
    public void Painting_on_a_mask_hides_the_layer()
    {
        var session = EditorSession.NewCanvas(50, 50, SKColors.White);
        var layer = session.AddImageLayer("top", Solid(50, 50, SKColors.Red));
        session.AddMask(layer);
        Assert.True(session.IsEditingMask);
        session.Tool = Tool.Brush;
        session.Foreground = SKColors.Black;
        session.Brush = new BrushSettings { Size = 20, Hardness = 1 };
        session.BeginStroke(new SKPoint(25, 25), out _);
        session.EndStroke();
        AssertColor(SKColors.White, session.Composite().GetPixel(25, 25));
        AssertColor(SKColors.Red, session.Composite().GetPixel(5, 5));
    }

    [Fact]
    public void A_mask_can_be_painted_past_its_layer()
    {
        // A 20 px layer in the middle of a 50 px canvas; its mask starts as small as the layer.
        var session = EditorSession.NewCanvas(50, 50, SKColors.White);
        var layer = session.AddImageLayer("top", Solid(20, 20, SKColors.Red), new SKPoint(25, 25));
        session.AddMask(layer);
        Assert.Equal(20, layer.Mask!.Width);
        session.Tool = Tool.Brush;
        session.Foreground = SKColors.Black;
        session.Brush = new BrushSettings { Size = 6, Hardness = 1 };
        session.BeginStroke(new SKPoint(5, 5), out var problem);
        Assert.Null(problem);
        session.EndStroke();
        var painted = session.Document.Find(layer.Id)!;
        // The mask grew to the canvas, black under the stroke and white elsewhere, and the layer's pixels stayed put.
        Assert.Equal(50, painted.Mask!.Width);
        Assert.Equal(0, painted.Mask.GetPixel(5, 5).Alpha);
        Assert.Equal(255, painted.Mask.GetPixel(45, 45).Alpha);
        AssertColor(SKColors.Red, session.Composite().GetPixel(25, 25));
        // What the mask hides stays hidden once paint reaches it.
        session.EditingMask = false;
        session.Foreground = SKColors.Blue;
        session.BeginStroke(new SKPoint(5, 5), out _);
        session.EndStroke();
        AssertColor(SKColors.White, session.Composite().GetPixel(5, 5));
        // Undo puts the small mask back.
        session.Undo();
        session.Undo();
        Assert.Equal(20, session.Document.Find(layer.Id)!.Mask!.Width);
    }

    [Fact]
    public void A_hide_all_mask_grows_black()
    {
        var session = EditorSession.NewCanvas(50, 50, SKColors.White);
        var layer = session.AddImageLayer("top", Solid(20, 20, SKColors.Red), new SKPoint(25, 25));
        session.AddMask(layer, hideAll: true);
        session.Tool = Tool.Brush;
        session.Foreground = SKColors.White;
        session.Brush = new BrushSettings { Size = 6, Hardness = 1 };
        session.BeginStroke(new SKPoint(5, 5), out _);
        session.EndStroke();
        var mask = session.Document.Find(layer.Id)!.Mask!;
        Assert.Equal(50, mask.Width);
        Assert.Equal(255, mask.GetPixel(5, 5).Alpha);
        Assert.Equal(0, mask.GetPixel(45, 45).Alpha);
        Assert.Equal(0, mask.GetPixel(25, 25).Alpha);
    }

    [Fact]
    public void A_fill_and_a_gradient_cover_the_canvas_on_a_mask()
    {
        var session = EditorSession.NewCanvas(50, 50, SKColors.White);
        var layer = session.AddImageLayer("top", Solid(20, 20, SKColors.Red), new SKPoint(25, 25));
        session.AddMask(layer);
        session.Fill(SKColors.Black);
        var filled = session.Document.Find(layer.Id)!.Mask!;
        Assert.Equal(50, filled.Width);
        Assert.Equal(0, filled.GetPixel(2, 2).Alpha);
        Assert.Equal(0, filled.GetPixel(25, 25).Alpha);
        session.Undo();
        Assert.Equal(20, session.Document.Find(layer.Id)!.Mask!.Width);

        session.Foreground = SKColors.Black;
        session.Background = SKColors.White;
        var target = session.Document.Find(layer.Id)!;
        var original = session.BeginGradient(target);
        session.DrawGradient(target, original, new SKPoint(0, 25), new SKPoint(50, 25));
        session.Commit();
        var graded = session.Document.Find(layer.Id)!.Mask!;
        Assert.Equal(50, graded.Width);
        Assert.True(graded.GetPixel(2, 25).Alpha < 40);
        Assert.True(graded.GetPixel(47, 25).Alpha > 215);
    }

    [Fact]
    public void Smearing_a_mask_stays_within_it()
    {
        var session = EditorSession.NewCanvas(50, 50, SKColors.White);
        var layer = session.AddImageLayer("top", Solid(20, 20, SKColors.Red), new SKPoint(25, 25));
        session.AddMask(layer);
        session.Tool = Tool.Smear;
        session.Brush = new BrushSettings { Size = 6, Hardness = 1 };
        session.BeginStroke(new SKPoint(5, 5), out _);
        session.EndStroke();
        Assert.Equal(20, session.Document.Find(layer.Id)!.Mask!.Width);
    }

    [Fact]
    public void Clone_stamp_copies_from_the_source_point()
    {
        var session = EditorSession.NewCanvas(100, 50, SKColors.White);
        session.SelectRect(new SKRect(0, 0, 50, 50));
        session.Fill(SKColors.Green);
        session.Deselect();
        session.Tool = Tool.CloneStamp;
        session.Brush = new BrushSettings { Size = 10, Hardness = 1 };
        Assert.False(session.BeginStroke(new SKPoint(75, 25), out var problem));
        Assert.NotNull(problem);
        session.SetCloneSource(new SKPoint(25, 25));
        Assert.True(session.BeginStroke(new SKPoint(75, 25), out _));
        session.EndStroke();
        AssertColor(SKColors.Green, session.Composite().GetPixel(75, 25));
    }

    [Fact]
    public void Spot_healing_removes_a_blemish()
    {
        var session = new EditorSession(new Document(120, 120));
        var layer = Layer.Raster("photo", Gradient(120, 120));
        session.Document.Layers.Add(layer);
        session.Document.SetActive(layer.Id);
        var clean = session.Composite().GetPixel(60, 60);
        session.SelectEllipse(new SKRect(55, 55, 65, 65));
        session.Fill(SKColors.Black);
        session.Deselect();
        session.Tool = Tool.SpotHealing;
        session.Brush = new BrushSettings { Size = 18, Hardness = 1 };
        session.BeginStroke(new SKPoint(60, 60), out _);
        session.EndStroke();
        AssertColor(clean, session.Composite().GetPixel(60, 60), 12);
    }

    [Fact]
    public void Destructive_adjustment_previews_cancels_and_commits()
    {
        var session = EditorSession.NewCanvas(10, 10, new SKColor(10, 20, 30));
        Assert.True(session.BeginPreview("Invert"));
        session.PreviewAdjustment(new InvertAdjustment());
        AssertColor(new SKColor(245, 235, 225), session.Composite().GetPixel(1, 1));
        session.CancelPreview();
        AssertColor(new SKColor(10, 20, 30), session.Composite().GetPixel(1, 1));
        Assert.False(session.CanUndo);
        session.Adjust(new InvertAdjustment());
        AssertColor(new SKColor(245, 235, 225), session.Composite().GetPixel(1, 1));
        Assert.True(session.CanUndo);
    }

    [Fact]
    public void Gaussian_blur_spreads_past_the_layer_edges()
    {
        var session = EditorSession.NewCanvas(100, 100);
        var layer = session.AddImageLayer("dot", Solid(10, 10, SKColors.Red));
        session.ApplyFilter(new FilterSettings { Kind = FilterKind.GaussianBlur, Radius = 4 });
        var blurred = session.Document.Find(layer.Id)!;
        Assert.True(blurred.Pixels!.Width > 10);
        Assert.True(session.Composite().GetPixel(43, 50).Alpha > 0);
    }

    [Fact]
    public void Levels_and_curves_map_values()
    {
        var levels = new LevelsAdjustment().WithRange(0, new LevelsRange { InputBlack = 50, InputWhite = 150 });
        using var bitmap = Solid(2, 2, new SKColor(100, 50, 200));
        levels.Apply(bitmap);
        AssertColor(new SKColor(128, 0, 255), bitmap.GetPixel(0, 0));

        var curves = new CurvesAdjustment().WithChannel(0, [new(0, 0), new(128, 192), new(255, 255)]);
        Assert.Equal(192, curves.Value(128, 0), 3);
        Assert.True(curves.Value(64, 0) > 64);
    }

    [Fact]
    public void Hue_shift_turns_red_into_green()
    {
        using var bitmap = Solid(2, 2, SKColors.Red);
        new HueSaturationAdjustment().WithShift(HueRange.Master, new HslShift(120, 0, 0)).Apply(bitmap);
        AssertColor(new SKColor(0, 255, 0), bitmap.GetPixel(0, 0));
    }

    [Fact]
    public void Crop_keeps_pixels_outside_and_undo_restores_the_size()
    {
        var session = EditorSession.NewCanvas(40, 40, SKColors.White);
        session.SelectRect(new SKRect(0, 0, 10, 10));
        session.Fill(SKColors.Red);
        session.Deselect();
        session.Crop(new SKRectI(5, 5, 25, 25));
        Assert.Equal(20, session.Document.Width);
        AssertColor(SKColors.Red, session.Composite().GetPixel(2, 2));
        AssertColor(SKColors.White, session.Composite().GetPixel(10, 10));
        session.ResizeCanvas(40, 40, Anchor.Center);
        AssertColor(SKColors.Red, session.Composite().GetPixel(6, 6)); // Kept from before the crop.
        session.Undo();
        session.Undo();
        Assert.Equal(40, session.Document.Width);
        AssertColor(SKColors.Red, session.Composite().GetPixel(2, 2));
    }

    [Fact]
    public void Image_size_resamples_layers()
    {
        var session = EditorSession.NewCanvas(40, 20, SKColors.Blue);
        session.ResizeImage(80, 40);
        Assert.Equal(80, session.Document.Width);
        Assert.Equal(80, session.ActiveLayer!.Pixels!.Width);
        AssertColor(SKColors.Blue, session.Composite().GetPixel(70, 30));
    }

    [Fact]
    public void Flip_canvas_mirrors_layer_positions()
    {
        var session = EditorSession.NewCanvas(40, 40);
        session.AddImageLayer("dot", Solid(10, 10, SKColors.Red), new SKPoint(5, 5));
        session.FlipCanvas(horizontally: true);
        AssertColor(SKColors.Red, session.Composite().GetPixel(35, 5));
        Assert.Equal(0, session.Composite().GetPixel(5, 5).Alpha);
    }

    [Fact]
    public void Merge_down_keeps_the_picture()
    {
        var session = EditorSession.NewCanvas(20, 20, SKColors.White);
        var top = session.AddImageLayer("top", Solid(10, 10, SKColors.Black));
        top.Opacity = 0.5;
        var before = session.Composite().GetPixel(10, 10);
        Assert.Equal("Merge Down", session.MergeTitle);
        session.MergeLayers();
        Assert.Single(session.Document.Layers);
        AssertColor(before, session.Composite().GetPixel(10, 10));
        session.Undo();
        Assert.Equal(2, session.Document.Layers.Count);
    }

    [Fact]
    public void Group_merge_and_ungroup()
    {
        var session = EditorSession.NewCanvas(20, 20, SKColors.White);
        var a = session.AddImageLayer("a", Solid(20, 20, SKColors.Red));
        var b = session.AddImageLayer("b", Solid(10, 10, SKColors.Blue));
        session.SelectLayer(a.Id);
        session.SelectLayer(b.Id, extend: true);
        session.GroupSelectedLayers();
        var folder = session.ActiveLayer!;
        Assert.True(folder.IsGroup);
        Assert.Equal(2, folder.Children.Count);
        Assert.Equal(2, session.Document.Layers.Count);
        session.Ungroup(folder);
        Assert.Equal(3, session.Document.Layers.Count);
        session.Undo();
        Assert.Equal("Merge Group", session.MergeTitle);
        session.MergeLayers();
        Assert.False(session.ActiveLayer!.IsGroup);
        AssertColor(SKColors.Blue, session.Composite().GetPixel(10, 10));
        AssertColor(SKColors.Red, session.Composite().GetPixel(1, 1));
    }

    [Fact]
    public void Copy_paste_and_layer_via_copy()
    {
        var session = EditorSession.NewCanvas(40, 40, SKColors.Red);
        session.SelectRect(new SKRect(10, 10, 20, 20));
        Assert.True(session.Copy());
        var pasted = session.Paste()!;
        Assert.Equal(10, pasted.Pixels!.Width);
        Assert.Equal(10, pasted.Transform.X);
        session.SelectLayer(session.Document.Layers[0].Id);
        session.LayerViaCopy();
        Assert.Equal(3, session.Document.Layers.Count);
        Assert.Null(session.Selection);
    }

    [Fact]
    public void Transform_scales_about_the_opposite_corner_and_rotates()
    {
        var session = EditorSession.NewCanvas(200, 200);
        var layer = session.AddImageLayer("box", Solid(50, 50, SKColors.Red), new SKPoint(100, 100));
        var edit = session.BeginTransform()!;
        edit.Resize(TransformHandle.BottomRight, new SKPoint(175, 175), free: false, fromCenter: false);
        session.CommitTransform();
        Assert.Equal(75, layer.Transform.X);
        Assert.Equal(100, layer.Transform.Width, 3);
        edit = session.BeginTransform()!;
        edit.RotateTo(new SKPoint(200, 125), new SKPoint(125, 200), snap: true);
        session.CommitTransform();
        Assert.Equal(90, layer.Transform.Rotation);
        session.Undo();
        session.Undo();
        Assert.Equal(50, session.Document.Find(layer.Id)!.Transform.Width);
    }

    [Fact]
    public void Project_round_trips()
    {
        var session = EditorSession.NewCanvas(30, 20, SKColors.White);
        var image = session.AddImageLayer("photo", Gradient(12, 12), new SKPoint(10, 10));
        image.Blend = BlendMode.Overlay;
        image.Opacity = 0.7;
        session.AddMask(image);
        session.AddAdjustmentLayer(new CurvesAdjustment().WithChannel(1, [new(0, 10), new(255, 240)]));
        session.ActiveLayer!.Clipped = true;
        session.SelectLayer(image.Id);
        session.GroupSelectedLayers();
        session.AddShape(new SKRect(2, 2, 12, 9));

        using var stream = new MemoryStream();
        ProjectFile.Write(session.Document, stream);
        stream.Position = 0;
        var loaded = ProjectFile.Read(stream);

        Assert.Equal(30, loaded.Width);
        Assert.Equal(session.Document.AllLayers().Select(l => (l.Id, l.Name, l.Kind)), loaded.AllLayers().Select(l => (l.Id, l.Name, l.Kind)));
        var photo = loaded.Find(image.Id)!;
        Assert.Equal(BlendMode.Overlay, photo.Blend);
        Assert.Equal(0.7, photo.Opacity);
        Assert.NotNull(photo.Mask);
        Assert.IsType<CurvesAdjustment>(loaded.AllLayers().Single(l => l.IsAdjustment).Adjustment);
        Assert.NotNull(loaded.AllLayers().Single(l => l.Shape != null).Shape);
        using var expected = DocumentRenderer.Flatten(session.Document);
        using var actual = DocumentRenderer.Flatten(loaded);
        Assert.Equal(expected.Bytes, actual.Bytes);
    }

    [Fact]
    public void Exports_encode_and_reload()
    {
        using var bitmap = Gradient(32, 24);
        foreach (var format in Enum.GetValues<ExportFormat>())
        {
            var bytes = ImageFiles.Encode(bitmap, format);
            using var reloaded = ImageFiles.Load(new MemoryStream(bytes));
            Assert.Equal(32, reloaded.Width);
        }
    }
}

public class FloatingSelectionTests
{
    [Fact]
    public void Moving_selected_pixels_cuts_them_out_and_takes_the_selection_along()
    {
        var session = EditorSession.NewCanvas(60, 40, SKColors.White);
        session.SelectRect(new SKRect(5, 5, 15, 15));
        session.Fill(SKColors.Red);
        Assert.True(session.BeginMovePixels(duplicate: false));
        session.MovePixelsBy(30, 10);
        session.EndMovePixels(keep: true);
        Assert.Equal(0, session.Composite().GetPixel(10, 10).Alpha);
        TestImages.AssertColor(SKColors.Red, session.Composite().GetPixel(40, 20));
        Assert.Equal(new SKRectI(35, 15, 45, 25), SelectionMask.Bounds(session.Selection!));
        session.Undo();
        TestImages.AssertColor(SKColors.Red, session.Composite().GetPixel(10, 10));
        Assert.Equal(new SKRectI(5, 5, 15, 15), SelectionMask.Bounds(session.Selection!));
    }

    [Fact]
    public void Duplicating_keeps_the_original_and_cancel_restores_everything()
    {
        var session = EditorSession.NewCanvas(60, 40, SKColors.White);
        session.SelectRect(new SKRect(5, 5, 15, 15));
        session.Fill(SKColors.Blue);
        session.BeginMovePixels(duplicate: true);
        session.MovePixelsBy(30, 0);
        session.EndMovePixels(keep: true);
        TestImages.AssertColor(SKColors.Blue, session.Composite().GetPixel(10, 10));
        TestImages.AssertColor(SKColors.Blue, session.Composite().GetPixel(40, 10));
        session.BeginMovePixels(duplicate: false);
        session.MovePixelsBy(0, 20);
        session.EndMovePixels(keep: false);
        TestImages.AssertColor(SKColors.Blue, session.Composite().GetPixel(40, 10));
    }
}

public class LiquifyTests
{
    [Fact]
    public void Liquify_pushes_an_edge_along_the_drag()
    {
        var session = EditorSession.NewCanvas(200, 100, SKColors.White);
        session.SelectRect(new SKRect(0, 0, 100, 100));
        session.Fill(SKColors.Black);
        session.Deselect();
        session.Tool = Tool.Smear;
        session.SmearMode = SmearMode.Liquify;
        session.Brush = new BrushSettings { Size = 60, Hardness = 0.5, Opacity = 1 };
        TestImages.AssertColor(SKColors.White, session.Composite().GetPixel(112, 50));
        Assert.True(session.BeginStroke(new SKPoint(90, 50), out _));
        for (var x = 92; x <= 130; x += 2) session.ContinueStroke(new SKPoint(x, 50));
        session.EndStroke();
        Assert.True(session.Composite().GetPixel(112, 50).Red < 60, "the black edge should have been pushed to the right");
        TestImages.AssertColor(SKColors.White, session.Composite().GetPixel(112, 5));
        session.Undo();
        TestImages.AssertColor(SKColors.White, session.Composite().GetPixel(112, 50));
    }
}

public class TextLayerTests
{
    [Fact]
    public void Text_layers_render_stay_live_and_round_trip()
    {
        var session = EditorSession.NewCanvas(400, 200, SKColors.White);
        var style = new TextStyle { Text = "Hello\nWorld", Size = 40, Color = 0xFFFF0000, FontFamily = EditorSession.FontFamilies.FirstOrDefault() ?? "sans-serif" };
        var layer = session.AddText(new SKPoint(20, 20), style);
        Assert.NotNull(layer.Text);
        Assert.Equal("Hello World", layer.Name);
        using (var flat = session.Flatten())
        {
            var red = 0;
            for (var y = 0; y < 200; y++) for (var x = 0; x < 400; x++) if (flat.GetPixel(x, y) is { Red: > 200, Green: < 80 }) red++;
            Assert.True(red > 200, $"expected red glyph pixels, found {red}");
        }
        session.Tool = Tool.Brush;
        Assert.False(session.BeginStroke(new SKPoint(30, 30), out var problem));
        Assert.Contains("text", problem);

        // Scaling redraws the glyphs at the new size instead of stretching pixels.
        var heightBefore = layer.Pixels!.Height;
        var edit = session.BeginTransform()!;
        edit.Set(SKRect.Create(edit.StartFrame.Left, edit.StartFrame.Top, edit.StartFrame.Width * 2, edit.StartFrame.Height * 2), 0);
        session.CommitTransform();
        Assert.InRange(layer.Text!.Size, 75, 85);
        Assert.InRange(layer.Pixels!.Height, heightBefore * 1.8, heightBefore * 2.2);
        Assert.Equal(layer.Pixels.Height, layer.Transform.Height);

        session.Begin("Edit Text");
        session.SetText(layer, layer.Text with { Text = "Changed" });
        session.Commit();
        Assert.Equal("Changed", layer.Name);
        session.Undo();
        Assert.Equal("Hello\nWorld", session.Document.Find(layer.Id)!.Text!.Text);

        using var stream = new MemoryStream();
        ProjectFile.Write(session.Document, stream);
        stream.Position = 0;
        Assert.Equal("Hello\nWorld", ProjectFile.Read(stream).Find(layer.Id)!.Text!.Text);

        session.RasterizeShape(session.Document.Find(layer.Id)!);
        Assert.True(session.BeginStroke(new SKPoint(30, 30), out _));
        session.EndStroke();
    }
}

public class RotateCanvasTests
{
    [Fact]
    public void Rotating_the_canvas_clockwise_moves_the_top_left_to_the_top_right()
    {
        var session = EditorSession.NewCanvas(60, 40, SKColors.White);
        session.AddImageLayer("dot", TestImages.Solid(10, 10, SKColors.Red), new SKPoint(5, 5));
        session.SelectRect(new SKRect(0, 0, 10, 10));
        session.RotateCanvas(clockwise: true);
        Assert.Equal((40, 60), (session.Document.Width, session.Document.Height));
        TestImages.AssertColor(SKColors.Red, session.Composite().GetPixel(35, 5));
        TestImages.AssertColor(SKColors.White, session.Composite().GetPixel(5, 5));
        TestImages.AssertColor(SKColors.White, session.Composite().GetPixel(20, 50));
        Assert.Equal(new SKRectI(30, 0, 40, 10), SelectionMask.Bounds(session.Selection!, 128));

        session.RotateCanvas(clockwise: false);
        Assert.Equal((60, 40), (session.Document.Width, session.Document.Height));
        TestImages.AssertColor(SKColors.Red, session.Composite().GetPixel(5, 5));
        session.Undo();
        session.Undo();
        Assert.Equal(60, session.Document.Width);
        Assert.Equal(0, session.Document.Layers[1].Transform.Rotation);
    }
}

public class SelectionOutlineTests
{
    /// <summary>A Magic Wand selection on detailed artwork: here a checkerboard of 2-pixel squares, one edge per pixel step.</summary>
    private static SKBitmap Checkerboard(int size)
    {
        var mask = Pixels.NewMask(size, size);
        var span = mask.GetPixelSpan();
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
                if (((x / 2) + (y / 2)) % 2 == 0) span[y * mask.RowBytes + x] = 255;
        Pixels.Invalidate(mask);
        return mask;
    }

    [Fact]
    public void A_reduced_outline_has_far_fewer_edges_covers_the_same_area_and_keeps_thin_parts()
    {
        using var mask = Checkerboard(512);
        using var full = SelectionMask.Outline(mask);
        Assert.True(full.PointCount > 20_000, $"{full.PointCount} points");
        using var reduced = SelectionMask.ReducedOutline(mask, 8);
        Assert.True(reduced.PointCount < full.PointCount / 100, $"{reduced.PointCount} points");
        Assert.Equal(new SKRect(0, 0, 512, 512), reduced.Bounds);         // Any coverage in a block selects it, so the whole board is outlined.

        // A one-pixel line is still there at a quarter of the resolution, eight document pixels wide.
        using var line = Pixels.NewMask(400, 300);
        var span = line.GetPixelSpan();
        for (var x = 100; x < 300; x++) span[150 * line.RowBytes + x] = 255;
        Pixels.Invalidate(line);
        using var thin = SelectionMask.ReducedOutline(line, 4);
        Assert.Equal(new SKRect(100, 148, 300, 152), thin.Bounds);
        using var same = SelectionMask.ReducedOutline(line, 1);
        Assert.Equal(new SKRect(100, 150, 300, 151), same.Bounds);      // A block of one is the exact outline.
    }
}
