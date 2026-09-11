---
name: release-mod
description: Publish a GitHub release for one of the mods in this repo (SanctuaryHud, IdleEngineers, EcoManager, BuildHotkeys, CameraUtilities, LadderReporter, ReplayManager, ModManager, ModLoader). Use when asked to release, publish, ship, or cut a version of a mod, or to build the release zips.
---

# Releasing a mod

Each release is a GitHub release carrying **two zips**, tagged `<Mod>-<version>`
and titled `<Display Name> <version>`. `tools/pack-release.ps1` does everything
mechanical; this file is the order to do it in and the decisions it cannot make
for you.

## Before anything

**Check what is actually being released.** `git log <last tag>..HEAD -- <Mod>/`
plus `shared/` — the shared code is compiled into every mod, so a change there
reaches a mod the next time it is rebuilt.

**Only release mods whose behaviour changed.** A `shared/HudCore.cs` edit does
*not* oblige a re-release of every mod: it is source, not a shipped artifact,
so already-published DLLs are frozen against the HudCore they were built with
and cannot be affected. Re-release a mod when *its* behaviour changed.

## 1. Version

`[BepInPlugin(guid, display, version)]` in the mod's main `.cs` is the **single
source of truth** — the packer reads the version and display name from it, so a
release cannot disagree with what the game reports. Bump it there and nowhere
else.

Roughly: patch for a fix, minor for a feature. These are 0.x mods; nothing has
gone 1.0 except ModLoader.

## 2. README

Update that mod's row in the README table so `Download` points at the new tag:

```
| [EcoManager](EcoManager/) | [**0.3.0**](https://github.com/Remmyboy/sanctuary-mods/releases/tag/EcoManager-0.3.0) | ... |
```

Adjust the "what it does" cell if the release changed what the mod is.

## 3. Land it on main first

The release is tagged at exactly the commit it was built from, and `-Publish`
refuses unless that commit is `origin/main` with nothing uncommitted in tracked
files, so the commit must be there first:

```bash
git push origin HEAD:main
```

If `main` has moved, merge it in and re-check the build — do not force. When
the merge touches `shared/`, confirm your side only *adds*
(`git diff main -- shared/HudCore.cs` should show no `-` lines) rather than
reverting someone else's work.

## 4. Write the body

One plain-text file describing the mod and what is new. It goes into the
`README.txt` of both zips **verbatim**, so:

- no markdown — no `**bold**`, no links
- ASCII hyphens, not en dashes
- lead with what the mod does, then a `New in <version>:` paragraph
- state the cost or the catch honestly; these READMEs are the only docs a
  downloader reads

## 5. Build and check

```bash
pwsh -NoProfile -File tools/pack-release.ps1 -Mod EcoManager -Body body.txt
```

Without `-Publish` it only builds, into `release/` (gitignored). The build is
always of the committed `HEAD`, checked out clean into a temporary git worktree,
so uncommitted edits are not in it (the script says so when there are any), and
each DLL's embedded source revision must equal `HEAD`. It fails loudly if the
DLL is missing, if a zip lacks an expected path, if a revision doesn't match, or
if any file from your own `BepInEx/config` leaked in. Look at the reported sizes: a Standalone that is
not several hundred KB means the BepInEx tree did not come through.

## 6. Publish

```bash
pwsh -NoProfile -File tools/pack-release.ps1 -Mod EcoManager -Body body.txt -Publish
```

Refuses if the tag exists locally, on origin or as a release — bump the version
rather than deleting a tag someone may have downloaded. Otherwise it creates an
annotated tag at the built commit, pushes it, and creates the release from that
existing tag (`--verify-tag`), failing if `gh` fails or the release doesn't list
both zips. If `gh release create` fails after the tag is pushed, the message says
how to finish from the same tag. Pass `-Notes notes.md` for a richer markdown release body;
otherwise it uses the plain body plus the standard zip bullets and the
client-side-only footer.

Publishing is public and awkward to undo. Have the user confirm the mod and
version unless they have already said to go ahead.

## 7. Afterwards

- `gh release view <tag>` — two assets present
- every README download link still resolves
- if the game is running, the packer built to a temp folder and did not disturb
  it; a normal `dotnet build` will redeploy the Debug DLLs

## Things that have caught people out

- **BepInEx.cfg must ship with `HideManagerGameObject = true`.** Sanctuary
  destroys foreign root GameObjects after start-up, so without it every plugin
  loads and then never updates. The vendored `tools/BepInEx.cfg` has it; a
  BepInEx someone installs themselves defaults it to false.
- **Never copy `BepInEx/config/` wholesale from the game folder.** It holds your
  own per-mod settings. The packer uses an allowlist and asserts nothing else
  got in.
- **`MapLocalFiles` has no release** and is built from source; leave its
  Download cell as `—`.
- **A stale `[BepInPlugin]` version** has happened before: LadderReporter shipped
  0.2.3 while its attribute still read 0.2.1. Bumping the attribute first is
  what stops that.
- **A release built from a dirty tree.** ReplayManager 0.2.0's DLL embeds
  commit `42bb4b3` while its tag points at `56d8819`: it was built with
  uncommitted changes. The clean-worktree build and the revision check are what
  make that impossible now.
