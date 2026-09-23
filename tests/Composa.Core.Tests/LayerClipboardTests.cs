using Composa.Editing;
using Composa.Filters;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;
using static Composa.Core.Tests.TestImages;

namespace Composa.Core.Tests;

/// <summary>Duplicate Layer over several layers, and Copy and Paste of layers taken whole.</summary>
[Collection(ClipboardCollection.Name)]
public class LayerClipboardTests
{
    private static EditorSession ThreeLayers(out Layer a, out Layer b, out Layer c)
    {
        var session = EditorSession.NewCanvas(100, 100, SKColors.White);
        a = session.AddImageLayer("A", Solid(10, 10, SKColors.Red), new SKPoint(20, 20));
        b = session.AddImageLayer("B", Solid(10, 10, SKColors.Green), new SKPoint(50, 50));
        c = session.AddImageLayer("C", Solid(10, 10, SKColors.Blue), new SKPoint(80, 80));
        return session;
    }

    [Fact]
    public void Several_duplicates_stack_together_above_the_topmost_original_and_end_up_selected()
    {
        var session = ThreeLayers(out var a, out var b, out var c);
        session.SelectLayer(a.Id);
        session.SelectLayer(c.Id, extend: true);
        session.DuplicateSelectedLayers();
        var names = session.Document.Layers.Select(l => l.Name).ToList();
        // Background, A, B, C, then both copies above C in the originals' order.
        Assert.Equal(["Background", "A", "B", "C", "A copy", "C copy"], names);
        Assert.Equal(2, session.Document.SelectedLayerIds.Count);
        Assert.Equal("C copy", session.ActiveLayer!.Name);                // C was the active original.
        Assert.DoesNotContain(a.Id, session.Document.SelectedLayerIds);
        Assert.Equal("Duplicate Layers", session.History.UndoName);
        session.Undo();
        Assert.Equal(4, session.Document.Layers.Count);
        _ = b;
    }

    [Fact]
    public void Ctrl_J_duplicates_a_folder_with_what_it_holds()
    {
        var session = ThreeLayers(out var a, out var b, out _);
        session.SelectLayer(a.Id);
        session.SelectLayer(b.Id, extend: true);
        session.GroupSelectedLayers();
        var folder = session.ActiveLayer!;
        session.LayerViaCopy();
        var copy = session.ActiveLayer!;
        Assert.NotEqual(folder.Id, copy.Id);
        Assert.True(copy.IsGroup);
        Assert.Equal(2, copy.Children.Count);
        Assert.All(copy.Children, child => Assert.Null(session.Document.Find(folder.Id)!.Children.FirstOrDefault(o => o.Id == child.Id)));
        Assert.Equal("Duplicate Layer", session.History.UndoName);
    }

    [Fact]
    public void Copy_with_nothing_selected_takes_the_layers_whole_and_paste_puts_copies_above()
    {
        var session = ThreeLayers(out var a, out var b, out var c);
        session.SelectLayer(a.Id);
        session.SelectLayer(b.Id, extend: true);
        session.GroupSelectedLayers();
        var folder = session.ActiveLayer!;
        session.SetEffects(folder.Children[0], new LayerEffects { Stroke = new StrokeEffect { Size = 3 } });
        Assert.True(session.CanCopyLayers);
        Assert.True(session.Copy());
        Assert.NotNull(EditorSession.CopiedLayers);
        session.SelectLayer(c.Id);
        var pasted = session.Paste()!;
        Assert.True(pasted.IsGroup);
        Assert.Equal(2, pasted.Children.Count);
        Assert.Equal(3, pasted.Children[0].Effects!.Stroke!.Size);          // Effects travel with the layer.
        Assert.Equal(pasted.Id, session.Document.Layers[^1].Id);            // Above C, the active layer at paste time.
        Assert.Equal(a.Transform.X, pasted.Children[0].Transform.X);      // In its own project it keeps its place.
        Assert.Equal("Paste Layer", session.History.UndoName);
        Assert.Equal(pasted.Id, session.Document.ActiveLayerId);
    }

    [Fact]
    public void Layers_pasted_into_another_project_arrive_centered_together()
    {
        var source = ThreeLayers(out var a, out _, out var c);
        source.SelectLayer(a.Id);
        source.SelectLayer(c.Id, extend: true);
        Assert.True(source.Copy());
        var target = EditorSession.NewCanvas(400, 200, SKColors.White);
        var pasted = target.Paste()!;
        Assert.Equal(3, target.Document.Layers.Count);
        Assert.Equal(2, target.Document.SelectedLayerIds.Count);
        // A covered 15…25, C 75…85: together 15…85, centered at 50, now centered on the 400 × 200 canvas.
        var bounds = target.Document.Layers.Skip(1).Select(l => l.Bounds).Aggregate(SKRect.Union);
        Assert.Equal(200, bounds.MidX, 0.5);
        Assert.Equal(100, bounds.MidY, 0.5);
        Assert.Equal(70, bounds.Width, 0.5);
        Assert.Equal("Paste Layers", target.History.UndoName);
        _ = pasted;
    }

    [Fact]
    public void An_adjustment_layer_copies_whole_and_a_pixel_copy_replaces_it()
    {
        var session = ThreeLayers(out var a, out _, out _);
        session.AddAdjustmentLayer(new InvertAdjustment());
        Assert.True(session.CanCopy);
        Assert.True(session.Copy());
        Assert.Null(EditorSession.Clipboard);                                // Nothing for other apps to take.
        Assert.IsType<InvertAdjustment>(session.Paste()!.Adjustment);
        session.SelectLayer(a.Id);
        session.SelectRect(new SKRect(20, 20, 25, 25));
        Assert.False(session.CanCopyLayers);
        Assert.True(session.Copy());
        Assert.Null(EditorSession.CopiedLayers);
        Assert.NotNull(EditorSession.Clipboard);
        Assert.NotNull(session.Paste()!.Pixels);
    }
}
