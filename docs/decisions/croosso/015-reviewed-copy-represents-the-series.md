# ADR-C015: The Copy You Reviewed Represents a Collapsed Series

*(Local ADR, numbered in this repo's own `C` sequence — see [README.md](README.md).)*

**Date**: 2026-11-15
**Status**: ACCEPTED
**Affects**: `Service/StrmSyncService.cs` (series collapse representative pick, `SyncSeriesCoreAsync`)

---

## Context

The series sync collapses entries that land in the same folder under the same cleaned name
into one representative, and only the representative is gated, fetched and written. The pick
preferred a copy with a stored episode hash, then the lowest SeriesId.

The de-dup view groups series by their **raw** provider name, so `EN| Some Show` and
`FR| Some Show` are two rows there. Name cleaning with `EN|` / `FR|` in *Content Remove Terms*
makes both `Some Show`, so the sync merges them.

## Problem

On a live library, a show was reviewed but never appeared. Three copies:

| SeriesId | Name | Audio | Reviewed |
|---|---|---|---|
| A (lowest) | `EN\| Some Show` | English (a remake) | no |
| B | `Some Show` | French (the original) | yes |
| C | `FR\| Some Show` | French (the original) | yes |

None had a stored hash, so the lowest id, the un-reviewed English copy, became the
representative. The review gate checks the representative alone, so the show was held as
"awaiting review" on every run while the user's review sat on its siblings.

The two productions share a title and a story but not the cast or the language, and nothing
separates them reliably: neither the Xtream payload nor Dispatcharr carries a TVDB id, the
EN and FR rows have no TMDB id, the one TMDB id present (on B) looks like the remake's, and
Dispatcharr gives all three copies the remake's cast list. Only the user knows which version
they want, and reviewing one row in the de-dup view is how they say it.

## Alternatives considered

1. **Group-level gate only** (the show passes if any copy is reviewed, pick unchanged).
   Rejected: the show would sync, but as the English copy the user did not pick.
2. **Stop collapsing copies whose language prefixes differ.** Rejected: two copies would
   write into one folder (or need a naming scheme for both), and the prefixes are the
   provider's convention, not data.
3. **Prefer a category or language by configuration.** Rejected for now: the user's default
   is the original language, which no field identifies; the review is a per-show choice the
   user already makes.

## Decision

With the review gate on, the representative order is:

1. a copy whose id is in the reviewed store,
2. a copy with a stored episode hash,
3. the lowest SeriesId.

A reviewed copy therefore passes the gate, and a deliberate un-review tombstone on a sibling
no longer holds (and removes the folder of) the copy the user kept.

When the pick moves away from a copy that has a stored hash (only possible when a reviewed
copy outranks it), the new representative is fetched even when delta-unchanged, like a series
in the failed list. Otherwise it would pre-fetch-skip and keep the old copy's files under its
name. Fetched episodes overwrite files whose URL differs; the old copy's extra episodes go
through orphan cleanup as usual.

With the gate off, the order is unchanged, so no ungated library switches versions.

When two reviewed copies in one group have different provider names, there is nothing left to
choose by: the hash then lowest id decides, and an Info line names the kept and the other
copies so the user can un-review the one they do not want. Same-name copies are one row in
the de-dup view, always reviewed together, and are not reported.

## Consequences

- Measured on a live catalogue of roughly 25,000 titles, several thousand with more than one
  copy: the representative changes for one title (A → B), whose outcome goes from held to
  synced. No show's representative switches away from files on disk. A few titles have two
  reviewed versions; where those are an `EN|` row and a `[MULTI-SUB]` row of the same audio,
  the pick is unchanged.
- The reviewed store now influences which copy is written, not only whether one is. Reviewing
  a second version of a show already on disk does not switch it (the hash wins among reviewed
  copies); un-reviewing the kept one does.
- Movies are unaffected: they collapse by stream id and TMDB id, not through this pick.
