# ADR-C006: Smart Skip Treats a Skipped Movie's Folder as In Use as a Whole

*(Local ADR, numbered in this repo's own `C` sequence — see [README.md](README.md).)*

**Date**: 2026-10-25
**Status**: ACCEPTED
**Affects**: `Service/StrmSyncService.cs` (the movie smart-skip path in `WriteMovieAsync`),
`Service/StrmSyncService.Cleanup.cs` (`RecordExistingStrms` doc), the orphan-cleanup
contract for multi-version entries

---

## Context

When Dispatcharr is enabled and a movie has more than one provider version, the movie
writer emits one `.strm` per version beside the main entry: `Title.strm`,
`Title - Version 2.strm`, … each holding a
`{DispatcharrUrl}/proxy/vod/movie/{uuid}?stream_id=N` URL. All entries land in
`writtenPaths`, so orphan cleanup keeps them for the run that wrote them.

On every later sync the movie is smart-skipped (not new, `Title.strm` exists). The skip
path registered only `Title.strm` in `writtenPaths` — the single-entry assumption the
writer had when the skip was written.

## Problem

Orphan cleanup deletes every owned `.strm` under the library root that the current run
did not register. A multi-version entry is owned (its content starts with
`DispatcharrUrl`, which is exactly what `StrmOwnership.IsOwnedStrm` matches) and, after
the skip, unregistered. So **the second sync deleted every ` - Version N` entry, even
while Dispatcharr still served them** — verified by test: first sync writes both
entries, second sync removes `Version 2` while keeping `Title.strm`. The version feature
was effectively one-shot: the entries never survived to the next run, and a user who
looked for their second version in Emby found it gone. The observed library state (0
version entries among the synced movies) was consistent with this having already run.

The deletion also had a masking effect: a version genuinely removed in Dispatcharr and
a version still served by Dispatcharr were indistinguishable on disk — both vanished
on the same schedule.

## Alternatives considered

1. **Register the version set on the skip path by re-querying Dispatcharr.** Rejected:
   the skip exists so that a steady-state sync makes no per-movie API calls; putting
   two Dispatcharr round trips back into it recreates the cost the skip was built to
   avoid.
2. **Have the skip path reconcile: query Dispatcharr and delete entries the provider
   no longer lists.** Rejected for the same reason, and because deletion belongs to
   the guarded delete paths, not the skip.
3. **Register only files matching the ` - Version N` naming pattern.** Rejected: it
   re-encodes the writer's naming convention in a second place, and a naming change
   would silently revive the bug. The failure path already had the right semantic:
   existing `.strm` files in an in-use folder count as in use.

## Decision

The smart-skip path calls `RecordExistingStrms(movieDir, writtenPaths)` — the same
helper the item-failure path uses — instead of adding `Title.strm` alone. A skipped
movie's folder is in use as a whole: everything in it survives orphan cleanup.

The multi-version set therefore freezes at write time, the same contract the main
entry's URL already has. Reconciliation happens on the next rewrite: a rewritten movie
registers exactly what it produces, and cleanup takes the version files that run no
longer produces. This is deliberately not driven by Dispatcharr state; nothing in this
plugin watches Dispatcharr's relations between syncs.

## Consequences

- Multi-version entries survive every skipped sync while Dispatcharr keeps serving
  them, and disappear only through a rewrite (naming-version upgrade, watermark reset,
  exclusion retick, folder deletion) or when the provider drops the movie.
- A version removed in Dispatcharr stays on disk until the next rewrite, exactly like a
  dead stream URL in the main entry. Playback of that version fails at the proxy; the
  file is not silently swept. That is the freeze contract, stated plainly.
- Foreign `.strm` files sitting in a skipped movie's folder are now registered too.
  They were never deletion candidates (not owned), so the only effect is on the ratio
  guard's denominator, and the failure path already produced that effect.
- One extra `Directory.GetFiles` per skipped movie folder, proportional to the movie
  count, no network, no file-content reads.
