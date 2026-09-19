# Agent notes for Compositor for Linux

## What this is

A Linux implementation of the macOS image editor [Compositor](https://github.com/robbietilton/Compositor). The upstream app is Swift on AppKit, SwiftUI, CoreImage, Metal and Vision, none of which exist on Linux, so nothing is shared at the source level: this repository reimplements the same feature set in C# on .NET 10, Avalonia 12 and SkiaSharp 3.

## Layout

- `src/Compositor.Core`: everything that is not UI. Document model, compositor, selections, brush engine, filters, adjustments, file IO and `EditorSession` (every editing command). It must never reference Avalonia.
- `src/Compositor.App`: the Avalonia desktop app. UI is built in C# (no XAML). `CanvasView` owns viewport, overlays and pointer tools; `LayersPanel` the layer stack; `MainWindow.*` the menus, shortcuts, tabs, options bar and file handling.
- `tests/Compositor.Core.Tests`: xUnit tests that drive `EditorSession` directly.
- `tests/Compositor.App.Tests`: Avalonia headless tests with real Skia rendering. They save screenshots to `artifacts/screenshots/` (git-ignored), which is the way to check UI changes visually without a display.

## Rules that keep the editor correct

- Bitmaps are immutable once they are part of a committed document. An edit copies the bitmap, changes the copy and swaps it into the layer. Undo snapshots (`Document.Clone()`) share bitmaps with the live document, so mutating or disposing a committed bitmap corrupts history and can crash in native code. Only dispose bitmaps you created during the same uncommitted edit.
- Color bitmaps are `Rgba8888` premultiplied; masks and selections are `Alpha8`. Create them through `Pixels.NewColor` and `Pixels.NewMask`.
- After changing a bitmap's pixels in place (only allowed during an uncommitted edit) call `Pixels.Invalidate(bitmap)` so cached `SKImage` wrappers are dropped.
- Every user-visible change goes through `EditorSession.Begin`/`Commit`/`Cancel` (or `Apply`) so it is undoable, then raises `Invalidate`/`InvalidateAll` and `LayersChanged` as needed.
- `DocumentRenderer.Render` splits large areas into bands rendered in parallel. Anything added to the render path must be a per-pixel operation that does not depend on neighbouring bands.
- Never invalidate a visual or raise layout-affecting events from inside `CanvasView.Render`.
- Layer lists are stored bottom to top; the Layers panel shows them reversed.

## Commands

```bash
dotnet build
dotnet test
dotnet run --project src/Compositor.App
scripts/publish.sh
```
