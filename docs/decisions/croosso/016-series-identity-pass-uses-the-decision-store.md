# ADR-C016: The Series Identity Pass Runs Through the Decision Store

*(Local ADR, numbered in this repo's own `C` sequence — see [README.md](README.md).)*

**Date**: 2026-11-15
**Status**: ACCEPTED
**Affects**: `Service/StrmSyncService.cs` (`ReconcileSeriesDecisionIdentity`,
`ReconcileSeriesDecisionIdentityForSync`), `Service/DecisionStore.cs` (an eighth store, file
format 3)

---

## Context

[ADR-F011](../fork/011-series-decisions-survive-id-changes.md) (andyj682's F009) gives the
series sync the movie side's identity pass: every series id holding a decision gets its TMDB id
recorded, and when such an id leaves the listing while its TMDB id is listed under another id,
the decision moves there. It was written for a tree without
[ADR-C001](001-decision-store-sidecar.md) and without un-review tombstones in the pass.

## Problem

Taken as is, the pass would not work here:

- It writes `ExcludedSeriesIds` and `ReviewedSeriesIdsJson` straight into the configuration.
  Since ADR-C001, `decisions.json` is authoritative once it exists and those fields are mirrors,
  so the moves would be overwritten by the store's next mirror refresh. On a real install the
  decisions would not move at all, and nothing would say so.
- Its TMDB records (`SeriesDecisionTmdbIdsJson`) live only in the configuration, where a stale
  dashboard save can overwrite them. The movie records are in the store for that reason.
- It ignores un-review tombstones (ADR-F008). A show the user withdrew would come back under its
  new id as un-reviewed but not tombstoned, so the on-disk exemption could re-review it.

## Decision

- The pass runs inside one `DecisionStore.Mutate`, like the movie pass: read, move and write
  under the store's lock. The episode lists stay in the configuration (sync bookkeeping, not a
  decision) and are written after the mutation.
- The series TMDB records are an eighth store, `SeriesDecisionTmdbIds`: routed on save,
  compared in `DecisionStoresEqual`, mirrored to `SeriesDecisionTmdbIdsJson`.
- The file format goes to 3. Versions 1 and 2 are still read; their missing series records are
  seeded from the configuration mirror. A build that knows only version 2 reads a version 3
  file as read-only and falls back to the mirrors, rather than load it and drop the records on
  its next write. This is the same reasoning as version 2 (ADR-F008).
- Tombstones are carried like the movie pass carries them: onto the new id, never onto an id
  that is reviewed, including one that got its mark from another copy merging in the same pass
  (keep wins). An id holding only a tombstone keeps its record. A lift that marks an id reviewed
  removes its tombstone, so the two stores stay disjoint (ADR-C010).

## Consequences

- Measured on a live catalogue: under a quarter of series ids carry a TMDB id in the listing,
  so the pass records an identity for a minority of the series decisions. Decisions on shows
  with no TMDB id are not protected; they behave as before.
- The first run after install only records identities: a move needs a recorded id to disappear.
  On that catalogue no kept show shared a TMDB id with an excluded or un-reviewed copy, so
  nothing could be lifted either.
- A provider can give two productions of one story the same TMDB id. If a reviewed copy of one
  disappears, its mark moves to the other, and under [ADR-C015](015-reviewed-copy-represents-the-series.md)
  the collapse may then pick the other version. The Info line for groups with two reviewed
  versions names it.
- The store file is written on every series sync, as it already is on every movie sync.
- Rolling back to a build before this one leaves the store read-only until the plugin is
  updated again. Decisions keep working from the mirrors in the meantime.
