# ADR-C005: The Name Pipeline Strips Square-Bracketed Quality Tags

*(Local ADR, numbered in this repo's own `C` sequence — see [README.md](README.md).)*

**Date**: 2026-10-25
**Status**: ACCEPTED
**Affects**: `Service/ContentNameCleaner.cs` (one regex), the review gate, the Seerr
monitor's series matching, folder naming for tagged variants

---

## Context

Providers cross-list variants with quality tags in square brackets: `Carrie [MULTI-SUB]`,
`The Diplomat [MULTI-SUB-AUDIO]`, `YAGA [MULTI-SUB]`. The cleaning pipeline stripped
box-drawing and pipe tags (`|EN|`) and dash prefixes (`QC - `), but not square brackets,
so a tagged variant's cleaned name never equaled its plain title. The consequences
stacked up in practice: the Seerr monitor could not match a request for `YAGA` against
the only fetched variant `YAGA [MULTI-SUB]` (the `EN| YAGA` variant that would have
matched sat in an unselected category), and the de-dup view showed tagged and plain
copies of the same title as different rows.

## Problem

A tag the provider uses to describe audio options was acting as part of the title's
identity. Every tagged variant had to be resolved by hand through the resolution view
or manual review, forever, because nothing in the pipeline could see that
`YAGA [MULTI-SUB]` is YAGA.

## Alternatives considered

1. **Leave it to the resolution view** (ADR-C004). It works, but it makes the user the
   permanent middleman for a mechanical rename the pipeline can do itself.
2. **Strip every bracketed group.** Rejected: a title can genuinely be bracketed. The
   film `[REC]` is the canonical example, and `[4K]`-style tags without hyphens are
   ambiguous enough to leave alone for now.
3. **Bump the STRM naming version** to force every library to rewrite under the new
   names at once. Rejected: the full re-fetch storms `get_series_info` across the whole
   catalogue, which trips the Dispatcharr per-relation refresh gate library-wide. This
   is the same cost the fork already declined once (see the `CurrentStrmNamingVersion`
   divergence note in `StrmSyncService`).

## Decision

- `ContentNameCleaner` strips square-bracketed groups whose content is ALL-CAPS
  letters, digits, spaces or hyphens **and contains at least one hyphen**:
  `[MULTI-SUB]`, `[MULTI-SUB-AUDIO]`, `[MULTI-AUDIO]` go; `[REC]` stays. The hyphen
  requirement is the boundary between a quality tag and a bracketed title.
- **No naming-version bump.** The transition is lazy: a title re-writes under its plain
  name the next time it re-syncs for any reason, its old tagged folder's owned files
  become orphans, and orphan cleanup removes them under its existing guards. Until
  then, an existing tagged folder keeps syncing normally, because the gate's on-disk
  exemption matches by folder name. A one-time mass rename is not worth a one-time
  mass re-fetch.

## Consequences

- The Seerr gate now matches a plain-named request against a tagged variant, which is
  the case that motivated this (YAGA).
- Tagged and plain variants of the same series now collapse into one de-dup row and,
  over time, merge on disk. Until a title re-syncs, a tagged folder and a later plain
  folder for the same title can coexist; orphan cleanup resolves that on the next
  changed sync of the title.
- A title whose entire name is a quality tag (`[MULTI-SUB]` alone) cleans to nothing and
  falls back to the raw name, same as any other empty cleaning result.
