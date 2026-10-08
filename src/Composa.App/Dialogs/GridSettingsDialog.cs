using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Composa.Model;
using SkiaSharp;

namespace Composa.App.Dialogs;

/// <summary>
/// View > Grid Settings: how the layout grid is drawn and spaced, after Photoshop's Guides, Grid & Slices preferences.
/// Every change shows on the canvas at once through the preview; the caller puts back what was there on Cancel.
/// </summary>
public static class GridSettingsDialog
{
    private static readonly IBrush Warning = new SolidColorBrush(Color.Parse("#E0A040"));

    public static async Task<(LayoutGrid Grid, GridAppearance Appearance)?> Show(Window owner, LayoutGrid initialGrid, GridAppearance initialAppearance,
        Action<LayoutGrid, GridAppearance> preview)
    {
        var grid = initialGrid.Normalized();
        var appearance = initialAppearance.Normalized();
        var updating = false;
        DialogWindow dialog = null!;

        var presets = Enum.GetValues<GridColorPreset>();
        var styles = Enum.GetValues<GridStyle>();
        ComboBox presetBox = null!, styleBox = null!;
        Controls.SliderField opacityField = null!;
        NumericUpDown spacingBox = null!, subdivisionsBox = null!;
        var swatch = new Border { Width = 44, Height = 24, CornerRadius = new CornerRadius(3), BorderBrush = Brushes.White, BorderThickness = new Thickness(1), Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) };
        var note = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 360, FontSize = 12 };

        void Change(GridAppearance a, LayoutGrid g)
        {
            appearance = a;
            grid = g;
            updating = true;
            presetBox.SelectedIndex = Array.IndexOf(presets, a.Preset);
            styleBox.SelectedIndex = Array.IndexOf(styles, a.Style);
            opacityField.Value = a.Opacity;
            spacingBox.Value = g.Spacing;
            subdivisionsBox.Value = g.Subdivisions;
            updating = false;
            swatch.Background = new SolidColorBrush(a.Color.ToAvalonia());
            note.Text = g.IsValid
                ? Loc.Format("A subdivision every {0} pixels.", g.Step.ToString("0.##"))
                : Loc.Format("Use gridlines every {0}-{1} pixels and {2}-{3} subdivisions, no more than the pixels between gridlines.",
                    LayoutGrid.MinSpacing, LayoutGrid.MaxSpacing, LayoutGrid.MinSubdivisions, LayoutGrid.MaxSubdivisions);
            note.Foreground = g.IsValid ? Palette.Secondary : Warning;
            dialog.CanAccept = g.IsValid;
            if (g.IsValid) preview(g, a);
        }

        presetBox = Ui.Combo(presets, appearance.Preset, p => Loc.T(GridAppearance.DisplayName(p)), p => { if (!updating) Change(appearance with { Preset = p }, grid); }, 150);
        ToolTip.SetTip(swatch, Loc.T("Choose a custom grid color"));
        swatch.PointerPressed += async (_, _) =>
        {
            // The picker shows on the canvas as it goes; a color other than the one in use becomes the Custom color.
            var before = appearance;
            GridAppearance Custom(SKColor color) => before with { Preset = GridColorPreset.Custom, CustomColor = (uint)color | 0xFF000000 };
            var picked = await Prompts.Color(dialog, Loc.T("Grid Color"), before.Color, color => Change(Custom(color), grid));
            Change(picked is { } color && color != before.Color ? Custom(color) : before, grid);
        };
        styleBox = Ui.Combo(styles, appearance.Style, s => Loc.T(GridAppearance.DisplayName(s)), s => { if (!updating) Change(appearance with { Style = s }, grid); }, 150);
        opacityField = Ui.SliderField("Opacity", appearance.Opacity, GridAppearance.MinOpacity, GridAppearance.MaxOpacity,
            v => Change(appearance with { Opacity = (int)v }, grid), width: 204, reset: GridAppearance.DefaultOpacity);
        spacingBox = Ui.Number(grid.Spacing, LayoutGrid.MinSpacing, LayoutGrid.MaxSpacing, v => { if (!updating) Change(appearance, grid with { Spacing = (int)v }); }, width: 120);
        subdivisionsBox = Ui.Number(grid.Subdivisions, LayoutGrid.MinSubdivisions, LayoutGrid.MaxSubdivisions, v => { if (!updating) Change(appearance, grid with { Subdivisions = (int)v }); }, width: 120);
        // The Custom color is kept, so it is still there if Custom is chosen again.
        var restore = Ui.TextButton("Restore Defaults", () => Change(new GridAppearance { CustomColor = appearance.CustomColor }, new LayoutGrid()));

        var form = CanvasDialogs.Form(
            (Loc.T("Color"), Ui.Row(8, presetBox, swatch)),
            (Loc.T("Style"), styleBox),
            (Loc.T("Opacity"), opacityField),
            (Loc.T("Gridline every"), Ui.Row(6, spacingBox, Ui.Label(Loc.T("pixels"), Palette.Secondary))),
            (Loc.T("Subdivisions"), subdivisionsBox));
        dialog = new DialogWindow("Grid Settings", Ui.Column(12, form, note, restore));
        Change(appearance, grid);
        return await dialog.Ask(owner) ? (grid, appearance) : null;
    }
}
