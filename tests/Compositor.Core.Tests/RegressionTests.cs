using Compositor.Editing;
using Compositor.Filters;
using Compositor.Model;
using Compositor.Painting;
using Compositor.Rendering;
using Compositor.Selections;
using SkiaSharp;
using static Compositor.Core.Tests.TestImages;

namespace Compositor.Core.Tests;

/// <summary>Bugs found in review, each pinned by the scenario that exposed it.</summary>
public class RegressionTests
{
    [Fact]
    public void A_command_issued_while_pixels_are_being_moved_does_not_corrupt_history()
    {
        var session = EditorSession.NewCanvas(60, 40, SKColors.Red);
        session.SelectRect(new SKRect(5, 5, 20, 20));
        session.BeginMovePixels(duplicate: false);
        session.MovePixelsBy(5, 0);
        session.Deselect(); // Finishes the move first instead of snapshotting its throwaway bitmap.
        session.MovePixelsBy(10, 0);
        session.EndMovePixels(keep: true);
        while (session.CanUndo) { session.Undo(); _ = session.Composite().GetPixel(1, 1); }
        AssertColor(SKColors.Red, session.Composite().GetPixel(7, 7));
    }

    [Fact]
    public void A_command_issued_mid_stroke_ends_the_stroke_as_its_own_undo_step()
    {
        var session = EditorSession.NewCanvas(100, 100, SKColors.White);
        session.Tool = Tool.Brush;
        session.Brush = new BrushSettings { Size = 20, Hardness = 1 };
        session.BeginStroke(new SKPoint(20, 50), out _);
        session.SelectAll();
        session.ContinueStroke(new SKPoint(80, 50));
        session.EndStroke();
        AssertColor(SKColors.White, session.Composite().GetPixel(80, 50));
        session.Undo(); // Select All
        AssertColor(SKColors.Black, session.Composite().GetPixel(20, 50));
        session.Undo(); // Brush
        AssertColor(SKColors.White, session.Composite().GetPixel(20, 50));
    }

    [Fact]
    public void Solo_ends_when_its_layer_disappears()
    {
        var session = EditorSession.NewCanvas(20, 20, SKColors.Red);
        var top = session.AddBlankLayer();
        session.SoloLayerId = top.Id;
        session.InvalidateAll();
        Assert.Equal(0, session.Composite().GetPixel(5, 5).Alpha);
        session.DeleteSelectedLayers();
        AssertColor(SKColors.Red, session.Composite().GetPixel(5, 5));
        Assert.Null(session.SoloLayerId);
    }

    [Fact]
    public void Blurring_a_mask_does_not_fade_its_borders()
    {
        var session = EditorSession.NewCanvas(80, 80, SKColors.Red);
        session.AddMask(session.ActiveLayer!);
        session.ApplyFilter(new FilterSettings { Kind = FilterKind.GaussianBlur, Radius = 8 });
        var mask = session.ActiveLayer!.Mask!;
        Assert.True(mask.GetPixel(0, 0).Alpha >= 250, $"corner was {mask.GetPixel(0, 0).Alpha}");
        Assert.True(mask.GetPixel(40, 0).Alpha >= 250);
    }

    [Fact]
    public void Spot_healing_stays_inside_the_selection()
    {
        var session = EditorSession.NewCanvas(120, 120, SKColors.White);
        session.SelectRect(new SKRect(50, 50, 70, 70));
        session.Fill(SKColors.Black);
        session.SelectRect(new SKRect(0, 0, 60, 120));
        session.Tool = Tool.SpotHealing;
        session.Brush = new BrushSettings { Size = 40, Hardness = 1 };
        session.BeginStroke(new SKPoint(60, 60), out _);
        session.EndStroke();
        AssertColor(SKColors.Black, session.Composite().GetPixel(65, 60));
        Assert.True(session.Composite().GetPixel(55, 60).Red > 150);
    }

    [Fact]
    public void Lifting_feathered_pixels_without_moving_them_changes_nothing()
    {
        var session = EditorSession.NewCanvas(80, 80, SKColors.Red);
        session.Feather = 6;
        session.SelectRect(new SKRect(20, 20, 60, 60));
        var before = session.ActiveLayer!.Pixels;
        session.BeginMovePixels(duplicate: false);
        session.MovePixelsBy(3, 0);
        session.MovePixelsBy(0, 0);
        session.EndMovePixels(keep: true);
        Assert.Same(before, session.ActiveLayer!.Pixels);
        Assert.False(session.History.UndoName.StartsWith("Move"));
    }

    [Fact]
    public void Merge_down_is_refused_onto_a_hidden_layer()
    {
        var session = EditorSession.NewCanvas(20, 20, SKColors.Red);
        var background = session.ActiveLayer!;
        session.AddBlankLayer();
        session.SetVisible(background, false);
        session.SelectLayer(session.Document.Layers[1].Id);
        Assert.False(session.CanMerge);
        session.MergeLayers();
        Assert.Equal(2, session.Document.Layers.Count);
    }

    [Fact]
    public void Edits_that_change_nothing_leave_no_undo_step()
    {
        var session = EditorSession.NewCanvas(100, 100, SKColors.White);
        var edit = session.BeginTransform()!;
        edit.MoveBy(0, 0);
        session.CommitTransform();
        Assert.False(session.CanUndo);

        session.Tool = Tool.Smear;
        session.SmearMode = SmearMode.Smudge;
        session.BeginStroke(new SKPoint(50, 50), out _);
        session.EndStroke();
        Assert.False(session.CanUndo);

        var pixels = session.ActiveLayer!.Pixels;
        session.ResizeImage(100, 100, 300);
        Assert.Same(pixels, session.ActiveLayer!.Pixels);
        Assert.Equal(300, session.Document.Resolution);
    }

    [Fact]
    public void A_cancelled_new_adjustment_layer_leaves_no_trace()
    {
        var session = EditorSession.NewCanvas(20, 20, SKColors.Red);
        var layer = session.AddAdjustmentLayer(new LevelsAdjustment(), commit: false);
        session.SetAdjustment(layer, new LevelsAdjustment().WithRange(0, new LevelsRange { Gamma = 2 }));
        session.Cancel();
        Assert.Single(session.Document.Layers);
        Assert.False(session.CanUndo);
        Assert.False(session.CanRedo);
        Assert.True(new LevelsAdjustment().ContentEquals(new LevelsAdjustment()));
        Assert.False(new LevelsAdjustment().ContentEquals(new LevelsAdjustment().WithRange(1, new LevelsRange { Gamma = 2 })));
    }
}

public class ContentAwareFillTests
{
    [Fact]
    public void Content_aware_fill_extends_an_image_past_its_edge()
    {
        var session = EditorSession.NewCanvas(200, 100);
        session.Document.Layers.Clear();
        var photo = session.AddImageLayer("photo", Gradient(120, 100), new SKPoint(60, 50), fit: false);
        Assert.Equal(0, session.Composite().GetPixel(150, 50).Alpha);
        session.SelectRect(new SKRect(110, 0, 170, 100));
        session.ContentAwareFill();
        var filled = session.Composite().GetPixel(150, 50);
        Assert.True(filled.Alpha > 200, $"expected the extension to be filled, found {filled}");
        Assert.Equal(0, session.Composite().GetPixel(190, 50).Alpha);
        session.Undo();
        Assert.Equal(0, session.Composite().GetPixel(150, 50).Alpha);
        Assert.Equal(120, session.Document.Find(photo.Id)!.Pixels!.Width);
    }
}

public class QaRegressionTests
{
    [Fact]
    public void Painting_beyond_a_rotated_scaled_layer_grows_it_without_moving_its_pixels()
    {
        var session = EditorSession.NewCanvas(300, 200);
        session.Document.Layers.Clear();
        var layer = session.AddImageLayer("box", Solid(50, 30, SKColors.Red), new SKPoint(100, 100), fit: false);
        layer.Transform = layer.Transform with { Width = 100, Height = 60, X = 50, Y = 70, Rotation = 30, FlipHorizontal = true };
        session.InvalidateAll();
        using var before = session.Flatten();
        session.Tool = Tool.Brush;
        session.Foreground = SKColors.Blue;
        session.Brush = new BrushSettings { Size = 20, Hardness = 1 };
        Assert.True(session.BeginStroke(new SKPoint(260, 30), out _));
        session.EndStroke();
        using var after = session.Flatten();
        AssertColor(SKColors.Blue, after.GetPixel(260, 30), 6);
        var moved = 0;
        for (var y = 60; y < 150; y += 3) for (var x = 40; x < 170; x += 3)
            if (Math.Abs(before.GetPixel(x, y).Alpha - after.GetPixel(x, y).Alpha) > 160) moved++; // Edge antialiasing may soften; nothing may shift.
        Assert.True(moved <= 3, $"{moved} sampled pixels of the existing picture changed");
    }

    [Fact]
    public void Blurring_a_canvas_filling_layer_keeps_its_edges_opaque()
    {
        var session = EditorSession.NewCanvas(120, 120, SKColors.White);
        session.ApplyFilter(new FilterSettings { Kind = FilterKind.GaussianBlur, Radius = 8 });
        Assert.Equal(255, session.Composite().GetPixel(0, 60).Alpha);
        session.ApplyFilter(new FilterSettings { Kind = FilterKind.MotionBlur, Radius = 30, Angle = 20 });
        Assert.Equal(255, session.Composite().GetPixel(0, 0).Alpha);
        Assert.Equal(120, session.ActiveLayer!.Pixels!.Width);
    }

    [Fact]
    public void Filter_radius_is_measured_in_document_pixels()
    {
        var session = EditorSession.NewCanvas(200, 200);
        session.Document.Layers.Clear();
        var big = Pixels.NewColor(800, 800);
        using (var canvas = new SKCanvas(big)) { canvas.Clear(SKColors.White); using var black = new SKPaint { Color = SKColors.Black }; canvas.DrawRect(0, 0, 400, 800, black); }
        session.AddImageLayer("photo", big);
        session.ApplyFilter(new FilterSettings { Kind = FilterKind.GaussianBlur, Radius = 8 });
        var nearEdge = session.Composite().GetPixel(108, 100).Red; // 8 document pixels past the edge: still clearly grey.
        Assert.InRange(nearEdge, 150, 250);
    }

    [Fact]
    public void Merging_or_grouping_clipped_layers_keeps_the_picture()
    {
        EditorSession Build(out Layer c2)
        {
            var s = EditorSession.NewCanvas(60, 60);
            s.Document.Layers.Clear();
            s.AddImageLayer("base", Solid(40, 40, SKColors.Red), new SKPoint(30, 30), fit: false);
            var c1 = s.AddImageLayer("c1", Solid(10, 10, SKColors.Blue), new SKPoint(15, 15), fit: false);
            s.ToggleClippingMask(c1);
            c2 = s.AddImageLayer("c2", Solid(10, 10, SKColors.Lime), new SKPoint(45, 45), fit: false);
            s.ToggleClippingMask(c2);
            return s;
        }
        var merged = Build(out _);
        merged.MergeLayers();
        AssertColor(SKColors.Lime, merged.Composite().GetPixel(45, 45));
        AssertColor(SKColors.Blue, merged.Composite().GetPixel(15, 15));

        var grouped = Build(out var top);
        grouped.SelectLayer(top.Id);
        grouped.GroupSelectedLayers();
        AssertColor(SKColors.Lime, grouped.Composite().GetPixel(45, 45));
    }

    [Fact]
    public void A_folder_mask_moves_with_the_folder()
    {
        var session = EditorSession.NewCanvas(200, 100);
        session.Document.Layers.Clear();
        var box = session.AddImageLayer("box", Solid(30, 30, SKColors.Red), new SKPoint(35, 35), fit: false);
        session.GroupSelectedLayers();
        var folder = session.ActiveLayer!;
        session.SelectRect(new SKRect(20, 20, 50, 50));
        session.AddMask(folder);
        session.Deselect();
        session.EditingMask = false;
        session.Nudge(100, 0);
        AssertColor(SKColors.Red, session.Composite().GetPixel(135, 35));
        session.Undo();
        AssertColor(SKColors.Red, session.Composite().GetPixel(35, 35));
        Assert.Equal(0, session.Composite().GetPixel(135, 35).Alpha);
    }

    [Fact]
    public void Image_size_rescales_live_text_and_shapes()
    {
        var session = EditorSession.NewCanvas(400, 200, SKColors.White);
        var text = session.AddText(new SKPoint(20, 20), new TextStyle { Text = "Hi", Size = 60, FontFamily = EditorSession.FontFamilies.FirstOrDefault() ?? "sans-serif" });
        session.ShapeKind = ShapeKind.RoundedRectangle;
        session.ShapeCornerRadius = 40;
        var shape = session.AddShape(new SKRect(200, 50, 360, 150))!;
        session.ResizeImage(200, 100);
        Assert.Equal(30, session.Document.Find(text.Id)!.Text!.Size, 1);
        Assert.Equal(20, session.Document.Find(shape.Id)!.Shape!.CornerRadius, 1);
        Assert.Equal(80, session.Document.Find(shape.Id)!.Pixels!.Width);
    }

    [Fact]
    public void Uneven_image_size_stretches_a_rotated_layer_like_the_flattened_picture()
    {
        var session = EditorSession.NewCanvas(120, 80, SKColors.White);
        var layer = session.AddImageLayer("box", Solid(40, 20, SKColors.Red), new SKPoint(60, 40), fit: false);
        layer.Transform = layer.Transform with { Rotation = 30 };
        session.InvalidateAll();
        using var before = session.Flatten();
        using var expected = EditorSession.Resample(before, 240, 80);
        session.ResizeImage(240, 80);
        using var actual = session.Flatten();
        var differing = 0;
        for (var y = 0; y < 80; y += 2) for (var x = 0; x < 240; x += 2)
            if (Math.Abs(expected.GetPixel(x, y).Green - actual.GetPixel(x, y).Green) > 60) differing++;
        Assert.True(differing < 40, $"{differing} sampled pixels differ from a plain stretch");
    }

    [Fact]
    public void A_corner_dragged_past_its_neighbours_folds_the_layer_instead_of_breaking_it()
    {
        var session = EditorSession.NewCanvas(200, 200);
        var layer = session.AddImageLayer("box", TestImages.Solid(100, 100, SKColors.Red), new SKPoint(150, 150)); // Centered: 100 to 200.
        var edit = session.BeginTransform()!;
        // The top-left corner goes far past the bottom-right one: the shape folds over itself.
        edit.DistortCorner(0, new SKPoint(190, 190));
        session.CommitTransform();
        var corners = layer.Transform.Corners(100, 100);
        Assert.False(Compositor.Model.Geometry.IsConvex(corners));
        using var flat = session.Flatten();
        // The two thin triangles along the right and bottom edges are painted; the fold's inside is not.
        TestImages.AssertColor(SKColors.Red, flat.GetPixel(197, 150));
        TestImages.AssertColor(SKColors.Red, flat.GetPixel(170, 197));
        Assert.Equal(0, flat.GetPixel(150, 150).Alpha);
        Assert.Equal(0, flat.GetPixel(60, 60).Alpha);
        session.Undo();
        TestImages.AssertColor(SKColors.Red, session.Composite().GetPixel(150, 150));
    }
}
