# ADR-C003: Release Identity — Own Repo, Own Feed, Own Version Sequence

*(Local ADR, numbered in this repo's own `C` sequence so it can never collide with an
upstream or fork ADR — see [README.md](README.md).)*

**Date**: 2026-10-24
**Status**: ACCEPTED (supersedes the initial draft written the same day, before the
project took its own name)
**Affects**: `Service/UpdateChecker.cs` (feed URLs, tag prefixes),
`Emby.Xtream.Plugin.csproj` (`<Version>`), the GitHub repository name,
`README.md`, the local `origin` remote

---

## Context

This project descends from `firestaerter3/emby-xtream`, via
`andyj682/emby-xtream-dedupe` (the de-duplication fork). Its distinguishing feature is
now bigger than its heritage: the review gate is fed by Seerr, so a user's requests
and watchlists — not manual de-dup review — decide what reaches the library. A user
requesting a show in Seerr is a far more natural selection mechanism than triaging a
25,000-title list by hand.

`UpdateChecker` compares the running assembly version against the latest GitHub
release of a hardcoded repository, and the dashboard offers an **Update Now** button
that downloads that release's DLL asset and replaces the installed plugin with it.
The constants were inherited from `andyj682/emby-xtream-dedupe`, whose feed advertised
v1.9.0, while this repo built with `<Version>0.0.0-dev</Version>` — so the running
version reported `0.0.0.0`, compared older than every release ever published by that
repo, and the banner never went away.

## Problem

One-click self-update is dangerous when the feed and the build disagree about whose
releases they describe. Clicking **Update Now** in that state downloads `andyj682`'s
v1.9.0 DLL and replaces this project's DLL with it — silently removing every feature
this repo added (the Seerr monitor among them) and reverting behavior changes the
library on disk was written against.

Separately, the project was still presenting itself under a name (`emby-xtream-dedupe`)
that describes someone else's feature and someone else's release sequence.

## Alternatives considered

1. **Add a "disable update check" toggle.** Stops the banner but keeps the trap for
   the next person who flips it back, and adds config surface for a problem that is
   really a wrong constant. The check itself is useful — it is the delivery path for
   this project's own releases.
2. **Make the repo URL configurable.** Runtime configurability for a value that is a
   property of the *build*, not the install: a DLL built from this repo should never
   offer another repo's releases, whatever the configuration says.
3. **Keep tracking `dedupe-vX.Y.Z` numbers.** The ADR-C002-style namespacing argument
   applies per-sequence: `seerr-vX.Y.Z` and `dedupe-vX.Y.Z` must never share a number
   that means two different things. A sequence of our own also frees this project from
   the parent's release cadence entirely — `seerr-v1.10.0` stands for
   "dedupe v1.9.0's state plus Seerr selection", not for whatever dedupe publishes next.

## Decision

- **The project is `croosso/emby-xtream-seerr`.** The GitHub repository is renamed;
  GitHub redirects the old name, so existing clones and links keep working. The
  plugin's assembly name stays `Emby.Xtream.Plugin.dll` — renaming it would orphan the
  installed plugin's configuration and folder identity for every existing install,
  and buys only cosmetics (see Consequences).
- **Release tags are `seerr-vX.Y.Z`, starting at `seerr-v1.10.0`.** The +0.1 over the
  parent's v1.9.0 records "one feature past dedupe". The parent prefixes still parse
  (`seerr-` first, then `dedupe-`, then bare `v`) so historical tags remain readable.
- **The two release-feed constants point at `croosso/emby-xtream-seerr`.** Until a
  release is published there, the feed 404s and the check fails into its silent error
  path: no banner, no button.
- **The assembly version is stamped from the csproj** (`<Version>1.10.0</Version>`),
  so the running version is comparable to tags. Bump it as part of cutting a release:
  the released DLL's assembly version must equal its tag's version, or every install
  of that release (including the one that shipped it) sees "update available" forever.

## Consequences

- The banner disappears now (no releases published yet) and returns only when this
  project publishes a genuinely newer release, with the button then installing this
  project's build — the trap is gone, not hidden.
- **Merge hazard**: upstream merges touching `UpdateChecker.cs` will conflict on, or
  silently revert, the feed constants, and merges touching `.github/workflows/release.yml`
  will do the same to its `seerr-v*` tag trigger and prefix stripping. Take this repo's
  side — the conflict is the ADR asserting itself.
- Publishing a release now carries the release-notes obligations in AGENTS.md
  (reporter credits, user-facing wording) and must include an
  `Emby.Xtream.Plugin.dll` asset built from the tagged commit, or the button will
  offer the wrong thing. Per AGENTS.md, creating the GitHub release itself requires
  the user's explicit go-ahead.
- The plugin will still show as "Emby Xtream …" inside Emby's plugin catalog (the
  assembly name is its identity there). Renaming the assembly is possible later but
  is a breaking migration for existing installs — configuration path, decisions
  store, and plugin records are all keyed on it — and is deliberately not part of
  this decision.
