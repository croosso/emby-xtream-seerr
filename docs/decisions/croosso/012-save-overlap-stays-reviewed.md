# ADR-C012: An ID a Save Puts in Both Review Stores Stays Reviewed

*(Local ADR, numbered in this repo's own `C` sequence — see [README.md](README.md).)*

**Date**: 2026-11-06
**Status**: ACCEPTED
**Supersedes**: the save rule of ADR-C010 ("saves are settled by what they changed")
**Affects**: `Service/DecisionStore.cs` (`Replace`)

---

## Context

ADR-C010 made the reviewed and un-reviewed stores disjoint with two rules: every write keeps
an overlapping id reviewed, and before that, a save settles an overlap by what it changed (the
side the save newly added was taken as the user's latest decision). The dashboard's merge was
fixed in the same change so that a click can no longer produce an overlap.

## Problem

The save rule deleted a title the user keeps, about four minutes after it was deployed.

1. The plugin page was loaded while one kept movie's id was in both stores.
2. The first sync on the new build healed the store: that id reviewed only.
3. The user excluded an unrelated title on the still-open page and saved. The page sent its
   copy of the stores, which still had the id in both.
4. Against the healed store the tombstone looked newly added, so the save rule took it as an
   un-review. The next sync's un-review pass removed the movie's folder.

The premise was wrong. With the dashboard fixed, an overlap can only reach a save from a stale
copy: a page loaded before the store healed, or an old cached script. It is never a decision,
and "newly added" relative to the store says nothing about which side the user meant.

## Alternatives considered

1. **Keep the save rule, and have the page reload the stores before saving.** Narrows the
   window but still lets any stale copy decide, and the failure deletes files.
2. **Tombstone wins on a save.** Same failure, worse.
3. **Reject a save that carries an overlap.** It would also reject the exclusion or setting
   the user was actually saving, over something they did not touch.

## Decision

`Replace` no longer settles overlaps itself. An id a save puts in both stores is handled by the
every-write rule from ADR-C010: it stays **reviewed** and its tombstone is dropped, with a
warning naming it. Reviewed is the reading that never removes files; an un-review that really
was lost this way is redone in one click and then sticks.

An un-review sent without an overlap (the reviewed mark removed, the tombstone added) lands as
before.

## Consequences

- A stale page can no longer un-review anything by saving.
- The narrow case ADR-C010's save rule targeted (un-review, then reload the de-dup view before
  saving, on the old script) now keeps the title reviewed. The fixed script cannot produce it.
- On the library that prompted this, the movie has to be re-reviewed once in the de-dup view; its
  folder is rewritten on the next sync and Emby sees it as a new item.
