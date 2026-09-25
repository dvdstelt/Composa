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

public class EffectsUiTests
{
    [AvaloniaFact]
    public void Effect_rows_select_edit_and_delete()
    {
        var window = new MainWindow { Width = 1280, Height = 800 };
        window.Show();
        var session = EditorSession.NewCanvas(800, 500, SKColors.White);
        window.AddSession(session);
        var box = Rendering.Pixels.NewColor(260, 160);
        using (var canvas = new SKCanvas(box))
        using (var paint = new SKPaint { Color = new SKColor(0xFF, 0x8A, 0x3D), IsAntialias = true })
            canvas.DrawRoundRect(new SKRect(0, 0, 260, 160), 30, 30, paint);
        var layer = session.AddImageLayer("Card", box);
        session.Background = new SKColor(0x20, 0x60, 0xC0);
        session.AddEffect(layer, LayerEffectKind.DropShadow);
        session.AddEffect(layer, LayerEffectKind.Stroke);
        session.AddEffect(layer, LayerEffectKind.OuterGlow);
        session.SetEffects(layer, layer.Effects! with
        {
            Shadow = layer.Effects.Shadow! with { Distance = 18, Blur = 24, Opacity = 0.6 }, Stroke = layer.Effects.Stroke! with { Size = 8 },
            OuterGlow = layer.Effects.OuterGlow! with { Size = 30, Color = 0xFFFFD040 }
        });
        Dispatcher.UIThread.RunJobs();
        Screenshots.Save(window, "22-layer-effects");

        var panel = window.GetVisualDescendants().OfType<LayersPanel>().Single();
        var rows = panel.GetVisualDescendants().OfType<Border>().Where(b => b.Tag is ValueTuple<Layer, LayerEffectKind>).ToList();
        Assert.Equal(3, rows.Count);
        Assert.Equal(LayerEffectKind.OuterGlow, ((ValueTuple<Layer, LayerEffectKind>)rows[^1].Tag!).Item2);
        var strokeRow = rows.Single(r => ((ValueTuple<Layer, LayerEffectKind>)r.Tag!).Item2 == LayerEffectKind.Stroke);
        var point = strokeRow.TranslatePoint(new Point(120, strokeRow.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal((layer.Id, LayerEffectKind.Stroke), session.SelectedEffect);

        // The second click of a double-click opens the effect's settings with a live preview.
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        var dialog = Assert.Single(window.OwnedWindows);
        Assert.Equal("Stroke", dialog.Title);
        Screenshots.Save(dialog, "23-stroke-dialog");
        dialog.Close(false);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(8, session.Document.Find(layer.Id)!.Effects!.Stroke!.Size);

        // Delete removes the highlighted effect rather than the layer.
        session.SelectedEffect = (layer.Id, LayerEffectKind.Stroke);
        window.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(session.Document.Find(layer.Id)!.Effects!.Stroke);
        Assert.NotNull(session.Document.Find(layer.Id));
        Assert.Equal("Remove Stroke", session.History.UndoName);
    }

    [AvaloniaFact]
    public void Alt_dragging_an_effect_row_copies_it_to_another_layer()
    {
        var window = new MainWindow { Width = 1280, Height = 800 };
        window.Show();
        var session = EditorSession.NewCanvas(300, 200, SKColors.White);
        window.AddSession(session);
        var first = session.AddImageLayer("first", Rendering.Pixels.NewColor(40, 40), new SKPoint(60, 60));
        var second = session.AddImageLayer("second", Rendering.Pixels.NewColor(40, 40), new SKPoint(200, 100));
        session.AddEffect(first, LayerEffectKind.ColorOverlay);
        Dispatcher.UIThread.RunJobs();
        var panel = window.GetVisualDescendants().OfType<LayersPanel>().Single();
        var effectRow = panel.GetVisualDescendants().OfType<Border>().Single(b => b.Tag is ValueTuple<Layer, LayerEffectKind>);
        var targetRow = panel.GetVisualDescendants().OfType<Border>().Single(b => b.Tag == second);
        var from = effectRow.TranslatePoint(new Point(120, effectRow.Bounds.Height / 2), window)!.Value;
        var to = targetRow.TranslatePoint(new Point(120, targetRow.Bounds.Height / 2), window)!.Value;
        window.MouseDown(from, MouseButton.Left, RawInputModifiers.Alt);
        window.MouseMove(new Point(from.X, (from.Y + to.Y) / 2), RawInputModifiers.Alt | RawInputModifiers.LeftMouseButton);
        window.MouseMove(to, RawInputModifiers.Alt | RawInputModifiers.LeftMouseButton);
        window.MouseUp(to, MouseButton.Left, RawInputModifiers.Alt);
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(session.Document.Find(second.Id)!.Effects?.ColorOverlay);
        Assert.Equal("Copy Color Overlay", session.History.UndoName);
    }
}
