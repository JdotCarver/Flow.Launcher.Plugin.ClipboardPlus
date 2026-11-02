<!-- deduplication-relative-time-design.md -->
# Deduplication and relative time: agreed design

Status: **implemented**. This records the decisions from the design discussion. The conservative best-effort limit was approved after review of synthetic Windows measurements. No performance improvement is assumed from deduplication. The existing image-encryption limitation remains out of scope; see [the optimization report](optimization-report.md).

## Deduplication

### Scope and matching

- Automatic consolidation is opt-in and applies to accessible plugin history, including saved database records and currently loaded temporary records, subject to the active data source. Enabling it covers existing entries and subsequent copies/imports. A manual cleanup is also available independently of the automatic setting.
- Plain text, rich text, images and file lists are eligible. Only exact content of the same type matches. Text is compared exactly, file paths exactly and in order, and images by the plugin-encoded image representation. Rich text requires both its RTF payload and stored plain-text value to match.
- Use hashes to find candidates, not as proof of equality. Verify actual content before destructive removal. If verification is unavailable or unsafe, leave both entries. Prefer extra duplicates over losing content. The pre-existing consecutive-copy filter uses type and MD5; the new feature must not rely on that hash alone as proof of equality for destructive consolidation.
- Protect every pinned entry, and retain one eligible unpinned copy even if a pinned match exists. Otherwise retain the newest eligible unpinned copy within each persistence class. If saved and temporary copies both exist, retain a representative of each rather than change persistence or delete saved history. Protected, skipped or differently persisted records may therefore remain duplicated.
- Do not update pinned timestamps or change pinned ordering. Last-copy time and frecency are explicitly deferred for separate discussion with the maintainer.

### Scheduling and safety

- Offer two verification modes for automatic cleanup. **Best effort** is the default: verify cheap candidates and skip expensive ones. **Thorough** also works through expensive candidates in the background. Both require exact verification before deletion; manual cleanup is always thorough.
- Schedule automatic work promptly without disrupting copying or querying. Responsiveness takes priority if immediate suppression conflicts with it, so temporary duplicates can appear. Measure actual capture, query and cleanup paths with synthetic data on the local Windows runner, including large images and realistic history sizes; review results together before finalizing best-effort limits. The approved best-effort budget is 4,096 character units, conservatively including combined rich-text representations and file-list overhead; images are skipped.
- While automatic cleanup is enabled, defer history-cap eviction until cleanup has resolved possible duplicates. Unverified candidates can leave the loaded history above its configured limit for an extended period; this is preferred to prematurely evicting unique temporary history. If verification completes without uncertainty, trim unpinned overflow using the normal oldest-first policy.
- Revalidate records and files before removal; skip changed or uncertain candidates. Completed safe groups may remain committed if a later group fails. Automatic work is silent, including on failure; diagnostics must not expose clipboard contents. Manual cleanup reports errors separately and notes skipped candidates.
- Avoid concurrent cleanup jobs. When a same-level run already covers the requested work, report it is running; queue a thorough manual pass after less thorough work. If an automatic thorough pass is already running, queue the missing manual-only work, including the orphan-cache scan, rather than repeat completed verification. Manual cleanup continues safely if its initiating UI closes.

### User interface

- Add a settings button and a configurable launcher cleanup keyword, default `cleanup`. Reject a keyword that conflicts with the clear keyword. The exact cleanup query shows the cleanup action above normal matching results; longer queries remain searches. Selecting the action starts cleanup; typing the keyword alone does not.
- Show manual progress when the initiating UI is available. On completion, report separate counts for plugin records removed, cache files removed and Windows items hidden, plus a brief skipped/error note where applicable. A launcher result row remains until the query changes or the launcher closes; settings use inline feedback. No pop-up is requested.
- Turning automatic cleanup off stops future automatic cleanup and automatic suppression; it does not restore deleted plugin records. Manual cleanup remains available.

### Windows clipboard history

- Never delete or modify Windows clipboard history as part of this feature. When integration is enabled, aim to suppress duplicate Windows-origin items inside the plugin, including on reload, without affecting Windows itself. Respect pinned protection; immediately show an item if Windows marks it pinned. Synchronization, reloading and Windows-history-only mode need dedicated testing.
- A dropdown chooses suppression lifetime for both automatic and manual cleanup. Default is **session only**; the alternative remembers suppressed Windows item IDs across reloads. Store IDs, not clipboard contents or content hashes for suppression. Re-verify that a suitable equivalent remains before hiding an item; otherwise show it. Prune IDs for items confirmed absent from a successful, complete Windows-history read, not on failed or incomplete reads.
- Disabling automatic cleanup stops applying automatic suppression. A manual cleanup can still use the chosen persistent lifetime even while automatic cleanup is disabled. Session-only manual suppression may disappear on reload. Persistent state should be limited to what is necessary and must not cause the last available copy to be hidden.

### Image cache

- On record consolidation, permanently delete only verified redundant, unshared files in the plugin-managed cache folder, preserving at least one usable cached copy. Do not delete files outside the managed folder, linked paths, files referenced by surviving records, changed files or files whose ownership/content cannot be safely verified. Leave uncertain files alone. A failed file deletion need not undo safe record consolidation; manual cleanup reports the issue without exposing paths.
- Manual cleanup also scans safely identifiable unreferenced cache PNGs for **byte-identical** duplicates. Distinct filenames do not prevent a match. Prefer files referenced by surviving records; if none are referenced, keep the newest by modification time with a deterministic tie-breaker. Do not scan orphaned files automatically.
- Track minimal provenance for newly created cache files, without storing clipboard contents or file hashes for this purpose. New cache captures use non-overwriting files and provenance even when automatic cleanup is off, so later manual cleanup can act safely; this deliberately changes the old capture path. Skip older or otherwise uncertain-origin files, especially those that might belong to Windows history. The extent to which safe orphan identification is feasible must be tested; skipped files are preferable to unsafe deletion.
- Do not delete a Windows-associated cache file merely because the plugin hides its Windows item. While automatic deduplication is enabled, when Windows reports that an item disappeared, consider deleting its verified, unshared plugin cache file only if another verified usable copy remains. Leave it if ownership, content, event state or surviving references are uncertain. This is new behavior and needs careful integration tests.
- Existing behavior for context: the settings **Clean cache folder** action permanently deletes the cache directory; startup removes a named temporary image, not the entire folder. No periodic general cache cleanup was found.

## Relative time

- Add an opt-in relative-time setting; absolute timestamps remain the default. Replace only the existing result-subtitle timestamp, retaining the pin indicator and source-app information.
- Follow the browser-history feature's age rules: under one minute “Just now”; under one hour whole minutes ago; then today, yesterday or weekday with time through six calendar days; seven or more days use a date, including the year for a different year. Future timestamps more than one minute ahead fall back to an absolute date; those within one minute use “Just now.” Use the plugin language and culture for labels, dates and clock formatting, rather than hard-coding English or a 12-hour clock.
- Recalculate on each query/result refresh; do not add a timer for an already-open unchanged list. Preserve existing absolute formatting when the setting is off.

## Validation and privacy

Use synthetic clipboard records, images, cache folders and temporary databases. Do not inspect or modify the user's actual clipboard history, database, settings or cache. Simulate Windows-history events and failures through appropriate test seams; any real integration test that would touch Windows clipboard history requires a separate safe plan and user approval. Cover type-specific exact comparisons, hash collisions, mutable/changed data, protected and saved records, concurrent mutations, partial failures, Windows pin/removal/reload behavior, safe cache provenance and deletion, keyword conflicts, and relative-time boundaries and localization. Build and test on the local Windows runner and document measured A/B methodology and limitations. GitHub Actions setup metadata remains subject to the repository's log retention policy.
