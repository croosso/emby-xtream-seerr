# Seerr-Driven Selection

*How this project's headline feature works: what you ask for in Seerr reaches your Emby
library on the first sync after your IPTV provider carries it — with no webhook, no
downloads, and no manual review step.*

Related decisions: [ADR-C002](../decisions/croosso/002-seerr-watchlist-feeds-review-gate.md)
(the design itself) and [ADR-C003](../decisions/croosso/003-update-feed-points-at-this-repo.md)
(this repo's release identity).

---

## What this repo changes

This project descends from
[firestaerter3/emby-xtream](https://github.com/firestaerter3/emby-xtream) via
[andyj682/emby-xtream-dedupe](https://github.com/andyj682/emby-xtream-dedupe), which added
the de-duplicated review workflow ("only sync what you have reviewed"). This repo's
contribution is the part that makes reviewing optional: **the review gate is fed by
Seerr**, so your requests and watchlists decide what syncs — a far more natural way to
choose than triaging a 25,000-title list by hand.

Its own decision records so far:

| ADR | What it decided |
|---|---|
| [ADR-C001](../decisions/croosso/001-decision-store-sidecar.md) | Decisions live in a plugin-owned `decisions.json` store; the configuration fields are mirrors. Mutations go through the store's lock, so a sync and a dashboard save can never silently lose each other's changes. |
| [ADR-C002](../decisions/croosso/002-seerr-watchlist-feeds-review-gate.md) | Seerr requests and watchlists feed the review gate, pulled at sync time. The subject of this page. |
| [ADR-C003](../decisions/croosso/003-update-feed-points-at-this-repo.md) | This project has its own name (`emby-xtream-seerr`), release feed, and `seerr-vX.Y.Z` version sequence — it does not offer another repo's releases through its own update button. |
| [ADR-C004](../decisions/croosso/004-seerr-resolution-view.md) | The Seerr Resolution view proposes provider entries for wanted titles the gate cannot match; a human reviews, the fuzzy matcher never acts on its own. |

## The setup this is built for

```
Seerr ──(no Radarr/Sonarr connected)──▶ a want-list, nothing downloads
Emby  ──(plugin)──▶ STRM library written from your IPTV providers via Dispatcharr
```

Seerr is deliberately left unconnected to Radarr and Sonarr. Connected, a request means
"download this"; unconnected, it means "I want to watch this eventually" — which is
exactly the information the review gate wants. The plugin becomes the fulfiller: the
IPTV provider catalogue carrying the title is the moment it can actually be watched.

## Pull, not push — and why

The integration **polls** Seerr at the start of each sync. It does not receive webhooks
and exposes no endpoint for Seerr to call. That is deliberate, for three reasons:

1. **A webhook cannot authenticate to this plugin.** Every plugin route is admin-only
   (Emby gives a route without a role check only a plain "any signed-in user" login), and
   Seerr cannot log in as your Emby admin. Admitting a token-authenticated public route
   would be a standing exception to that policy — for an integration that does not need
   one.
2. **Request events fire at the wrong time.** Seerr says "approved" the moment you click;
   the provider may not carry the title for weeks. The moment that actually matters is
   the sync discovering the title in the catalogue — so that is where the match happens.
3. **The wanted list is derived data with an authoritative owner.** Seerr's API is the
   source of truth; a persisted copy in the plugin would only add staleness. The
   snapshot is pulled fresh at sync time (cached for 15 minutes so a movie and series
   sync running back to back poll once) and never written to disk.

## What happens on each sync

```
sync starts (scheduled or manual — same code path; two runs cannot overlap)
  │
  ├─ 1. catalogue fetch: the full movie/series list from the configured Xtream
  │     connection (in this setup: Dispatcharr's aggregated API)
  │
  ├─ 2. Seerr poll (only when "Only sync what you have reviewed" AND the
  │     Seerr monitor are both enabled):
  │       GET /api/v1/user            → every user
  │       GET /api/v1/request         → pending + approved requests (declined excluded)
  │       GET /api/v1/user/{id}/watchlist → each user's watchlist rows
  │       GET /api/v1/tv/{tmdbId}     → name + first-air date for request rows,
  │                                       which carry no title of their own
  │     → an in-memory snapshot: movie TMDB IDs + series names with first-air years
  │
  ├─ 3. per title, the review gate decides, in this order:
  │       a. already reviewed          → syncs
  │       b. deliberately un-reviewed   → held (its files are removed) — this wins over
  │                                      everything below, including a Seerr match
  │       c. Seerr match               → syncs and is marked reviewed
  │       d. already on disk           → syncs and is marked reviewed
  │       e. otherwise                 → held — counted and logged, never written,
  │                                      never excluded, nothing deleted
  │
  ├─ 4. writes (`.strm` / `.nfo`) only for titles that passed the gate
  │
  └─ 5. folds every auto-reviewed ID through the decision store's lock, so a
        decision made in the dashboard mid-sync is never lost
```

## How a title is recognized

| Media | Match key | Notes |
|---|---|---|
| Movies | the provider-supplied TMDB ID | exact ID equality; no TMDB lookup is performed for held titles, so a provider that does not populate `tmdb_id` keeps holding its titles |
| Series | the cleaned, sanitized title | series list payloads carry no TMDB ID, so both sides run the same name pipeline |
| Series, year-suffixed | `Name (YYYY)` where `YYYY` equals Seerr's first-air year | catches providers that tag titles like `Breaking Bad (2008)` without over-matching a same-named remake from a different year |

Two provider title formats are therefore both handled: a language prefix (`EN| Name`,
stripped by the name cleaning pipeline) and a first-air-year suffix (`Name (2008)`,
matched by year). Titles matching only their exact name require the year to be known —
watchlist rows carry no dates, so a year-suffixed provider title matches a *request*
rather than a watchlist row.

## When a movie does not match, and what to do about it

Movie matching is strict on the TMDB ID, and that is deliberate: a provider ID proves
identity, a title alone only suggests it. Strictness buys precision (no false
auto-reviews) at the cost of coverage, and there are three distinct situations where a
requested movie does not auto-review. Each has its own diagnosis and its own remedy.

**1. The provider has not added the movie yet.** Nothing has failed. The monitor is
watching the ID, the movie is simply not in the catalogue, and the title syncs on the
first run after the provider carries it. This is the normal case for new releases.
Diagnosis: the sync log shows the monitor's count (`Seerr monitor: watching 1 movie
id(s) ...`) but no match line for the title. Remedy: none, wait.

**2. The provider lists the movie, but without a TMDB ID.** The monitor never performs
a TMDB lookup for held titles (see ADR-C002: the gate is deliberately ordered so a held
title costs no fallback lookup, and holding thousands of them nightly is the gate's
whole point). A provider that ships its catalogue with an empty `tmdb_id` field will
therefore keep holding a requested movie forever, and the log shows no sign of what to
do. Diagnosis: check the provider's VOD list for the title and look at its `tmdb_id`
field (Dispatcharr exposes it directly; a browser request to
`player_api.php?action=get_vod_streams` shows it). An empty field is this case.
Remedy: review the title manually in the de-dup view, or read on.

**3. The provider lists the movie with the right TMDB ID, but in a category you have
not selected.** The sync only fetches your selected categories, so the gate never sees
the entry at all. This one is easy to mistake for case 2: same symptom (watched, never
matched), different cause. Diagnosis: search the whole catalogue for the title and note
which category carries the version with the populated `tmdb_id`. Providers commonly
cross-list the same movie several ways, and the variants differ in whether they carry
an ID: a bare `Name` entry in one of your selected categories may have an empty
`tmdb_id` while the `Name (Year)` entry in a category you have not ticked carries the
matching one. Remedy: select that category in the plugin's movie category settings, or
review the title manually.

The log line to read first is the monitor's own count: if it shows the movie as
watched, Seerr and the monitor are both fine and the cause is one of the three cases
above. If it does not, the problem is on the Seerr side (the request is still pending,
declined, or the poll failed and the monitor stood down with a warning).

A bounded escape hatch for case 2 is deliberately not built into the gate: per-title
lookup cost against a held catalogue is exactly what the gate exists to avoid. Instead,
the **Seerr Resolution view** (on both the Movies and Series tabs, beside the browse and
de-duplicated views) lists every wanted title with no automatic match and proposes the
provider entries a title search finds: exact ID, name match, or similar name, each with
its category and score. Reviewing a candidate marks it reviewed through the ordinary
save path, and the next sync writes it; an exact ID found in a category you have not
selected is flagged with the category name instead of a review button, because the fix
there is selecting the category. Unreleased titles are not listed: they cannot be in a
provider catalogue yet. Fuzzy proposals are never acted on automatically; see
[ADR-C004](../decisions/croosso/004-seerr-resolution-view.md).

## When Seerr is unreachable

Nothing breaks. One warning is logged, the monitor stands down for that run, and the
review gate holds everything — its normal, reversible behavior. The next sync retries.
The monitor never fails the sync and never persists its snapshot, so there is no state
to repair.

## Configuration

*Plugin Config → STRM Sync Settings*:

1. **Only sync movies and series you have reviewed** — the gate. Required: the monitor
   exists to exempt held titles, so with the gate off there is nothing to do and Seerr is
   not polled at all.
2. **Treat titles requested or watchlisted in Seerr as reviewed** — the monitor.
3. **Seerr URL** and **Seerr API key** — the API key is generated in Seerr under
   Settings → API Key; grant its account the admin role so it can read every user's
   watchlist. The key is stored with the plugin configuration, never logged, and redacted
   from the sanitized log download.

## What you will see in the log

```
Seerr monitor: watching 0 movie id(s) and 2 series name(s) — 2 request row(s) and 0 watchlist row(s) from 2 user(s)
Seerr monitor: 3 show(s) you asked for appeared in the catalogue — synced and marked reviewed: Reacher (2022), The Walking Dead, The Walking Dead (2010)
Review gate: 24968 un-reviewed show(s) held out of the library. They are NOT excluded — ...
```

Every match is logged by name, so a wrong auto-review (a same-named remake, for instance)
is visible and can be withdrawn: un-review the title in the de-dup view and the next sync
removes its files and puts it back in the review queue.
