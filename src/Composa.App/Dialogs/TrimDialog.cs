using Avalonia;
using Avalonia.Controls;
using Composa.Editing;

namespace Composa.App.Dialogs;

/// <summary>Image &gt; Trim: what counts as empty along the edges, and which edges to take away.</summary>
public static class TrimDialog
{
    public static async Task<TrimOptions?> Show(Window owner, TrimOptions initial)
    {
        var options = initial;
        var basedOn = new StackPanel { Spacing = 4 };
        foreach (var choice in Enum.GetValues<TrimBasedOn>())
        {
            var radio = new RadioButton { Content = TrimOptions.DisplayName(choice), IsChecked = choice == initial.BasedOn, GroupName = "trim-based-on" };
            radio.IsCheckedChanged += (_, _) => { if (radio.IsChecked == true) options = options with { BasedOn = choice }; };
            basedOn.Children.Add(radio);
        }
        var edges = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,24,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto") };
        DialogWindow dialog = null!;
        void Edge(string label, bool value, Func<TrimOptions, bool, TrimOptions> set, int row, int column)
        {
            var box = Ui.Check(label, value, v => { options = set(options, v); dialog.CanAccept = options.TrimsAny; });
            Grid.SetRow(box, row);
            Grid.SetColumn(box, column);
            edges.Children.Add(box);
        }
        Edge("Top", initial.Top, (o, v) => o with { Top = v }, 0, 0);
        Edge("Bottom", initial.Bottom, (o, v) => o with { Bottom = v }, 0, 2);
        Edge("Left", initial.Left, (o, v) => o with { Left = v }, 1, 0);
        Edge("Right", initial.Right, (o, v) => o with { Right = v }, 1, 2);
        var body = Ui.Column(14,
            Ui.Label("Based On", Palette.Secondary, weight: Avalonia.Media.FontWeight.SemiBold), basedOn,
            Ui.Separator(false),
            Ui.Label("Trim Away", Palette.Secondary, weight: Avalonia.Media.FontWeight.SemiBold), edges);
        body.MinWidth = 260;
        dialog = new DialogWindow("Trim", body) { CanAccept = initial.TrimsAny };
        return await dialog.Ask(owner) ? options : null;
    }
}
