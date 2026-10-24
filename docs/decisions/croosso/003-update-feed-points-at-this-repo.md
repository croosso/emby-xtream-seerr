# ADR-C003: The Update Feed Points at This Repo's Own Releases

*(Local ADR, numbered in this repo's own `C` sequence so it can never collide with an
upstream or fork ADR — see [README.md](README.md).)*

**Date**: 2026-10-24
**Status**: ACCEPTED
**Affects**: `Service/UpdateChecker.cs` (two URL constants),
`Emby.Xtream.Plugin.csproj` (`<Version>`)

---

## Context

`UpdateChecker` compares the running assembly version against the latest GitHub
release of a hardcoded repository, and the dashboard offers an **Update Now** button
that downloads that release's DLL asset and replaces the installed plugin with it.

The constants were inherited from `andyj682/emby-xtream-dedupe`, whose feed advertised
v1.9.0. Meanwhile this fork builds locally with `<Version>0.0.0-dev</Version>`, so the
running version reported as `0.0.0.0` compares older than every release ever published
by that repo — a permanently visible, permanently wrong update banner.

## Problem

One-click self-update is dangerous when the feed and the build disagree about whose
releases they describe. Clicking **Update Now** in this fork's current state downloads
`andyj682`'s v1.9.0 DLL and replaces this fork's DLL with it — silently removing every
feature this repo has added (the Seerr monitor among them) and reverting behavior
changes the library on disk was written against. The banner cannot be dismissed
permanently: it reappears on every dashboard load, inviting exactly that click.

## Alternatives considered

1. **Add a "disable update check" toggle.** Stops the banner but keeps the trap for the
   next person who flips it back, and adds config surface for a problem that is really
   a wrong constant. The check itself is useful — it is the delivery path for this
   repo's own releases.
2. **Make the repo URL configurable.** Runtime configurability for a value that is a
   property of the *build*, not the install: a DLL built from this repo should never
   offer another repo's releases, whatever the configuration says.

## Decision

- The two release-feed constants point at **`croosso/emby-xtream-dedupe`** — this
  repo's public origin. Until a release is published there, the feed 404s and the
  check fails into its silent error path: no banner, no button.
- The version comparison stays against this fork's own `dedupe-vX.Y.Z` tag sequence,
  which `CompareVersions` already unprefixedes. The fork deliberately tracks
  upstream's version numbers behind the `dedupe-` prefix — the same
  namespacing rule as the ADR sequences, for the same reason: a shared number that
  means two different things is a silent collision.
- The assembly version is stamped from the csproj (`<Version>1.9.0</Version>`), so
  the running version is comparable to tags. Bump it as part of cutting a release:
  the released DLL's assembly version must equal its tag's version, or every install
  of that release (including the one that shipped it) sees "update available" forever.

## Consequences

- The banner disappears now (no releases published yet) and returns only when this
  repo publishes a genuinely newer release, with the button then installing this
  repo's build — the trap is gone, not hidden.
- **Merge hazard**: upstream merges touching `UpdateChecker.cs` will conflict on, or
  silently revert, these two constants. Take this repo's side — the conflict is the
  ADR asserting itself.
- Publishing a release of this fork now carries the release-notes obligations in
  AGENTS.md (reporter credits, user-facing wording) and must include an
  `Emby.Xtream.Plugin.dll` asset built from the tagged commit, or the button will
  offer the wrong thing.
