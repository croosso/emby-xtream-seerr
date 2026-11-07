# ADR-C007: A Movie Missing From the Catalogue Waits Out a Grace Period Before Cleanup Deletes It

*(Local ADR, numbered in this repo's own `C` sequence — see [README.md](README.md).)*

**Date**: 2026-11-06
**Status**: ACCEPTED
**Affects**: `Service/StrmSyncService.Cleanup.cs` (`CleanupOrphans`, `SelectDueOrphans`),
`Service/OrphanGraceLedger.cs`, `Service/StrmSyncService.cs` (movie sync wiring,
`WriteMovieAsync`), `PluginConfiguration.OrphanGracePeriodHours`, the dashboard cleanup
settings

---

## Context

Orphan cleanup deletes every owned `.strm` the current run did not produce. ADR-013 made
that safe against a fetch that *failed*: a category that errors trips
`FailedCategoryCount` and cleanup does not run. It assumed the converse, that a fetch which
*succeeded* lists every title the provider still has.

With Dispatcharr in front of several providers that assumption does not hold. Observed on a
live setup (Dispatcharr 0.31 with the VOD Manager and vod-probe plugins, four M3U accounts):

- The nightly M3U refresh (00:30) gives providers that ship titles without a TMDB id fresh
  duplicate movie rows, under raw names (`EN| Eternity`, a new id every night), and moves
  their relations off the merged movie onto those rows.
- vod-probe (01:00 onward, in batches) resolves the TMDB ids and merges the duplicates back.
  The catalogue shrinks from about 46k to 31k titles over the day.
- VOD Manager (10:00) deletes the relations it rejects. The next refresh brings them back,
  except when that refresh aborts, which it did for one account on 2026-11-06.
- The Xtream feed lists a movie in a category only while one of its relations sits there,
  and the plugin reads only the selected categories.

So a merged, multi-provider movie (exactly the ones with Dispatcharr version entries) is
regularly invisible for several hours, with the same id and name before and after. The
plugin's daily catalogue snapshots showed `4K - Eternity (2025)` as id 75469 every night
from 10-28 to 11-05 and missing on 11-06; the snapshot is written from the unfiltered fetch.

## Problem

Every sync that ran inside such a window deleted the movie, and the first sync after it
wrote it again. Between 11-03 and 11-06 cleanup removed the files of every multi-version
movie in the library five times (22, 39, 53, 53 files and a few singles). A title whose
relations never came back to a selected category (`Hope (SUBS) (2026)`) stayed deleted.
Each round trip also costs the Emby items their watched state and metadata.

Nothing in the fetch distinguishes this from a real removal: the response is well formed,
every category answers, and the ratio guard stays quiet because a few dozen files is far
below any sane threshold on a library of thousands.

## Alternatives considered

1. **Tighten the ratio guard.** Rejected: the losses are small in proportion by nature. A
   threshold low enough to catch them would block every legitimate cleanup.
2. **Ask the user to select every category.** Done as a mitigation on the affected setup,
   and it removes most of the windows, but not the one where all of a title's relations sit
   on a duplicate row. It is also not something every user will know to do.
3. **Ask Dispatcharr whether the movie still exists before deleting.** Rejected: the movie
   row does exist; what is missing is a relation in a category the plugin reads, and the
   answer depends on Dispatcharr internals and on plugins this repo does not control. It
   would also put API calls back onto the cleanup path for every candidate.
4. **Require N consecutive missing runs.** Rejected in favour of time: a run count means a
   different window at every sync interval, and a manual sync would count as a run.
5. **Time-based grace for everything, including renames.** Rejected: a title written under
   a new folder (provider rename, folder-naming or folder-mode change) would leave both
   folders in Emby for a whole grace period.

## Decision

Orphan cleanup for movies deletes a candidate only when one of these holds:

- **It moved.** Its URL is one this run wrote or found up to date at another path. A moved
  title is not missing; deleting immediately avoids duplicate folders.
- **It has been missing for the grace period.** `OrphanGracePeriodHours`, default 24, so a
  once-a-day sync deletes on about its second miss (its third if the schedule drifts early). 0 restores immediate deletion.

The first-missing time per candidate lives in `orphan-grace-movies.json` under the records
root (beside `decisions.json`), keyed by the path relative to the library root. The ledger
is rebuilt from each run's candidates, so a title that comes back drops out and its next
absence starts a fresh clock.

It is recomputed only on runs that are allowed to delete: the failed-category,
empty-catalogue and ratio guards return before it, as before.

Every failure delays, never accelerates:

- an unreadable or unknown-format ledger loads as empty, restarting every clock;
- a first-missing time in the future is replaced by now;
- a failed write means the next run starts the clocks again;
- with no records root there is no ledger, and nothing absent is ever due. The files are
  kept and a warning says why. Deleting immediately there would bring the bug back.

The decision logic lives in `StrmSyncService.Cleanup.cs` so the mutation tests cover it,
and `SelectDueOrphans` is pinned there by `scripts/check-delete-sites.py`. The ledger I/O
is a separate class with no delete calls.

## Consequences

- A title genuinely removed by the provider stays in the library for up to the grace
  period plus one sync interval. Playback of it fails at the provider in that window.
- Folder renames converge in one sync, as before, because the URL is unchanged. A title
  whose URL changes *and* moves (new stream id under a new name) is treated as missing: the
  old folder waits out the grace period beside the new one.
- Series are unchanged: their cleanup still deletes on the first miss. The same reasoning
  applies to them; wiring it in needs the series writer to collect URLs the way the movie
  writer now does, and is left for a follow-up.
- Per-item exclusions and the un-review removal pass do not go through orphan cleanup and
  are unaffected: those are user decisions and take effect on the next sync.
- One small JSON file under the records root, rewritten on every movie sync that reaches
  cleanup. Each orphan candidate is read once more to compare its URL; candidates are
  already read once for the ownership check, so the cost stays proportional to deletions.
