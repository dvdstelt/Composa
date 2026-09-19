# Compositor for Linux

A layer-based image editor for compositing and retouching, with Photoshop-style tools and shortcuts. It is a native Linux implementation of [Compositor](https://github.com/robbietilton/Compositor), Robbie Tilton's free and open-source macOS app.

The macOS app is written in Swift on top of AppKit, SwiftUI, CoreImage, Metal and Vision, so it cannot be compiled for Linux. This project rebuilds the same editor from scratch in C# with .NET 10, [Avalonia](https://avaloniaui.net/) and [SkiaSharp](https://github.com/mono/SkiaSharp). It runs on X11 and Wayland (through XWayland).

## Features

### Layers
- Layers and folders with 16 blend modes and opacity
- Layer masks on layers, folders and adjustment layers: paint, fill, gradient, invert, blur, apply, disable
- Clipping masks (Alt-click a layer, or Ctrl+Alt+G)
- Adjustment layers: Hue/Saturation, Levels, Curves, Exposure, Gradient Map, Grain, Brightness/Contrast, Invert
- Merge Down, Merge Layers, Merge Group (Ctrl+E) and Flatten Image
- Duplicate, rename inline, reorder and nest by drag and drop; Alt-drag to duplicate
- Swipe down the eye column to show or hide many layers; Alt-click an eye to solo a layer

### Transform
- Non-destructive move, scale, rotate and flip: images keep their full resolution however small you make them
- Free distort by Ctrl-dragging a corner
- Transform several layers, or a whole folder, together
- Snapping to canvas and layer edges and centers, with guides
- Exact values for position, size and angle; arrow keys nudge (Shift for 10 px)
- Live shape layers (rectangle, rounded rectangle, ellipse) that are redrawn sharp when scaled

### Selections
- Rectangle and Ellipse Marquee, Freehand and Polygonal Lasso, Magic Wand
- Add, subtract and intersect; move the outline; move or duplicate the pixels inside
- Select All, Inverse, Expand, Contract, Feather; load a layer's pixels or mask as a selection
- Content-Aware Fill

### Painting and retouching
- Brush and Eraser with size, hardness and stroke-level opacity; Shift-click for straight lines
- Spot Healing Brush (content-aware)
- Clone Stamp, aligned or not, sampling one layer or all of them
- Smear tool: Blur, Smudge, Dodge and Burn
- Gradient tool (linear or radial, to background or to transparent)
- Eyedropper and a full color picker
- Every painting tool also works on masks

### Adjustments and filters
- Levels (with Auto and a histogram), Curves, Hue/Saturation (master and six color ranges, Colorize), Exposure, Gradient Map, Grain, Brightness/Contrast, Invert
- Gaussian Blur and Motion Blur that spread past a layer's edges, Sharpen, Add Noise, Lens Correction, Remove Background
- Live previews, limited to the selection when there is one

### Canvas and files
- Multiple projects in tabs
- Crop with snapping, Shift to keep proportions, Alt for symmetric cropping; Trim
- Canvas Size and Image Size
- Smooth downsampling when zoomed out, crisp pixels and a pixel grid when zoomed in
- Open PNG, JPEG, WebP, BMP and GIF; drop files onto the window; paste images from other apps
- Export PNG, JPEG and WebP; Copy Merged
- Undo history limited by memory, not by a fixed step count

## Differences from the macOS app

- Projects are saved as `.compositor` files: a zip archive with a JSON manifest and one PNG per layer and mask. The macOS `.comp` package format is not read or written yet.
- Remove Background clears a plain backdrop connected to the image's edges. The macOS app uses Apple's Vision subject detection, which has no Linux equivalent.
- HEIC and TIFF cannot be opened, because Skia does not decode them. Convert them first.
- The Liquify mode of the Smear tool is not implemented; Dodge and Burn are offered in its place.
- There is no auto-update.

## Requirements

- Linux x64 (arm64 should work by publishing with `scripts/publish.sh linux-arm64`)
- To build: the .NET 10 SDK
- Fontconfig and the usual X11 libraries, present on any desktop distribution

## Build and run

```bash
dotnet run --project src/Compositor.App
```

Open files straight from the command line:

```bash
dotnet run --project src/Compositor.App -- photo.jpg project.compositor
```

## Install

Build a self-contained release (no .NET needed on the target machine) and install it for your user, with a launcher, icon and the `.compositor` file type:

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

The core library is tested directly through `EditorSession`. The app tests run Avalonia headless with real Skia rendering and write screenshots to `artifacts/screenshots/`.

## Shortcuts

| Keys | Action |
| --- | --- |
| V M L W C | Move, Marquee, Lasso, Magic Wand, Crop (M and L again switch variants) |
| B E J S R | Brush, Eraser, Spot Healing, Clone Stamp, Smear |
| G U I H Z | Gradient, Shape, Eyedropper, Hand, Zoom |
| Space, Ctrl+wheel | Pan, zoom at the cursor |
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

## License

MIT, see [LICENSE](LICENSE). Compositor for macOS is Copyright (c) 2026 Wonder Assembly LLC, also MIT.
