using Avalonia.Controls;
using Avalonia.Layout;
using Compositor.Editing;
using Compositor.Model;

namespace Compositor.App;

public sealed partial class MainWindow
{
    /// <summary>Rebuilds the bar under the tabs with the current tool's settings.</summary>
    private void RebuildOptions()
    {
        refreshOptions = null;
        if (session == null) { optionsHost.Child = null; return; }
        var s = session;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, VerticalAlignment = VerticalAlignment.Center };
        void Add(params Control[] controls) => row.Children.AddRange(controls);
        Control Title(string text) => Ui.Label(text, weight: Avalonia.Media.FontWeight.SemiBold);

        switch (s.Tool)
        {
            case Tool.Move:
                Add(Title("Move"));
                BuildTransformFields(row);
                break;
            case Tool.Brush or Tool.SpotHealing or Tool.CloneStamp or Tool.Smear:
                Add(Title(s.Tool switch { Tool.SpotHealing => "Spot Healing", Tool.CloneStamp => "Clone Stamp", Tool.Smear => "Smear", _ => "Brush" }));
                if (s.Tool == Tool.Brush)
                    Add(Ui.Combo(new[] { "Paint", "Erase" }, s.EraserMode ? "Erase" : "Paint", v => v, v => { s.EraserMode = v == "Erase"; SelectTool(Tool.Brush); }, 90));
                if (s.Tool == Tool.Smear)
                    Add(Ui.Combo(Enum.GetValues<SmearMode>(), s.SmearMode, v => v.ToString(), v => { s.SmearMode = v; UpdateStatus(); }, 100));
                var size = Ui.SliderRow("Size", s.Brush.Size, 1, 500, v => s.Brush = s.Brush with { Size = v }, 1, "0", 130);
                var hardness = Ui.SliderRow("Hardness", s.Brush.Hardness * 100, 0, 100, v => s.Brush = s.Brush with { Hardness = v / 100 }, 1, "0", 90);
                Add(size.Row, hardness.Row);
                Action<double>? setOpacity = null;
                if (s.Tool != Tool.SpotHealing)
                {
                    var opacity = Ui.SliderRow(s.Tool == Tool.Smear ? "Strength" : "Opacity", s.Brush.Opacity * 100, 1, 100, v => s.Brush = s.Brush with { Opacity = v / 100 }, 1, "0", 90);
                    setOpacity = opacity.Set;
                    Add(opacity.Row);
                }
                if (s.Tool == Tool.CloneStamp)
                    Add(Ui.Check("Aligned", s.CloneAligned, v => s.CloneAligned = v), Ui.Check("Sample all layers", s.SampleAllLayers, v => s.SampleAllLayers = v));
                refreshOptions = () =>
                {
                    size.Set(Math.Min(500, s.Brush.Size));
                    hardness.Set(s.Brush.Hardness * 100);
                    setOpacity?.Invoke(s.Brush.Opacity * 100);
                };
                break;
            case Tool.Marquee or Tool.Lasso or Tool.Wand:
                Add(Title(s.Tool == Tool.Marquee ? "Marquee" : s.Tool == Tool.Lasso ? "Lasso" : "Magic Wand"));
                if (s.Tool == Tool.Marquee) Add(Ui.Combo(Enum.GetValues<MarqueeKind>(), s.MarqueeKind, v => v.ToString(), v => { s.MarqueeKind = v; SelectTool(Tool.Marquee); }, 110));
                if (s.Tool == Tool.Lasso) Add(Ui.Combo(Enum.GetValues<LassoKind>(), s.LassoKind, v => v.ToString(), v => { s.LassoKind = v; SelectTool(Tool.Lasso); }, 110));
                if (s.Tool == Tool.Wand)
                    Add(Ui.SliderRow("Tolerance", s.WandTolerance, 0, 255, v => s.WandTolerance = (int)v, 1, "0", 120).Row,
                        Ui.Check("Contiguous", s.WandContiguous, v => s.WandContiguous = v), Ui.Check("Sample all layers", s.SampleAllLayers, v => s.SampleAllLayers = v));
                else Add(Ui.SliderRow("Feather", s.Feather, 0, 100, v => s.Feather = v, 1, "0", 100).Row);
                Add(Ui.Separator(), Flat("Select All", s.SelectAll), Flat("Deselect", s.Deselect), Flat("Inverse", s.InvertSelection));
                break;
            case Tool.Gradient:
                Add(Title("Gradient"),
                    Ui.Combo(new[] { "Linear", "Radial" }, s.GradientRadial ? "Radial" : "Linear", v => v, v => s.GradientRadial = v == "Radial", 90),
                    Ui.Check("Foreground to transparent", s.GradientToTransparent, v => { s.GradientToTransparent = v; UpdateStatus(); }));
                var gradientOpacity = Ui.SliderRow("Opacity", s.GradientOpacity * 100, 1, 100, v => s.GradientOpacity = v / 100, 1, "0", 100);
                Add(gradientOpacity.Row);
                refreshOptions = () => gradientOpacity.Set(s.GradientOpacity * 100);
                break;
            case Tool.Shape:
                Add(Title("Shape"), Ui.Combo(Enum.GetValues<ShapeKind>(), s.ShapeKind, v => v == ShapeKind.RoundedRectangle ? "Rounded Rectangle" : v.ToString(), v => s.ShapeKind = v, 160),
                    Ui.SliderRow("Corner radius", s.ShapeCornerRadius, 0, 400, v => s.ShapeCornerRadius = v, 1, "0", 120).Row,
                    Ui.Label("Fills with the foreground color", Palette.Secondary));
                break;
            case Tool.Text:
                Add(Title("Text"), Ui.Label("Click on the canvas to add text in the foreground color, or click existing text to edit it", Palette.Secondary));
                break;
            case Tool.Crop:
                Add(Title("Crop"));
                var readout = Ui.Label("Drag on the canvas to choose the area to keep", Palette.Secondary);
                var apply = Ui.TextButton("Apply", canvas.ApplyCrop, accent: true);
                var cancel = Ui.TextButton("Cancel", canvas.CancelCrop);
                Add(readout, apply, cancel, Ui.Separator(), Flat("Trim transparent edges", s.TrimCanvas));
                refreshOptions = () =>
                {
                    apply.IsEnabled = cancel.IsEnabled = canvas.HasCrop;
                    readout.Text = canvas.CropRect is { } crop ? $"{Math.Round(crop.Width)} × {Math.Round(crop.Height)} px" : "Drag on the canvas to choose the area to keep";
                };
                refreshOptions();
                break;
            case Tool.Eyedropper:
                Add(Title("Eyedropper"), Ui.Label("Samples the merged image", Palette.Secondary));
                break;
            case Tool.Hand or Tool.Zoom:
                Add(Title(s.Tool == Tool.Hand ? "Hand" : "Zoom"), Flat("Fit", canvas.Fit), Flat("100%", () => canvas.ZoomTo(1)), Flat("200%", () => canvas.ZoomTo(2)));
                break;
        }
        optionsHost.Child = row;
    }

    private static Button Flat(string text, Action action)
    {
        var button = Ui.TextButton(text, action);
        button.MinWidth = 0;
        return button;
    }

    private void BuildTransformFields(StackPanel row)
    {
        var s = session!;
        var layer = s.ActiveLayer;
        row.Children.Add(Ui.Check("Transform controls", canvas.ShowTransformControls, v => { canvas.ShowTransformControls = v; canvas.InvalidateVisual(); }));
        if (layer?.Pixels == null)
        {
            row.Children.Add(Ui.Label(layer == null ? "No layer selected" : layer.IsGroup ? "Moves every layer in the folder" : "This layer has no pixels", Palette.Secondary));
            return;
        }
        var updating = false;
        NumericUpDown Field(string label, Func<LayerTransform, double> get, Func<LayerTransform, double, LayerTransform> set, double min, double max, string format)
        {
            var box = Ui.Number(get(layer.Transform), min, max, v =>
            {
                if (updating || s.Document.Find(layer.Id) is not { } live) return;
                s.SetTransform(live, set(live.Transform, v));
            }, 1, format, 74);
            row.Children.Add(Ui.Row(5, Ui.Label(label, Palette.Secondary), box));
            return box;
        }
        var x = Field("X", t => t.X, (t, v) => t with { X = v }, -100000, 100000, "0.#");
        var y = Field("Y", t => t.Y, (t, v) => t with { Y = v }, -100000, 100000, "0.#");
        var w = Field("W", t => t.Width, (t, v) => t with { Width = Math.Max(1, v), Height = Math.Max(1, t.Height * v / Math.Max(1e-6, t.Width)) }, 1, 100000, "0.#");
        var h = Field("H", t => t.Height, (t, v) => t with { Height = Math.Max(1, v) }, 1, 100000, "0.#");
        var angle = Field("∠", t => t.Rotation, (t, v) => t with { Rotation = v }, -360, 360, "0.##");
        refreshOptions = () =>
        {
            if (s.Document.Find(layer.Id) is not { } live) return;
            updating = true;
            x.Value = (decimal)live.Transform.X; y.Value = (decimal)live.Transform.Y;
            w.Value = (decimal)live.Transform.Width; h.Value = (decimal)live.Transform.Height;
            angle.Value = (decimal)live.Transform.Rotation;
            updating = false;
        };
    }
}
