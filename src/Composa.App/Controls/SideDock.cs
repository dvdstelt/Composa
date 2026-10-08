using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Composa.App.Controls;

/// <summary>One panel under the Layers panel: a header that collapses it and the panel itself, sized by the splitter above it.</summary>
public sealed class DockSection(string title, Control content, DockPanelState state)
{
    public const double MinHeight = 90;

    public string Title { get; } = title;
    public Control Content { get; } = content;
    public DockPanelState State { get; set; } = state;

    /// <summary>Raised when the section is shown, hidden, collapsed, expanded or resized, so the window can remember it.</summary>
    public event Action? Changed;

    internal void RaiseChanged() => Changed?.Invoke();
}

/// <summary>
/// The column on the right: the Layers panel on top, taking whatever room is left, and under it the sections the
/// Window menu shows, each with a header that collapses it and a splitter above it to size it.
/// </summary>
public sealed class SideDock : UserControl
{
    private const double MinTopHeight = 160;

    private readonly Control top;
    private readonly IReadOnlyList<DockSection> sections;
    private readonly Grid grid = new();

    public SideDock(Control top, params DockSection[] sections)
    {
        this.top = top;
        this.sections = sections;
        Content = grid;
        Layout();
    }

    public DockSection Section(string title) => sections.Single(s => s.Title == title);

    public void SetVisible(DockSection section, bool visible)
    {
        if (section.State.Visible == visible) return;
        section.State = section.State with { Visible = visible };
        Layout();
        section.RaiseChanged();
    }

    public void SetCollapsed(DockSection section, bool collapsed)
    {
        if (section.State.Collapsed == collapsed) return;
        section.State = section.State with { Collapsed = collapsed };
        Layout();
        section.RaiseChanged();
    }

    /// <summary>Rows from the top: the Layers panel, then a splitter and a section for every section shown.</summary>
    private void Layout()
    {
        grid.Children.Clear();
        grid.RowDefinitions.Clear();
        // Each layout builds new section frames, so the panels leave the old ones first.
        foreach (var section in sections)
            if (section.Content.Parent is Panel parent) parent.Children.Remove(section.Content);
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Star) { MinHeight = MinTopHeight });
        grid.Children.Add(top);
        foreach (var section in sections.Where(s => s.State.Visible))
        {
            var open = !section.State.Collapsed;
            // A collapsed section is only its header, so there is nothing to size and the splitter would only mislead.
            var splitter = new GridSplitter { ResizeDirection = GridResizeDirection.Rows, ResizeBehavior = GridResizeBehavior.PreviousAndNext, Height = 5, Background = Palette.Window, IsEnabled = open };
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetRow(splitter, grid.RowDefinitions.Count - 1);
            grid.Children.Add(splitter);

            var row = new RowDefinition(open ? new GridLength(Math.Max(DockSection.MinHeight, section.State.Height)) : GridLength.Auto) { MinHeight = open ? DockSection.MinHeight : 0 };
            grid.RowDefinitions.Add(row);
            var panel = Build(section, open);
            Grid.SetRow(panel, grid.RowDefinitions.Count - 1);
            grid.Children.Add(panel);
            splitter.DragCompleted += (_, _) =>
            {
                section.State = section.State with { Height = Math.Round(row.ActualHeight) };
                section.RaiseChanged();
            };
        }
    }

    private Control Build(DockSection section, bool open)
    {
        var chevron = Icons.Create(open ? Icons.ChevronDown : Icons.ChevronRight, 12, Palette.Secondary);
        var title = Ui.Label(Loc.T(section.Title), weight: FontWeight.SemiBold);
        var header = new Border
        {
            Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand), Padding = new Thickness(8, 8, 10, 6),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { chevron, title } }
        };
        ToolTip.SetTip(header, open ? Loc.Format("Collapse {0}", Loc.T(section.Title)) : Loc.Format("Expand {0}", Loc.T(section.Title)));
        header.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(header).Properties.IsLeftButtonPressed) return;
            SetCollapsed(section, open);
            e.Handled = true;
        };
        var panel = new DockPanel { Background = Palette.Panel };
        DockPanel.SetDock(header, Dock.Top);
        panel.Children.Add(header);
        if (open) panel.Children.Add(section.Content);
        return panel;
    }
}
