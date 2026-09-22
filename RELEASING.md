# Releasing Composa

Cutting a release is drafting it on GitHub. The version is derived from the tag by MinVer, so no version number is edited in a file, and the workflow builds and attaches every artifact.

## Steps

1. Move the `## [Unreleased]` entries in `CHANGELOG.md` under a new `## [x.y.z]` heading with today's date, and leave a fresh empty `## [Unreleased]` above it. Commit that on a branch and merge it.
2. On GitHub, go to Releases and draft a new release.
3. Create a new tag in the form `vX.Y.Z` (for example `v0.2.0`) targeting `main`. GitHub creates the tag when the release is published, so nothing needs to be pushed from a terminal.
4. Paste the changelog section for this version into the release body.
5. Publish. Publishing marks a tag containing `-` (for example `v0.3.0-beta.1`) as a pre-release.
6. Wait for the **Release** workflow. It builds every artifact and attaches them to the release that triggered it.
7. Check the release page: every expected artifact is present, and `sha256sums.txt` lists them all.

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
