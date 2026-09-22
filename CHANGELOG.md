# Changelog

All notable changes to Composa are recorded here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Release packaging for Linux: AppImage, .deb, .rpm and a tarball, built by GitHub Actions when a release is published.
- The application reports when a newer version is available.
- The version is shown in the About dialog and derived from the git tag.
- An icon set for all three platforms, generated from the source SVG.

### Changed

- The project is now called Composa. It was Compositor for Linux, a name that no longer fits now that Windows and macOS builds are planned.
- Projects are saved as `.cmps` rather than `.compositor`.
