using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Composa.Editing;
using Composa.Model;
using SkiaSharp;

namespace Composa.App.Tests;

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

    [AvaloniaFact]
    public void Printable_keys_stay_unhandled_while_typing_so_the_platform_delivers_the_characters()
    {
        // X11 only sends text input for a key press nobody handled, so a handled letter would never be typed.
        var handled = new Dictionary<Key, bool>();
        window.AddHandler(InputElement.KeyDownEvent, (_, e) => handled[e.Key] = e.Handled, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        Click(80, 120);
        Assert.True(session.IsEditingText);
        window.KeyPressQwerty(PhysicalKey.B, RawInputModifiers.None);
        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        window.KeyPressQwerty(PhysicalKey.Digit1, RawInputModifiers.None);
        window.KeyPressQwerty(PhysicalKey.Period, RawInputModifiers.Shift);
        window.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.Control);
        window.KeyPressQwerty(PhysicalKey.D, RawInputModifiers.Control);
        window.KeyPressQwerty(PhysicalKey.Backspace, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.False(handled[Key.B]);                                        // Left for the text input that follows.
        Assert.False(handled[Key.Space]);
        Assert.False(handled[Key.D1]);
        Assert.False(handled[Key.OemPeriod]);
        Assert.True(handled[Key.A]);                                         // Ctrl+A selected all in the text.
        Assert.True(handled[Key.D]);                                         // Ctrl+D swallowed: no deselect while typing.
        Assert.True(handled[Key.Back]);
        Assert.Equal(Tool.Text, session.Tool);                               // B did not switch to the Brush...
        Assert.True(session.IsEditingText);                                  // ...and 1 did not change any opacity or end the edit.
        Assert.Equal(1, session.ActiveLayer!.Opacity);
    }

    [AvaloniaFact]
    public void Clicking_starts_point_text_that_is_typed_live_and_committed_with_ctrl_enter()
    {
        Click(80, 120);
        Assert.True(session.IsEditingText);
        var layer = session.TextEditLayer!;
        window.KeyTextInput("Composa");
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        window.KeyTextInput("for Linux");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Composa\nfor Linux", layer.Text!.Text);
        Assert.Equal(0xFFFFC857u, layer.Text.Color);
        Screenshots.Save(window, "19-text-typing");
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
        Assert.Equal("Composa for Linux", session.Document.Find(layer.Id)!.Name);
        Screenshots.Save(window, "20-text-layer");
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
        Screenshots.Save(window, "20b-paragraph-box");
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.False(session.IsEditingText);
        Assert.Single(session.Document.Layers);
        Assert.False(session.CanUndo);
    }

    private static async Task Pump(Func<bool> until)
    {
        for (var i = 0; i < 400 && !until(); i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
        Dispatcher.UIThread.RunJobs();
    }

    private void PressSwatch(string tip)
    {
        var swatch = window.GetVisualDescendants().OfType<Border>().First(b => ToolTip.GetTip(b) as string == tip);
        var center = swatch.TranslatePoint(new Point(swatch.Bounds.Width / 2, swatch.Bounds.Height / 2), window)!.Value;
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The picker's working color shows on the text as it changes, from the Type bar's swatch and from the foreground swatch alike; Cancel puts the text's own color back.</summary>
    [AvaloniaFact]
    public async Task Text_being_typed_previews_the_color_picker_and_cancel_restores_it()
    {
        Click(80, 120);
        window.KeyTextInput("Color");
        Dispatcher.UIThread.RunJobs();
        var layer = session.TextEditLayer!;
        Assert.Equal(0xFFFFC857u, layer.Text!.Color);

        PressSwatch("Text color");
        await Pump(() => window.OwnedWindows.Count > 0);
        var dialog = Assert.Single(window.OwnedWindows);
        dialog.GetVisualDescendants().OfType<ColorView>().First().Color = Avalonia.Media.Color.FromRgb(0x20, 0xC0, 0xFF);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0xFF20C0FFu, layer.Text!.Color);                    // Previewed live on the canvas.
        Assert.Equal(new SKColor(0xFF, 0xC8, 0x57), session.Foreground);   // The swatch waits for OK.
        dialog.Close(false);
        await Pump(() => window.OwnedWindows.Count == 0);
        Assert.Equal(0xFFFFC857u, layer.Text!.Color);                    // Cancel: back to its own color.
        Assert.True(session.IsEditingText);

        PressSwatch("Foreground color");
        await Pump(() => window.OwnedWindows.Count > 0);
        dialog = Assert.Single(window.OwnedWindows);
        dialog.GetVisualDescendants().OfType<ColorView>().First().Color = Avalonia.Media.Color.FromRgb(0x10, 0x80, 0x30);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0xFF108030u, layer.Text!.Color);
        dialog.Close(true);
        await Pump(() => window.OwnedWindows.Count == 0);
        Assert.Equal(0xFF108030u, layer.Text!.Color);
        Assert.Equal(new SKColor(0x10, 0x80, 0x30), session.Foreground);   // OK: the text color is the foreground color.
        Assert.Equal("Color", layer.Text.Text);
    }

    /// <summary>A text layer that is only selected previews the picker too, and the pick undoes as one step without opening the text for typing.</summary>
    [AvaloniaFact]
    public async Task A_selected_text_layer_previews_the_color_picker_without_opening_for_typing()
    {
        var layer = session.AddText(new SKPoint(100, 100), session.TextDefaults with { Text = "Hello", Color = 0xFF000000 });
        window.SelectTool(Tool.Text);
        Dispatcher.UIThread.RunJobs();
        var steps = session.History.Count;

        PressSwatch("Text color");
        await Pump(() => window.OwnedWindows.Count > 0);
        var dialog = Assert.Single(window.OwnedWindows);
        dialog.GetVisualDescendants().OfType<ColorView>().First().Color = Avalonia.Media.Color.FromRgb(0x20, 0xC0, 0xFF);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0xFF20C0FFu, layer.Text!.Color);                    // Previewed live on the canvas.
        Assert.False(session.IsEditingText);
        dialog.Close(false);
        await Pump(() => window.OwnedWindows.Count == 0);
        Assert.Equal(0xFF000000u, session.Document.Find(layer.Id)!.Text!.Color); // Cancel: back to its own color.
        Assert.Equal(steps, session.History.Count);                       // And no step left behind.

        PressSwatch("Text color");
        await Pump(() => window.OwnedWindows.Count > 0);
        dialog = Assert.Single(window.OwnedWindows);
        dialog.GetVisualDescendants().OfType<ColorView>().First().Color = Avalonia.Media.Color.FromRgb(0x10, 0x80, 0x30);
        Dispatcher.UIThread.RunJobs();
        dialog.GetVisualDescendants().OfType<ColorView>().First().Color = Avalonia.Media.Color.FromRgb(0x10, 0x80, 0x40);
        Dispatcher.UIThread.RunJobs();
        dialog.Close(true);
        await Pump(() => window.OwnedWindows.Count == 0);
        Assert.Equal(0xFF108040u, session.Document.Find(layer.Id)!.Text!.Color);
        Assert.Equal(new SKColor(0x10, 0x80, 0x40), session.Foreground);   // OK: the text color is the foreground color.
        Assert.False(session.IsEditingText);
        Assert.Equal(steps + 1, session.History.Count);                   // The whole pick is one step.
        Assert.Equal("Change Text Style", session.History.UndoName);
        session.Undo();
        Assert.Equal(0xFF000000u, session.Document.Find(layer.Id)!.Text!.Color);
    }

    /// <summary>With the Move tool, a double-click on live text opens it for typing where the pointer is; the toolbar follows to the Type tool.</summary>
    [AvaloniaFact]
    public void Double_clicking_live_text_with_the_move_tool_opens_it_for_typing()
    {
        var layer = session.AddText(new SKPoint(100, 100), session.TextDefaults with { Text = "Hello" });
        window.SelectTool(Tool.Move);
        Dispatcher.UIThread.RunJobs();
        // A little in from the right edge of the letters, so the caret lands at the end of the word.
        var x = (float)(layer.Transform.X + layer.Transform.Width - Text.TextLayout.Padding - 2);
        var y = (float)(layer.Transform.Y + layer.Transform.Height / 2);
        Click(x, y);
        Assert.False(session.IsEditingText);            // One click moves, as before.
        Click(x, y);                                     // The second click of a double-click.
        Assert.True(session.IsEditingText);
        Assert.Equal(layer.Id, session.TextEditLayer!.Id);
        Assert.Equal(Tool.Text, session.Tool);
        Assert.Equal(5, session.TextEdit!.Caret);
        var typeButton = window.GetVisualDescendants().OfType<ToggleButton>().Single(b => (ToolTip.GetTip(b) as string)?.StartsWith("Type", StringComparison.Ordinal) == true);
        var moveButton = window.GetVisualDescendants().OfType<ToggleButton>().Single(b => (ToolTip.GetTip(b) as string)?.StartsWith("Move", StringComparison.Ordinal) == true);
        Assert.True(typeButton.IsChecked);
        Assert.False(moveButton.IsChecked);
        window.KeyTextInput("!");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Hello!", session.TextEditLayer.Text!.Text);
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Edit Text", session.History.UndoName);
    }

    [AvaloniaFact]
    public void Typing_a_size_in_the_bar_restyles_the_layer_without_opening_it_for_typing()
    {
        var layer = session.AddText(new SKPoint(100, 100), new TextStyle { Text = "Hello", Size = 73, FontFamily = session.TextDefaults.FontFamily });
        window.SelectTool(Tool.Text); // Rebuilds the bar for the new layer.
        Dispatcher.UIThread.RunJobs();
        var size = window.GetVisualDescendants().OfType<NumericUpDown>().First(n => n.Value == 73);
        var box = size.GetVisualDescendants().OfType<TextBox>().First();
        box.Focus();
        box.SelectAll();
        window.KeyPressQwerty(PhysicalKey.Backspace, RawInputModifiers.None);
        window.KeyTextInput("61");
        Dispatcher.UIThread.RunJobs();
        Assert.False(session.IsEditingText);
        Assert.Equal("Hello", layer.Text!.Text);
        Assert.Equal(61, layer.Text.Size);
        Assert.Same(box, window.FocusManager!.GetFocusedElement()); // The keyboard stays in the field.
        Assert.Equal("Change Text Style", session.History.UndoName);
        session.Undo();
        Assert.Equal(73, session.Document.Find(layer.Id)!.Text!.Size);
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
