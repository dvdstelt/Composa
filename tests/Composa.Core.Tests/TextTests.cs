using Composa.Editing;
using Composa.IO;
using Composa.Model;
using Composa.Text;
using SkiaSharp;
using static Composa.Core.Tests.TestImages;

namespace Composa.Core.Tests;

public class TextLayoutTests
{
    private static readonly string Family = EditorSession.FontFamilies.FirstOrDefault(f => f.Contains("Sans", StringComparison.OrdinalIgnoreCase)) ?? EditorSession.FontFamilies.First();

    [Fact]
    public void Point_text_is_as_big_as_its_longest_line()
    {
        var layout = new TextLayout(new TextStyle { Text = "Hello\nComposa for Linux", Size = 40, FontFamily = Family });
        Assert.Equal(2, layout.Lines.Count);
        Assert.Equal("[Hello][Composa for Linux]", layout.ToString());
        Assert.True(layout.Lines[1].VisibleWidth > layout.Lines[0].VisibleWidth);
        Assert.Equal((int)Math.Ceiling(layout.Lines[1].VisibleWidth + 2 * TextLayout.Padding + 4), layout.Width);
        Assert.Equal(48, layout.LineHeight); // Auto leading: 120% of the size.
        Assert.Equal(layout.Lines[0].Baseline + 48, layout.Lines[1].Baseline);
    }

    [Fact]
    public void Paragraph_text_wraps_at_spaces_and_clips_to_its_box()
    {
        var style = new TextStyle { Text = "one two three four five six", Size = 20, FontFamily = Family, BoxWidth = 120, BoxHeight = 60 };
        var layout = new TextLayout(style);
        Assert.Equal(120, layout.Width);
        Assert.Equal(60, layout.Height);
        Assert.True(layout.Lines.Count >= 3);
        foreach (var line in layout.Lines)
        {
            Assert.True(line.VisibleWidth <= 120 - 2 * TextLayout.Padding + 0.01, $"line {line.Start}-{line.End} is too wide");
            if (line.End < layout.Text.Length) Assert.Equal(' ', layout.Text[line.End - 1]); // Breaks fall after a space.
        }
        Assert.True(layout.Overflows);
        using var bitmap = layout.Render();
        Assert.Equal((120, 60), (bitmap.Width, bitmap.Height));
    }

    [Fact]
    public void Tracking_and_leading_spread_the_letters_and_lines()
    {
        var plain = new TextLayout(new TextStyle { Text = "abc\nabc", Size = 30, FontFamily = Family });
        var spaced = new TextLayout(new TextStyle { Text = "abc\nabc", Size = 30, FontFamily = Family, Tracking = 10, Leading = 100 });
        Assert.Equal(plain.Lines[0].VisibleWidth + 30, spaced.Lines[0].VisibleWidth, 0.5);
        Assert.Equal(100, spaced.Lines[1].Baseline - spaced.Lines[0].Baseline, 0.01);
    }

    [Fact]
    public void Alignment_moves_lines_inside_the_box()
    {
        var right = new TextLayout(new TextStyle { Text = "ab\nabcd", Size = 30, FontFamily = Family, Alignment = TextAlignment.Right });
        Assert.True(right.Lines[0].X > right.Lines[1].X);
        Assert.Equal(right.Lines[0].X + right.Lines[0].VisibleWidth, right.Lines[1].X + right.Lines[1].VisibleWidth, 0.01);
        var center = new TextLayout(new TextStyle { Text = "ab\nabcd", Size = 30, FontFamily = Family, Alignment = TextAlignment.Center, BoxWidth = 300, BoxHeight = 100 });
        Assert.Equal(150, center.Lines[0].X + center.Lines[0].VisibleWidth / 2, 0.5);
    }

    [Fact]
    public void Caret_geometry_round_trips_through_points()
    {
        var layout = new TextLayout(new TextStyle { Text = "Hello world\nSecond", Size = 24, FontFamily = Family });
        for (var index = 0; index <= layout.Text.Length; index++)
        {
            var (x, top, bottom) = layout.CaretAt(index);
            Assert.Equal(index, layout.IndexAt(new SKPoint(x + 0.1f, (top + bottom) / 2)));
        }
        Assert.Equal(1, layout.LineOf(12));
        Assert.Equal(0, layout.LineOf(11));
        var below = layout.IndexOnAdjacentLine(3, 1);
        Assert.InRange(below, 12, 18);
        Assert.Equal(0, layout.IndexOnAdjacentLine(3, -1));
        Assert.Equal(6, layout.WordStart(8));
        Assert.Equal(11, layout.WordEnd(8));
        var rects = layout.SelectionRects(3, 14);
        Assert.Equal(2, rects.Count);
        Assert.True(rects[0].Right > rects[0].Left && rects[1].Top > rects[0].Top);
    }

    [Fact]
    public void Layer_names_come_from_the_first_words()
    {
        Assert.Equal("Text", new TextStyle { Text = "  \n " }.LayerName());
        Assert.Equal("Hello World", new TextStyle { Text = "Hello\n\nWorld  " }.LayerName());
        Assert.Equal(40, new TextStyle { Text = new string('x', 100) }.LayerName().Length);
    }
}

public class TextEditorTests
{
    [Fact]
    public void Typing_selecting_and_deleting()
    {
        var editor = new TextEditor(new TextStyle { Text = "" });
        editor.Insert("Hello");
        editor.Insert(" world");
        Assert.Equal("Hello world", editor.Text);
        Assert.Equal(11, editor.Caret);
        editor.MoveHorizontal(-1, select: false, word: true);
        Assert.Equal(6, editor.Caret);
        editor.MoveToDocumentEdge(end: true, select: true);
        Assert.Equal("world", editor.SelectedText);
        editor.Insert("there");
        Assert.Equal("Hello there", editor.Text);
        editor.Backspace(word: true);
        Assert.Equal("Hello ", editor.Text);
        editor.MoveToDocumentEdge(end: false, select: false);
        editor.Delete();
        Assert.Equal("ello ", editor.Text);
        editor.SelectAll();
        editor.Insert("A\r\nB");
        Assert.Equal("A\nB", editor.Text);
    }

    [Fact]
    public void Undo_groups_typing_and_restores_the_caret()
    {
        var editor = new TextEditor(new TextStyle { Text = "" });
        foreach (var c in "abc") editor.Insert(c.ToString());
        editor.Insert("\n"); // A line break ends the typing run.
        editor.Insert("d");
        Assert.Equal("abc\nd", editor.Text);
        Assert.True(editor.Undo());
        Assert.Equal("abc\n", editor.Text);
        Assert.True(editor.Undo());
        Assert.Equal("abc", editor.Text);
        Assert.True(editor.Undo());
        Assert.Equal("", editor.Text);
        Assert.False(editor.Undo());
        Assert.True(editor.Redo());
        Assert.Equal("abc", editor.Text);
        Assert.Equal(3, editor.Caret);
        editor.ChangeStyle(s => s with { Size = 90 });
        Assert.Equal(90, editor.Style.Size);
        editor.Undo();
        Assert.Equal(72, editor.Style.Size);
    }

    [Fact]
    public void Surrogate_pairs_move_as_one_character()
    {
        var editor = new TextEditor(new TextStyle { Text = "a😀b" });
        editor.MoveToDocumentEdge(end: false, select: false);
        editor.MoveHorizontal(1, false);
        editor.MoveHorizontal(1, false);
        Assert.Equal(3, editor.Caret);
        editor.Backspace();
        Assert.Equal("ab", editor.Text);
    }
}

public class TextSessionTests
{
    private static readonly string Family = EditorSession.FontFamilies.FirstOrDefault(f => f.Contains("Sans", StringComparison.OrdinalIgnoreCase)) ?? EditorSession.FontFamilies.First();

    [Fact]
    public void New_point_text_is_typed_live_committed_once_and_discarded_when_empty()
    {
        var session = EditorSession.NewCanvas(400, 200, SKColors.White);
        session.TextDefaults = new TextStyle { FontFamily = Family, Size = 40 };
        session.Foreground = SKColors.Red;
        var editor = session.BeginText(new SKPoint(50, 60));
        Assert.True(session.IsEditingText);
        Assert.Equal(Tool.Text, session.Tool);
        Assert.Equal(2, session.Document.Layers.Count);
        var layer = session.TextEditLayer!;
        Assert.Equal(0xFFFF0000u, layer.Text!.Color);
        editor.Insert("Hi");
        Assert.Equal("Hi", layer.Text!.Text);
        Assert.Equal("Hi", layer.Name);
        Assert.Equal(50 - TextLayout.Padding, layer.Transform.X);
        var narrow = layer.Pixels!.Width;
        editor.Insert(" there");
        Assert.True(layer.Pixels!.Width > narrow); // Point text grows as it is typed.
        Assert.True(session.FinishText());
        Assert.False(session.IsEditingText);
        Assert.Equal("Text", session.History.UndoName);
        Assert.Equal("Hi there", session.Document.Find(layer.Id)!.Text!.Text);
        Assert.Equal("", session.TextDefaults.Text);
        Assert.Equal(40, session.TextDefaults.Size);

        session.BeginText(new SKPoint(10, 10));
        Assert.Equal(3, session.Document.Layers.Count);
        session.FinishText(); // Nothing typed: the layer goes away and no undo step is left.
        Assert.Equal(2, session.Document.Layers.Count);
        Assert.Equal("Text", session.History.UndoName);
    }

    [Fact]
    public void Editing_existing_text_can_be_cancelled_or_committed()
    {
        var session = EditorSession.NewCanvas(400, 200, SKColors.White);
        var layer = session.AddText(new SKPoint(20, 20), new TextStyle { Text = "Hello", FontFamily = Family, Size = 40 });
        var before = session.History.Count;
        var editor = session.EditText(layer)!;
        editor.SelectAll();
        editor.Insert("Changed");
        Assert.Equal("Changed", layer.Text!.Text);
        session.CancelText();
        Assert.Equal("Hello", session.Document.Find(layer.Id)!.Text!.Text);
        Assert.Equal(before, session.History.Count);

        editor = session.EditText(session.Document.Find(layer.Id)!)!;
        session.FinishText(); // Unchanged: no undo step.
        Assert.Equal(before, session.History.Count);

        editor = session.EditText(session.Document.Find(layer.Id)!)!;
        editor.Insert("!");
        session.Tool = Tool.Move; // Leaving the tool commits.
        Assert.False(session.IsEditingText);
        Assert.Equal("Hello!", session.Document.Find(layer.Id)!.Text!.Text);
        Assert.Equal("Edit Text", session.History.UndoName);
        Assert.Equal(before + 1, session.History.Count);
    }

    [Fact]
    public void Paragraph_boxes_keep_their_size_and_wrap()
    {
        var session = EditorSession.NewCanvas(400, 300, SKColors.White);
        session.TextDefaults = new TextStyle { FontFamily = Family, Size = 20 };
        var editor = session.BeginText(new SKRect(40, 60, 240, 180));
        var layer = session.TextEditLayer!;
        Assert.Equal((200d, 120d), (layer.Transform.Width, layer.Transform.Height));
        Assert.Equal((40d, 60d), (layer.Transform.X, layer.Transform.Y));
        editor.Insert("Text that wraps inside its paragraph box for sure");
        Assert.Equal((200d, 120d), (layer.Transform.Width, layer.Transform.Height));
        Assert.True(editor.Layout.Lines.Count > 1);
        session.SetTextBox(300, 150);
        Assert.Equal((300d, 150d), (layer.Transform.Width, layer.Transform.Height));
        Assert.Equal((40d, 60d), (layer.Transform.X, layer.Transform.Y));
        session.FinishText();
        Assert.Equal((300d, 150d), (session.Document.Find(layer.Id)!.Text!.BoxWidth, session.Document.Find(layer.Id)!.Text!.BoxHeight));
    }

    [Fact]
    public void Right_aligned_point_text_grows_leftward_and_rotation_keeps_its_anchor()
    {
        var session = EditorSession.NewCanvas(400, 200, SKColors.White);
        var layer = session.AddText(new SKPoint(300, 40), new TextStyle { Text = "ab", FontFamily = Family, Size = 30, Alignment = TextAlignment.Right });
        var rightEdge = layer.Transform.X + layer.Transform.Width;
        session.Begin("Edit Text");
        session.SetText(layer, layer.Text! with { Text = "abcdef" });
        session.Commit();
        Assert.Equal(rightEdge, layer.Transform.X + layer.Transform.Width, 0.5);

        session.SetTransform(layer, layer.Transform with { Rotation = 30 });
        var corner = layer.Matrix.MapPoint(layer.Pixels!.Width, 0);
        session.Begin("Edit Text");
        session.SetText(layer, layer.Text! with { Text = "abcdefghij" });
        session.Commit();
        var after = layer.Matrix.MapPoint(layer.Pixels!.Width, 0);
        Assert.Equal(corner.X, after.X, 0.5);
        Assert.Equal(corner.Y, after.Y, 0.5);
        Assert.Equal(30, layer.Transform.Rotation);
    }

    [Fact]
    public void Fill_recolors_live_text_and_the_style_round_trips()
    {
        var session = EditorSession.NewCanvas(200, 100, SKColors.White);
        var layer = session.AddText(new SKPoint(10, 10), new TextStyle { Text = "Hi", FontFamily = Family, Size = 40, Tracking = 3, Leading = 50, Bold = true });
        Assert.True(session.CanFill);
        session.Fill(SKColors.Blue);
        Assert.Equal(0xFF0000FFu, layer.Text!.Color);
        Assert.NotNull(layer.Text);
        Assert.Equal("Fill Text", session.History.UndoName);
        using var stream = new MemoryStream();
        ProjectFile.Write(session.Document, stream);
        stream.Position = 0;
        var loaded = ProjectFile.Read(stream).Find(layer.Id)!.Text!;
        Assert.Equal(layer.Text, loaded);
    }

    [Fact]
    public void Scaling_and_image_size_carry_the_spacing_and_box_along()
    {
        var session = EditorSession.NewCanvas(400, 400, SKColors.White);
        var layer = session.AddText(new SKPoint(10, 10), new TextStyle { Text = "Box text", FontFamily = Family, Size = 20, Tracking = 2, BoxWidth = 200, BoxHeight = 100 });
        session.SetTransform(layer, layer.Transform with { Width = 400, Height = 200 });
        Assert.Equal(40, layer.Text!.Size, 0.01);
        Assert.Equal(4, layer.Text.Tracking, 0.01);
        Assert.Equal((400d, 200d), (layer.Text.BoxWidth, layer.Text.BoxHeight));
        Assert.Equal((400, 200), (layer.Pixels!.Width, layer.Pixels.Height));
        session.ResizeImage(200, 200);
        Assert.Equal((200d, 100d), (layer.Text!.BoxWidth, layer.Text.BoxHeight));
        Assert.Equal(20, layer.Text.Size, 0.01);
    }

    [Fact]
    public void Text_layers_take_effects_too()
    {
        var session = EditorSession.NewCanvas(300, 100, SKColors.White);
        var layer = session.AddText(new SKPoint(10, 10), new TextStyle { Text = "Shadow", FontFamily = Family, Size = 40 });
        Assert.True(session.AddEffect(layer, LayerEffectKind.DropShadow));
        using var flat = session.Flatten();
        var dark = 0;
        for (var y = 0; y < flat.Height; y++) for (var x = 0; x < flat.Width; x++) if (flat.GetPixel(x, y).Red < 200) dark++;
        Assert.True(dark > 100);
    }
}
