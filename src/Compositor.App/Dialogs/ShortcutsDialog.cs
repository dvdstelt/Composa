using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace Compositor.App.Dialogs;

/// <summary>A command that can be triggered by a key, with the key it started with and the one it has now.</summary>
public sealed class Shortcut(string id, string title, string group, KeyGesture? gesture, Action run, Func<bool>? enabled = null, bool hidden = false)
{
    public string Id { get; } = id;
    public string Title { get; } = title;
    /// <summary>Which list the shortcut appears in: "Menus" or "Tools and Canvas".</summary>
    public string Group { get; } = group;
    public KeyGesture? Default { get; } = gesture;
    public KeyGesture? Gesture { get; set; } = gesture;
    public Action Run { get; } = run;
    public Func<bool>? Enabled { get; } = enabled;
    /// <summary>Alternates (Ctrl+Y for Redo) that are not listed or rebound.</summary>
    public bool Hidden { get; } = hidden;
    /// <summary>The menu item showing the gesture, if any.</summary>
    public MenuItem? Item { get; set; }

    public bool Matches(KeyEventArgs e) => Gesture != null && Gesture.Key == e.Key && Gesture.KeyModifiers == e.KeyModifiers;

    /// <summary>What the shortcut reads as in a menu or the shortcuts window.</summary>
    public static string Label(KeyGesture? gesture)
    {
        if (gesture == null) return "None";
        var key = gesture.Key switch
        {
            Key.OemPlus => "+", Key.OemMinus => "-", Key.OemOpenBrackets => "[", Key.OemCloseBrackets => "]", Key.OemQuotes => "'", Key.OemSemicolon => ";",
            Key.OemBackslash => "\\", Key.OemPipe => "\\", Key.Back => "Backspace", Key.Return => "Enter", Key.Escape => "Esc", Key.OemPeriod => ".", Key.OemComma => ",",
            >= Key.D0 and <= Key.D9 => ((int)gesture.Key - (int)Key.D0).ToString(),
            _ => gesture.Key.ToString()
        };
        var modifiers = "";
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Control)) modifiers += "Ctrl+";
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Alt)) modifiers += "Alt+";
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Shift)) modifiers += "Shift+";
        return modifiers + key;
    }
}

/// <summary>Lets every menu command and tool key be given another key. Changes apply when saved and are kept between launches.</summary>
public static class ShortcutsDialog
{
    /// <summary>Returns the new gesture for every listed shortcut, or null when cancelled.</summary>
    public static async Task<Dictionary<string, KeyGesture?>?> Edit(Window owner, IReadOnlyList<Shortcut> shortcuts)
    {
        var listed = shortcuts.Where(s => !s.Hidden).ToList();
        var draft = listed.ToDictionary(s => s.Id, s => s.Gesture);
        Shortcut? recording = null;
        var buttons = new Dictionary<string, Button>();
        var problem = Ui.Label("", new SolidColorBrush(Color.Parse("#FFB454")));
        problem.TextWrapping = TextWrapping.Wrap;
        problem.MaxWidth = 560;
        var search = new TextBox { PlaceholderText = "Search shortcuts", Width = 300 };
        var rows = new StackPanel { Spacing = 2 };
        DialogWindow? dialog = null;

        string? Problem()
        {
            var seen = new Dictionary<string, string>();
            foreach (var shortcut in listed)
            {
                var gesture = draft[shortcut.Id];
                if (gesture == null) continue;
                var key = gesture.ToString();
                if (seen.TryGetValue(key, out var other)) return $"{Shortcut.Label(gesture)} is assigned to both {other} and {shortcut.Title}.";
                seen[key] = shortcut.Title;
            }
            return null;
        }

        void Refresh()
        {
            foreach (var shortcut in listed)
            {
                var button = buttons[shortcut.Id];
                button.Content = recording == shortcut ? "Press keys…" : Shortcut.Label(draft[shortcut.Id]);
                button.Classes.Set("accent", recording == shortcut);
            }
            var text = Problem();
            problem.Text = text ?? "";
            problem.IsVisible = text != null;
            if (dialog != null) dialog.CanAccept = text == null && recording == null;
        }

        void Build()
        {
            rows.Children.Clear();
            var filter = search.Text?.Trim() ?? "";
            foreach (var group in new[] { "Menus", "Tools and Canvas" })
            {
                var matching = listed.Where(s => s.Group == group && (filter.Length == 0 || s.Title.Contains(filter, StringComparison.OrdinalIgnoreCase))).ToList();
                if (matching.Count == 0) continue;
                var heading = Ui.Label(group, weight: FontWeight.SemiBold);
                heading.Margin = new Thickness(0, 8, 0, 4);
                rows.Children.Add(heading);
                foreach (var shortcut in matching)
                {
                    var button = new Button { Width = 150, HorizontalContentAlignment = HorizontalAlignment.Center, Padding = new Thickness(6, 3) };
                    button.Click += (_, _) => { recording = recording == shortcut ? null : shortcut; Refresh(); };
                    buttons[shortcut.Id] = button;
                    var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Width = 560 };
                    var title = Ui.Label(shortcut.Title);
                    grid.Children.Add(title);
                    Grid.SetColumn(button, 1);
                    grid.Children.Add(button);
                    rows.Children.Add(grid);
                }
            }
            Refresh();
        }
        search.TextChanged += (_, _) => Build();

        var scroll = new ScrollViewer { Content = rows, Height = 420, Width = 590, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        var restore = Ui.TextButton("Restore Defaults", () => { foreach (var shortcut in listed) draft[shortcut.Id] = shortcut.Default; recording = null; Refresh(); });
        var notes = new TextBlock
        {
            Text = "Click a shortcut, then press its new key combination; Escape stops recording and Backspace clears it. Brush size and hardness ([ ] and { }), the opacity digits, Space to pan and the modifier-and-mouse gestures are fixed.",
            Foreground = Palette.Secondary, TextWrapping = TextWrapping.Wrap, MaxWidth = 590
        };
        var body = Ui.Column(10, notes, Ui.Row(10, search, restore), scroll, problem);
        Build();
        dialog = new DialogWindow("Keyboard Shortcuts", body, "Save");
        dialog.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (recording == null) return;
            e.Handled = true;
            if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.None) return;
            if (e.Key == Key.Escape) { recording = null; Refresh(); return; }
            draft[recording.Id] = e.Key == Key.Back && e.KeyModifiers == KeyModifiers.None ? null : new KeyGesture(e.Key, e.KeyModifiers);
            recording = null;
            Refresh();
        }, RoutingStrategies.Tunnel);
        dialog.Opened += (_, _) => search.Focus();
        return await dialog.Ask(owner) ? draft : null;
    }
}
