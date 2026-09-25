using Composa.Editing;
using Composa.IO;
using Composa.Model;
using Composa.Selections;
using SkiaSharp;
using static Composa.Core.Tests.TestImages;

namespace Composa.Core.Tests;

public class GuideTests
{
    [Fact]
    public void Guides_are_added_moved_cleared_and_undone()
    {
        var session = EditorSession.NewCanvas(200, 100);
        var vertical = session.AddGuide(GuideAxis.Vertical, 40)!;
        var horizontal = session.AddGuide(GuideAxis.Horizontal, 25)!;
        Assert.Equal(2, session.Guides.Count);
        Assert.True(session.CanClearGuides);
        session.MoveGuide(vertical.Id, 70);
        Assert.Equal(70, session.Guides.Single(g => g.Id == vertical.Id).Position);
        Assert.Equal("Move Guide", session.History.UndoName);
        session.Undo();
        Assert.Equal(40, session.Guides.Single(g => g.Id == vertical.Id).Position);
        session.RemoveGuide(horizontal.Id);
        Assert.Single(session.Guides);
        session.ClearGuides();
        Assert.Empty(session.Guides);
        session.Undo();
        session.Undo();
        Assert.Equal(2, session.Guides.Count);
    }

    [Fact]
    public void Locked_guides_cannot_be_created_or_moved_but_can_be_cleared()
    {
        var session = EditorSession.NewCanvas(200, 100);
        var guide = session.AddGuide(GuideAxis.Vertical, 10)!;
        session.View = session.View with { LockGuides = true };
        Assert.Null(session.AddGuide(GuideAxis.Vertical, 20));
        session.MoveGuide(guide.Id, 50);
        Assert.Equal(10, session.Guides[0].Position);
        session.ClearGuides();
        Assert.Empty(session.Guides);
    }

    [Fact]
    public void Canvas_operations_carry_guides_along()
    {
        var session = EditorSession.NewCanvas(100, 50);
        session.AddGuide(GuideAxis.Vertical, 20);
        session.AddGuide(GuideAxis.Horizontal, 10);
        double Vertical() => session.Guides.Single(g => g.Axis == GuideAxis.Vertical).Position;
        double Horizontal() => session.Guides.Single(g => g.Axis == GuideAxis.Horizontal).Position;

        session.ResizeCanvas(140, 80, Anchor.BottomRight); // Extra pixels on the left and top.
        Assert.Equal((60d, 40d), (Vertical(), Horizontal()));
        session.Undo();
        session.ResizeImage(200, 100);
        Assert.Equal((40d, 20d), (Vertical(), Horizontal()));
        session.Undo();
        session.FlipCanvas(horizontally: true);
        Assert.Equal((80d, 10d), (Vertical(), Horizontal()));
        session.FlipCanvas(horizontally: false);
        Assert.Equal((80d, 40d), (Vertical(), Horizontal()));
        session.Undo();
        session.Undo();
        session.RotateCanvas(clockwise: true); // (x, y) -> (height - y, x): the vertical guide at x=20 becomes horizontal at y=20.
        Assert.Equal((40d, 20d), (Vertical(), Horizontal()));
        session.RotateCanvas(clockwise: false);
        Assert.Equal((20d, 10d), (Vertical(), Horizontal()));
    }

    [Fact]
    public void Snap_targets_follow_the_view_options()
    {
        var session = EditorSession.NewCanvas(400, 300);
        session.AddImageLayer("red", Solid(100, 60, SKColors.Red)); // Centered: 150-250 x 120-180.
        var (xs, ys) = session.SnapTargets(includeCenters: false);
        Assert.Equal([0, 400, 150, 250], xs.Distinct().OrderBy(v => Array.IndexOf(new float[] { 0, 400, 150, 250 }, v)));
        Assert.Contains(200, session.SnapTargets(includeCenters: true).Xs);
        session.View = session.View with { Snap = false };
        Assert.Empty(session.SnapTargets().Xs);
        session.View = new ViewOptions { SnapToLayers = false };
        Assert.Equal([0, 400], session.SnapTargets(includeCenters: false).Xs);
        session.View = new ViewOptions { SnapToLayers = false, SnapToDocumentBounds = false };
        Assert.Empty(session.SnapTargets().Xs);
        session.AddGuide(GuideAxis.Vertical, 33);
        Assert.Equal([33], session.SnapTargets().Xs);
        session.View = session.View with { ShowGuides = false };
        Assert.Empty(session.SnapTargets().Xs); // Hidden extras do not snap.
        session.View = session.View with { ShowGrid = true, SnapToGrid = true };
        Assert.Contains(64, session.SnapTargets().Xs);
        Assert.Contains(8, session.SnapTargets().Xs);
        Assert.Equal(32, session.SnapGuidePosition(GuideAxis.Vertical, 35, null, 4)); // Guides hidden: the grid line wins.
        session.View = session.View with { ShowGuides = true };
        Assert.Equal(33, session.SnapGuidePosition(GuideAxis.Vertical, 35, null, 4));
    }

    [Fact]
    public void Moves_snap_to_targets_and_report_the_lines()
    {
        var session = EditorSession.NewCanvas(400, 300);
        var box = new SKRect(10, 10, 60, 40);
        var (dx, dy, snapX, snapY) = session.SnapMove(box, new HashSet<Guid>(), 187, 0, 6);
        Assert.Equal(190, dx); // The left edge lands on the canvas center.
        Assert.Equal(200, snapX);
        Assert.Null(snapY);
        Assert.Equal(0, dy);
    }

    [Fact]
    public void Layout_grid_lines_include_majors_and_subdivisions()
    {
        var lines = LayoutGrid.Lines(64).ToList();
        Assert.Equal(0, lines.First());
        Assert.Equal(64, lines.Last());
        Assert.Contains(8, lines);
        Assert.True(LayoutGrid.IsMajor(0) && LayoutGrid.IsMajor(64) && !LayoutGrid.IsMajor(8));
    }

    [Fact]
    public void Guides_round_trip_through_the_project()
    {
        var session = EditorSession.NewCanvas(80, 40);
        session.AddGuide(GuideAxis.Vertical, 16);
        session.AddGuide(GuideAxis.Horizontal, 12.5);
        using var stream = new MemoryStream();
        ProjectFile.Write(session.Document, stream);
        stream.Position = 0;
        Assert.Equal(session.Guides, ProjectFile.Read(stream).Guides);
    }
}

public class LineShapeTests
{
    [Fact]
    public void A_line_runs_between_the_dragged_points_and_keeps_them_when_scaled()
    {
        var session = EditorSession.NewCanvas(200, 200, SKColors.White);
        session.Foreground = SKColors.Red;
        session.ShapeLineWidth = 6;
        var layer = session.AddLine(new SKPoint(20, 100), new SKPoint(180, 100))!;
        Assert.Equal(ShapeKind.Line, layer.Shape!.Kind);
        Assert.StartsWith("Line", layer.Name);
        Assert.Equal((17d, 97d), (layer.Transform.X, layer.Transform.Y));
        Assert.Equal((166d, 6d), (layer.Transform.Width, layer.Transform.Height));
        AssertColor(SKColors.Red, session.Composite().GetPixel(100, 100));
        AssertColor(SKColors.White, session.Composite().GetPixel(100, 90));
        AssertColor(SKColors.White, session.Composite().GetPixel(10, 100));

        // A diagonal line stores its ends as fractions of its box and stays on them when the layer is scaled.
        var diagonal = session.AddLine(new SKPoint(20, 20), new SKPoint(80, 60))!;
        Assert.True(diagonal.Shape!.StartX < 0.1 && diagonal.Shape.EndX > 0.9);
        session.SetTransform(diagonal, diagonal.Transform with { Width = diagonal.Transform.Width * 2, Height = diagonal.Transform.Height * 2 });
        var pixels = diagonal.Pixels!;
        Assert.Equal((int)Math.Round(diagonal.Transform.Width), pixels.Width);
        var startX = (int)(diagonal.Shape.StartX!.Value * pixels.Width + 2);
        var startY = (int)(diagonal.Shape.StartY!.Value * pixels.Height + 1);
        Assert.True(pixels.GetPixel(startX, startY).Alpha > 0);
        Assert.Equal(0, pixels.GetPixel(pixels.Width - 2, 1).Alpha); // The opposite corner stays empty.
        Assert.Null(session.AddLine(new SKPoint(5, 5), new SKPoint(5, 5)));
    }

    [Fact]
    public void Shape_kinds_cycle_and_image_size_scales_the_thickness()
    {
        var session = EditorSession.NewCanvas(100, 100);
        session.Tool = Tool.Shape;
        session.ShapeKind = ShapeKind.Ellipse;
        session.CycleToolMode();
        Assert.Equal(ShapeKind.Line, session.ShapeKind);
        session.CycleToolMode();
        Assert.Equal(ShapeKind.Rectangle, session.ShapeKind);
        session.ShapeLineWidth = 10;
        var line = session.AddLine(new SKPoint(0, 50), new SKPoint(100, 50))!;
        session.ResizeImage(50, 50);
        Assert.Equal(5, line.Shape!.LineWidth, 0.01);
        Assert.Equal(ShapeKind.Line, line.Shape.Kind);
        session.Tool = Tool.Brush;
        session.CycleToolMode();
        Assert.True(session.EraserMode);
        session.Tool = Tool.Wand;
        session.CycleToolMode();
        Assert.Equal(WandMode.Object, session.WandMode);
    }
}

public class ObjectSelectionTests
{
    [Fact]
    public void Object_mode_selects_the_thing_under_the_click_and_deselects_on_the_backdrop()
    {
        var session = EditorSession.NewCanvas(120, 80, SKColors.White);
        session.AddImageLayer("box", Solid(30, 20, SKColors.Red), new SKPoint(40, 40));
        session.AddImageLayer("dot", Solid(10, 10, SKColors.Blue), new SKPoint(100, 20));
        session.SampleAllLayers = true;
        session.SelectObject(40, 40);
        Assert.NotNull(session.Selection);
        Assert.Equal(new SKRectI(25, 30, 55, 50), SelectionMask.Bounds(session.Selection!, 128));
        Assert.Equal("Object Selection", session.History.UndoName);
        session.ObjectEdgeOffset = -3;
        session.SelectObject(100, 20, SelectionMode.Add);
        Assert.True(session.Selection!.GetPixel(100, 20).Alpha > 200);
        Assert.True(session.Selection!.GetPixel(40, 40).Alpha > 200);
        Assert.True(session.Selection!.GetPixel(93, 20).Alpha > 100); // Loosened outward by three pixels.
        session.SelectObject(5, 5); // The backdrop: nothing to select.
        Assert.Null(session.Selection);

        Assert.True(session.SelectSubject());
        Assert.True(session.Selection!.GetPixel(40, 40).Alpha > 200 && session.Selection.GetPixel(100, 20).Alpha > 200);
        Assert.True(session.Selection!.GetPixel(5, 5).Alpha < 30);
        Assert.False(EditorSession.NewCanvas(20, 20, SKColors.White).SelectSubject());
    }

    [Fact]
    public void Feather_expand_and_contract_amounts_live_on_the_session()
    {
        var session = EditorSession.NewCanvas(60, 60);
        session.SelectRect(new SKRect(20, 20, 40, 40));
        session.SelectionFeatherAmount = 6;
        session.FeatherSelection(session.SelectionFeatherAmount);
        var row = Enumerable.Range(0, 60).Select(x => (int)session.Selection!.GetPixel(x, 30).Alpha).ToList();
        Assert.True(row.Count(v => v is > 8 and < 247) >= 4, "the feathered edge should fade");
        session.FeatherSelection(6);
        var softer = Enumerable.Range(0, 60).Select(x => (int)session.Selection!.GetPixel(x, 30).Alpha).ToList();
        Assert.True(softer.Count(v => v is > 8 and < 247) > row.Count(v => v is > 8 and < 247), "feathering again softens further");
    }
}
