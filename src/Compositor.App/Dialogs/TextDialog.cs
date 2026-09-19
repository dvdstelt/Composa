using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Compositor.Editing;
using Compositor.Model;
using SkiaSharp;
using TextAlignment = Compositor.Model.TextAlignment;

namespace Compositor.App.Dialogs;

public static class TextDialog
{
    /// <summary>Edits text settings; <paramref name="changed"/> runs (debounced) on every change so the canvas follows along.</summary>
    public static async Task<TextStyle?> Edit(Window owner, TextStyle initial, Action<TextStyle> changed)
    {
        var current = initial;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        timer.Tick += (_, _) => { timer.Stop(); changed(current); };
        void Update(TextStyle value) { current = value; timer.Stop(); timer.Start(); }

        var box = new TextBox
        {
            Text = initial.Text, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, Width = 420, Height = 150, PlaceholderText = "Type here",
            VerticalContentAlignment = VerticalAlignment.Top
        };
        box.TextChanged += (_, _) => Update(current with { Text = box.Text ?? "" });

        var families = EditorSession.FontFamilies;
        var family = families.Contains(initial.FontFamily) ? initial.FontFamily : families.FirstOrDefault(f => f.Contains("Sans", StringComparison.OrdinalIgnoreCase)) ?? families.FirstOrDefault() ?? initial.FontFamily;
        if (family != initial.FontFamily) current = current with { FontFamily = family };
        var font = Ui.Combo(families, family, f => f, f => Update(current with { FontFamily = f }), 230);
        var size = Ui.Number(initial.Size, 4, 2000, v => Update(current with { Size = v }), 1, "0.#", 80);
        var bold = Ui.Check("Bold", initial.Bold, v => Update(current with { Bold = v }));
        var italic = Ui.Check("Italic", initial.Italic, v => Update(current with { Italic = v }));
        var alignment = Ui.Combo(Enum.GetValues<TextAlignment>(), initial.Alignment, a => a.ToString(), a => Update(current with { Alignment = a }), 100);
        var spacing = Ui.Number(initial.LineSpacing, 0.5, 4, v => Update(current with { LineSpacing = v }), 0.05, "0.00", 70);

        var swatch = new Border { Width = 44, Height = 24, CornerRadius = new CornerRadius(3), BorderBrush = Brushes.White, BorderThickness = new Thickness(1), Background = new SolidColorBrush(new SKColor(initial.Color).ToAvalonia()), Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) };
        swatch.PointerPressed += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(swatch) is not Window window || await Prompts.Color(window, "Text Color", new SKColor(current.Color)) is not { } picked) return;
            swatch.Background = new SolidColorBrush(picked.ToAvalonia());
            Update(current with { Color = (uint)picked });
        };

        var body = Ui.Column(12, box,
            Ui.Row(10, Ui.Label("Font", Palette.Secondary), font, Ui.Label("Size", Palette.Secondary), size, Ui.Label("px", Palette.Secondary)),
            Ui.Row(14, bold, italic, Ui.Label("Align", Palette.Secondary), alignment, Ui.Label("Line spacing", Palette.Secondary), spacing, Ui.Label("Color", Palette.Secondary), swatch));
        var dialog = new DialogWindow(initial.Text.Length == 0 ? "Add Text" : "Edit Text", body);
        dialog.Opened += (_, _) => { box.Focus(); box.SelectAll(); if (current != initial) changed(current); };
        var accepted = await dialog.Ask(owner);
        timer.Stop();
        return accepted ? current : null;
    }
}
