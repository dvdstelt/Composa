using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.App.Controls;
using Composa.App.Dialogs;
using Composa.Editing;
using Composa.Model;
using SkiaSharp;

namespace Composa.App.Tests;

/// <summary>The Drop Shadow dialog's angle dial and the field beside it.</summary>
public class AngleDialTests
{
    private readonly MainWindow window;
    private readonly EditorSession session;
    private readonly Layer layer;
    private readonly Window dialog;
    private readonly AngleDial dial;
    private readonly SliderField field;

    public AngleDialTests()
    {
        window = new MainWindow { Width = 1280, Height = 800 };
        window.Show();
        session = EditorSession.NewCanvas(600, 400, SKColors.White);
        window.AddSession(session);
        var box = Rendering.Pixels.NewColor(200, 120);
        using (var canvas = new SKCanvas(box))
        using (var paint = new SKPaint { Color = new SKColor(0xFF, 0x8A, 0x3D), IsAntialias = true })
            canvas.DrawRoundRect(new SKRect(0, 0, 200, 120), 24, 24, paint);
        layer = session.AddImageLayer("Card", box);
        session.AddEffect(layer, LayerEffectKind.DropShadow);
        session.SetEffects(layer, layer.Effects! with { Shadow = layer.Effects.Shadow! with { Angle = 139, Distance = 10, Blur = 10, Opacity = 0.74 } });
        _ = EffectsDialog.Edit(window, session, layer, LayerEffectKind.DropShadow);
        Dispatcher.UIThread.RunJobs();
        dialog = window.OwnedWindows.Last();
        dial = dialog.GetVisualDescendants().OfType<AngleDial>().Single();
        field = dialog.GetVisualDescendants().OfType<SliderField>().Single(f => f.Label == "Angle");
    }

    private Point OnDial(double degrees, double radius = 15)
    {
        var radians = degrees * Math.PI / 180;
        var local = new Point(dial.Bounds.Width / 2 + Math.Cos(radians) * radius, dial.Bounds.Height / 2 - Math.Sin(radians) * radius);
        return dial.TranslatePoint(local, dialog)!.Value;
    }

    private double Angle => session.Document.Find(layer.Id)!.Effects!.Shadow!.Angle;

    [AvaloniaFact]
    public void The_dial_and_the_field_open_on_the_shadow_angle_and_follow_each_other()
    {
        Assert.Equal(139, dial.Value);
        Assert.Equal(139, field.Value);

        // A press turns the hand to the pointer: straight up is 90, as Photoshop counts it.
        dialog.MouseDown(OnDial(90), MouseButton.Left);
        dialog.MouseUp(OnDial(90), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(90, Angle);
        Assert.Equal(90, field.Value);

        // Dragging around the rim keeps turning it, and the offset follows the light: light from the left drops the shadow right.
        dialog.MouseDown(OnDial(0), MouseButton.Left);
        dialog.MouseMove(OnDial(-45), RawInputModifiers.LeftMouseButton);
        dialog.MouseMove(OnDial(180), RawInputModifiers.LeftMouseButton);
        dialog.MouseUp(OnDial(180), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(180, Angle);
        var (dx, dy) = session.Document.Find(layer.Id)!.Effects!.Shadow!.Offset;
        Assert.Equal(10, dx, 6);
        Assert.Equal(0, dy, 6);

        // Shift snaps to 15 degree steps.
        dialog.MouseDown(OnDial(52), MouseButton.Left, RawInputModifiers.Shift);
        dialog.MouseUp(OnDial(52), MouseButton.Left, RawInputModifiers.Shift);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(45, Angle);

        // Typing into the field turns the dial.
        field.BeginEdit();
        Dispatcher.UIThread.RunJobs();
        var editor = field.GetVisualDescendants().OfType<TextBox>().Single();
        editor.Text = "-120";
        dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(-120, Angle);
        Assert.Equal(-120, dial.Value);

        // The wheel and the arrows turn by one degree; the dial wraps around instead of stopping at the edge.
        dialog.MouseWheel(OnDial(0, 0), new Vector(0, -1), RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(-121, Angle);
        dialog.MouseDown(OnDial(180), MouseButton.Left);
        dialog.MouseUp(OnDial(180), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(180, Angle);
        Assert.True(dial.IsFocused);
        dialog.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(-179, Angle);
        Assert.Equal(-179, field.Value);
    }

    [AvaloniaFact]
    public void The_shadow_dialog_renders_with_the_dial()
    {
        Assert.True(Screenshots.Save(dialog, "46-drop-shadow-dialog"));
        Assert.Equal(4, dialog.GetVisualDescendants().OfType<SliderField>().Count());
    }
}
