using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.App.Dialogs;
using Composa.Editing;
using Composa.Model;
using SkiaSharp;

namespace Composa.App.Tests;

/// <summary>Dragging a number field's label changes its value in whole numbers, as in Photoshop; Alt makes it finer.</summary>
public class LabelScrubTests
{
    private readonly MainWindow window;
    private readonly EditorSession session;

    public LabelScrubTests()
    {
        window = new MainWindow { Width = 1280, Height = 800 };
        window.Show();
        session = EditorSession.NewCanvas(600, 400, SKColors.White);
        window.AddSession(session);
        Dispatcher.UIThread.RunJobs();
    }

    private static TextBlock Label(Visual root, string text) => root.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == text);

    private static void Drag(Window on, Control label, double dx, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        var start = label.TranslatePoint(new Point(label.Bounds.Width / 2, label.Bounds.Height / 2), on)!.Value;
        on.MouseDown(start, MouseButton.Left, modifiers);
        on.MouseMove(start + new Vector(dx, 0), RawInputModifiers.LeftMouseButton | modifiers);
        on.MouseUp(start + new Vector(dx, 0), MouseButton.Left, modifiers);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Dragging_a_transform_label_moves_the_layer_in_whole_pixels_and_undoes_as_one_step()
    {
        var layer = session.AddImageLayer("box", Rendering.Pixels.NewColor(80, 60), new SKPoint(300, 200));
        var x = layer.Transform.X;
        window.SelectTool(Tool.Move);
        Dispatcher.UIThread.RunJobs();
        var label = Label(window, "X");
        Assert.NotNull(label.Cursor); // The resize cursor says it drags.

        Drag(window, label, 40);
        Assert.Equal(x + 40, session.Document.Find(layer.Id)!.Transform.X);
        Assert.Equal("Transform", session.History.UndoName);
        session.Undo();
        Assert.Equal(x, session.Document.Find(layer.Id)!.Transform.X);

        // Alt: ten pixels of travel move it one.
        Drag(window, label, 40, RawInputModifiers.Alt);
        Assert.Equal(x + 4, session.Document.Find(layer.Id)!.Transform.X);
    }

    [AvaloniaFact]
    public void Dragging_the_units_of_the_text_size_resizes_the_text_and_a_click_does_not()
    {
        var layer = session.AddText(new SKPoint(100, 100), new TextStyle { Text = "Hello", Size = 73, FontFamily = session.TextDefaults.FontFamily });
        window.SelectTool(Tool.Text);
        Dispatcher.UIThread.RunJobs();
        var label = Label(window, "px");
        Drag(window, label, 0);
        Assert.Equal(73, session.Document.Find(layer.Id)!.Text!.Size);
        Drag(window, label, 10);
        Assert.Equal(83, session.Document.Find(layer.Id)!.Text!.Size);
        Assert.False(session.IsEditingText);
    }

    [AvaloniaFact]
    public void A_dialog_label_drags_its_number_field()
    {
        _ = CanvasDialogs.NewCanvas(window, SKColors.White);
        Dispatcher.UIThread.RunJobs();
        var dialog = window.OwnedWindows.Last();
        var width = dialog.GetVisualDescendants().OfType<NumericUpDown>().First(n => n.Value == 1920);
        Drag(dialog, Label(dialog, "Width"), -20);
        Assert.Equal(1900, width.Value);
        Assert.Equal(1080, dialog.GetVisualDescendants().OfType<NumericUpDown>().First(n => n.Value == 1080).Value);
        dialog.Close();
        Dispatcher.UIThread.RunJobs();
    }
}
