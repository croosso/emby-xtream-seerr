# ADR-C002: The Seerr Watchlist Feeds the Review Gate, Pulled at Sync Time

*(Local ADR, numbered in this repo's own `C` sequence so it can never collide with an
upstream or fork ADR — see [README.md](README.md).)*

**Date**: 2026-10-24
**Status**: ACCEPTED
**Affects**: `Service/SeerrClient.cs` (new), `Service/SeerrMonitor.cs` (new),
`StrmSyncService` (both review gates), `PluginConfiguration` (Seerr settings),
`LogSanitizer` (+ `XtreamTunerApi` call site), `Configuration/Web/config.{html,js}`

---

## Context

The review gate (`RequireReviewBeforeSync`) holds a provider's new titles out of the
library until the user reviews them. The user decides what is worth watching in a
separate tool: a Seerr instance (https://github.com/seerr-team/seerr) deliberately
left unconnected to Radarr/Sonarr, so a request in Seerr is a want, not a download.
When the provider catalogue later carries that title, the two systems should meet:
the title should reach the library without a second manual review step.

Both sides already speak TMDB IDs. Seerr's API exposes every request and every user
watchlist with `tmdbId` and `mediaType` (`movie`/`tv`). The provider's VOD payload
carries `tmdb_id` for movies. The gate's on-disk exemption and the ADR-F009 wanted
set already established TMDB-ID-as-identity for movies.

ADR-C001 anticipated this integration as a *webhook* that marks titles reviewed on
request approval, with a REST endpoint as a thin wrapper over `AddReviewed`.

## Problem

Two identity gaps and one trust question:

1. **Series list payloads carry no TMDB ID.** Measured 0 of 9,979 on `get_series`
   (see the series review gate). So a monitored series cannot be recognized at gate
   time the way a movie is. The TMDB ID only arrives on the `get_series_info`
   detail fetch — the expensive call the gate deliberately runs *after* its decision,
   and the one that trips Dispatcharr's per-relation refresh gate.
2. **Webhooks cannot authenticate to this plugin.** Every plugin route is
   admin-only by policy, and Seerr cannot log in as an Emby admin. Admitting a
   token-authenticated public route would be a standing exception to that policy for
   an integration that does not need it. A webhook also fires on *request events*
   (approve, available) — moments unrelated to when the provider catalogue actually
   carries the title, so the plugin would still need its own matching step.
3. **What does a match mean?** The gate's contract is that a held title is *held* —
   never excluded, never written. A new "monitored" source of auto-review must not
   quietly override deliberate withdrawals (ADR-F008 un-review tombstones), and must
   not become a second, unbounded TMDB-lookup cost on held titles (the gate is
   deliberately ordered so a held title costs no fallback lookup).

## Alternatives considered

1. **Push: Seerr webhook → plugin REST route → `AddReviewed`.** The path ADR-C001
   anticipated. Rejected for (2) above: a non-Emby-authenticated route is a policy
   exception, and the event fires at the wrong time — the provider may not carry
   the title for weeks after Seerr says "approved". The plugin would need the
   matching machinery anyway; the webhook only adds the auth problem.
2. **Poll Radarr/Sonarr for monitored items.** Workable, but couples the wanted
   list to tools configured for newsgroup downloads the user does not want
   triggered, and series would arrive TVDB-keyed, forcing a TMDB↔TVDB
   reconciliation the plugin has no home for. Seerr is already the user's
   want-list and is TMDB-keyed for both media types.
3. **Persist a monitored-ID store in `decisions.json`.** Rejected: the monitored
   set is *derived* data with an authoritative owner (Seerr). Persisting a copy
   creates staleness and a second writer without adding any capability — the set
   is small, cheap to fetch, and only consulted at sync time. (This is the
   opposite of the ADR-C001 analysis: decisions are unrecoverable user intent and
   need a durable store; the monitored set is neither.)
4. **Fetch `get_series_info` per held series to recover its TMDB ID for matching.**
   Rejected: it moves the gate's cost from "no HTTP per held title" to "one detail
   fetch per held title per sync" — exactly the storm the gate ordering and the
   Dispatcharr refresh gate exist to prevent, and it would run for every held
   title in the catalogue, monitored or not.

## Decision

The plugin **pulls** a monitored set from Seerr at the start of each sync run, and
the review gate treats a match as a keep decision.

- **`SeerrClient`** (`Service/SeerrClient.cs`) reads the Seerr REST API with
  `X-Api-Key` auth: all requests (`/api/v1/request`, pending and approved, declined
  excluded), and every user's watchlist (`/api/v1/user` → `/api/v1/user/{id}/watchlist`).
  Payloads are parsed tolerantly (ADR-010's philosophy for external JSON): IDs are
  read from `media`/`mediaInfo`/top level, titles from `subject`/`name`/`title`,
  and pagination from either the `pageInfo` or the `page`/`totalPages` envelope,
  because Seerr's own API surface has already drifted between those shapes.
- **`SeerrMonitor`** (`Service/SeerrMonitor.cs`) turns those rows into a snapshot:
  a TMDB-ID set for movies, and a *normalized-title* set for series (the series
  identity gap: a series is matched by the same cleaned, sanitized name the gate
  computes for the provider's title, cross-checked against nothing else available
  at gate time). The snapshot lives in memory with a short TTL so a back-to-back
  movie and series sync polls Seerr once. It is never persisted.
- **Both review gates consult the snapshot after the ADR-F008 tombstone check and
  before the on-disk exemption.** A match is recorded through the same
  `autoReviewed` list the on-disk exemption uses, so the fold goes through
  `DecisionStore.AddReviewed` under the store's lock — no new mutation path, no
  new store (ADR-C001). The tombstone-first ordering means an explicit un-review
  always wins over a Seerr match: withdrawal is the newer, more specific intent.
- **Matching is bounded and free of extra HTTP against the provider.** Movies match
  on the provider-supplied `tmdb_id` only. Series match on the normalized folder
  name only. Neither match performs a TMDB fallback lookup; a title without an ID
  simply stays held. Name collisions are possible for series (a remake with the
  same title); every match is logged so a wrong auto-review is visible, and the
  ADR-F008 un-review path removes the result.
- **The monitor only runs when the review gate is on.** With the gate off nothing
  is held, so there is nothing to exempt; requiring the gate bounds both the poll
  cost and the surface. A failed poll (Seerr down, bad key) logs one warning and
  stands the monitor down for that run — the gate then holds everything, which is
  the pre-existing, reversible behavior. The monitor never fails the sync.
- **Auto-review is the only mode.** A "flag but hold" mode was considered and
  deferred: the review queue UI has no notion of a badge, and a monitored title
  is by definition something the user already asked for. The ADR-F008 un-review
  path is the withdrawal mechanism if a match turns out wrong.

## Consequences

- A title requested or watchlisted in Seerr syncs into the library the first sync
  after the provider carries it, and lands in the reviewed checkpoint under its
  provider ID — self-healing against later ID churn, same as the on-disk exemption.
- A monitored movie on a provider that does not populate `tmdb_id` will not match.
  That is the deliberate cost of keeping the gate free of fallback lookups; the
  TMDB coverage percentage is already logged per sync when TMDB folder naming is
  on, which is the diagnostic.
- Series matching is name-based and can over-match same-named shows; movie
  matching cannot over-match but can under-match. Both failure modes are logged,
  and neither can delete anything — a bad match only writes files, which exclusion
  or un-review removes.
- Seerr's API key is a new secret in the configuration. `LogSanitizer` redacts it
  from the sanitized log download alongside the existing credentials, and the
  plugin never logs it.
- The REST endpoint ADR-C001 anticipated is still the right shape for a *future*
  push integration (e.g. an external automation marking decisions); this ADR does
  not build it, because the pull covers the use case without the auth exception.
