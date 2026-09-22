using Composa.Editing;
using Composa.Filters;
using Composa.Model;
using Composa.Painting;
using Composa.Rendering;
using Composa.Selections;
using SkiaSharp;

namespace Composa.Core.Tests;

/// <summary>
/// Drives an <see cref="EditorSession"/> with a long random sequence of edits, undos and redos, checking after every
/// step that no layer holds a disposed or mismatched bitmap, no edit is left pending, and the document still renders.
/// </summary>
public class SessionFuzzTests
{
    [Theory]
    [InlineData(1, 500)]
    [InlineData(3, 1400)]
    [InlineData(21, 500)]
    [InlineData(34, 500)]
    public void Random_edit_sequences_keep_the_document_consistent(int seed, int steps)
    {
        var random = new Random(seed);
        var session = EditorSession.NewCanvas(160, 120, SKColors.White);
        var log = new List<string>();
        SKPoint P() => new(random.Next(-20, 180), random.Next(-20, 140));
        SKRect R() { var a = P(); var b = P(); return new SKRect(a.X, a.Y, b.X, b.Y).Standardized; }
        Layer? Any() { var all = session.Document.AllLayers().ToList(); return all.Count == 0 ? null : all[random.Next(all.Count)]; }

        void Check(string what)
        {
            foreach (var layer in session.Document.AllLayers())
            {
                if (layer.Pixels != null && layer.Pixels.Handle == IntPtr.Zero) throw new Exception($"disposed pixels in {layer.Name} after {what}");
                if (layer.Mask != null && layer.Mask.Handle == IntPtr.Zero) throw new Exception($"disposed mask in {layer.Name} after {what}");
                if (layer.Kind == LayerKind.Raster && layer.Pixels == null) throw new Exception($"raster without pixels after {what}");
                if (layer.Mask != null && layer.Pixels != null && (layer.Mask.Width != layer.Pixels.Width || layer.Mask.Height != layer.Pixels.Height)) throw new Exception($"mask size mismatch in {layer.Name} after {what}: {layer.Mask.Width}x{layer.Mask.Height} vs {layer.Pixels.Width}x{layer.Pixels.Height}");
            }
            if (session.Selection is { } s && (s.Handle == IntPtr.Zero || s.Width != session.Document.Width || s.Height != session.Document.Height)) throw new Exception($"bad selection after {what}");
            if (session.IsInteracting) throw new Exception($"edit left pending after {what}");
            if (session.IsEditingText) throw new Exception($"text edit left open after {what}");
            foreach (var guide in session.Guides) if (!guide.IsValid) throw new Exception($"bad guide after {what}");
            var c = session.Composite();
            if (c.Width != session.Document.Width) throw new Exception("composite size");
            using var view = Pixels.NewColor(80, 60);
            session.RenderView(view, new SKRectI(0, 0, 80, 60), new RenderView(0.3f, new SKPoint(5, 5)));
        }

        var ops = new (string Name, Action Run)[]
        {
            ("blank", () => session.AddBlankLayer()),
            ("image", () => { var b = Pixels.NewColor(random.Next(5, 120), random.Next(5, 90)); b.Erase(new SKColor((uint)random.Next() | 0xFF000000)); session.AddImageLayer("img", b, P(), random.Next(2) == 0); }),
            ("adjust-layer", () => session.AddAdjustmentLayer(Adjustment.Create((AdjustmentKind)random.Next(8)))),
            ("shape", () => { session.ShapeKind = (ShapeKind)random.Next(4); session.ShapeLineWidth = random.Next(1, 12); session.AddShape(R()); }),
            ("text", () => session.AddText(P(), new TextStyle { Text = "Ab\nc", Size = random.Next(6, 40), Tracking = random.Next(-2, 6), Leading = random.Next(2) == 0 ? 0 : random.Next(8, 60), BoxWidth = random.Next(2) == 0 ? null : random.Next(20, 120), BoxHeight = random.Next(2) == 0 ? null : random.Next(20, 90) })),
            ("text-edit", () =>
            {
                var editor = random.Next(3) == 0 && session.Document.AllLayers().FirstOrDefault(l => l.Text != null) is { } live ? session.EditText(live) : random.Next(2) == 0 ? session.BeginText(P()) : session.BeginText(R());
                if (editor == null) return;
                for (var i = random.Next(4); i > 0; i--) editor.Insert(random.Next(3) == 0 ? "\n" : "word ");
                if (random.Next(3) == 0) editor.ChangeStyle(st => st with { Size = random.Next(6, 60), Alignment = (TextAlignment)random.Next(3) });
                if (random.Next(4) == 0) session.SetTextBox(random.Next(16, 200), random.Next(16, 120));
                if (random.Next(4) == 0) editor.Undo();
                if (random.Next(5) == 0) session.CancelText(); else session.FinishText();
            }),
            ("effects", () =>
            {
                if (Any() is not { Pixels: not null } l) return;
                var kind = (LayerEffectKind)random.Next(5);
                switch (random.Next(4))
                {
                    case 0: session.AddEffect(l, kind); break;
                    case 1: session.ToggleEffect(l, kind); break;
                    case 2: session.RemoveEffect(l, kind); break;
                    default: session.Apply("Edit", () => session.SetEffects(l, new LayerEffects { Stroke = new StrokeEffect { Size = random.Next(0, 30), Inside = random.Next(2) == 0 }, Shadow = new ShadowEffect { Distance = random.Next(0, 40), Blur = random.Next(0, 30), Angle = random.Next(-180, 180) } })); break;
                }
            }),
            ("guides", () =>
            {
                switch (random.Next(4))
                {
                    case 0: session.AddGuide((GuideAxis)random.Next(2), random.Next(-10, 200)); break;
                    case 1: if (session.Guides.Count > 0) session.MoveGuide(session.Guides[random.Next(session.Guides.Count)].Id, random.Next(0, 200)); break;
                    case 2: if (session.Guides.Count > 0) session.RemoveGuide(session.Guides[random.Next(session.Guides.Count)].Id); break;
                    default: session.ClearGuides(); break;
                }
            }),
            ("object-select", () => { session.SampleAllLayers = random.Next(2) == 0; session.ObjectEdgeOffset = random.Next(-4, 5); var p = P(); if (random.Next(3) == 0) session.SelectSubject((SelectionMode)random.Next(4)); else session.SelectObject((int)p.X, (int)p.Y, (SelectionMode)random.Next(4)); }),
            ("cycle-mode", () => { session.Tool = (Tool)random.Next(15); session.CycleToolMode(); }),
            ("select-layer", () => { if (Any() is { } l) session.SelectLayer(l.Id, random.Next(3) == 0, random.Next(5) == 0); }),
            ("delete", () => { if (session.Document.AllLayers().Count() > 1) session.DeleteSelectedLayers(); }),
            ("duplicate", () => session.DuplicateSelectedLayers()),
            ("group", () => session.GroupSelectedLayers()),
            ("ungroup", () => { if (session.ActiveLayer is { IsGroup: true } g) session.Ungroup(g); }),
            ("merge", () => session.MergeLayers()),
            ("flatten", () => { if (random.Next(10) == 0) session.FlattenImage(); }),
            ("visible", () => { if (Any() is { } l) session.SetVisible(l, random.Next(2) == 0); }),
            ("opacity", () => { if (Any() is { } l) session.Apply("Opacity", () => session.SetOpacity(l, random.NextDouble())); }),
            ("blend", () => { if (Any() is { } l) session.SetBlend(l, (BlendMode)random.Next(16)); }),
            ("clip", () => { if (Any() is { } l) session.ToggleClippingMask(l); }),
            ("move-layer", () => session.MoveActiveLayer(random.Next(2) * 2 - 1)),
            ("reorder", () => { var t = Any(); session.MoveLayers(session.SelectedRoots(), t, (LayerDrop)random.Next(3)); }),
            ("mask-add", () => { if (session.ActiveLayer is { } l) session.AddMask(l, random.Next(2) == 0); }),
            ("mask-delete", () => { if (session.ActiveLayer is { } l) session.DeleteMask(l); }),
            ("mask-apply", () => { if (session.ActiveLayer is { } l) session.ApplyMask(l); }),
            ("mask-toggle-edit", () => session.EditingMask = random.Next(2) == 0),
            ("select-rect", () => session.SelectRect(R(), (SelectionMode)random.Next(4))),
            ("select-ellipse", () => { session.Feather = random.Next(3) == 0 ? 4 : 0; session.SelectEllipse(R(), (SelectionMode)random.Next(4)); }),
            ("select-poly", () => session.SelectPolygon([P(), P(), P(), P()], (SelectionMode)random.Next(4))),
            ("select-all", () => session.SelectAll()),
            ("deselect", () => session.Deselect()),
            ("invert-sel", () => session.InvertSelection()),
            ("expand", () => session.ExpandSelection(random.Next(1, 6))),
            ("contract", () => session.ContractSelection(random.Next(1, 6))),
            ("feather", () => session.FeatherSelection(random.Next(1, 8))),
            ("move-sel", () => session.MoveSelection(random.Next(-30, 30), random.Next(-30, 30))),
            ("wand", () => { session.SampleAllLayers = random.Next(2) == 0; session.WandContiguous = random.Next(2) == 0; var p = P(); session.SelectWand((int)p.X, (int)p.Y, (SelectionMode)random.Next(4)); }),
            ("sel-from-layer", () => { if (Any() is { } l) { if (random.Next(2) == 0) session.SelectLayerPixels(l); else session.SelectLayerMask(l); } }),
            ("fill", () => session.Fill(new SKColor((uint)random.Next() | 0xFF000000))),
            ("clear", () => session.ClearSelection()),
            ("adjust", () => session.Adjust(random.Next(3) switch { 0 => new InvertAdjustment(), 1 => new LevelsAdjustment().WithRange(0, new LevelsRange { Gamma = 1.5 }), _ => new HueSaturationAdjustment().WithShift(HueRange.Master, new HslShift(40, 10, 5)) })),
            ("filter", () => session.ApplyFilter(new FilterSettings { Kind = (FilterKind)random.Next(6), Radius = random.Next(1, 9), Amount = random.Next(5, 60), Distortion = 30 })),
            ("preview-cancel", () => { if (session.BeginPreview("p")) { session.PreviewFilter(new FilterSettings { Kind = FilterKind.GaussianBlur, Radius = 3 }); session.PreviewAdjustment(new InvertAdjustment()); session.CancelPreview(); } }),
            ("caf", () => session.ContentAwareFill()),
            ("stroke", () =>
            {
                session.Tool = (Tool)new[] { (int)Tool.Brush, (int)Tool.SpotHealing, (int)Tool.CloneStamp, (int)Tool.Smear }[random.Next(4)];
                session.EraserMode = random.Next(3) == 0; session.SmearMode = (SmearMode)random.Next(5); session.SampleAllLayers = random.Next(2) == 0; session.CloneAligned = random.Next(2) == 0;
                session.Brush = new BrushSettings { Size = random.Next(1, 60), Hardness = random.NextDouble(), Opacity = random.NextDouble() };
                if (random.Next(2) == 0) session.SetCloneSource(P());
                if (!session.BeginStroke(P(), out _, random.Next(4) == 0)) return;
                for (var i = random.Next(6); i > 0; i--) session.ContinueStroke(P(), (float)random.NextDouble());
                if (random.Next(6) == 0) session.CancelStroke(); else session.EndStroke();
            }),
            ("stroke-interrupted", () => { session.Tool = Tool.Brush; if (session.BeginStroke(P(), out _)) { session.ContinueStroke(P()); session.SelectAll(); session.ContinueStroke(P()); session.EndStroke(); } }),
            ("gradient", () => { if (session.EditableLayer is { } l) { session.GradientRadial = random.Next(2) == 0; session.GradientToTransparent = random.Next(2) == 0; session.Begin("Gradient"); if (!session.IsEditingMask) session.EnsureCoversCanvas(l); var o = session.IsEditingMask ? l.Mask! : l.Pixels!; session.DrawGradient(l, o, P(), P()); session.DrawGradient(l, o, P(), P()); if (random.Next(4) == 0) session.Cancel(); else session.Commit(); } }),
            ("transform", () =>
            {
                if (session.BeginTransform() is not { } e) return;
                switch (random.Next(5)) { case 0: e.MoveBy(random.Next(-40, 40), random.Next(-40, 40)); break; case 1: e.Resize((TransformHandle)random.Next(2, 10), P(), random.Next(2) == 0, random.Next(2) == 0); break; case 2: e.RotateTo(P(), P(), random.Next(2) == 0); break; case 3: e.DistortCorner(random.Next(4), P()); break; default: e.MoveBy(0, 0); break; }
                if (random.Next(5) == 0) session.CancelTransform(); else session.CommitTransform();
            }),
            ("nudge", () => session.Nudge(random.Next(-3, 4), random.Next(-3, 4))),
            ("flip-layer", () => session.FlipLayers(random.Next(2) == 0)),
            ("flip-canvas", () => session.FlipCanvas(random.Next(2) == 0)),
            ("rotate-canvas", () => { if (random.Next(4) == 0) session.RotateCanvas(random.Next(2) == 0); }),
            ("rotate-layer", () => session.RotateLayers(random.Next(2) == 0 ? 90 : -90)),
            ("move-pixels", () => { if (session.BeginMovePixels(random.Next(2) == 0)) { session.MovePixelsBy(random.Next(-30, 30), random.Next(-30, 30)); session.MovePixelsBy(random.Next(-30, 30), random.Next(-30, 30)); session.EndMovePixels(random.Next(4) != 0); } }),
            ("copy-paste", () => { if (random.Next(2) == 0) session.Copy(); else session.CopyMerged(); session.Paste(); }),
            ("cut", () => session.Cut()),
            ("via-copy", () => session.LayerViaCopy()),
            ("crop", () => { if (random.Next(6) == 0) { var r = R(); if (r.Width > 8 && r.Height > 8) session.Crop(Geometry.RoundOut(r)); } }),
            ("canvas-size", () => { if (random.Next(6) == 0) session.ResizeCanvas(random.Next(60, 220), random.Next(60, 180), (Anchor)random.Next(9)); }),
            ("image-size", () => { if (random.Next(6) == 0) session.ResizeImage(random.Next(60, 220), random.Next(60, 180)); }),
            ("trim", () => { if (random.Next(8) == 0) session.TrimCanvas(); }),
            ("rasterize", () => { if (session.ActiveLayer is { IsLive: true } l) session.RasterizeShape(l); }),
            ("solo", () => session.SoloLayerId = random.Next(3) == 0 ? Any()?.Id : null),
            ("undo", () => { for (var i = random.Next(1, 5); i > 0; i--) session.Undo(); }),
            ("redo", () => { for (var i = random.Next(1, 4); i > 0; i--) session.Redo(); }),
            ("roundtrip", () => { if (random.Next(10) != 0) return; using var ms = new MemoryStream(); Composa.IO.ProjectFile.Write(session.Document, ms); ms.Position = 0; var doc = Composa.IO.ProjectFile.Read(ms); using var a = DocumentRenderer.Flatten(session.Document); using var b = DocumentRenderer.Flatten(doc); if (!a.Bytes.AsSpan().SequenceEqual(b.Bytes)) throw new Exception("round trip changed the picture"); }),
        };

        for (var step = 0; step < steps; step++)
        {
            var (name, run) = ops[random.Next(ops.Length)];
            log.Add(name);
            try { run(); Check(name); }
            catch (Exception error)
            {
                throw new Xunit.Sdk.XunitException($"seed {seed} step {step} op {name}: {error.Message}\nlast ops: {string.Join(", ", log.TakeLast(12))}\n{error.StackTrace}");
            }
            if (step % 100 == 99) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        }
    }
}
