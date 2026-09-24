using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Composa.Editing;
using Composa.Model;
using SkiaSharp;

namespace Composa.App.Dialogs;

/// <summary>One effect's controls, bound to the layer that opened the panel. Changes preview on the canvas.</summary>
public static class EffectsDialog
{
    /// <summary>
    /// Edits one effect of a layer. Every change is written to the layer at once so the canvas follows; the caller
    /// wraps the call in Begin/Commit (or Cancel), so the whole dialog undoes as one step.
    /// </summary>
    public static async Task<bool> Edit(Window owner, EditorSession session, Layer layer, LayerEffectKind kind)
    {
        var effects = layer.Effects ?? LayerEffects.Empty;
        if (!effects.Contains(kind)) return false;
        void Set(LayerEffects next) { effects = next; session.SetEffects(layer, next); }
        var rows = new StackPanel { Spacing = 10 };
        const double fieldWidth = 330;
        Control Slider(string label, double value, double min, double max, Action<double> changed, double step = 1, string format = "0") =>
            Ui.SliderField(label, value, min, max, changed, step, format, fieldWidth);
        // Photoshop's dial for the light's direction, with the field beside it for an exact number; each follows the other.
        Control Angle(double value, Action<double> changed)
        {
            var dial = new Controls.AngleDial(value);
            var field = Ui.SliderField("Angle", dial.Value, -180, 180, v => { dial.Value = v; changed(v); }, 1, "0", fieldWidth - Controls.AngleDial.DefaultSize - 10);
            dial.Changed += v => { field.Value = v; changed(v); };
            return Ui.Row(10, dial, field);
        }
        Control Swatch()
        {
            var swatch = new Border { Width = 44, Height = 24, CornerRadius = new CornerRadius(3), BorderBrush = Brushes.White, BorderThickness = new Thickness(1), Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) };
            void Paint() => swatch.Background = new SolidColorBrush(new SKColor(effects.ColorOf(kind) ?? 0xFF000000).ToAvalonia());
            Paint();
            ToolTip.SetTip(swatch, LayerEffects.DisplayName(kind) + " color");
            swatch.PointerPressed += async (_, _) =>
            {
                if (TopLevel.GetTopLevel(swatch) is not Window window || await Prompts.Color(window, LayerEffects.DisplayName(kind) + " Color", new SKColor(effects.ColorOf(kind) ?? 0xFF000000)) is not { } picked) return;
                Set(effects.WithColor(kind, (uint)picked | 0xFF000000));
                Paint();
            };
            return Ui.Row(10, Ui.Label("Color", Palette.Secondary, weight: FontWeight.Normal) is var l ? Width(l, 70) : l, swatch);
        }

        switch (kind)
        {
            case LayerEffectKind.Stroke:
                var stroke = effects.Stroke!;
                rows.Children.Add(Ui.Row(10, Width(Ui.Label("Position", Palette.Secondary), 70),
                    Ui.Combo(new[] { "Outside", "Inside" }, stroke.Inside ? "Inside" : "Outside", v => v, v => Set(effects with { Stroke = effects.Stroke! with { Inside = v == "Inside" } }), 120)));
                rows.Children.Add(Swatch());
                rows.Children.Add(Slider("Size", stroke.Size, 0, 100, v => Set(effects with { Stroke = effects.Stroke! with { Size = v } })));
                rows.Children.Add(Slider("Opacity", stroke.Opacity * 100, 0, 100, v => Set(effects with { Stroke = effects.Stroke! with { Opacity = v / 100 } })));
                break;
            case LayerEffectKind.DropShadow:
            case LayerEffectKind.InnerShadow:
                var inner = kind == LayerEffectKind.InnerShadow;
                var shadow = inner ? effects.InnerShadow! : effects.Shadow!;
                void SetShadow(Func<ShadowEffect, ShadowEffect> change) => Set(inner ? effects with { InnerShadow = change(effects.InnerShadow!) } : effects with { Shadow = change(effects.Shadow!) });
                rows.Children.Add(Swatch());
                rows.Children.Add(Slider("Opacity", shadow.Opacity * 100, 0, 100, v => SetShadow(s => s with { Opacity = v / 100 })));
                rows.Children.Add(Angle(shadow.Angle, v => SetShadow(s => s with { Angle = v })));
                rows.Children.Add(Slider("Distance", shadow.Distance, 0, inner ? 100 : 250, v => SetShadow(s => s with { Distance = v })));
                rows.Children.Add(Slider("Blur", shadow.Blur, 0, 250, v => SetShadow(s => s with { Blur = v })));
                break;
            case LayerEffectKind.ColorOverlay:
                rows.Children.Add(Swatch());
                rows.Children.Add(Slider("Opacity", effects.ColorOverlay!.Opacity * 100, 0, 100, v => Set(effects with { ColorOverlay = effects.ColorOverlay! with { Opacity = v / 100 } })));
                break;
            case LayerEffectKind.OuterGlow:
                var glow = effects.OuterGlow!;
                rows.Children.Add(Swatch());
                rows.Children.Add(Slider("Opacity", glow.Opacity * 100, 0, 100, v => Set(effects with { OuterGlow = effects.OuterGlow! with { Opacity = v / 100 } })));
                rows.Children.Add(Slider("Size", glow.Size, 0, 250, v => Set(effects with { OuterGlow = effects.OuterGlow! with { Size = v } })));
                break;
            case LayerEffectKind.InnerGlow:
                var innerGlow = effects.InnerGlow!;
                rows.Children.Add(Swatch());
                rows.Children.Add(Slider("Opacity", innerGlow.Opacity * 100, 0, 100, v => Set(effects with { InnerGlow = effects.InnerGlow! with { Opacity = v / 100 } })));
                rows.Children.Add(Slider("Size", innerGlow.Size, 0, 100, v => Set(effects with { InnerGlow = effects.InnerGlow! with { Size = v } })));
                break;
        }
        var dialog = new DialogWindow(LayerEffects.DisplayName(kind), rows);
        return await dialog.Ask(owner);
    }

    private static Control Width(TextBlock label, double width)
    {
        label.Width = width;
        return label;
    }
}
