using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using Composa.Editing;
using Composa.Filters;
using Composa.Model;
using SkiaSharp;
using BlendMode = Composa.Model.BlendMode;
using TextAlignment = Avalonia.Media.TextAlignment;

namespace Composa.App.Controls;

/// <summary>The layer stack: visibility, thumbnails, masks, folders, blend mode and opacity, drag-to-reorder.</summary>
public sealed class LayersPanel : UserControl
{
    private EditorSession? session;
    private readonly StackPanel rows = new();
    private readonly ComboBox blend;
    private readonly Slider opacity;
    private readonly TextBlock opacityText = new() { Width = 36, TextAlignment = TextAlignment.Right, Foreground = Palette.Secondary };
    private readonly Border dropLine = new() { Height = 2, Background = Palette.Accent, IsVisible = false, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Top };
    private readonly Dictionary<Guid, Border> rowFor = [];
    private bool updating;
    private bool opacityDragging;
    private double opacityStart;
    private Guid? renaming;
    private static readonly ConditionalWeakTable<SKBitmap, Bitmap> Thumbnails = new();

    // Drag state.
    private Layer? pressed;
    private Point pressPoint;
    private bool dragging;
    private (Layer Target, LayerDrop Drop)? dropTarget;
    private bool? eyeSwipe;
    private bool? thumbnailTarget;
    private bool rebuilding, rebuildAgain;

    public event Action<Layer>? EditAdjustmentRequested;
    public event Action<Layer>? EditTextRequested;
    public event Action<AdjustmentKind>? NewAdjustmentRequested;
    /// <summary>Double-click on an effect row: open its settings.</summary>
    public event Action<Layer, LayerEffectKind>? EditEffectRequested;
    /// <summary>The footer's effects menu: add an effect to the active layer.</summary>
    public event Action<LayerEffectKind>? NewEffectRequested;

    /// <summary>The blend mode at each index of the blend menu; null for the lines between groups.</summary>
    private readonly List<BlendMode?> blendAt = [];

    // An effect row being Alt-dragged onto another layer.
    private (Layer Layer, LayerEffectKind Kind)? effectDrag;
    private Point effectDragStart;
    private Border? effectDropRow;

    static LayersPanel() => Composa.Rendering.Pixels.Invalidated += bitmap => Thumbnails.Remove(bitmap);

    public LayersPanel()
    {
        // Grouped as Photoshop groups them (darkening, lightening, contrast, comparative, component) with a line between,
        // so the long list stays readable. The lines are disabled items, which the keyboard and the pointer skip.
        var items = new List<object>();
        for (var group = 0; group < BlendModeExtensions.Groups.Length; group++)
        {
            if (group > 0) { items.Add(new ComboBoxItem { Content = Ui.Separator(false), IsEnabled = false, Padding = new Thickness(0, 4), MinHeight = 0 }); blendAt.Add(null); }
            foreach (var mode in BlendModeExtensions.Groups[group]) { items.Add(new ComboBoxItem { Content = mode.DisplayName() }); blendAt.Add(mode); }
        }
        blend = new ComboBox { ItemsSource = items, HorizontalAlignment = HorizontalAlignment.Stretch };
        blend.SelectionChanged += (_, _) =>
        {
            if (updating || session?.ActiveLayer is not { } layer || blend.SelectedIndex < 0 || blendAt[blend.SelectedIndex] is not { } mode) return;
            foreach (var target in session.SelectedRoots()) session.SetBlend(target, mode);
        };
        opacity = new Slider { Minimum = 0, Maximum = 100, Value = 100, VerticalAlignment = VerticalAlignment.Center };
        opacity.ValueChanged += (_, e) =>
        {
            opacityText.Text = $"{Math.Round(e.NewValue)}%";
            if (updating || session?.ActiveLayer is not { } layer) return;
            if (!opacityDragging) { opacityDragging = true; opacityStart = layer.Opacity; session.Begin("Opacity"); }
            session.SetOpacity(layer, Math.Round(e.NewValue) / 100);
        };
        opacity.AddHandler(PointerReleasedEvent, (_, _) => EndOpacityDrag(), Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble, true);
        opacity.LostFocus += (_, _) => EndOpacityDrag();
        opacity.KeyUp += (_, _) => EndOpacityDrag();

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto"), Margin = new Thickness(10, 8, 10, 6) };
        var title = Ui.Label("Layers", weight: FontWeight.SemiBold);
        title.Margin = new Thickness(0, 0, 0, 6);
        Grid.SetColumnSpan(title, 3);
        var blendRow = new Grid { ColumnDefinitions = new ColumnDefinitions("112,10,Auto,*,Auto") };
        blendRow.Children.Add(blend);
        var opacityLabel = Ui.Label("Opacity", Palette.Secondary);
        Grid.SetColumn(opacityLabel, 2);
        blendRow.Children.Add(opacityLabel);
        Grid.SetColumn(opacity, 3);
        opacity.Margin = new Thickness(6, 0, 0, 0);
        blendRow.Children.Add(opacity);
        Grid.SetColumn(opacityText, 4);
        blendRow.Children.Add(opacityText);
        Grid.SetRow(blendRow, 1);
        Grid.SetColumnSpan(blendRow, 3);
        header.Children.Add(title);
        header.Children.Add(blendRow);

        var adjustmentMenu = new ContextMenu();
        foreach (var kind in Enum.GetValues<AdjustmentKind>())
        {
            var item = new MenuItem { Header = Adjustment.Create(kind).DisplayName + "…" };
            item.Click += (_, _) => NewAdjustmentRequested?.Invoke(kind);
            adjustmentMenu.Items.Add(item);
        }
        var adjustButton = Ui.IconButton(Icons.Adjust, "New adjustment layer", () => { });
        adjustButton.Click += (_, _) => adjustmentMenu.Open(adjustButton);
        var effectsMenu = new ContextMenu();
        foreach (var kind in Enum.GetValues<LayerEffectKind>())
        {
            var item = new MenuItem { Header = LayerEffects.DisplayName(kind) + "…" };
            item.Click += (_, _) => NewEffectRequested?.Invoke(kind);
            effectsMenu.Items.Add(item);
        }
        var effectsButton = Ui.IconButton(Icons.Effects, "Layer effects: stroke, drop shadow, color overlay, inner shadow, outer glow", () => { });
        effectsButton.Click += (_, _) => { if (session?.ActiveLayer is { Pixels: not null }) effectsMenu.Open(effectsButton); };

        var footer = Ui.Row(2,
            Ui.IconButton(Icons.Plus, "New layer (Ctrl+Shift+N)", () => session?.AddBlankLayer()),
            Ui.IconButton(Icons.Folder, "Group selected layers (Ctrl+G)", () => session?.GroupSelectedLayers()),
            Ui.IconButton(Icons.Mask, "Add layer mask (Alt: hide all)", () => { if (session?.ActiveLayer is { } layer) session.AddMask(layer); }),
            effectsButton,
            adjustButton,
            Ui.IconButton(Icons.Trash, "Delete layer, mask or effect", () => DeleteLayerOrMask()));
        footer.HorizontalAlignment = HorizontalAlignment.Center;
        footer.Margin = new Thickness(0, 4);

        var list = new Panel();
        rows.AddHandler(PointerMovedEvent, (_, e) =>
        {
            if (eyeSwipe is not { } show || session == null) return;
            if (!e.GetCurrentPoint(rows).Properties.IsLeftButtonPressed) { eyeSwipe = null; return; }
            var position = e.GetPosition(rows);
            if (position.X > 40) return;
            var row = rows.Children.OfType<Border>().FirstOrDefault(r => position.Y >= r.Bounds.Top && position.Y < r.Bounds.Bottom);
            if (row?.Tag is Layer under && under.Visible != show) session.SetVisible(under, show);
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble, true);
        rows.AddHandler(PointerReleasedEvent, (_, _) => eyeSwipe = null, Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble, true);
        list.Children.Add(rows);
        list.Children.Add(dropLine);
        var scroll = new ScrollViewer { Content = list, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        scroll.PointerPressed += (_, e) => { if (e.Source == scroll || e.Source == list) Focus(); };

        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto"), Background = Palette.Panel };
        layout.Children.Add(header);
        var top = Ui.Separator(false); Grid.SetRow(top, 1); layout.Children.Add(top);
        Grid.SetRow(scroll, 2); layout.Children.Add(scroll);
        var bottom = Ui.Separator(false); Grid.SetRow(bottom, 3); layout.Children.Add(bottom);
        Grid.SetRow(footer, 4); layout.Children.Add(footer);
        Content = layout;
    }

    public EditorSession? Session
    {
        get => session;
        set
        {
            if (session != null) session.LayersChanged -= Rebuild;
            session = value;
            if (session != null) session.LayersChanged += Rebuild;
            Rebuild();
        }
    }

    /// <summary>The trash button and Delete: a highlighted effect goes first, then the targeted mask, then the layers.</summary>
    public void DeleteLayerOrMask()
    {
        if (session == null) return;
        if (session.SelectedEffect != null) { session.RemoveSelectedEffect(); return; }
        if (session.ActiveLayer is not { } layer) return;
        if (session.IsEditingMask) session.DeleteMask(layer); else session.DeleteSelectedLayers();
    }

    public void BeginRename()
    {
        renaming = session?.ActiveLayer?.Id;
        Rebuild();
    }

    private void EndOpacityDrag()
    {
        if (!opacityDragging) return;
        opacityDragging = false;
        // Dragging back to where it started is not an edit.
        if (session?.ActiveLayer is { } layer && layer.Opacity == opacityStart) session.Cancel(); else session?.Commit();
    }

    private void Rebuild()
    {
        // Clearing the rows detaches a rename box, whose lost-focus handler commits the name and asks for another
        // rebuild. That request is honoured after this one finishes instead of interleaving with it.
        if (rebuilding) { rebuildAgain = true; return; }
        rebuilding = true;
        try
        {
            do
            {
                rebuildAgain = false;
                RebuildRows();
            } while (rebuildAgain);
        }
        finally { rebuilding = false; }
    }

    private void RebuildRows()
    {
        rows.Children.Clear();
        rowFor.Clear();
        updating = true;
        var active = session?.ActiveLayer;
        blend.IsEnabled = opacity.IsEnabled = active != null;
        blend.SelectedIndex = blendAt.IndexOf(active?.Blend ?? BlendMode.Normal);
        if (!opacityDragging) opacity.Value = (active?.Opacity ?? 1) * 100;
        opacityText.Text = $"{Math.Round(opacity.Value)}%";
        updating = false;
        if (session == null) return;
        AddRows(session.Document.Layers, 0, parentVisible: true);
    }

    private void AddRows(List<Layer> layers, int depth, bool parentVisible)
    {
        for (var i = layers.Count - 1; i >= 0; i--)
        {
            var layer = layers[i];
            rows.Children.Add(BuildRow(layer, depth, parentVisible));
            if (layer.Effects is { } effects) foreach (var kind in effects.Kinds) rows.Children.Add(BuildEffectRow(layer, kind, depth, parentVisible && layer.Visible));
            if (layer.IsGroup && !layer.Collapsed) AddRows(layer.Children, depth + 1, parentVisible && layer.Visible);
        }
    }

    private Control BuildRow(Layer layer, int depth, bool parentVisible)
    {
        var current = session!;
        var selected = current.Document.SelectedLayerIds.Contains(layer.Id);
        var dim = !layer.Visible || !parentVisible;

        var eye = new Button { Classes = { "flat" }, Width = 28, Height = 28, Padding = new Thickness(0), Content = Icons.Create(Icons.Eye, 15, layer.Visible ? null : new SolidColorBrush(Color.Parse("#555555"))) };
        ToolTip.SetTip(eye, "Show or hide (drag down the column to swipe, Alt-click to show only this layer)");
        eye.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))
            {
                current.SoloLayerId = current.SoloLayerId == layer.Id ? null : layer.Id;
                current.InvalidateAll();
                e.Handled = true;
                return;
            }
            // Swiping down the eye column shows or hides every layer passed over. The pointer is released from the eye
            // (whose row is about to be rebuilt) and the list itself follows the drag from here.
            eyeSwipe = !layer.Visible;
            e.Pointer.Capture(null);
            current.SetVisible(layer, eyeSwipe.Value);
            e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(depth * 14, 0, 0, 0), Opacity = dim ? 0.45 : 1 };
        if (layer.Clipped) content.Children.Add(Icons.Create(Icons.ClipArrow, 13, Palette.Secondary));
        if (layer.IsGroup)
        {
            var chevron = new Button { Classes = { "flat" }, Padding = new Thickness(2), Content = Icons.Create(layer.Collapsed ? Icons.ChevronRight : Icons.ChevronDown, 12) };
            chevron.Click += (_, _) => { layer.Collapsed = !layer.Collapsed; Rebuild(); };
            content.Children.Add(chevron);
            content.Children.Add(Icons.Create(Icons.Folder, 18));
        }
        else if (layer.IsAdjustment) content.Children.Add(Thumb(Icons.Create(Icons.Adjust, 18), selected && !current.IsEditingMask));
        else if (layer.Pixels != null)
        {
            var pixelThumb = Thumb(new Image { Source = Thumbnail(layer.Pixels), Stretch = Stretch.Uniform }, selected && layer.Id == current.ActiveLayer?.Id && !current.IsEditingMask);
            // The row's own press handler (which bubbles next) selects the layer and then applies this target.
            pixelThumb.PointerPressed += (_, _) => thumbnailTarget = false;
            content.Children.Add(pixelThumb);
        }
        if (layer.Mask != null)
        {
            var maskThumb = Thumb(new Image { Source = Thumbnail(layer.Mask), Stretch = Stretch.Uniform, Opacity = layer.MaskEnabled ? 1 : 0.35 },
                layer.Id == current.ActiveLayer?.Id && current.IsEditingMask);
            maskThumb.PointerPressed += (_, _) => thumbnailTarget = true;
            ToolTip.SetTip(maskThumb, "Layer mask: click to paint on it, Shift-click to disable, Ctrl-click to load as selection");
            maskThumb.AddHandler(PointerPressedEvent, (_, e) =>
            {
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) { current.SetMaskEnabled(layer, !layer.MaskEnabled); e.Handled = true; }
                else if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) { current.SelectLayerMask(layer); e.Handled = true; }
            }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            content.Children.Add(maskThumb);
        }

        if (renaming == layer.Id)
        {
            var box = new TextBox { Text = layer.Name, MinWidth = 110, Padding = new Thickness(4, 2) };
            var done = false;
            void Finish(bool keep)
            {
                if (done) return;
                done = true;
                renaming = null;
                if (keep && box.Text is { } text) current.Rename(layer, text);
                Rebuild();
            }
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Finish(true); e.Handled = true; } else if (e.Key == Key.Escape) { Finish(false); e.Handled = true; } };
            box.LostFocus += (_, _) => Finish(true);
            box.AttachedToVisualTree += (_, _) => { box.Focus(); box.SelectAll(); };
            content.Children.Add(box);
        }
        else
        {
            var name = Ui.Label(layer.Name);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.MaxWidth = 150;
            if (layer.IsLive) name.FontStyle = FontStyle.Italic;
            content.Children.Add(name);
        }

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        grid.Children.Add(eye);
        Grid.SetColumn(content, 1);
        grid.Children.Add(content);
        var row = new Border
        {
            Child = grid, Background = selected ? Palette.Selected : Brushes.Transparent, Padding = new Thickness(4, 3), MinHeight = 40,
            BorderBrush = Palette.Divider, BorderThickness = new Thickness(0, 0, 0, 1), Tag = layer
        };
        rowFor[layer.Id] = row;
        row.PointerPressed += (_, e) => RowPressed(layer, row, e);
        row.PointerMoved += (_, e) => RowMoved(e);
        row.PointerReleased += (_, e) => RowReleased(e);
        row.PointerCaptureLost += (_, _) => { if (dragging) EndRowDrag(); };
        row.ContextMenu = BuildMenu(layer);
        return row;
    }

    /// <summary>An effect belongs visually to its layer but has its own selection and visibility control.</summary>
    private Control BuildEffectRow(Layer layer, LayerEffectKind kind, int depth, bool parentVisible)
    {
        var current = session!;
        var enabled = layer.Effects!.IsEnabled(kind);
        var selected = current.SelectedEffect is { } s && s.LayerId == layer.Id && s.Kind == kind;
        var eye = new Button { Classes = { "flat" }, Width = 24, Height = 22, Padding = new Thickness(0), Content = Icons.Create(enabled ? Icons.Eye : Icons.EyeOff, 12, enabled ? Palette.Secondary : new SolidColorBrush(Color.Parse("#555555"))) };
        ToolTip.SetTip(eye, enabled ? "Hide " + LayerEffects.DisplayName(kind).ToLowerInvariant() : "Show " + LayerEffects.DisplayName(kind).ToLowerInvariant());
        eye.Click += (_, _) => current.ToggleEffect(layer, kind);
        var name = Ui.Label(LayerEffects.DisplayName(kind), enabled ? Palette.Foreground : Palette.Secondary);
        name.FontSize = 11.5;
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(depth * 14 + 46, 0, 0, 0), Opacity = parentVisible ? 1 : 0.45, Children = { eye, name } };
        var row = new Border
        {
            Child = content, Background = selected ? Palette.Selected : Brushes.Transparent, Padding = new Thickness(4, 1), Height = 24,
            BorderBrush = Palette.Divider, BorderThickness = new Thickness(0, 0, 0, 1), Tag = (layer, kind)
        };
        ToolTip.SetTip(row, "Click to select, double-click to edit, Alt-drag onto another layer to copy the " + LayerEffects.DisplayName(kind).ToLowerInvariant());
        row.PointerPressed += (_, e) =>
        {
            if (e.Source == eye || (e.Source as Control)?.FindAncestorOfType<Button>() == eye) return;
            var properties = e.GetCurrentPoint(row).Properties;
            if (!properties.IsLeftButtonPressed && !properties.IsRightButtonPressed) return;
            if (current.Document.ActiveLayerId != layer.Id) current.SelectLayer(layer.Id);
            current.SelectedEffect = (layer.Id, kind);
            if (e.ClickCount == 2 && properties.IsLeftButtonPressed) { EditEffectRequested?.Invoke(layer, kind); e.Handled = true; return; }
            if (properties.IsLeftButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Alt))
            {
                effectDrag = (layer, kind);
                effectDragStart = e.GetPosition(rows);
                e.Pointer.Capture(row);
            }
            Rebuild();
            e.Handled = true;
        };
        row.PointerMoved += (_, e) => EffectDragMoved(e);
        row.PointerReleased += (_, e) => EffectDragReleased(e);
        row.PointerCaptureLost += (_, _) => { effectDrag = null; ClearEffectDrop(); };
        var menu = new ContextMenu();
        void Add(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        Add("Edit " + LayerEffects.DisplayName(kind) + "…", () => EditEffectRequested?.Invoke(layer, kind));
        Add(enabled ? "Hide" : "Show", () => current.ToggleEffect(layer, kind));
        Add("Delete", () => current.RemoveEffect(layer, kind));
        row.ContextMenu = menu;
        return row;
    }

    private void EffectDragMoved(PointerEventArgs e)
    {
        if (effectDrag == null || session == null) return;
        if (!e.GetCurrentPoint(rows).Properties.IsLeftButtonPressed) { effectDrag = null; ClearEffectDrop(); return; }
        var position = e.GetPosition(rows);
        if (Math.Abs(position.Y - effectDragStart.Y) < 6 && effectDropRow == null) return;
        ClearEffectDrop();
        var target = rows.Children.OfType<Border>().FirstOrDefault(r => r.Tag is Layer && position.Y >= r.Bounds.Top && position.Y < r.Bounds.Bottom);
        if (target?.Tag is Layer layer && session.CanCopyEffect(effectDrag.Value.Kind, effectDrag.Value.Layer, layer))
        {
            effectDropRow = target;
            target.BorderBrush = Palette.Accent;
            target.BorderThickness = new Thickness(1);
        }
    }

    private void EffectDragReleased(PointerReleasedEventArgs e)
    {
        var drag = effectDrag;
        var drop = effectDropRow;
        effectDrag = null;
        ClearEffectDrop();
        e.Pointer.Capture(null);
        if (drag == null || session == null || drop?.Tag is not Layer target) return;
        session.CopyEffect(drag.Value.Kind, drag.Value.Layer, target);
    }

    private void ClearEffectDrop()
    {
        if (effectDropRow == null) return;
        effectDropRow.BorderBrush = Palette.Divider;
        effectDropRow.BorderThickness = new Thickness(0, 0, 0, 1);
        effectDropRow = null;
    }

    private static Border Thumb(Control child, bool highlighted, Action? click = null)
    {
        var border = new Border
        {
            Width = 34, Height = 34, Child = child, Background = new SolidColorBrush(Color.Parse("#4A4A4A")), ClipToBounds = true,
            BorderBrush = highlighted ? Brushes.White : new SolidColorBrush(Color.Parse("#161616")), BorderThickness = new Thickness(highlighted ? 2 : 1)
        };
        _ = click;
        return border;
    }

    private static unsafe Bitmap Thumbnail(SKBitmap source)
    {
        if (Thumbnails.TryGetValue(source, out var cached)) return cached;
        const int side = 68;
        var scale = Math.Min((double)side / source.Width, (double)side / source.Height);
        int w = Math.Max(1, (int)Math.Round(source.Width * scale)), h = Math.Max(1, (int)Math.Round(source.Height * scale));
        using var small = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(small))
        {
            if (source.ColorType == SKColorType.Alpha8)
            {
                // Masks show as grayscale: white where they reveal.
                canvas.Clear(SKColors.Black);
                using var paint = new SKPaint { ColorFilter = SKColorFilter.CreateBlendMode(SKColors.White, SKBlendMode.SrcIn) };
                canvas.DrawBitmap(source, new SKRect(0, 0, w, h), paint);
            }
            else
            {
                // Sample a reduced copy first: far cheaper than filtering tens of megapixels for a 68 px thumbnail.
                using var image = SKImage.FromPixels(source.PeekPixels());
                canvas.DrawImage(image, new SKRect(0, 0, w, h), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
            }
        }
        var bitmap = new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, small.GetPixels(), new PixelSize(w, h), new Vector(96, 96), small.RowBytes);
        Thumbnails.Add(source, bitmap);
        return bitmap;
    }

    private ContextMenu BuildMenu(Layer layer)
    {
        var current = session!;
        var menu = new ContextMenu();
        void Add(string header, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => { if (!current.Document.SelectedLayerIds.Contains(layer.Id)) current.SelectLayer(layer.Id); action(); };
            menu.Items.Add(item);
        }
        if (layer.IsAdjustment) Add("Edit Adjustment…", () => EditAdjustmentRequested?.Invoke(layer));
        Add("Rename", () => { renaming = layer.Id; Rebuild(); });
        Add("Duplicate", current.DuplicateSelectedLayers);
        Add("Delete", current.DeleteSelectedLayers);
        menu.Items.Add(new Separator());
        Add(layer.Clipped ? "Release Clipping Mask" : "Create Clipping Mask", () => current.ToggleClippingMask(layer), current.CanClip(layer));
        if (layer.Mask == null)
        {
            Add("Add Layer Mask", () => current.AddMask(layer));
            Add("Add Layer Mask (Hide All)", () => current.AddMask(layer, hideAll: true));
        }
        else
        {
            Add(layer.MaskEnabled ? "Disable Layer Mask" : "Enable Layer Mask", () => current.SetMaskEnabled(layer, !layer.MaskEnabled));
            Add("Apply Layer Mask", () => current.ApplyMask(layer), layer.Pixels != null);
            Add("Delete Layer Mask", () => current.DeleteMask(layer));
            Add("Select Mask", () => current.SelectLayerMask(layer));
        }
        if (layer.Pixels != null) Add("Select Pixels", () => current.SelectLayerPixels(layer));
        if (layer.Pixels != null)
        {
            var effects = new MenuItem { Header = "Layer Effects" };
            foreach (var kind in Enum.GetValues<LayerEffectKind>())
            {
                var item = new MenuItem { Header = LayerEffects.DisplayName(kind) + "…" };
                item.Click += (_, _) => { if (!current.Document.SelectedLayerIds.Contains(layer.Id)) current.SelectLayer(layer.Id); if (layer.Effects?.Contains(kind) == true) EditEffectRequested?.Invoke(layer, kind); else NewEffectRequested?.Invoke(kind); };
                effects.Items.Add(item);
            }
            menu.Items.Add(effects);
        }
        if (layer.Text != null) Add("Edit Text…", () => EditTextRequested?.Invoke(layer));
        if (layer.IsLive) Add("Rasterize Layer", () => current.RasterizeShape(layer));
        menu.Items.Add(new Separator());
        Add("Group Selected Layers", current.GroupSelectedLayers);
        if (layer.IsGroup) Add("Ungroup", () => current.Ungroup(layer));
        Add(current.MergeTitle, current.MergeLayers, current.CanMerge);
        return menu;
    }

    // ---- Selection and drag-reorder -----------------------------------------------------------------------------

    private void RowPressed(Layer layer, Border row, PointerPressedEventArgs e)
    {
        if (session == null || e.Source is TextBox) return;
        var properties = e.GetCurrentPoint(row).Properties;
        if (properties.IsRightButtonPressed)
        {
            if (!session.Document.SelectedLayerIds.Contains(layer.Id)) session.SelectLayer(layer.Id);
            return;
        }
        if (!properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2)
        {
            // Selecting on the first click rebuilt the rows, so the control's own double-tap never sees both clicks.
            if (layer.IsAdjustment) EditAdjustmentRequested?.Invoke(layer);
            else if (layer.Text != null && e.Source is Image) EditTextRequested?.Invoke(layer); // Double-click the thumbnail to edit, the name to rename.
            else { renaming = layer.Id; Rebuild(); }
            e.Handled = true;
            return;
        }
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt) && session.CanClip(layer)) { session.ToggleClippingMask(layer); return; }
        var extend = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var range = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        pressed = layer;
        pressPoint = e.GetPosition(rows);
        dragging = false;
        if (session.SelectedEffect != null) { session.SelectedEffect = null; session.NotifyLayersChanged(); }
        var target = thumbnailTarget;
        thumbnailTarget = null;
        if (extend || range || !session.Document.SelectedLayerIds.Contains(layer.Id)) session.SelectLayer(layer.Id, extend, range);
        if (target is { } mask && !extend && !range)
        {
            // Clicking a thumbnail chooses what painting affects: the layer's pixels or its mask.
            if (session.Document.ActiveLayerId != layer.Id) session.SelectLayer(layer.Id);
            session.EditingMask = mask && layer.Mask != null;
            session.NotifyLayersChanged();
        }
        if (rowFor.TryGetValue(layer.Id, out var rebuilt)) e.Pointer.Capture(rebuilt);
    }

    private void RowMoved(PointerEventArgs e)
    {
        if (pressed == null || session == null) return;
        if (!e.GetCurrentPoint(rows).Properties.IsLeftButtonPressed) { EndRowDrag(); return; }
        var position = e.GetPosition(rows);
        if (!dragging && Math.Abs(position.Y - pressPoint.Y) < 6) return;
        dragging = true;
        dropTarget = null;
        dropLine.IsVisible = false;
        foreach (var child in rows.Children)
        {
            if (child is not Border { Tag: Layer target } row) continue;
            var top = row.Bounds.Top;
            if (position.Y < top || position.Y > row.Bounds.Bottom) continue;
            var fraction = (position.Y - top) / Math.Max(1, row.Bounds.Height);
            var drop = target.IsGroup && fraction is > 0.3 and < 0.7 ? LayerDrop.Into : fraction < 0.5 ? LayerDrop.Above : LayerDrop.Below;
            // Just under an open folder's header is the top of its contents, not the far side of the whole folder.
            if (drop == LayerDrop.Below && target.IsGroup && !target.Collapsed && target.Children.Count > 0) drop = LayerDrop.Into;
            dropTarget = (target, drop);
            dropLine.IsVisible = drop != LayerDrop.Into;
            dropLine.Margin = new Thickness(0, (drop == LayerDrop.Above ? top : row.Bounds.Bottom) - 1, 0, 0);
            foreach (var other in rows.Children.OfType<Border>()) { other.BorderBrush = Palette.Divider; other.BorderThickness = new Thickness(0, 0, 0, 1); }
            if (drop == LayerDrop.Into) { row.BorderBrush = Palette.Accent; row.BorderThickness = new Thickness(1); dropLine.IsVisible = false; }
            break;
        }
    }

    private void EndRowDrag()
    {
        var wasDragging = dragging;
        pressed = null;
        dragging = false;
        dropTarget = null;
        dropLine.IsVisible = false;
        if (wasDragging) Rebuild();
    }

    private void RowReleased(PointerReleasedEventArgs e)
    {
        // Taken before the capture is released: releasing it raises capture-lost, which ends an abandoned drag.
        var layer = pressed;
        pressed = null;
        var wasDragging = dragging;
        dragging = false;
        e.Pointer.Capture(null);
        dragging = wasDragging;
        dropLine.IsVisible = false;
        if (session == null || layer == null) return;
        if (!dragging)
        {
            // A plain click on one of several selected layers narrows the selection to it.
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Shift) && session.Document.SelectedLayerIds.Count > 1)
                session.SelectLayer(layer.Id);
            return;
        }
        dragging = false;
        if (dropTarget is { } drop)
        {
            var moving = session.SelectedRoots();
            if (e.KeyModifiers.HasFlag(KeyModifiers.Alt)) { session.DuplicateSelectedLayers(); moving = session.SelectedRoots(); }
            session.MoveLayers(moving, drop.Target, drop.Drop);
        }
        else Rebuild();
        dropTarget = null;
    }
}
