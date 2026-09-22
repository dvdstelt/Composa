using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.App.Controls;
using Composa.Editing;
using Composa.Model;
using SkiaSharp;

namespace Composa.App.Tests;

public class LayersPanelInputTests
{
    private readonly MainWindow window;
    private readonly EditorSession session;
    private readonly LayersPanel panel;

    public LayersPanelInputTests()
    {
        window = new MainWindow { Width = 1280, Height = 800 };
        window.Show();
        session = EditorSession.NewCanvas(300, 200, SKColors.White);
        window.AddSession(session);
        for (var i = 0; i < 3; i++) session.AddBlankLayer();
        Dispatcher.UIThread.RunJobs();
        panel = window.GetVisualDescendants().OfType<LayersPanel>().Single();
    }

    /// <summary>Rows top to bottom, as shown.</summary>
    private List<Border> Rows() => panel.GetVisualDescendants().OfType<Border>().Where(b => b.Tag is Layer).ToList();

    private Point Center(Border row, double x) => row.TranslatePoint(new Point(x, row.Bounds.Height / 2), window)!.Value;

    /// <summary>Rows are rebuilt when the selection changes, so the point is taken before the press.</summary>
    private void Click(Border row, double x, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        var point = Center(row, x);
        window.MouseDown(point, MouseButton.Left, modifiers);
        window.MouseUp(point, MouseButton.Left, modifiers);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Clicking_selects_and_ctrl_click_extends()
    {
        var rows = Rows();
        Assert.Equal(4, rows.Count);
        Click(rows[2], 150);
        Assert.Equal(((Layer)rows[2].Tag!).Id, session.Document.ActiveLayerId);
        Click(Rows()[0], 150, RawInputModifiers.Control);
        Assert.Equal(2, session.Document.SelectedLayerIds.Count);
    }

    [AvaloniaFact]
    public void Swiping_down_the_eye_column_hides_each_layer_passed()
    {
        var rows = Rows();
        window.MouseDown(Center(rows[0], 16), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        for (var i = 1; i < 3; i++)
        {
            window.MouseMove(Center(Rows()[i], 16), RawInputModifiers.LeftMouseButton);
            Dispatcher.UIThread.RunJobs();
        }
        window.MouseUp(Center(Rows()[2], 16), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        var layers = Rows().Select(r => (Layer)r.Tag!).ToList();
        Assert.Equal([false, false, false, true], layers.Select(l => l.Visible));
        // After the swipe ended, merely hovering changes nothing.
        window.MouseMove(Center(Rows()[3], 16));
        Assert.True(((Layer)Rows()[3].Tag!).Visible);
    }

    [AvaloniaFact]
    public void Dragging_a_row_reorders_layers()
    {
        var rows = Rows();
        var top = (Layer)rows[0].Tag!;
        window.MouseDown(Center(rows[0], 150), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        var target = Rows()[2];
        var below = target.TranslatePoint(new Point(150, target.Bounds.Height - 4), window)!.Value;
        window.MouseMove(Center(Rows()[1], 150), RawInputModifiers.LeftMouseButton);
        window.MouseMove(below, RawInputModifiers.LeftMouseButton);
        window.MouseUp(below, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, session.Document.Layers.IndexOf(top)); // Was index 3 (top); now just above the background.
        Assert.Equal("Reorder Layers", session.History.UndoName);
    }

    [AvaloniaFact]
    public void Double_click_renames_even_an_unselected_layer()
    {
        var row = Rows()[2];
        var layer = (Layer)row.Tag!;
        Click(row, 150);
        Click(Rows()[2], 150);
        var box = Assert.IsType<TextBox>(window.FocusManager?.GetFocusedElement());
        box.Text = "Sky";
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Sky", session.Document.Find(layer.Id)!.Name);
    }
}
