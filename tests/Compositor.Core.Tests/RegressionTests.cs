using Compositor.Editing;
using Compositor.Filters;
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
