using System.Text.Json;
using Composa.Editing;
using Composa.IO;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;
using static Composa.Core.Tests.TestImages;

namespace Composa.Core.Tests;

public class LayerEffectsTests
{
    private static (EditorSession Session, Layer Layer) RedBoxOnWhite()
    {
        var session = EditorSession.NewCanvas(120, 120, SKColors.White);
        var layer = session.AddImageLayer("box", Solid(40, 40, SKColors.Red), new SKPoint(60, 60));
        return (session, layer);
    }

    [Fact]
    public void Drop_shadow_falls_away_from_the_light_and_stays_out_of_the_pixels()
    {
        var (session, layer) = RedBoxOnWhite();
        Assert.True(session.AddEffect(layer, LayerEffectKind.DropShadow));
        session.SetEffects(layer, layer.Effects! with { Shadow = new ShadowEffect { Angle = 90, Distance = 20, Blur = 0, Opacity = 1, Color = 0xFF0000FF } });
        var flat = session.Composite();
        AssertColor(SKColors.Red, flat.GetPixel(60, 60));                 // The pixels themselves are untouched.
        AssertColor(SKColors.Blue, flat.GetPixel(60, 95));                // Straight below the box: the shadow.
        AssertColor(SKColors.White, flat.GetPixel(60, 30));               // Nothing above it.
        AssertColor(SKColors.White, flat.GetPixel(20, 60));               // Nothing beside it.
        Assert.Equal("Add Drop Shadow", session.History.UndoName);
        session.Undo();
        AssertColor(SKColors.White, session.Composite().GetPixel(60, 95));
    }

    [Fact]
    public void Stroke_rings_the_shape_outside_or_inside()
    {
        var (session, layer) = RedBoxOnWhite();
        session.AddEffect(layer, LayerEffectKind.Stroke);
        session.SetEffects(layer, new LayerEffects { Stroke = new StrokeEffect { Size = 6, Color = 0xFF00FF00, Opacity = 1 } });
        var flat = session.Composite();
        AssertColor(new SKColor(0, 255, 0), flat.GetPixel(37, 60));      // Just outside the left edge.
        AssertColor(SKColors.Red, flat.GetPixel(42, 60));                 // Inside: pixels win over an outside stroke.
        AssertColor(SKColors.White, flat.GetPixel(30, 60));               // Beyond the stroke's reach.
        session.SetEffects(layer, new LayerEffects { Stroke = new StrokeEffect { Size = 6, Color = 0xFF00FF00, Opacity = 1, Inside = true } });
        flat = session.Composite();
        AssertColor(new SKColor(0, 255, 0), flat.GetPixel(42, 60));      // Inside stroke covers the edge pixels.
        AssertColor(SKColors.Red, flat.GetPixel(60, 60));                 // The middle stays.
        AssertColor(SKColors.White, flat.GetPixel(37, 60));
    }

    [Fact]
    public void Color_overlay_and_inner_shadow_stay_inside_the_shape()
    {
        var (session, layer) = RedBoxOnWhite();
        session.SetEffects(layer, new LayerEffects { ColorOverlay = new ColorOverlayEffect { Color = 0xFF0000FF, Opacity = 1 } });
        AssertColor(SKColors.Blue, session.Composite().GetPixel(60, 60));
        AssertColor(SKColors.White, session.Composite().GetPixel(20, 60));
        session.SetEffects(layer, new LayerEffects { InnerShadow = new ShadowEffect { Angle = 90, Distance = 10, Blur = 0, Opacity = 1, Color = 0xFF000000 } });
        var flat = session.Composite();
        AssertColor(SKColors.Black, flat.GetPixel(60, 43));               // The band along the top edge, inside.
        AssertColor(SKColors.Red, flat.GetPixel(60, 70));                 // Further in the pixels show.
        AssertColor(SKColors.White, flat.GetPixel(60, 30));               // Nothing spills outside.
    }

    [Fact]
    public void Outer_glow_lights_every_side_and_stays_out_of_the_pixels()
    {
        var (session, layer) = RedBoxOnWhite();
        Assert.True(session.AddEffect(layer, LayerEffectKind.OuterGlow));
        Assert.Equal(0xFFFFFFFFu, layer.Effects!.OuterGlow!.Color);        // White by default, as Photoshop's is.
        session.SetEffects(layer, new LayerEffects { OuterGlow = new OuterGlowEffect { Size = 10, Color = 0xFF0000FF, Opacity = 1 } });
        var flat = session.Composite();
        AssertColor(SKColors.Red, flat.GetPixel(60, 60));                 // The pixels themselves are untouched.
        foreach (var (x, y) in new[] { (60, 37), (60, 82), (37, 60), (82, 60) })
        {
            var glow = flat.GetPixel(x, y);                                // Just outside each edge: blue over white.
            Assert.True(glow.Blue > 200 && glow.Red < 200, $"Expected a blue glow at {x},{y} but found {glow}");
        }
        AssertColor(SKColors.White, flat.GetPixel(60, 12));               // Well beyond its reach.
        Assert.True(layer.VisibleBounds.Top < layer.Bounds.Top);
        Assert.Equal("Add Outer Glow", session.History.UndoName);
        session.SetEffects(layer, new LayerEffects { OuterGlow = new OuterGlowEffect { Size = 10, Color = 0xFF0000FF, Opacity = 1, Enabled = false } });
        AssertColor(SKColors.White, session.Composite().GetPixel(60, 37));
    }

    [Fact]
    public void Inner_glow_lights_the_inside_of_the_edges_and_stays_within_the_shape()
    {
        var (session, layer) = RedBoxOnWhite();
        Assert.True(session.AddEffect(layer, LayerEffectKind.InnerGlow));
        Assert.Equal((10d, 0xFFFFFFFFu, 0.75), (layer.Effects!.InnerGlow!.Size, layer.Effects.InnerGlow.Color, layer.Effects.InnerGlow.Opacity));
        session.SetEffects(layer, new LayerEffects { InnerGlow = new InnerGlowEffect { Size = 12, Color = 0xFF0000FF, Opacity = 1 } });
        var flat = session.Composite();
        foreach (var (x, y) in new[] { (60, 41), (60, 78), (41, 60), (78, 60) })
        {
            var glow = flat.GetPixel(x, y);                                // Just inside each edge: blue over red.
            Assert.True(glow.Blue > 80 && glow.Red < 200, $"Expected a blue glow at {x},{y} but found {glow}"); // Half strength at the edge itself, as a Gaussian of the shape is.
        }
        AssertColor(SKColors.Red, flat.GetPixel(60, 60), 8);              // The middle is barely touched.
        AssertColor(SKColors.White, flat.GetPixel(60, 37));               // Nothing spills outside.
        Assert.Equal(2, layer.Effects!.Margin());                          // An inside effect needs no room around the layer.
        Assert.Equal("Add Inner Glow", session.History.UndoName);
        session.SetEffects(layer, new LayerEffects { InnerGlow = new InnerGlowEffect { Size = 12, Color = 0xFF0000FF, Opacity = 1, Enabled = false } });
        AssertColor(SKColors.Red, session.Composite().GetPixel(60, 41));
    }

    [Fact]
    public void Effects_follow_the_mask_and_the_transform()
    {
        var (session, layer) = RedBoxOnWhite();
        session.AddMask(layer, hideAll: true);
        session.EditingMask = false;
        session.SetEffects(layer, new LayerEffects { Stroke = new StrokeEffect { Size = 4, Color = 0xFF00FF00, Opacity = 1 } });
        // Fully hidden: nothing to stroke around.
        AssertColor(SKColors.White, session.Composite().GetPixel(38, 60));
        session.DeleteMask(layer);
        session.SetTransform(layer, layer.Transform with { X = 20, Y = 20, Width = 80, Height = 80 });
        var flat = session.Composite();
        // Scaled up two times: the 4 px stroke reads as 8 document pixels wide.
        AssertColor(new SKColor(0, 255, 0), flat.GetPixel(15, 60));
        AssertColor(SKColors.White, flat.GetPixel(9, 60));
        Assert.True(layer.VisibleBounds.Left < layer.Bounds.Left);
    }

    [Fact]
    public void Changing_the_selection_drops_the_highlighted_effect()
    {
        var (session, layer) = RedBoxOnWhite();
        session.AddEffect(layer, LayerEffectKind.Stroke);
        Assert.NotNull(session.SelectedEffect);
        var layersChanged = 0;
        session.LayersChanged += () => layersChanged++;
        session.SelectRect(new SKRect(0, 0, 10, 10));
        Assert.Null(session.SelectedEffect);
        Assert.Equal(1, layersChanged);
        // Delete now has no effect to take: it is left to clear the selected pixels.
        Assert.False(session.RemoveSelectedEffect());
        Assert.NotNull(layer.Effects!.Stroke);
    }

    [Fact]
    public void Disabled_effects_keep_their_settings_and_deleting_the_selected_one_removes_it()
    {
        var (session, layer) = RedBoxOnWhite();
        session.SetEffects(layer, new LayerEffects { Shadow = new ShadowEffect { Distance = 20, Blur = 0, Opacity = 1 }, Stroke = new StrokeEffect() });
        session.ToggleEffect(layer, LayerEffectKind.DropShadow);
        Assert.False(layer.Effects!.IsEnabled(LayerEffectKind.DropShadow));
        Assert.Equal(20, layer.Effects.Shadow!.Distance);
        AssertColor(SKColors.White, session.Composite().GetPixel(60, 95));
        Assert.Equal([LayerEffectKind.Stroke, LayerEffectKind.DropShadow], layer.Effects.Kinds);
        session.SelectedEffect = (layer.Id, LayerEffectKind.Stroke);
        Assert.True(session.RemoveSelectedEffect());
        Assert.Null(layer.Effects.Stroke);
        Assert.Null(session.SelectedEffect);
        session.Undo();
        Assert.NotNull(session.Document.Find(layer.Id)!.Effects!.Stroke);
    }

    [Fact]
    public void Copying_an_effect_and_folders_cannot_take_one()
    {
        var (session, layer) = RedBoxOnWhite();
        var other = session.AddImageLayer("other", Solid(10, 10, SKColors.Blue), new SKPoint(20, 20));
        session.SetEffects(layer, new LayerEffects { Stroke = new StrokeEffect { Size = 3, Color = 0xFF123456 } });
        Assert.True(session.CanCopyEffect(LayerEffectKind.Stroke, layer, other));
        Assert.False(session.CanCopyEffect(LayerEffectKind.DropShadow, layer, other));
        session.CopyEffect(LayerEffectKind.Stroke, layer, other);
        Assert.Equal(layer.Effects!.Stroke, other.Effects!.Stroke);
        session.GroupSelectedLayers();
        Assert.False(session.AddEffect(session.ActiveLayer!, LayerEffectKind.Stroke));
    }

    [Fact]
    public void Effects_round_trip_through_the_project_file_and_render_the_same()
    {
        var (session, layer) = RedBoxOnWhite();
        session.SetEffects(layer, new LayerEffects
        {
            Stroke = new StrokeEffect { Size = 5, Color = 0xFF00FF00, Inside = true, Enabled = false },
            Shadow = new ShadowEffect { Angle = 45, Distance = 12, Blur = 6, Opacity = 0.7, Color = 0xFF102030 },
            ColorOverlay = new ColorOverlayEffect { Color = 0xFFFF00FF, Opacity = 0.4 },
            OuterGlow = new OuterGlowEffect { Size = 9, Color = 0xFFFFEE00, Opacity = 0.8 },
            InnerGlow = new InnerGlowEffect { Size = 7, Color = 0xFF00FFEE, Opacity = 0.6 }
        });
        using var stream = new MemoryStream();
        ProjectFile.Write(session.Document, stream);
        stream.Position = 0;
        var loaded = ProjectFile.Read(stream);
        Assert.Equal(layer.Effects, loaded.Find(layer.Id)!.Effects);
        using var expected = DocumentRenderer.Flatten(session.Document);
        using var actual = DocumentRenderer.Flatten(loaded);
        Assert.Equal(expected.Bytes, actual.Bytes);
    }

    [Fact]
    public void Mac_projects_bring_their_effects_along()
    {
        var folder = Path.Combine(Path.GetTempPath(), "composa-effects-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "images"));
        try
        {
            var id = Guid.NewGuid();
            File.WriteAllBytes(Path.Combine(folder, "images", id + ".png"), ImageFiles.Encode(Solid(10, 10, SKColors.Red), ExportFormat.Png));
            var manifest = new
            {
                format = "com.compositor.project", version = 8, colorSpace = "sRGB", documentID = Guid.NewGuid(), width = 50, height = 50,
                layers = new object[]
                {
                    new
                    {
                        id, name = "Box", isVisible = true, transform = new { origin = new[] { 10.0, 10.0 }, size = new[] { 10.0, 10.0 }, rotation = 0.0 },
                        imageFile = id + ".png", opacity = 1.0, blendMode = "Normal",
                        effects = new
                        {
                            stroke = new { size = 3.0, red = 0.0, green = 1.0, blue = 0.0, opacity = 1.0, inside = false },
                            shadow = new { enabled = false, angle = 120.0, distance = 8.0, blur = 4.0, red = 0.0, green = 0.0, blue = 0.0, opacity = 0.5 },
                            innerShadow = new { angle = 90.0, distance = 3.0, blur = 2.0, red = 1.0, green = 0.0, blue = 0.0, opacity = 0.6 },
                            outerGlow = new { size = 14.0, red = 1.0, green = 1.0, blue = 0.0, opacity = 0.9 },
                            innerGlow = new { size = 6.0, red = 0.0, green = 1.0, blue = 1.0, opacity = 0.4 }
                        }
                    }
                }
            };
            File.WriteAllText(Path.Combine(folder, "manifest.json"), JsonSerializer.Serialize(manifest));
            var document = MacProject.Load(folder);
            var effects = Assert.Single(document.Layers).Effects!;
            Assert.Equal(3, effects.Stroke!.Size);
            Assert.Equal(0xFF00FF00u, effects.Stroke.Color);
            Assert.False(effects.Shadow!.Enabled);
            Assert.Equal(120, effects.Shadow.Angle);
            Assert.Equal(0.6, effects.InnerShadow!.Opacity, 3);
            Assert.Null(effects.ColorOverlay);
            Assert.Equal((14d, 0xFFFFFF00u, 0.9), (effects.OuterGlow!.Size, effects.OuterGlow.Color, effects.OuterGlow.Opacity));
            Assert.Equal((6d, 0xFF00FFFFu, 0.4), (effects.InnerGlow!.Size, effects.InnerGlow.Color, effects.InnerGlow.Opacity));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Margin_reaches_as_far_as_the_visible_effects()
    {
        Assert.Equal(0, LayerEffects.Empty.Margin());
        Assert.Equal(12, new LayerEffects { Stroke = new StrokeEffect { Size = 10 } }.Margin());
        Assert.Equal(2, new LayerEffects { Stroke = new StrokeEffect { Size = 10, Inside = true } }.Margin());
        Assert.Equal(0, new LayerEffects { Stroke = new StrokeEffect { Size = 10, Enabled = false } }.Margin());
        Assert.Equal(20 + 30 + 2, new LayerEffects { Shadow = new ShadowEffect { Distance = 20, Blur = 10 } }.Margin());
        Assert.Equal(2, new LayerEffects { ColorOverlay = new ColorOverlayEffect() }.Margin());
        Assert.Equal(30 + 2, new LayerEffects { OuterGlow = new OuterGlowEffect { Size = 20 } }.Margin());
        Assert.Equal(2, new LayerEffects { InnerGlow = new InnerGlowEffect { Size = 40 } }.Margin());
    }
}
