# Changelog

All notable changes to Composa are recorded here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Photoshop Large Document (`.psb`) files open through the same importer as `.psd`.
- Simple Photoshop text arrives as editable text: horizontal type layers keep their wording, font, size, color, alignment, tracking and leading. Vertical, sheared or unevenly scaled text still becomes pixels, and the import report says what was dropped.
- SVG files open and place as image layers, drawn by ImageMagick's SVG renderer. Opened, an SVG becomes a document at the size it declares; placed, it is drawn to fit the canvas, so a small icon still comes in sharp.
- A Photoshop file that would not fit in memory has its layers and masks cropped to the canvas instead of being refused; the import report lists every layer that was cut. A file that fits imports exactly as before.
- Text being typed previews the color picker's working color on the canvas, from the Type bar's swatch and from the foreground swatch alike. Cancel puts its own color back.
- With the Move tool, a double-click on text opens it for typing where you clicked.

### Changed

- A document's total raster now has its own budget, separate from the limit on any one layer: a quarter of the machine's memory, between 200 and 800 megapixels. One layer, canvas or export may be up to 200 megapixels (was 100). A print banner with dozens of large layers no longer fails to open against a limit meant for a single image.
- Marching ants around a detailed Magic Wand selection are drawn from a screen-resolution outline when zoomed out, so a selection with hundreds of thousands of edges no longer takes seconds per redraw.
- Clicking with the Type tool puts the first baseline at the pointer, as Photoshop does, so the letters rise from where you clicked instead of appearing a line lower.

## [1.0.0] - 2026-09-23

The first stable release, and the first for Windows.

### Added

- Windows builds: an installer that needs no administrator rights and a portable zip, for x64 and arm64. The installer adds a Start menu entry, makes Composa the program for `.cmps` projects and offers it under Open with for images without taking any over.
- HEIC, AVIF, TIFF and camera RAW open on Windows with nothing else installed: the Windows build carries ImageMagick, with its licence notices next to the executable. Linux builds keep using the distribution's ImageMagick.

### Changed

- On Windows, preferences are kept in `%APPDATA%\Composa` and crash-recovery copies in `%LOCALAPPDATA%\Composa`. Linux keeps its XDG locations unchanged.
- When ImageMagick is available but cannot read a file either, the error now gives ImageMagick's reason instead of suggesting to install it.

## [0.3.0] - 2026-09-23

Catches up with Compositor 1.2.3 to 1.2.6.

### Added

- Camera Raw Filter: Light, Color (with Auto white balance and an eyedropper on the panel's thumbnail), Effects (texture, clarity, dehaze, glow, vignette, grain), Curve, Color Mixer, Color Grading, Detail, Optics and Calibration, each group switchable off without clearing it, and a histogram of the graded layer.
- Finishing filters: Vignette in any color, which on an empty layer paints across the whole canvas; Bloom / Glow; Tonal Contrast.
- Gaussian Blur, Motion Blur and Add Noise as adjustment layers. Add Noise, as a layer and as a filter, offers a Gaussian distribution.
- The Inner Glow layer effect.
- Image > Trim… with a choice of transparent pixels or a corner's color, and which edges to trim.
- Copy and paste whole layers with nothing selected, folders and adjustments included, within a project or into another tab.
- A right-click menu on every layer row for the layer, its folder and its mask.
- Crop ratios 3:4 and 9:16, and a crop box that starts at the selection.
- Composa reports when a newer version is available, as a dismissable strip rather than a dialog. It never downloads or installs anything; the notice links to the release page. The check is one anonymous request a day, it can be turned off under Help, and builds installed from the `.deb` or `.rpm` never check at all because apt and dnf own updates for them.

### Changed

- Zoom In and Zoom Out step through fixed stops (12.5% to 1600%), anchored on the view's center.
- Duplicate Layer and Ctrl+J duplicate every selected layer as one step; several copies stack together above the topmost original and end up selected.
- Lens Correction keeps only Remove Distortion; the vignette has a filter of its own.
- Grain's Roughness adds smaller particles whose size follows Size instead of one-pixel noise.
- Project files are written as format version 3, which older builds cannot open when they hold the new adjustment layers or effect.

## [0.2.0] - 2026-09-23

The first release with downloadable packages. Composa has been buildable from source for a while; this is the first version you can simply install.

### Added

- Downloads for Linux on x86-64 and arm64, in four formats: an AppImage that runs on any distribution, a `.deb`, an `.rpm`, and a portable tarball with a per-user install script. Every release is published with a `sha256sums.txt`.
- The `.deb` and `.rpm` install a launcher, icons and the `.cmps` file type, and recommend ImageMagick rather than requiring it: it is needed only to open HEIC, AVIF, TIFF and camera RAW files.
- The version is shown in the About dialog. It is derived from the git tag, so a build can always be identified.
- An icon set covering the Linux hicolor sizes, Windows and macOS.
- AppStream metadata, so the application appears properly in GNOME Software and KDE Discover.

### Changed

- The project is now called **Composa**. It was Compositor for Linux, a name that no longer fits now that Windows and macOS builds are planned, and one that invited confusion with the macOS app it reimplements.
- Projects are saved as `.cmps` rather than `.compositor`. Existing files still open, because the reader looks at the archive manifest rather than the file extension.
- Preferences and crash-recovery files moved from `~/.config/compositor` and `~/.cache/compositor` to `~/.config/composa` and `~/.cache/composa`. Settings from before the rename are not carried over.

### Fixed

- Camera RAW and HEIC files could report a misleading error instead of saying that ImageMagick was missing. ImageMagick is now located once and asked to identify itself rather than trusted for its name.

[Unreleased]: https://github.com/dvdstelt/Composa/compare/v0.2.0...HEAD
[0.2.0]: https://github.com/dvdstelt/Composa/releases/tag/v0.2.0
