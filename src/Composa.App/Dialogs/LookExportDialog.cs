using Avalonia.Controls;
using Avalonia.Media;

namespace Composa.App.Dialogs;

public static class LookDialogs
{
    /// <summary>
    /// Export Look: which layers go into the table, which are left out and why, and how many points the table gets.
    /// Returns the size, or null when cancelled.
    /// </summary>
    public static async Task<int?> ExportLook(Window owner, IReadOnlyList<string> baked, IReadOnlyList<(string Name, string Why)> leftOut, int initialSize)
    {
        var size = Filters.LookBake.Sizes.Contains(initialSize) ? initialSize : 33;
        var rows = new List<Control> { Ui.Label(Loc.T("Baked into the look, bottom to top:")) };
        foreach (var name in baked) rows.Add(Indented(Ui.Label(name, Palette.Secondary)));
        if (leftOut.Count > 0)
        {
            var heading = Ui.Label(Loc.T("Left out, because a look can only change colors:"));
            heading.Margin = new Avalonia.Thickness(0, 8, 0, 0);
            rows.Add(heading);
            foreach (var (name, why) in leftOut) rows.Add(Indented(Ui.Label($"{name} {why}", Palette.Secondary)));
        }
        var sizes = Ui.Combo(Filters.LookBake.Sizes, size, n => Loc.Format("{0} points", n), n => size = n, 130);
        var sizeRow = Ui.Row(8, Ui.Label(Loc.T("Size")), sizes);
        sizeRow.Margin = new Avalonia.Thickness(0, 12, 0, 0);
        rows.Add(sizeRow);
        var body = new StackPanel { Spacing = 4, MaxWidth = 420 };
        foreach (var row in rows) body.Children.Add(row);
        var dialog = new DialogWindow("Export Look", body, "Export…");
        return await dialog.Ask(owner) ? size : null;
    }

    private static Control Indented(TextBlock label)
    {
        label.Margin = new Avalonia.Thickness(16, 0, 0, 0);
        label.TextWrapping = TextWrapping.Wrap;
        return label;
    }
}
