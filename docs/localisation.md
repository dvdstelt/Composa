# Localising the interface

Composa's interface can be drawn in another language without changing what the program is. The rule
that makes that safe is one sentence: **English is the identity, the translation is only what is
read.** Every name a document stores, a setting keys, an icon matches or an agent sends stays English
and is looked up at the moment it is drawn.

## How it works

`Loc.T(name)` returns the words for an English name in the language being drawn, and the name itself
when nothing is translated. `Loc.Format(key, args…)` does the same for a sentence with placeholders,
so a translation controls word order instead of reassembling fragments.

- `Strings.resx` holds the sentences that carry placeholders. Its keys are the English names, so a
  missing translation degrades to English rather than to a broken string.
- `Strings.zh-CN.resx` holds Simplified Chinese. A language is one file: adding one needs no code.
- `Settings.Language` stores the choice — `"auto"` follows the operating system, `"en"` is English,
  or a culture code from `Loc.Available`. `App.Initialize` applies it before any window builds text.
- The SDK builds a satellite assembly per culture, and `dotnet publish` copies it beside the app, so
  no packaging change was needed: the installer already takes `*` with `recursesubdirs`.

## What stays English, and why

These are not oversights. Translating any of them would break something.

| Kept English | Because |
|---|---|
| History step names | `HistoryPanel.IconFor` picks each row's icon by matching those words, and `AdjustmentNames` / `FilterNames` are sets of them |
| `Shortcut.Id` | the key a rebound gesture is stored under, in `Settings.Shortcuts` |
| Dock section titles | `Settings.Dock` is keyed by title, and `SideDock.Section(title)` looks a section up by it |
| `BlendMode.DisplayName()` | the MCP `set_layer` tool parses an agent's `blend` argument against it; only the Layers panel translates what it draws |
| Layer, document and file names | the person's own data: `Ui.Label` deliberately does not translate, since callers hand it `layer.Name` and paths |
| MCP tool names, resource URIs, `.cmps` contents | the protocol and the file format |

So a document saved in Chinese and opened in English is the same document, and an agent keeps working
in any interface language.

## Two panels, one English word

Some English word means different things in two places, and one resource key cannot carry both
translations. `Loc.In(name, context)` looks up `name@context` first and falls back to the plain
`name`, so both readings live in one file: Camera Raw's **Light** panel is 「亮度」 while the Dither
dialog's **Light** swatch is 「浅色」. It is a separate method rather than an optional argument on
`Loc.T` so that `Loc.T` stays usable as a method group, which is how the option bars pass it to
`Ui.Combo` and `Select`.

## Where translation happens

At the border between a name and a pixel, never at the border between two pieces of state:

- `MainWindow.BuildMenu` — `Top`, `Item` and `Sub` translate the header and record the English name,
  so `SetLanguage` can re-set every header without rebuilding the menu.
- `RefreshMenuState` — Undo, Redo, Merge and the clipping toggle are rewritten on each open, so they
  translate there too.
- `Ui.Check`, `Ui.TextButton`, `Ui.IconButton`, `SliderField` — these only ever receive interface
  words, so they translate once, inside.
- `ToolHint` and the status bar — each sentence is looked up whole. A sentence built from a ternary
  translates its branches separately, so no fragment is ever pasted into a translated string.

## Testing

`LocalisationTests` pins the rules rather than the wording: an untranslated name reads as itself,
English puts everything back, an unshipped culture falls back to English, a blend mode keeps its
English name for an agent, and a placeholder format leaves no `{` behind. Each case restores English
afterwards, because the language is process-wide state and the rest of the suite asserts English
names. `The_window_builds_in_Chinese` builds the whole window in Chinese and keeps a screenshot, so a
layout that cannot hold the words is visible.

Tests run in English whatever the machine's language: `Loc.Apply` treats a host that does not persist
settings (`Settings.Persist == false`, which is what `TestApp` sets) as a program driving the editor
rather than a person, and keeps it in English.

## Adding a language

1. Copy `Strings.zh-CN.resx` to `Strings.<culture>.resx` and translate the values. Keys are English
   names and must not change.
2. Add the culture to `Loc.Available` with the name that language calls itself, and to
   `SatelliteResourceLanguages` in `Composa.App.csproj`.

Nothing else. A key nobody translated shows English, so a partially translated language is usable.
