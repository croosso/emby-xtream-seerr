# ADR-C010: The Reviewed and Un-reviewed Stores Never Hold the Same ID

*(Local ADR, numbered in this repo's own `C` sequence — see [README.md](README.md).)*

**Date**: 2026-11-06
**Status**: ACCEPTED, save rule SUPERSEDED BY [ADR-C012](012-save-overlap-stays-reviewed.md)
**Affects**: `Service/DecisionStore.cs` (`Mutate`, `Replace`),
`Configuration/Web/config.js` (`mergeServerReviewed`)

---

## Context

A title is either kept (its ids in `ReviewedVodStreamIds` / `ReviewedSeriesIds`) or
deliberately withdrawn (its ids in the un-review tombstone stores, ADR-F008). The dashboard's
review toggle keeps the two exclusive: un-reviewing removes the reviewed mark and adds the
tombstone, re-reviewing does the opposite.

Nothing else enforced it. On a live library, one kept movie's id sat in both stores. The
record shows how: it was reviewed first; later a phone browser sent two configuration saves
0.8 s apart and a movie sync, and from then on the id was in both.

## Problem

Two independent gaps let the overlap in:

- **The dashboard merge unions.** Reloading the de-dup view calls `mergeServerReviewed`,
  which unions the server's reviewed set and tombstones into the page's copies so it picks up
  marks the sync added. Un-review a title, reload the view before saving, and the server's old
  reviewed mark is merged back beside the new tombstone. The code documented this as an
  accepted edge ("re-marks it reviewed"), but it does not re-mark it: it leaves both, and the
  next save stores both.
- **The store accepts whatever a save carries.** `Replace` applies the incoming sets
  wholesale and never checks them against each other.

An id in both stores is ambiguous. The review gate checks the reviewed store first, so the
sync keeps the title, while the de-dup view, the tombstone removal pass and anything reading
the tombstones see a withdrawal that is not in effect. Which one the user meant cannot be read
off the stores.

## Alternatives considered

1. **Tombstone wins everywhere.** Rejected: it would change what the sync does with an
   existing overlap from "keep" to "withdraw and remove the files", on the strength of a mark
   that is just as likely to be the stale half.
2. **Fix only the dashboard.** Rejected: it stops new overlaps from that path but leaves the
   existing ones, and the identity reconcile can carry a reviewed mark and a tombstone from
   two re-issued ids onto one id without the page involved.
3. **Fix only the store, always with "reviewed wins".** Rejected for saves: in the merge case
   the tombstone is the user's newest action, and silently reverting it would read as the
   un-review button not working.

## Decision

**Saves are settled by what they changed.** *(Superseded by ADR-C012: a save's overlap now
stays reviewed like any other. This rule un-reviewed a kept title from a stale page.)* `Replace` compares each id that the incoming
configuration puts in both stores with the stored state before the save:

- added to the tombstones by this save, already reviewed before it → **un-reviewed** (the
  reviewed half is the stale merge);
- added to the reviewed store by this save, already a tombstone before it → **reviewed**;
- added to both, or to neither → left to the rule below.

**Every write keeps the stores disjoint.** After any mutation, before it is persisted, an id
still in both stores is kept **reviewed** and its tombstone dropped, with a warning that names
the ids. That is how the review gate already treats such an id, so no title changes state in
the library. It runs on every `Mutate`, including the identity reconcile every movie sync
performs, so a store that already holds an overlap heals on the next sync. Unreadable (null)
stores are left alone for repair, as everywhere else.

**The dashboard stops creating overlaps.** `mergeServerReviewed` never merges an id into one
set while the page holds it in the other: the page's own state is the user's latest decision.

## Consequences

- An id can no longer be both kept and withdrawn; the de-dup view, the gate and the
  tombstone pass agree.
- On upgrade, existing overlaps resolve to reviewed on the first movie sync, logged as a
  warning listing the ids. On the library that prompted this, the movie keeps its files;
  un-reviewing it again in the de-dup view now sticks.
- A page left open while another device changes a decision still wins on save, as any web
  form does (already documented on `RouteDecisionStoreWrites`); what changes is that it can
  no longer write a contradiction.
- The dashboard change has no automated test (the repo has no JavaScript harness); the store
  rules that guard against it are tested.
