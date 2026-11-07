# ADR-C009: The Name Pipeline Strips Quality-Label Dash Prefixes ("4K - ", "4K-NF - ")

*(Local ADR, numbered in this repo's own `C` sequence — see [README.md](README.md).)*

**Date**: 2026-11-06
**Status**: ACCEPTED
**Affects**: `Service/ContentNameCleaner.cs` (`DashPrefixRegex`),
`Service/StrmSyncService.cs` (`BuildLibraryIdentityIndex`)
**Reverses**: the "quality tag preserved, use ContentRemoveTerms" choice made with the
original dash rule (upstream `1bec2b9`)

---

## Context

`ContentNameCleaner` strips dash-style prefix labels. Upstream introduced the rule for
country codes (`EN - `, `FR - `) and limited it to exactly two uppercase letters so that show
acronyms providers format the same way (`FBI - Most Wanted`, `NCIS - Los Angeles`) keep their
names. Quality labels were pinned as preserved by test (`4K-NF - Arcane`, `FHD - The Crown`)
with the note "use ContentRemoveTerms". The code comment kept claiming `"4K-NF - "` was
removed.

## Problem

Providers prefix titles with quality labels: `4K - `, `4K-NF - `, `4K-AMZ - `,
`4K-FR-HDR - `, `4K-D+ - ` (all seen in one live catalogue; Dispatcharr's VOD Manager
title-cleanup list carries the same family). Left in place, the label becomes part of the
folder name. With NFO files and TMDB folder naming off, the folder name is all Emby has, and
its TMDB search for `4K - Some Film` finds nothing. An unmatched movie gets no provider id, so
Emby cannot group its Dispatcharr version files and shows one movie per file: ten copies of
`4K - Some Film` in the library.

`ContentRemoveTerms` is not an answer. It removes a term anywhere in a title, so `4K - `
would also be cut from the middle of a name, every variant (`4K-NF - `, `4K-AMZ - `, …) needs
its own line, and nothing tells the user the line is needed until the library is wrong.

## Alternatives considered

1. **Widen to any 2–4 character label.** Rejected: it eats `FBI`, `CSI`, `NCIS`, which is
   exactly what the original limit protects.
2. **Strip any label containing a digit.** Rejected: too loose (`24 - `, `U2 - `, `007 - `
   can be titles or parts of them).
3. **Leave it to TMDB folder naming.** It fixes Emby's matching, but not the name the user
   sees, the de-dup view, or the Seerr monitor's name matching.

## Decision

The dash rule strips a leading label that is either:

- exactly two uppercase letters, as before; or
- a **resolution token** (`4K`, `8K`, `UHD`, `FHD`, `HD`, `SD`), optionally followed by
  hyphen-joined tags of uppercase letters, digits or `+` (`4K-NF`, `4K-FR-HDR`, `4K-D+`),

followed by ` - `. Case-sensitive, once per name. `4KIDS - …`, `4K-Title` (no separator) and
lowercase labels stay untouched.

**The on-disk identity index also records each folder name as the cleaner would produce it
today.** The review gate's exemption for titles already on disk matches the cleaned provider
name against folder names. Without this, a folder written as `4K - Title (2025)` stops
matching the moment the provider name starts cleaning to `Title (2025)`: a title whose id was
never reviewed would be held, its files would become orphans, and cleanup would delete them
(after ADR-C007's grace period). Verified by test: the new rule without the index change holds
the title. This also makes ADR-C005's statement that tagged folders "keep syncing because the
gate's on-disk exemption matches by folder name" true; it was not, for the same reason. The
user's own remove terms are not applied in the index, since the index is built without
configuration.

No naming-version bump.

## Consequences

- An affected title moves to its clean folder on the **next** sync, not lazily: smart skip
  probes the new folder path, which does not exist yet, so the title is rewritten. The old
  folder holds the same URLs, so ADR-C007 deletes it in the same run as a move; series
  cleanup deletes on first miss anyway. Only affected titles are re-fetched.
- Emby sees a renamed folder as a new item: watched state and progress on affected titles can
  reset, once.
- `FHD - The Crown` and `4K-NF - Arcane` now clean to `The Crown` and `Arcane`. A real title
  that starts with a bare resolution token followed by ` - ` would lose that token; none is
  known.
- The index holds up to twice as many names. They are only used for set lookups.
