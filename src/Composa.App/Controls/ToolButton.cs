using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.App.Dialogs;
using Path = Avalonia.Controls.Shapes.Path;

namespace Composa.App.Controls;

/// <summary>
/// One tool of a rail button's group: its name and icon, the key that picks it, whether it is the one the button stands for, and
/// what picking it does.
/// </summary>
public sealed record ToolChoice(string Name, Icons.Icon Icon, Func<KeyGesture?> Key, Func<bool> IsCurrent, Action Choose);

/// <summary>
/// A button in the tool rail. One that holds a group of tools (the marquees, the lassos) shows the group's current tool with a
/// small triangle in its corner and opens the group beside itself when it is pressed and held or right-clicked, as Photoshop's
/// toolbar does: every tool of the group with its icon, name and key, the current one marked. A click picks the tool the button
/// shows. After a hold, sliding onto a tool and letting go picks it; letting go anywhere else leaves the group open to click in.
/// </summary>
public sealed class ToolButton : ToggleButton
{
    /// <summary>How long a press has to last before the group opens.</summary>
    public static readonly TimeSpan HoldDelay = TimeSpan.FromMilliseconds(400);

    private static readonly Geometry CornerMark = Geometry.Parse("M4 0 V4 H0 Z");

    private readonly IReadOnlyList<ToolChoice> group;
    private readonly DispatcherTimer holdTimer;
    private MenuFlyout? flyout;
    /// <summary>
    /// The press that is still down opened the group. The flyout takes the keyboard focus as it opens, which ends the button's
    /// pressed state, but the pointer stays captured by the button until it is let go.
    /// </summary>
    private bool openedByHold;

    public ToolButton(Icons.Icon icon, string tip, IReadOnlyList<ToolChoice>? group = null)
    {
        this.group = group ?? [];
        Classes.Add("tool");
        Show(icon);
        ToolTip.SetTip(this, Loc.T(tip));
        holdTimer = new DispatcherTimer { Interval = HoldDelay };
        holdTimer.Tick += (_, _) =>
        {
            holdTimer.Stop();
            if (!IsPressed) return;
            openedByHold = true;
            OpenGroup();
        };
        ContextRequested += (_, e) =>
        {
            if (!HasGroup) return;
            OpenGroup(fromKeyboard: !e.TryGetPosition(this, out Point _));
            e.Handled = true;
        };
    }

    protected override Type StyleKeyOverride => typeof(ToggleButton);

    public bool HasGroup => group.Count > 1;

    public bool IsGroupOpen => flyout?.IsOpen == true;

    /// <summary>The group's tool the button stands for now; null for a tool without a group.</summary>
    public ToolChoice? Current => group.FirstOrDefault(c => c.IsCurrent());

    /// <summary>The open group's items, top to bottom.</summary>
    public IReadOnlyList<MenuItem> GroupItems => IsGroupOpen ? flyout!.Items.OfType<MenuItem>().ToList() : [];

    /// <summary>Shows the group's current tool: its icon on the button, its name and key in the tooltip.</summary>
    public void Refresh()
    {
        if (Current is not { } current) return;
        Show(current.Icon);
        var key = current.Key() is { } gesture ? $" ({Shortcut.Label(gesture)})" : "";
        var others = group.Where(c => c != current).Select(c => c.Name).ToList();
        // The tool names are looked up for display; the record's Name stays English, which is what the
        // options bar and the tests match on. The joining word comes from the resource too.
        var list = others.Count == 1 ? Loc.T(others[0]) : Loc.Format("{0} and {1}", string.Join(", ", others.SkipLast(1).Select(Loc.T)), Loc.T(others[^1]));
        ToolTip.SetTip(this, Loc.Format("{0}{1} · click and hold for {2}", Loc.T(current.Name), key, list));
    }

    /// <summary>
    /// Opens the group beside the button. It is built afresh each time, so it shows the keys as they are bound now. Opened by the
    /// pointer, no tool is highlighted until the pointer is over one, as in Photoshop; opened from the keyboard, the arrow keys
    /// start from the current tool.
    /// </summary>
    public void OpenGroup(bool fromKeyboard = false)
    {
        if (!HasGroup) return;
        flyout?.Hide();
        ToolTip.SetIsOpen(this, false);
        flyout = new MenuFlyout { Placement = PlacementMode.RightEdgeAlignedTop, HorizontalOffset = 4 };
        foreach (var choice in group)
        {
            var item = new MenuItem
            {
                Header = Loc.T(choice.Name), Icon = Icons.Create(choice.Icon, 16), InputGesture = choice.Key(), Tag = choice,
                ToggleType = MenuItemToggleType.Radio, IsChecked = choice.IsCurrent()
            };
            item.Click += (_, _) => choice.Choose();
            flyout.Items.Add(item);
        }
        flyout.ShowAt(this);
        // Showing the flyout focuses its first item, which also highlights it.
        var items = flyout.Items.OfType<MenuItem>().ToList();
        if (fromKeyboard) items.FirstOrDefault(i => i.IsChecked == true)?.Focus(NavigationMethod.Directional);
        else if (items.FirstOrDefault()?.FindAncestorOfType<MenuFlyoutPresenter>() is { } presenter) presenter.SelectedIndex = -1;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        openedByHold = false;
        base.OnPointerPressed(e);
        if (HasGroup && IsPressed) holdTimer.Start();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!openedByHold) return;
        var under = ItemAt(e);
        foreach (var item in GroupItems) item.IsSelected = item == under;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        holdTimer.Stop();
        var picked = openedByHold ? ItemAt(e) : null;
        base.OnPointerReleased(e);
        openedByHold = false;
        if (picked?.Tag is not ToolChoice choice) return;
        flyout?.Hide();
        choice.Choose();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        holdTimer.Stop();
        openedByHold = false;
        base.OnPointerCaptureLost(e);
    }

    /// <summary>Letting go of the press that opened the group is not a click: the group stays open to choose from.</summary>
    protected override void OnClick()
    {
        if (!openedByHold) base.OnClick();
    }

    /// <summary>
    /// The group's item under the pointer. The press keeps the pointer captured by the button while the items sit in the
    /// flyout's own popup, so they are found through screen coordinates rather than by hit testing.
    /// </summary>
    private MenuItem? ItemAt(PointerEventArgs e)
    {
        var screen = this.PointToScreen(e.GetPosition(this));
        return GroupItems.FirstOrDefault(item => TopLevel.GetTopLevel(item) != null && new Rect(item.Bounds.Size).Contains(item.PointToClient(screen)));
    }

    private void Show(Icons.Icon icon)
    {
        var glyph = Icons.Create(icon, 19);
        if (!HasGroup) { Content = glyph; return; }
        var mark = new Path
        {
            Data = CornerMark, Fill = Palette.Secondary, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            IsHitTestVisible = false
        };
        Content = new Panel { Width = 30, Height = 27, Children = { glyph, mark } };
    }
}
