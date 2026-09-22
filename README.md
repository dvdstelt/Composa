# Composa

A layer-based image editor for compositing and retouching, with Photoshop-style tools and shortcuts. It is a from-scratch implementation of [Compositor](https://github.com/robbietilton/Compositor), Robbie Tilton's free and open-source macOS app.

The macOS app is written in Swift on top of AppKit, SwiftUI, CoreImage, Metal and Vision, so it cannot be compiled for anything else. Composa rebuilds the same editor from scratch in C# with .NET 10, [Avalonia](https://avaloniaui.net/) and [SkiaSharp](https://github.com/mono/SkiaSharp), which run on Linux, Windows and macOS alike.

Today Composa is built and tested on Linux only, on X11 and Wayland (through XWayland). Windows and macOS builds are planned; nothing in the code is Linux-specific by design, but neither has been run yet.

> [!IMPORTANT]
> This entire codebase was created by Claude Code Fable 5.1 in a single prompt. I ran it once and played around with it, but haven't looked at the code at all.
> The prompt was `Create a version based on the code of Compositor. I'm going to bed. Don't ask questions and don't finish until you're done.`
> There's still a lot of work to make this work on Linux, Windows and Mac. But I always really liked Photoshop and hopefully this can become my new tool in the future.

## Features

### Layers
- Layers and folders with 24 blend modes, grouped in the menu as Photoshop groups them, and opacity
- Layer effects: Stroke (outside or inside), Drop Shadow, Outer Glow, Color Overlay and Inner Shadow, each switchable, editable with a live preview and copied between layers by Alt-dragging
- Layer masks on layers, folders and adjustment layers: paint, fill, gradient, invert, blur, apply, disable
- Clipping masks (Alt-click a layer, or Ctrl+Alt+G)
- Adjustment layers: Hue/Saturation, Levels, Curves, Exposure, Gradient Map, Grain, Brightness/Contrast, Black & White, Color Balance, Invert
- Merge Down, Merge Layers, Merge Group (Ctrl+E) and Flatten Image
- Duplicate, rename inline, reorder and nest by drag and drop; Alt-drag to duplicate
- Swipe down the eye column to show or hide many layers; Alt-click an eye to solo a layer

### Transform
- Non-destructive move, scale, rotate and flip: images keep their full resolution however small you make them
- Free distort by Ctrl-dragging a corner; the handles follow the corners, which keep distorting once the layer is distorted, and a corner dragged past the opposite edge folds the layer over itself
- Ctrl-drag moves the current layer with any tool active, as Photoshop's temporary Move tool does
- Auto Select picks the layer under the pointer, including one stacked on a selected background that covers the canvas; turn it off to drag the current layer from anywhere (Ctrl-click still picks)
- Transform several layers, or a whole folder, together
- Snapping to canvas and layer edges and centers, with guides
- Exact values for position, size and angle; arrow keys nudge (Shift for 10 px)
- Live shape layers (rectangle, rounded rectangle, ellipse, line) that are redrawn sharp when scaled
- Rulers, guides dragged out of them, a layout grid, and snapping to guides, grid, layers and the canvas (View > Snap To)

### Selections
- Rectangle and Ellipse Marquee, Freehand and Polygonal Lasso, Magic tool with Wand (similar colors) and Object (the thing under the click) modes
- Add, subtract and intersect; move the outline; move or duplicate the pixels inside
- Select All, Inverse, Subject, Expand, Contract, Feather (also as buttons with amounts in the tool bar); load a layer's pixels or mask as a selection
- Content-Aware Fill, which can also extend an image past its edges

### Painting and retouching
- Brush and Eraser with size, hardness, stroke-level opacity and Smoothing, which trails the pointer so a shaky hand still draws a smooth line; Shift-click for straight lines
- Spot Healing Brush (content-aware)
- Clone Stamp, aligned or not, sampling one layer or all of them
- Smear tool: Liquify (push), Blur, Smudge, Dodge and Burn
- Gradient tool (linear or radial, to background or to transparent) that stays adjustable: drag either end, Enter applies
- Type tool: type straight onto the canvas as point text or in a dragged-out paragraph box, with font, size, style, color, alignment, tracking and leading in the tool bar; text stays editable and sharp when scaled
- Eyedropper and a full color picker
- Pen pressure varies the brush size on graphics tablets
- Every painting tool also works on masks

### Adjustments and filters
- Levels (with Auto and a histogram), Curves, Hue/Saturation (master and six color ranges, Colorize), Exposure, Gradient Map, Grain, Brightness/Contrast, Invert
- Black & White with Photoshop's six color weights, so reds and greens stay apart instead of flattening into one gray, and an optional tint for sepia or cyanotype; Color Balance for shadows, midtones and highlights separately, with Preserve Luminosity
- Gaussian Blur and Motion Blur that spread past a layer's edges, Sharpen, Add Noise, Lens Correction, Remove Background
- Live previews, limited to the selection when there is one

### Canvas and files
- Multiple projects in tabs
- Crop with snapping, Shift to keep proportions, Alt for symmetric cropping; Trim
- Canvas Size, Image Size, and quarter-turn rotation of the canvas or of single layers
- Smooth downsampling when zoomed out, crisp pixels and a pixel grid when zoomed in
- Open PNG, JPEG, WebP, BMP and GIF (and HEIC, AVIF and TIFF through ImageMagick when it is installed); drop files onto the window; paste images from other apps
- Open camera RAW files (Canon, Nikon, Sony, Fujifilm, DNG and more) through ImageMagick when it is installed: a develop step with exposure, temperature and tint and a live preview comes first, working on a 16-bit decode, so you choose what to keep before the image becomes an 8-bit layer
- Open Photoshop files: layers, folders, masks, clipping, opacity, blend modes, solid fill shapes and adjustments come in editable, and a report lists everything that has to be converted before anything is applied; dropped onto an open document, a Photoshop file arrives inside a folder
- Export PNG, JPEG (with a live preview of the compression and the file size) and WebP; Copy Merged
- Undo history limited by memory, not by a fixed step count
- Tool settings stick between launches: Auto Select, the transform controls, the pixel grid, rulers, guides, the grid, Snap and the Snap To options keep what you last set them to
- Autosave for crash recovery: unsaved work is copied to `~/.cache/composa/recovery` every two minutes and offered back after an unclean exit

## Differences from the macOS app

- Projects are saved as `.cmps` files: a zip archive with a JSON manifest and one PNG per layer and mask. Projects from the macOS app (`.comp` packages, which are plain folders on Linux) can be opened with File > Open macOS Project Folder or by dropping the folder on the window; they are not written back in that format. Per-range hue bands, separately placed masks and Liquify strokes have no equivalent here and are simplified on import.
- Remove Background, Select > Subject and the Magic tool's Object mode work from the plain backdrop connected to the image's edges: the subject is everything else, and an object is the connected piece of it under the click. The macOS app uses Apple's Vision subject detection, which has no Linux equivalent, so busy backgrounds defeat these here.
- HEIC, AVIF and TIFF open only when ImageMagick (`magick` or `convert`) is installed, because Skia does not decode them itself.
- A mask always moves and scales with its layer; it cannot be unlinked and transformed on its own.
- Layers cannot be dragged between tabs. Copy and paste (Ctrl+C, Ctrl+V) carries pixels across, keeping their position.
- Hue/Saturation offers the master and six fixed color ranges; the ranges' widths are not adjustable.
- Point text grows from the edge its alignment reads from (right-aligned text grows leftward); the macOS app keeps the top-left corner.
- Layer effects are drawn on the CPU from a cached image; while a brush stroke is in progress they follow the pixels the stroke started from and catch up when it ends.
- Photoshop files are opened, never written. Text, smart objects and paths other than solid rectangles and ellipses arrive as pixels, layer effects are dropped, and adjustments other than Levels, Curves, Hue/Saturation, Brightness/Contrast, Exposure, Black & White, Color Balance and Invert are skipped; every such change is listed before the import goes ahead. Only 8-bit RGB `.psd` files open (no PSB, CMYK or 16-bit).
- Camera RAW files open only through ImageMagick's LibRaw delegate. The develop step applies exposure and white balance to the 16-bit decoded frame rather than to the sensor data, as Apple's RAW pipeline does on macOS, so its temperature and tint are relative to the camera's reading and there is no tone Boost control.
- There is no auto-update.

Beyond the macOS app, this version adds Ctrl-drag to move a layer with any tool, Bold and Italic text, Brightness/Contrast, Sharpen, Dodge and Burn, WebP export, canvas and layer rotation, pen pressure, and autosave with crash recovery. Its Photoshop import also opens flattened files and zip-compressed layers, keeps solid color fill layers live, and maps Brightness/Contrast, Exposure, Invert, Black & White and Color Balance adjustments.

## Requirements

- Linux x64 (arm64 should work by publishing with `scripts/publish.sh linux-arm64`). Windows and macOS are not built yet.
- To build: the .NET 10 SDK
- Fontconfig and the usual X11 libraries, present on any desktop distribution

## Build and run

```bash
dotnet run --project src/Composa.App
```

Open files straight from the command line:

```bash
dotnet run --project src/Composa.App -- photo.jpg project.cmps
```

## Install

Build a self-contained release (no .NET needed on the target machine) and install it for your user, with a launcher, icon and the `.cmps` file type:

```bash
scripts/publish.sh
```

```bash
scripts/install.sh
```

## Tests

```bash
dotnet test
```

- `tests/Composa.Core.Tests` drives the editor through `EditorSession`: compositing, selections, every brush mode, healing, filters, canvas operations, project files, the macOS importer, regressions found in review, and a fuzz test that runs thousands of random edits, undos and redos while checking the document stays consistent.
- `tests/Composa.App.Tests` runs the real window with Avalonia's headless platform and Skia rendering. Every tool, the layers panel, typing on the canvas, guides and the dialogs are driven with pointer, key and text events, and screenshots of the window and each dialog are written to `artifacts/screenshots/`, which is the way to review UI changes without a display.

## Shortcuts

| Keys | Action |
| --- | --- |
| V M L W C | Move, Marquee, Lasso, Magic, Crop (M and L again switch variants) |
| B E J S R | Brush, Eraser, Spot Healing, Clone Stamp, Smear |
| G U T I H Z | Gradient, Shape, Type, Eyedropper, Hand, Zoom |
| Tab | Switch the current tool's mode (Wand/Object, Paint/Erase, the shape, and so on) |
| Ctrl+drag | Move the current layer with any tool |
| Space, middle button, Ctrl+wheel | Pan, zoom at the cursor |
| Ctrl+0, Ctrl+1 | Fit canvas, actual pixels |
| [ ] and { } | Brush size and hardness |
| 1 to 0 | Brush opacity, or layer opacity with the Move tool |
| X, D | Swap colors, reset to black and white |
| Alt+Backspace, Ctrl+Backspace | Fill with foreground, background |
| Shift+Backspace | Content-Aware Fill |
| Ctrl+A, Ctrl+D, Ctrl+Shift+I | Select all, deselect, inverse |
| Ctrl+J | Duplicate layer, or layer via copy with a selection |
| Ctrl+G, Ctrl+E, Ctrl+Alt+G | Group, merge, clipping mask |
| Ctrl+L, Ctrl+M, Ctrl+U, Ctrl+I | Levels, Curves, Hue/Saturation, Invert |
| \ | Switch between painting the layer and its mask |
| Ctrl+R, Ctrl+', Ctrl+; | Rulers, grid, guides |
| Ctrl+Shift+;, Ctrl+Alt+; | Snap, lock guides |
| Ctrl+Alt+A | Select Subject |
| While typing: Ctrl+Enter, Escape, Alt+arrows | Finish, cancel, tracking and leading |

Every shortcut can be changed in Help > Keyboard Shortcuts (F1).

## License

MIT, see [LICENSE](LICENSE). Compositor for macOS is Copyright (c) 2026 Wonder Assembly LLC, also MIT.
