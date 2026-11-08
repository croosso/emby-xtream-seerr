# ADR-C014: The Pre-Release Filter Judges Each Version by Its Own Provider Name

*(Local ADR, numbered in this repo's own `C` sequence — see [README.md](README.md).)*

**Date**: 2026-11-08
**Status**: ACCEPTED
**Affects**: `Service/ReleaseTagFilter.cs` (new), `Service/StrmSyncService.cs` (movie sync,
`WriteMovieAsync`, retry), `Api/XtreamTunerApi.cs` (de-duplicated movie listing),
`Client/Models/DispatcharrVodModels.cs`, `PluginConfiguration`, the settings page

---

## Context

Providers list cam and telesync copies of films still in theatres, tagged in the name:
`EN| The Odyssey - 2026 [HDTS]`, `EN - The Odyssey (TELESYNC) (2026)`,
`FR| L'Odyssée - 2026 [VOSTFR] [HDCAM]`. Users want them out of the library and out of the
review list.

Dispatcharr's M3U account filters (*Stream Name*, *Group*, *Stream URL*) look like the place to
do this, but as of Dispatcharr 0.33.0 they apply to live streams only: `apps/vod/tasks.py` never
reads `M3UFilter`, and there is no VOD name filter. A `HDTS` rule on each account changes nothing
for movies.

## Problem

Dispatcharr merges a film's relations from every account into one movie row and gives that row
**one** name, taken from one relation. As relations move between rows, which they do daily
(ADR-C007), the name goes stale. Measured on a real instance with four providers:

- 121 movie rows had a pre-release tag in their name.
- In 110 of them every relation was tagged.
- In 11 the row was named after a tagged relation but held untagged ones, some likely retail:
  `Resident Evil (CAM) (2026)` held two `4K-TOP - Resident Evil (2026)` relations, and
  `EN| The Odyssey - 2026 [HDTS]` held, beside lion's `(TELESYNC)` copy, an untagged lion copy
  matching another provider's untagged one in size, resolution, frame rate and 5.1 audio.
- Conversely, 168 relations carried a tag, more than the rows did: tagged copies hide under
  clean row names.

So the title's name is wrong in both directions. Excluding by title (the per-item exclusion, by
id) throws away the clean copies too, and the ids churn daily anyway.

## Alternatives considered

1. **Filter on the title name only.** Cheap, one regex per title, but it drops the 11 mixed
   titles' clean copies and misses tagged versions under clean names.
2. **Detect cams from the stream itself.** Dispatcharr's `vod-probe` plugin records audio
   channels, frame rate and bitrate. Cams tend to be stereo at 25/30 fps, retail 5.1 at
   23.976. It would catch the copies a provider never tags, but it is a heuristic with real
   false positives (a stereo foreign release, a 25 fps PAL source) and the data is a
   third-party plugin's. Left for later, on top of this, if needed.
3. **Ask Dispatcharr to filter VOD.** The right long-term home, but it is not there today.
4. **Judge each version by its own provider name.** Dispatcharr's
   `/api/vod/movies/{id}/providers/` returns every relation's `custom_properties.basic_data.name`,
   the name that provider gave the film. Chosen.

## Decision

- `EnableReleaseTagFilter` (off by default) with `ReleaseTagFilterPattern`, a case-insensitive
  regex; blank means `ReleaseTagFilter.DefaultPattern`, which matches HDTS, TS, TELESYNC, HDCAM,
  CAM, CAMRIP, HDTC, TC, TELECINE, SCR, SCREENER and DVDSCR **only inside `[ ]` or `( )`**, so
  `Cats` or `TS Eliot` never match. An invalid pattern filters nothing and logs an error.
- **Without Dispatcharr** the title's name decides.
- **With Dispatcharr**, versions decide:
  - Before the review gate, a title whose name is tagged has its versions looked up, and is
    left out only when every version is tagged. Only tagged titles are looked up, so this is a
    handful of calls, not one per title.
  - At write time, tagged versions are dropped from the version list. When any were dropped,
    the kept ones are written as proxy entries (`?stream_id=`) even if only one is left: the
    plain Xtream URL lets Dispatcharr pick a relation by account priority, which can be the copy
    just dropped. When every version is tagged (a clean-named title hiding tagged copies), the
    write returns `Filtered`.
- **Fail open.** A lookup that throws or returns no versions (a listed movie always has one)
  keeps the title. A version with no provider name is kept. Absence is not evidence.
- **A left-out title's files are removed** through `RemoveExcludedContent`, the exclusion
  pass, with its safety contract unchanged: only this plugin's own files, never a folder the
  run wrote into, postponed on a partial catalogue.
- **The de-duplicated movie list hides** what the sync would leave out, by the same rule, so
  the review view does not offer titles the sync will never write.
- "Retry failed items" treats `Filtered` as done.

## Consequences

- Self-healing: when a provider swaps a telesync for a retail copy, or Dispatcharr merges a
  clean relation into the row, the next sync picks it up with no decision to undo. Nothing is
  stored, so the id churn cannot break it.
- An untagged cam gets through. Several providers (e.g. the strong accounts' `FR - …` names)
  never tag; option 2 is the answer if that matters.
- The version check runs where versions are written, after smart skip. A title already on
  disk and not new keeps the version files it has until it is next rewritten; with
  Dispatcharr's daily row churn that is usually the next sync. A title dropped at the
  title-level check is removed on the next sync regardless.
- A title left out at write time has already passed the wanted-set capture point (ADR-F009),
  so it can appear in the wanted set for that run. Harmless: the wanted set feeds repairs, and
  a filtered title has no files.
- Series are not filtered. Pre-release tags are a theatrical-film thing; episodes are not
  released as cams.
