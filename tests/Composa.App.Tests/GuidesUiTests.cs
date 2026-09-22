using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Composa.App.Controls;
using Composa.Editing;
using Composa.Model;
using SkiaSharp;

namespace Composa.App.Tests;

public class GuidesUiTests
{
    private readonly MainWindow window;
    private readonly EditorSession session;

    public GuidesUiTests()
    {
        window = new MainWindow { Width = 1280, Height = 800 };
        window.Show();
        session = EditorSession.NewCanvas(600, 400, SKColors.White);
        window.AddSession(session);
        session.View = session.View with { ShowRulers = true, ShowGrid = true };
        window.Canvas.ViewOptionsChanged(rulersWereShown: false);
        window.SelectTool(Tool.Move);
        Dispatcher.UIThread.RunJobs();
    }

    private Point At(float x, float y) => window.Canvas.TranslatePoint(window.Canvas.ToScreen(new SKPoint(x, y)), window)!.Value;
    private Point OnCanvas(double x, double y) => window.Canvas.TranslatePoint(new Point(x, y), window)!.Value;

    private void Drag(Point from, Point to)
    {
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(new Point((from.X + to.X) / 2, (from.Y + to.Y) / 2), RawInputModifiers.LeftMouseButton);
        window.MouseMove(to, RawInputModifiers.LeftMouseButton);
        window.MouseUp(to, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Guides_come_out_of_the_rulers_move_with_the_move_tool_snap_moves_and_go_back_into_the_ruler()
    {
        // Pull a horizontal guide from the top ruler down to y = 100, and a vertical one to x = 250.
        Drag(OnCanvas(400, CanvasView.RulerThickness / 2), At(300, 100));
        Drag(OnCanvas(CanvasView.RulerThickness / 2, 300), At(250, 200));
        Assert.Equal(2, session.Guides.Count);
        var horizontal = session.Guides.Single(g => g.Axis == GuideAxis.Horizontal);
        Assert.Equal(100, horizontal.Position);
        Assert.Equal(250, session.Guides.Single(g => g.Axis == GuideAxis.Vertical).Position);
        Assert.Equal("New Guide", session.History.UndoName);

        // With the Move tool the guide itself drags.
        Drag(At(150, 100), At(150, 160));
        Assert.Equal(160, session.Guides.Single(g => g.Id == horizontal.Id).Position);
        Assert.Equal("Move Guide", session.History.UndoName);

        // A layer moved near the vertical guide snaps its edge onto it.
        var box = Rendering.Pixels.NewColor(60, 40);
        box.Erase(SKColors.Red);
        var layer = session.AddImageLayer("box", box, new SKPoint(100, 300));
        Dispatcher.UIThread.RunJobs();
        Drag(At(100, 300), At(100 + 117, 300)); // Right edge would land at 247; the guide at 250 pulls it over.
        Assert.Equal(190, layer.Transform.X);
        Directory.CreateDirectory(WindowTests.Shots);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        window.CaptureRenderedFrame()?.Save(Path.Combine(WindowTests.Shots, "25-rulers-guides-grid.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);

        // Dropping a guide back onto a ruler deletes it.
        Drag(At(150, 160), OnCanvas(400, 5));
        Assert.Single(session.Guides);
        Assert.Equal("Delete Guide", session.History.UndoName);

        // Locked guides stay put.
        session.View = session.View with { LockGuides = true };
        Drag(At(250, 300), At(300, 300));
        Assert.Equal(250, session.Guides.Single().Position);
        Assert.Equal(GuideAxis.Vertical, session.Guides.Single().Axis);
    }

    [AvaloniaFact]
    public void View_menu_shortcuts_toggle_rulers_grid_guides_and_snap()
    {
        window.KeyPressQwerty(PhysicalKey.R, RawInputModifiers.Control);
        Assert.False(session.View.ShowRulers);
        window.KeyPressQwerty(PhysicalKey.Quote, RawInputModifiers.Control);
        Assert.False(session.View.ShowGrid);
        window.KeyPressQwerty(PhysicalKey.Semicolon, RawInputModifiers.Control);
        Assert.False(session.View.ShowGuides);
        window.KeyPressQwerty(PhysicalKey.Semicolon, RawInputModifiers.Control | RawInputModifiers.Shift);
        Assert.False(session.View.Snap);
        window.KeyPressQwerty(PhysicalKey.Semicolon, RawInputModifiers.Control | RawInputModifiers.Alt);
        Assert.True(session.View.LockGuides);
    }
}
