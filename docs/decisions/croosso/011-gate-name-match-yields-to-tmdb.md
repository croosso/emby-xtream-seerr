# ADR-C011: The Review Gate's Name Match Yields When TMDB Ids Disagree

*(Local ADR, numbered in this repo's own `C` sequence — see [README.md](README.md).)*

**Date**: 2026-11-06
**Status**: ACCEPTED
**Affects**: `Service/StrmSyncService.cs` (`BuildLibraryIdentityIndex`,
`NameHeldOnlyByOtherTitles`, the movie review gate's on-disk exemption)

---

## Context

With the review gate on, a movie whose stream id is not reviewed is held, unless the user
already keeps it on disk. "On disk" is matched two ways: the provider's TMDB id against the
`[tmdbid=…]` in folder names, or the cleaned provider name against folder names. A match
auto-reviews the new id ("came back under a new StreamId"), so a title whose id the provider
re-issued keeps syncing.

The name match exists for folders that carry no TMDB id (written with TMDB folder naming off,
or for titles with no id). It never checked whether the folder it matched belonged to a
different TMDB title.

## Problem

ADR-C009 made `4K - Some Film (2025)` (TMDB id A) clean to `Some Film (2025)`, the name of a
different movie the user keeps (TMDB id B, folder `Some Film (2025) [tmdbid=B]`). On the first
sync after it shipped, the gate matched the name, logged "1 title you already keep came back
under a new StreamId — Some Film (2025)", auto-reviewed the new id and wrote a second
`Some Film (2025) [tmdbid=A]` folder with six versions. The user never reviewed that title.

Any widening of the cleaner can do this, and so can two films that simply share a title and
year.

## Alternatives considered

1. **Drop the name match when the provider has a TMDB id.** Rejected: a folder written with
   folder naming off carries no id, and the name is then the only evidence the user keeps the
   title. Those are exactly the titles the exemption protects from being held and cleaned up.
2. **Match on name and year only.** The year is already in the name here and both are 2025.
3. **Trust the folder's id over the provider's.** Rejected: either side can be the wrong one;
   the gate only needs to know that they disagree.

## Decision

The identity index also records, per indexed name (raw and cleaned), the TMDB ids of the
folders that carry it, with 0 for a folder without an id. The movie gate's name match does not
count when the provider supplies a TMDB id and **every** folder with that name carries a
different, known TMDB id (`NameHeldOnlyByOtherTitles`). The title is held for review like any
new one.

The name match still counts when:

- the provider has no TMDB id;
- any folder with that name has no TMDB id (nothing is known against it);
- one of the folders carries the provider's id (that already matches on TMDB).

Series are unchanged: the series list carries no TMDB id, so a conflict cannot be detected.

## Consequences

- Same-name titles with different TMDB ids are no longer auto-reviewed; they wait in the
  review queue.
- Decisions already made are not undone. On the library that prompted this, the wrongly
  auto-reviewed title stays reviewed until the user un-reviews or excludes it.
- If a provider re-issues a stream id **and** changes the title's TMDB id at the same time, the
  new id is held instead of auto-reviewed, and the old folder (no longer written) is cleaned up
  after the grace period (ADR-C007). The title shows up in the review queue, which is where a
  changed identity should be confirmed.
- One more dictionary in the index, filled during the same directory walk.
