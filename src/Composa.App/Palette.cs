using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace Composa.App;

public static class Palette
{
    public static readonly IBrush Window = new SolidColorBrush(Color.Parse("#242424"));
    public static readonly IBrush Panel = new SolidColorBrush(Color.Parse("#2B2B2B"));
    public static readonly IBrush PanelRaised = new SolidColorBrush(Color.Parse("#343434"));
    public static readonly IBrush Divider = new SolidColorBrush(Color.Parse("#1A1A1A"));
    public static readonly IBrush Foreground = new SolidColorBrush(Color.Parse("#E4E4E4"));
    public static readonly IBrush Secondary = new SolidColorBrush(Color.Parse("#9A9A9A"));
    public static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#3D9BFF"));
    public static readonly IBrush Selected = new SolidColorBrush(Color.Parse("#3A4A5E"));
    public static readonly IBrush Hover = new SolidColorBrush(Color.Parse("#3A3A3A"));

    public static Styles Styles()
    {
        var styles = new Styles();
        styles.Resources["ComposaForeground"] = Foreground;
        styles.Add(new Style(x => x.OfType<Window>()) { Setters = { new Setter(TemplatedControl.BackgroundProperty, Window), new Setter(TemplatedControl.FontSizeProperty, 12.5) } });
        styles.Add(new Style(x => x.OfType<TextBlock>()) { Setters = { new Setter(Layoutable.VerticalAlignmentProperty, VerticalAlignment.Center) } });
        // The options bar under the tabs: every control takes the slider fields' height, so combos, number boxes, buttons and fields
        // share one centre line instead of each keeping the height its theme gives it.
        var optionsHeight = Controls.SliderField.DefaultHeight;
        foreach (var type in new[] { typeof(ComboBox), typeof(NumericUpDown), typeof(TextBox), typeof(Button) })
            styles.Add(new Style(x => x.OfType<StackPanel>().Class("options").Descendant().Is(type))
            {
                Setters = { new Setter(Layoutable.HeightProperty, optionsHeight), new Setter(Layoutable.MinHeightProperty, 0.0) }
            });
        styles.Add(new Style(x => x.OfType<StackPanel>().Class("options").Descendant().OfType<NumericUpDown>())
        {
            Setters = { new Setter(NumericUpDown.VerticalContentAlignmentProperty, VerticalAlignment.Center) }
        });
        styles.Add(new Style(x => x.OfType<StackPanel>().Class("options").Descendant().OfType<TextBox>())
        {
            Setters =
            {
                new Setter(Layoutable.MinHeightProperty, 0.0), new Setter(TemplatedControl.PaddingProperty, new Thickness(6, 0)),
                new Setter(TextBox.VerticalContentAlignmentProperty, VerticalAlignment.Center)
            }
        });
        styles.Add(new Style(x => x.OfType<StackPanel>().Class("options").Descendant().OfType<Button>())
        {
            Setters = { new Setter(TemplatedControl.PaddingProperty, new Thickness(10, 0)), new Setter(ContentControl.VerticalContentAlignmentProperty, VerticalAlignment.Center) }
        });
        styles.Add(new Style(x => x.OfType<Button>().Class("flat"))
        {
            Setters =
            {
                new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent), new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(0)),
                new Setter(TemplatedControl.PaddingProperty, new Thickness(6)), new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(6))
            }
        });
        styles.Add(new Style(x => x.OfType<ToggleButton>().Class("tool"))
        {
            Setters =
            {
                new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent), new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(0)),
                new Setter(Layoutable.WidthProperty, 36.0), new Setter(Layoutable.HeightProperty, 33.0), new Setter(TemplatedControl.PaddingProperty, new Thickness(0)),
                new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(7)),
                new Setter(ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Center), new Setter(ContentControl.VerticalContentAlignmentProperty, VerticalAlignment.Center)
            }
        });
        return styles;
    }
}
