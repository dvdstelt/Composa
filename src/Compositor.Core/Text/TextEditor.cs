using Compositor.Model;
using SkiaSharp;

namespace Compositor.Text;

/// <summary>
/// The state of text being typed on the canvas: the style (with the content), the caret, the selection anchor and a
/// local undo history for typing. It knows nothing about layers; the session re-renders the layer after each change.
/// </summary>
public sealed class TextEditor
{
    private readonly List<(TextStyle Style, int Caret, int Anchor)> undo = [];
    private readonly List<(TextStyle Style, int Caret, int Anchor)> redo = [];
    private TextLayout? layout;
    private bool lastWasTyping;

    public TextEditor(TextStyle style)
    {
        Style = style.Clamped();
        Caret = Anchor = Style.Text.Length;
    }

    public TextStyle Style { get; private set; }
    public string Text => Style.Text;
    public int Caret { get; private set; }
    public int Anchor { get; private set; }
    public bool HasSelection => Caret != Anchor;
    public int SelectionStart => Math.Min(Caret, Anchor);
    public int SelectionEnd => Math.Max(Caret, Anchor);
    public string SelectedText => Text.Substring(SelectionStart, SelectionEnd - SelectionStart);
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;

    /// <summary>The layout of the current style, rebuilt only when the style changes.</summary>
    public TextLayout Layout => layout ??= new TextLayout(Style);

    /// <summary>Raised after every change to the style or the content.</summary>
    public event Action? Changed;

    // ---- Editing --------------------------------------------------------------------------------------------------

    /// <summary>Types text over the selection. Consecutive typing undoes as one step.</summary>
    public void Insert(string text)
    {
        text = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\t", "    ");
        if (text.Length == 0 && !HasSelection) return;
        Record(typing: text.Length <= 2 && !text.Contains('\n'));
        int start = SelectionStart, end = SelectionEnd;
        var content = Text.Remove(start, end - start).Insert(start, text);
        if (content.Length > TextStyle.MaxLength) return;
        Apply(Style with { Text = content }, start + text.Length);
    }

    public void Backspace(bool word = false)
    {
        if (HasSelection) { DeleteSelection(); return; }
        if (Caret == 0) return;
        var to = word ? Layout.WordStart(Caret) : Caret - Step(Caret, -1);
        Record(typing: !word);
        Apply(Style with { Text = Text.Remove(to, Caret - to) }, to);
    }

    public void Delete(bool word = false)
    {
        if (HasSelection) { DeleteSelection(); return; }
        if (Caret >= Text.Length) return;
        var to = word ? Layout.WordEnd(Caret) : Caret + Step(Caret, 1);
        Record(typing: false);
        Apply(Style with { Text = Text.Remove(Caret, to - Caret) }, Caret);
    }

    private void DeleteSelection()
    {
        Record(typing: false);
        var start = SelectionStart;
        Apply(Style with { Text = Text.Remove(start, SelectionEnd - start) }, start);
    }

    /// <summary>A surrogate pair is one character to the caret.</summary>
    private int Step(int index, int direction)
    {
        if (direction < 0) return index >= 2 && char.IsLowSurrogate(Text[index - 1]) && char.IsHighSurrogate(Text[index - 2]) ? 2 : 1;
        return index + 1 < Text.Length && char.IsHighSurrogate(Text[index]) && char.IsLowSurrogate(Text[index + 1]) ? 2 : 1;
    }

    /// <summary>Changes anything but the content (font, size, color, spacing, box). Undoable within the editor.</summary>
    public void ChangeStyle(Func<TextStyle, TextStyle> change)
    {
        var next = change(Style) with { Text = Text };
        next = next.Clamped();
        if (next == Style) return;
        Record(typing: false);
        Apply(next, Caret, Anchor);
    }

    // ---- Caret ----------------------------------------------------------------------------------------------------

    public void MoveTo(int index, bool select)
    {
        Caret = Math.Clamp(index, 0, Text.Length);
        if (!select) Anchor = Caret;
        lastWasTyping = false;
        Changed?.Invoke();
    }

    public void MoveHorizontal(int direction, bool select, bool word = false)
    {
        if (!select && HasSelection && !word) { MoveTo(direction < 0 ? SelectionStart : SelectionEnd, false); return; }
        var target = word
            ? (direction < 0 ? Layout.WordStart(Caret) : Layout.WordEnd(Caret))
            : Caret + direction * Step(Caret, direction);
        MoveTo(target, select);
    }

    public void MoveVertical(int direction, bool select) => MoveTo(Layout.IndexOnAdjacentLine(Caret, direction), select);

    public void MoveToLineEdge(bool end, bool select)
    {
        var line = Layout.Lines[Layout.LineOf(Caret)];
        MoveTo(end ? line.End : line.Start, select);
    }

    public void MoveToDocumentEdge(bool end, bool select) => MoveTo(end ? Text.Length : 0, select);

    public void SelectAll()
    {
        Anchor = 0;
        Caret = Text.Length;
        lastWasTyping = false;
        Changed?.Invoke();
    }

    public void SelectWordAt(int index)
    {
        Anchor = Layout.WordStart(Math.Min(index + 1, Text.Length));
        Caret = Layout.WordEnd(Anchor);
        lastWasTyping = false;
        Changed?.Invoke();
    }

    /// <summary>Puts the caret at a point in layout pixels; with <paramref name="select"/> the anchor stays.</summary>
    public void ClickAt(SKPoint point, bool select) => MoveTo(Layout.IndexAt(point), select);

    // ---- Undo -----------------------------------------------------------------------------------------------------

    private void Record(bool typing)
    {
        if (typing && lastWasTyping && undo.Count > 0) return;
        undo.Add((Style, Caret, Anchor));
        if (undo.Count > 200) undo.RemoveAt(0);
        redo.Clear();
        lastWasTyping = typing;
    }

    private void Apply(TextStyle style, int caret, int? anchor = null)
    {
        Style = style;
        layout = null;
        Caret = Math.Clamp(caret, 0, Text.Length);
        Anchor = Math.Clamp(anchor ?? Caret, 0, Text.Length);
        Changed?.Invoke();
    }

    public bool Undo()
    {
        if (undo.Count == 0) return false;
        redo.Add((Style, Caret, Anchor));
        var (style, caret, anchor) = undo[^1];
        undo.RemoveAt(undo.Count - 1);
        lastWasTyping = false;
        Apply(style, caret, anchor);
        return true;
    }

    public bool Redo()
    {
        if (redo.Count == 0) return false;
        undo.Add((Style, Caret, Anchor));
        var (style, caret, anchor) = redo[^1];
        redo.RemoveAt(redo.Count - 1);
        lastWasTyping = false;
        Apply(style, caret, anchor);
        return true;
    }
}
