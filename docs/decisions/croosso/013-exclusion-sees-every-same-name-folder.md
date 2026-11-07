# ADR-C013: Exclusion Looks at Every Folder With the Name, and Respects Their TMDB Ids

*(Local ADR, numbered in this repo's own `C` sequence — see [README.md](README.md).)*

**Date**: 2026-11-06
**Status**: ACCEPTED
**Affects**: `Service/StrmSyncService.Cleanup.cs` (`RemoveExcludedContent`, `ExcludedItem`),
`Service/StrmSyncService.cs` (the exclusion and un-review item lists)

---

## Context

The exclusion pass, and the un-review removal pass that reuses it, finds an item's folder by
its cleaned name with any `[tmdbid=…]` / `[tvdbid=…]` suffix stripped, so a title is found
whatever metadata-id naming was in force when it was written (ADR-012). It skips a folder the
current run wrote (ADR-018) and deletes only files the plugin owns (ADR-014).

## Problem

The folder index kept **one folder per stripped name**: the first the directory listing
returned. With TMDB folder naming, two different titles that share a name have two folders,
`Title (2025) [tmdbid=A]` and `Title (2025) [tmdbid=B]`, and they strip to the same key.

- If the first folder belonged to the included title, the pass found it, saw it was written
  this run, kept it, and never looked at the excluded title's own folder. Seen on a live
  library: an excluded title's folder survived the exclusion and was left for orphan cleanup's
  grace period (ADR-C007) to remove a day later.
- If the first folder belonged to the other title and that title was **not** written this run
  (missing from the catalogue for a few hours, which ADR-C007 documents as routine with
  Dispatcharr), the pass deleted the other title's files. A test reproduces this.

Same-name pairs became common with ADR-C009, which strips quality prefixes and so makes
`4K - Title (2025)` and `Title (2025)` share a name.

## Alternatives considered

1. **Compute the excluded item's exact folder name** with the current naming settings and
   delete only that. Rejected: ADR-012 deliberately matches regardless of the naming settings
   in force when the folder was written; a folder written before a settings change would be
   missed.
2. **Skip every tagged folder when several share the name.** Safe, but it would stop
   excluding the common single-folder case under TMDB folder naming.

## Decision

- The index holds **every** folder per stripped name, in a stable order, and the pass
  considers each of them.
- A folder written this run is kept, as before.
- A folder tagged with a TMDB id that differs from the excluded item's own TMDB id is
  **skipped**: it belongs to another title that only shares the name. Whether that title is
  gone is orphan cleanup's decision, under its own guards.
- Otherwise (the ids match, the folder is untagged, or the provider gave the item no TMDB id)
  the folder is a candidate, and only owned files are deleted, as before.

Excluded and un-reviewed items now carry the provider's TMDB id (`ExcludedItem`). Series carry
none, because the series list payload has no TMDB id, so series behave as before except that
every same-name folder is considered.

## Consequences

- Excluding one of two same-name titles removes exactly its own folder in that sync.
- Another title's tagged folder is never removed by an exclusion of a different TMDB title.
- Where the item has no TMDB id, every untagged-or-tagged folder with the name that the run did
  not write is a candidate, where before only the first was. That case is a title with no TMDB
  id sharing a name with a tagged folder of a title missing from the run; the ownership check
  still applies, and it is the same name-only matching ADR-012 chose.
