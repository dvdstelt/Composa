using Avalonia.Controls;
using Avalonia.Media;
using Compositor.IO.Psd;

namespace Compositor.App.Dialogs;

/// <summary>What a Photoshop file loses on the way in, listed per layer, with the choice to go ahead or not.</summary>
public static class PsdConversionDialog
{
    public static Task<bool> Confirm(Window owner, string fileName, IReadOnlyList<PsdConversion> conversions)
    {
        var rows = new StackPanel { Spacing = 10 };
        foreach (var item in conversions.Take(500))
        {
            var message = Ui.Label(item.Message, Palette.Foreground);
            message.TextWrapping = TextWrapping.Wrap;
            message.MaxWidth = 470;
            rows.Children.Add(Ui.Column(2, Ui.Label(item.LayerName, Palette.Foreground, weight: FontWeight.SemiBold), message));
        }
        if (conversions.Count > 500) rows.Children.Add(Ui.Label($"…and {conversions.Count - 500} more.", Palette.Secondary));
        var intro = Ui.Label("Compositor will convert these Photoshop features. Nothing is applied until you continue.", Palette.Secondary);
        intro.TextWrapping = TextWrapping.Wrap;
        intro.MaxWidth = 500;
        var list = new ScrollViewer { Content = rows, MaxHeight = 320, Width = 500, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        var body = Ui.Column(12, intro, list);
        return new DialogWindow($"Open {fileName}?", body, "Import").Ask(owner);
    }
}
