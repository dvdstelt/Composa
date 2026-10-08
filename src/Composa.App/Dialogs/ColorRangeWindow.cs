using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Composa.Editing;
using Composa.Selections;

namespace Composa.App.Dialogs;

/// <summary>
/// Select > Color Range's panel: the eyedroppers, a black-and-white preview, Fuzziness and Invert, with the selection
/// following on the canvas. It is the one panel that is not modal, because the colors are clicked on the canvas
/// itself; it closes by itself when the edit ends for any reason, including another edit beginning.
/// </summary>
public sealed class ColorRangeWindow : DialogWindow
{
    private readonly EditorSession session;
    private readonly Action refresh;
    private bool ending;

    private ColorRangeWindow(EditorSession session, Control body, Action refresh) : base("Color Range", body)
    {
        this.session = session;
        this.refresh = refresh;
        WindowStartupLocation = WindowStartupLocation.Manual;
        session.ColorRangeChanged += OnChanged;
        Closed += (_, _) => session.ColorRangeChanged -= OnChanged;
        refresh();
    }

    /// <summary>Opens the panel beside the owner's right edge, for an edit the session has already begun.</summary>
    public static ColorRangeWindow Open(Window owner, EditorSession session)
    {
        var modes = Enum.GetValues<ColorRangeSample>();
        var buttons = new List<(ColorRangeSample Mode, ToggleButton Button)>();
        foreach (var mode in modes)
        {
            var button = new ToggleButton { Classes = { "tool" }, Width = 36, Height = 26, Content = Eyedropper(mode) };
            ToolTip.SetTip(button, mode switch
            {
                ColorRangeSample.Sample => Loc.T("Click the image to select that color"),
                ColorRangeSample.Add => Loc.T("Click the image to add that color to the selection (or Shift-click with any eyedropper)"),
                _ => Loc.T("Click the image to take that color out of the selection (or Alt-click with any eyedropper)")
            });
            button.Click += (_, _) => session.SetColorRangeSampleMode(mode);
            buttons.Add((mode, button));
        }
        var preview = new Image { Stretch = Stretch.Uniform };
        var frame = new Border
        {
            Background = Brushes.Black, BorderBrush = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)), BorderThickness = new Thickness(1),
            Width = ColorRange.PreviewWidth, Height = ColorRange.PreviewHeight, Child = preview, HorizontalAlignment = HorizontalAlignment.Center
        };
        var hint = new TextBlock { Foreground = Palette.Secondary, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = ColorRange.PreviewWidth };
        var fuzziness = Ui.SliderField("Fuzziness", ColorRange.DefaultFuzziness, ColorRange.MinFuzziness, ColorRange.MaxFuzziness,
            v => session.SetColorRangeFuzziness((int)v), width: ColorRange.PreviewWidth, reset: ColorRange.DefaultFuzziness);
        ToolTip.SetTip(fuzziness, Loc.T("How far a color may be from the picked ones and still be selected"));
        var invert = Ui.Check("Invert", false, v => session.SetColorRangeInvert(v));
        ToolTip.SetTip(invert, Loc.T("Select everything except those colors, such as all but a green screen"));
        var body = Ui.Column(12, Ui.Row(6, buttons.Select(b => (Control)b.Button).ToArray()), frame, hint, fuzziness, invert);

        void Refresh()
        {
            if (session.ColorRange is not { } edit) return;
            foreach (var (mode, button) in buttons) button.IsChecked = mode == edit.SampleMode;
            preview.Source = edit.Preview == null ? null : Ui.ToAvaloniaBitmap(edit.Preview, ColorRange.PreviewWidth * 2);
            hint.Text = edit.HasColors
                ? Loc.Format("Shift-click adds a color, Alt-click takes one away. {0} pixels selected.", edit.Count.ToString("N0"))
                : Loc.T("Click the image to pick the color to select.");
            fuzziness.Value = edit.Fuzziness;
            invert.IsChecked = edit.Invert;
        }

        var window = new ColorRangeWindow(session, body, Refresh);
        // Beside the canvas rather than over it, so the picture stays clickable.
        window.Position = new PixelPoint(owner.Position.X + Math.Max(0, (int)owner.Bounds.Width - 380), owner.Position.Y + 120);
        window.Show(owner);
        return window;
    }

    /// <summary>The eyedropper, with a plus or minus beside it for Add and Remove.</summary>
    private static Control Eyedropper(ColorRangeSample mode)
    {
        var icon = Icons.Create(Icons.Eyedropper, 15);
        if (mode == ColorRangeSample.Sample) return icon;
        var badge = new TextBlock { Text = mode == ColorRangeSample.Add ? "+" : "−", FontSize = 11, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1, Children = { icon, badge } };
    }

    private void OnChanged()
    {
        if (session.ColorRange != null) { refresh(); return; }
        // The edit ended: through this panel, another edit, or a closed tab.
        if (ending) return;
        ending = true;
        Close();
    }

    protected override void Accept()
    {
        if (ending) return;
        session.CommitColorRange();
    }

    protected override void Reject()
    {
        if (ending) return;
        session.CancelColorRange();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        // The window's own close button cancels, as Escape does.
        if (!ending && session.ColorRange != null) { ending = true; session.CancelColorRange(); }
    }
}
