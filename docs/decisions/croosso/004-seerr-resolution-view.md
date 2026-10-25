# ADR-C004: The Seerr Resolution View Proposes, a Human Reviews

*(Local ADR, numbered in this repo's own `C` sequence — see [README.md](README.md).)*

**Date**: 2026-10-24
**Status**: ACCEPTED
**Affects**: `Service/SeerrFuzzyMatch.cs` (new), `Service/SeerrResolutionService.cs` (new),
`SeerrClient` (movie detail fetch), `SeerrMonitoredSnapshot` (retains the raw wanted
items), `StrmSyncService` (resolution accessor), `Api/XtreamTunerApi.cs`
(`GET /XtreamTuner/Seerr/Resolutions`), `Configuration/Web/config.{html,js}` (the view)

---

## Context

The review gate matches movies strictly by TMDB ID (ADR-C002), which is deliberate: an
ID proves identity, a title only suggests it. The cost is that a request cannot
auto-review when the provider ships the title without a `tmdb_id`, or with the matching
ID in a category the sync does not fetch. The documented remedy until now was reading
the log, checking the provider's catalogue by hand, and reviewing the title in the
de-dup view. That is archaeology, and the diagnosis of the Step Up case (three failure
modes, each with a different remedy) showed exactly how much digging it takes.

## Problem

Strict matching is right, but when it cannot fire the user has no way to see *why* or
to act on it from the plugin's own interface. The information needed to propose the
right provider entry exists (the wanted title, its release date, the provider
catalogue), it is just never brought together anywhere a person can look at.

## Alternatives considered

1. **Loosen the gate's movie matching** (fuzzy name matching at sync time). Rejected:
   the strictness is the feature. A wrong auto-review lands a wrong title in the
   library with no human in the loop; the gate would need the same proposals this
   view builds, but would act on them unsupervised.
2. **A persistent Seerr-tmdb to provider-id mapping**, so a resolved link survives
   provider ID churn. Deferred, not rejected: it is a new persistent store, new
   sync-path code, and a new class of stale state. A one-click review already gets the
   title on disk, and the on-disk exemption keeps it there from then on, so churn
   after resolution is a minor annoyance rather than a loss. If churn makes
   re-resolution common, the mapping store is the upgrade path.
3. **A mutating resolve endpoint** (`POST .../Resolve`). Rejected: the dashboard
   already reviews titles by editing its client-side copy of the reviewed list and
   saving, which routes through `UpdateConfiguration` into the decision store under
   its lock (ADR-C001). A second write path would be a second thing to keep correct.

## Decision

- **A read-only resolution endpoint** (`GET /XtreamTuner/Seerr/Resolutions`, admin-only
  like every route) lists, per wanted title with no automatic match, the provider
  entries a title search proposes: an exact TMDB match, a name match, or a similar
  name, each with its score and its category.
- **Unreleased titles are excluded up front.** Seerr's TMDB proxy carries the release
  or first-air date, and a title that has not been released cannot be in a provider
  catalogue, so proposing candidates for it is noise. The gate picks it up normally
  when a provider adds it.
- **An exact TMDB match in an unselected category is surfaced without a review
  button**, flagged with the category's name: the remedy is selecting the category,
  because the sync never fetches what it is not told to and reviewing the entry would
  do nothing.
- **Fuzzy matching is a proposal tool, never a matcher.** `SeerrFuzzyMatch` normalizes
  both sides (language tags, `EN|`-style prefixes, bracketed tags, parenthesized
  years) and scores by edit distance with a year-agreement boost; anything at or above
  the threshold is shown with its score, and only a human click turns a proposal into
  a review. A same-named remake with a different year is demoted, not hidden.
- **Reviewing reuses the de-dup view's own client-side toggle**, so the decision
  travels the ordinary save path and the decision store stays the single writer. A
  series candidate carries every provider ID of its name group, because the sync
  collapses cross-listed copies to one representative and reviewing only one copy
  could leave the title held under the copy the sync actually picks.
- **Titles that would auto-match, candidates already reviewed, and gate-equivalent
  series matches are not listed**: the view is only for what needs a human.

## Consequences

- The three failure modes from the documentation ("not added yet", "no tmdb_id",
  "right ID in an unselected category") are now distinguishable on one screen, and two
  of them are fixable from it.
- The monitor's snapshot retains the raw wanted items, which costs one list of small
  objects per poll and nothing else; the resolution view fetches movie details and
  the provider catalogues on demand (cached for a few minutes), not at sync time.
- The catalogue fetch for the view reads the provider's full lists (all categories,
  not only the selected ones), which is what makes the unselected-category hint
  possible. It is a read-only dashboard fetch, not part of the sync.
- Provider ID churn after a resolution still leaves the resolved title re-matched only
  by the on-disk exemption, per the deferred alternative above.
