using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Compositor.Editing;
using Compositor.Model;
using SkiaSharp;

namespace Compositor.App.Tests;

/// <summary>Typing on the canvas with the Type tool, driven through real pointer, key and text events.</summary>
public class TextEditingTests
{
    private readonly MainWindow window;
    private readonly EditorSession session;

    public TextEditingTests()
    {
        window = new MainWindow { Width = 1280, Height = 800 };
        window.Show();
        session = EditorSession.NewCanvas(900, 500, new SKColor(0x1E, 0x22, 0x2E));
        window.AddSession(session);
        session.TextDefaults = new TextStyle { FontFamily = EditorSession.FontFamilies.FirstOrDefault(f => f.Contains("Sans", StringComparison.OrdinalIgnoreCase)) ?? EditorSession.FontFamilies.First(), Size = 48 };
        session.Foreground = new SKColor(0xFF, 0xC8, 0x57);
        window.SelectTool(Tool.Text);
        Dispatcher.UIThread.RunJobs();
    }

    private Point At(float x, float y) => window.Canvas.TranslatePoint(window.Canvas.ToScreen(new SKPoint(x, y)), window)!.Value;

    private void Click(float x, float y, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.MouseDown(At(x, y), MouseButton.Left, modifiers);
        window.MouseUp(At(x, y), MouseButton.Left, modifiers);
        Dispatcher.UIThread.RunJobs();
    }

    private void Capture(string name)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Directory.CreateDirectory(WindowTests.Shots);
        window.CaptureRenderedFrame()?.Save(Path.Combine(WindowTests.Shots, name + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }

    [AvaloniaFact]
    public void Clicking_starts_point_text_that_is_typed_live_and_committed_with_ctrl_enter()
    {
        Click(80, 120);
        Assert.True(session.IsEditingText);
        var layer = session.TextEditLayer!;
        window.KeyTextInput("Compositor");
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        window.KeyTextInput("for Linux");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Compositor\nfor Linux", layer.Text!.Text);
        Assert.Equal(0xFFFFC857u, layer.Text.Color);
        Capture("19-text-typing");
        // Letters are text, not tool shortcuts.
        Assert.Equal(Tool.Text, session.Tool);
        window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.Shift | RawInputModifiers.Control);
        Assert.Equal("Linux", session.TextEdit!.SelectedText);
        window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.Alt);
        Assert.Equal(1, layer.Text.Tracking);
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();
        Assert.False(session.IsEditingText);
        Assert.Equal("Text", session.History.UndoName);
        Assert.Equal("Compositor for Linux", session.Document.Find(layer.Id)!.Name);
        Capture("20-text-layer");
        Assert.Equal(1, session.History.Count);
    }

    [AvaloniaFact]
    public void Dragging_makes_a_paragraph_box_and_escape_discards_new_text()
    {
        window.MouseDown(At(100, 100), MouseButton.Left);
        window.MouseMove(At(300, 200), RawInputModifiers.LeftMouseButton);
        window.MouseMove(At(400, 260), RawInputModifiers.LeftMouseButton);
        window.MouseUp(At(400, 260), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.True(session.IsEditingText);
        var layer = session.TextEditLayer!;
        Assert.Equal((300d, 160d), (layer.Text!.BoxWidth, layer.Text.BoxHeight));
        window.KeyTextInput("Words that wrap inside the box they were dragged out for");
        Dispatcher.UIThread.RunJobs();
        Assert.True(session.TextEdit!.Layout.Lines.Count > 1);
        Assert.Equal(300, layer.Pixels!.Width);
        // The bottom-right handle grows the box; the top-left corner stays put.
        var corner = At(400, 260);
        window.MouseDown(corner, MouseButton.Left);
        window.MouseMove(At(460, 300), RawInputModifiers.LeftMouseButton);
        window.MouseUp(At(460, 300), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal((360d, 200d), (layer.Text!.BoxWidth, layer.Text.BoxHeight));
        Assert.Equal((100d, 100d), (layer.Transform.X, layer.Transform.Y));
        Capture("20b-paragraph-box");
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.False(session.IsEditingText);
        Assert.Single(session.Document.Layers);
        Assert.False(session.CanUndo);
    }

    [AvaloniaFact]
    public void Clicking_existing_text_places_the_caret_and_the_bar_changes_the_style()
    {
        var layer = session.AddText(new SKPoint(100, 100), new TextStyle { Text = "Hello", Size = 60, FontFamily = session.TextDefaults.FontFamily });
        Dispatcher.UIThread.RunJobs();
        Click(105, 130); // Just inside the first letter.
        Assert.True(session.IsEditingText);
        Assert.Equal(0, session.TextEdit!.Caret);
        window.KeyTextInput("Oh ");
        Assert.Equal("Oh Hello", layer.Text!.Text);
        session.ChangeTextStyle(s => s with { Alignment = TextAlignment.Center, Size = 40 });
        Assert.Equal(40, layer.Text.Size);
        // Switching tools commits.
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.Control);
        window.SelectTool(Tool.Move);
        Assert.False(session.IsEditingText);
        Assert.Equal("Oh Hello", session.Document.Find(layer.Id)!.Text!.Text);
        Assert.Equal("Edit Text", session.History.UndoName);
        session.Undo();
        Assert.Equal("Hello", session.Document.Find(layer.Id)!.Text!.Text);
    }
}
