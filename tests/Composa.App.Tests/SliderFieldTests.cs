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

/// <summary>The options bar's slider fields: drag to change, Alt-drag for fine steps, double-click to type, wheel and arrows step by one.</summary>
public class SliderFieldTests
{
    private readonly MainWindow window;
    private readonly EditorSession session;

    public SliderFieldTests()
    {
        window = new MainWindow { Width = 1280, Height = 800 };
        window.Show();
        session = EditorSession.NewCanvas(600, 400, SKColors.White);
        window.AddSession(session);
        Dispatcher.UIThread.RunJobs();
    }

    private SliderField Field(string label) => window.GetVisualDescendants().OfType<SliderField>().Single(f => f.Label == label);

    private Point Center(Control control) => control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

    private SliderField Feather()
    {
        window.SelectTool(Tool.Marquee);
        Dispatcher.UIThread.RunJobs();
        var field = Field("Feather");
        Assert.Equal(0, session.Feather);
        Assert.Equal(120, field.Bounds.Width);
        return field;
    }

    [AvaloniaFact]
    public void Dragging_maps_the_field_width_to_the_range_and_Alt_slows_it_ten_times()
    {
        var field = Feather();
        var start = Center(field);
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(start + new Vector(60, 0), RawInputModifiers.LeftMouseButton);
        window.MouseUp(start + new Vector(60, 0), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        // 120 px stand for 0 to 100, so 60 px are 50.
        Assert.Equal(50, session.Feather);
        Assert.Equal(50, field.Value);

        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(start + new Vector(-60, 0), RawInputModifiers.LeftMouseButton | RawInputModifiers.Alt);
        window.MouseUp(start + new Vector(-60, 0), MouseButton.Left, RawInputModifiers.Alt);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(45, session.Feather);
    }

    [AvaloniaFact]
    public void A_press_that_does_not_travel_leaves_the_value_alone()
    {
        var field = Feather();
        var at = Center(field) + new Vector(40, 0);
        window.MouseDown(at, MouseButton.Left);
        window.MouseMove(at + new Vector(2, 0), RawInputModifiers.LeftMouseButton);
        window.MouseUp(at + new Vector(2, 0), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, session.Feather);
        Assert.True(field.IsFocused);
    }

    [AvaloniaFact]
    public void Double_click_types_an_exact_value_and_Escape_abandons_it()
    {
        var field = Feather();
        var at = Center(field);
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.True(field.IsEditing);
        var editor = field.GetVisualDescendants().OfType<TextBox>().Single();
        Assert.True(editor.IsFocused);
        Assert.Equal("0", editor.Text);
        window.KeyTextInput("22");
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.False(field.IsEditing);
        Assert.Equal(22, session.Feather);

        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.True(field.IsEditing);
        window.KeyTextInput("99");
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.False(field.IsEditing);
        Assert.Equal(22, session.Feather);
        Assert.Equal(22, field.Value);
    }

    [AvaloniaFact]
    public void Typed_values_are_clamped_and_rounded_to_the_step()
    {
        var field = Feather();
        field.BeginEdit();
        Dispatcher.UIThread.RunJobs();
        var editor = field.GetVisualDescendants().OfType<TextBox>().Single();
        editor.Text = "250";
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(100, session.Feather);
        field.BeginEdit();
        Dispatcher.UIThread.RunJobs();
        editor.Text = "12.6";
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(13, session.Feather);
    }

    [AvaloniaFact]
    public void The_wheel_and_the_arrow_keys_step_by_one_and_the_canvas_does_not_take_the_arrows()
    {
        session.AddImageLayer("box", Rendering.Pixels.NewColor(80, 60), new SKPoint(300, 200));
        var layerX = session.ActiveLayer!.Transform.X;
        var field = Feather();
        var at = Center(field);
        window.MouseWheel(at, new Vector(0, 1), RawInputModifiers.None);
        window.MouseWheel(at, new Vector(0, 1), RawInputModifiers.None);
        window.MouseWheel(at, new Vector(0, -1), RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, session.Feather);

        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.True(field.IsFocused);
        window.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
        window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
        window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        window.KeyPressQwerty(PhysicalKey.End, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(100, session.Feather);
        window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(99, session.Feather);
        Assert.Equal(layerX, session.ActiveLayer!.Transform.X);

        // A letter still reaches the tool shortcuts while the field has focus.
        window.KeyPressQwerty(PhysicalKey.B, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(Tool.Brush, session.Tool);
    }

    [AvaloniaFact]
    public void Setting_the_value_from_code_redraws_without_reporting_a_change()
    {
        var changes = new List<double>();
        var field = new SliderField("Size", 10, 1, 500) { Width = 120 };
        field.Changed += changes.Add;
        var host = new Window { Width = 300, Height = 100, Content = new StackPanel { Children = { field } } };
        host.Show();
        Dispatcher.UIThread.RunJobs();
        field.Value = 250;
        Assert.Equal(250, field.Value);
        field.Value = 9999;
        Assert.Equal(500, field.Value);
        Assert.Empty(changes);
        field.Nudge(-1);
        Assert.Equal([499], changes);
        Assert.NotNull(ToolTip.GetTip(field));
    }

    [AvaloniaFact]
    public void The_brush_bar_refreshes_its_fields_from_the_session()
    {
        window.SelectTool(Tool.Brush);
        Dispatcher.UIThread.RunJobs();
        var size = Field("Size");
        Assert.Equal(session.Brush.Size, size.Value);
        window.KeyPressQwerty(PhysicalKey.BracketRight, RawInputModifiers.None);
        window.KeyPressQwerty(PhysicalKey.BracketRight, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(session.Brush.Size, size.Value);
        Assert.NotEqual(0, size.Value);
    }

    [AvaloniaFact]
    public void The_bars_render_their_fields()
    {
        window.SelectTool(Tool.Brush);
        Dispatcher.UIThread.RunJobs();
        Field("Hardness").Value = 63;
        Screenshots.Save(window, "42-slider-fields-brush");
        window.SelectTool(Tool.Marquee);
        Dispatcher.UIThread.RunJobs();
        session.Feather = 22;
        window.SelectTool(Tool.Marquee);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(22, Field("Feather").Value);
        Screenshots.Save(window, "43-slider-fields-marquee");
        session.AddText(new SKPoint(100, 100), session.TextDefaults with { Text = "Hello" });
        window.SelectTool(Tool.Text);
        Dispatcher.UIThread.RunJobs();
        Screenshots.Save(window, "44-options-bar-type");
        window.SelectTool(Tool.Move);
        Dispatcher.UIThread.RunJobs();
        Screenshots.Save(window, "45-options-bar-move");
    }
}

/// <summary>A colored track along the field and the Reset button that the editor shows on the left.</summary>
public class SliderFieldTrackTests
{
    private readonly Window window;
    private readonly SliderField field;

    public SliderFieldTrackTests()
    {
        field = Ui.SliderField("Hue", 30, -180, 180, _ => { }, 1, "0", 330, SliderTracks.Spectrum(0), reset: 0);
        window = new Window { Width = 400, Height = 100, Content = new StackPanel { Margin = new Thickness(20), Children = { field } } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
    }

    private Point Center(Control control) => control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

    private void DoubleClick(Point at)
    {
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void The_spectrum_runs_around_the_hue_circle_and_the_reset_button_puts_the_value_back()
    {
        var reported = new List<double>();
        field.Changed += reported.Add;
        var track = SliderTracks.Spectrum(0);
        Assert.Equal(13, track.Count);
        Assert.Equal(track[0], track[12]);                                  // Red to red: the circle closes.
        Assert.True(track[6].R > track[6].G && track[6].R > track[6].B);    // Its middle is the centre hue.
        Assert.True(track[2].B > track[2].R && track[2].B > track[2].G);    // Blue sits 120 degrees before it, green 120 after.
        Assert.True(track[10].G > track[10].R && track[10].G > track[10].B);
        Assert.False(field.IsResetVisible);

        DoubleClick(Center(field));
        Assert.True(field.IsEditing);
        Assert.True(field.IsResetVisible);
        var reset = field.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "Reset");
        var button = (Border)reset.Parent!;
        Assert.True(button.Bounds.Left < 10);                                // On the left of the box.
        var editor = field.GetVisualDescendants().OfType<TextBox>().Single();
        Assert.True(editor.Bounds.Left >= button.Bounds.Right);            // The editor keeps the rest.
        Screenshots.Save(window, "42-slider-track-reset");

        var at = Center(button);
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.False(field.IsEditing);
        Assert.False(field.IsResetVisible);
        Assert.Equal(0, field.Value);
        Assert.Equal([0], reported);                                        // One change, from the reset alone.
    }

    [AvaloniaFact]
    public void A_field_without_a_reset_value_has_no_reset_button()
    {
        var plain = Ui.SliderField("Amount", 20, 0, 100, _ => { }, 1, "0", 330, SliderTracks.Chroma);
        ((StackPanel)window.Content!).Children.Add(plain);
        Dispatcher.UIThread.RunJobs();
        DoubleClick(Center(plain));
        Assert.True(plain.IsEditing);
        Assert.False(plain.IsResetVisible);
        var editor = plain.GetVisualDescendants().OfType<TextBox>().Single();
        Assert.Equal(0, editor.Bounds.Left);
        plain.Reset = 0;                                                    // Setting it while open shows the button at once.
        Dispatcher.UIThread.RunJobs();
        Assert.True(plain.IsResetVisible);
    }
}
