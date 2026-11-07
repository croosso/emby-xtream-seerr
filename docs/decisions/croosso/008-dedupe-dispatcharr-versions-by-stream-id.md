# ADR-C008: One Dispatcharr Version Entry per Distinct Stream ID

*(Local ADR, numbered in this repo's own `C` sequence — see [README.md](README.md).)*

**Date**: 2026-11-06
**Status**: ACCEPTED
**Affects**: `Service/StrmSyncService.cs` (`WriteMovieAsync`, the Dispatcharr multi-version
branch)

---

## Context

With Dispatcharr enabled, the movie writer asks `/api/vod/movies/{id}/providers/` for the
movie's provider relations and writes one `.strm` per relation: `Title.strm`,
`Title - Version 2.strm`, … each holding
`{DispatcharrUrl}/proxy/vod/movie/{uuid}?stream_id={stream_id}`.

A relation is unique per (M3U account, stream id), not per stream id. Two M3U accounts on
the same upstream provider (two logins, typically to get more connections) each carry a
relation for the same stream, with the same `stream_id`. On a live setup with two such
accounts, `4K - Eternity (2025)` had 11 relations and 7 distinct stream ids, so 4 of its
11 version files were copies of another one.

## Problem

The copies are indistinguishable everywhere a user can see them, and they also do
nothing. Dispatcharr's VOD proxy (`_get_content_and_relation`) resolves `?stream_id=X` to
the **first** active relation with that stream id in account priority order, and then fails
over across **all** of the movie's active relations whichever one it started from. Two files
with the same `stream_id` therefore start on the same relation and fail over in the same
order. The second one adds an entry to Emby's version picker and nothing else.

## Alternatives considered

1. **Keep the copies, add `m3u_account_id` to the URL so each starts on its own account.**
   The proxy accepts it, but only as a fallback after `stream_id`: with both parameters it
   still picks the first relation matching the stream id. Making the copies distinct would
   rely on Dispatcharr changing that order. The relation id is not a URL parameter at all.
   And failover already reaches the other account when the first is at capacity.
2. **De-duplicate on the version label or URL text after building the entries.** Same
   result, but it hides the reason: the stream id is what the URL carries and what
   Dispatcharr matches on, so that is the key to collapse on.
3. **Leave it to Dispatcharr's VOD Manager to prune.** It selects by measured quality and
   language, not identity, and is an optional third-party plugin.

## Decision

`WriteMovieAsync` collapses the provider list on `stream_id` before numbering versions,
keeping the first occurrence so Dispatcharr's order (and with it which file is the main
entry) is preserved. Everything downstream is unchanged:

- more than one distinct stream: main entry plus ` - Version N`, numbered over distinct
  streams;
- one distinct stream (only copies of one stream): the single-entry path a one-provider
  movie already takes, which writes the plain Xtream URL.

## Consequences

- Emby's version picker lists each stream once.
- Existing libraries keep their copies until the movie is rewritten. The smart-skip path
  freezes a skipped movie's version set (ADR-C006), so the fix applies on the next rewrite
  (new title, naming change, watermark reset, folder removed, or one sync with *Smart Skip*
  off). On that rewrite, version numbers can shift: each existing file's content is updated
  in place, and files the rewrite no longer produces go through orphan cleanup. A dropped
  duplicate holds a URL the rewrite wrote at another path, so ADR-C007 treats it as moved and
  deletes it on that run instead of waiting out the grace period.
- A movie whose relations are all copies of one stream moves from the Dispatcharr proxy URL
  to the Xtream URL on its next rewrite, the same as any single-provider movie.
- No extra requests: the collapse runs on the response the writer already fetched.
