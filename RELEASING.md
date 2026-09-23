# Releasing Composa

Cutting a release is drafting it on GitHub. The version is derived from the tag by MinVer, so no version number is edited in a file, and the workflow builds and attaches every artifact.

## Steps

1. Move the `## [Unreleased]` entries in `CHANGELOG.md` under a new `## [x.y.z]` heading with today's date, and leave a fresh empty `## [Unreleased]` above it. Commit that on a branch and merge it.
2. On GitHub, go to Releases and draft a new release. Create a new tag in the form `vX.Y.Z` (for example `v0.2.0`) targeting `main`, and paste in the changelog section for this version. **Save it as a draft; do not publish yet.**
3. Run the **Release** workflow manually, giving it that tag as `draft_tag`. It builds every artifact and attaches them to the draft.
4. Check the draft: every expected artifact is present, and `sha256sums.txt` lists them all. Download one and run it.
5. Publish the draft. GitHub creates the tag at that moment, so nothing is ever pushed from a terminal. A tag containing `-` (for example `v0.3.0-beta.1`) is marked as a pre-release.

Publishing first and letting the workflow fill the release in afterwards also works, and is one step shorter. The reason the draft comes first is the few minutes in between: a published release with no downloads on it is worse than no release, because the update check points people at whatever the latest release is.

### Why the draft needs its tag passed in

GitHub does not create a git tag until a release is published, so while it is still a draft there is no tag for MinVer to read and the artifacts would come out as `0.0.0-alpha.0.N`. Passing `draft_tag` sets MinVer's own version override for that build, so the artifacts carry the version the release is about to have. Publishing the draft then creates that same tag on the same commit, and the two agree.

## Versioning

Composa follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html): breaking is major, a feature is minor, a fix is patch. Pre-release suffixes sort alphabetically, so use `-alpha`, `-beta` and then `-preview` as the last stage before a stable release.

MinVer derives the version from the nearest tag. A commit that is not tagged builds as a pre-release of the next patch, so a development build reports something like `0.2.1-alpha.0.7` and is never mistaken for a release.

## Verifying a build locally

```bash
scripts/publish.sh
```

That produces a self-contained build under `dist/`. To confirm what version a build thinks it is, open About in the Help menu.

## If the release workflow fails

The release stays published with whatever artifacts it managed to attach, which is worse than no release at all, because the update check will start pointing people at it. Either delete the release and its tag and start again, or fix the workflow and re-run the failed jobs so the missing artifacts are attached.
