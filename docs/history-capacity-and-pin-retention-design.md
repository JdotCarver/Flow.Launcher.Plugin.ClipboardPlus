<!-- history-capacity-and-pin-retention-design.md -->
# History capacity and pin retention: agreed design

Status: **design confirmed; not yet implemented**. This document records the decisions for a future implementation. It supersedes the capacity-counting assumptions in the [deduplication design](deduplication-relative-time-design.md), not its duplicate-verification safety rules.

## Loaded history capacity

- The configured record limit counts **unpinned loaded records only**. Pinned records are additional, with no assumed Windows pin maximum.
- Enforce the same rule for individual captures, bulk loads and imports. When over the applicable limit, unload the oldest unpinned records first; never unload a pin to satisfy the limit. Enforce promptly when an item is unpinned or the configured limit is lowered.
- The limit governs the loaded plugin list, not database retention. Unloading a saved record for capacity does not delete it from the database; it may be considered again on reload. An unsaved unloaded record can be lost. Explicit deletion and time-based retention remain separate mechanisms.
- With automatic duplicate cleanup, retain the previously agreed bounded allowance of up to 25 **additional unpinned** loaded records for unresolved work. Pins do not consume this allowance. When the allowance is exhausted, unload the oldest unpinned record even if a duplicate decision is still uncertain. Respect the existing verification and concurrency safeguards.

## Time-based retention and pins

- Add one global checkbox in the expiry settings, labelled along the lines of **Protect pins from expiring**. It defaults to checked for both existing and new installations. When checked, automatic time-based expiry excludes pins. When unchecked, pins are subject to the existing retention duration for their data type.
- Treat all pins alike, including safety pins created for Windows imports whose pin state could not be verified. Consequently, disabling protection can expire such an import even if it was actually pinned in Windows. User-directed deletion and existing clear actions retain their current behavior; in particular, clear-all can include pins and clear-unpinned excludes them.
- Changing the checkbox in either direction immediately triggers a retention pass. Before disabling protection, ask for confirmation explaining that expired pins may be permanently deleted immediately. Cancel leaves the box checked and runs no pass. Enabling protection needs no confirmation. The tooltip must explain that either change runs cleanup immediately and that cleanup can permanently delete records; even a pass with pin protection enabled may delete expired unpinned saved records.
- Remove successfully expired records from the loaded list and refresh results immediately. If the pass fails, retain the user's setting, show an inline error in settings without clipboard contents or record details, and retry at the next normal expiry pass. Do not imply that a failed pass necessarily deleted nothing unless the implementation guarantees that.
- When an old protected pin is unpinned, enforce loaded-list capacity immediately; its age-based database expiry follows the next normal expiry pass unless another action triggers a pass sooner. The existing durations are type-specific; “Always” effectively avoids expiry. Database expiry is separate from Windows-only history.

## Windows pin recovery

- After untrusted Windows pin metadata becomes trustworthy, use the first trusted snapshot to establish a baseline without retroactively reconciling existing pins. Subsequent genuine pin and unpin events update and persist the corresponding state.
- Existing safety pins remain unchanged by establishing the baseline; later genuine events or explicit user actions can change them. This is distinct from the user's choice to allow all pins to expire.

## Implementation and validation notes

- Exercise actual individual capture, bulk loading, changed limits and unpin paths with synthetic records, including all-pinned history, saved versus unsaved records, duplicate overflow and database reload. Verify no capacity eviction deletes a saved database record.
- Test the pin-expiry checkbox defaults for upgrades and new settings; both toggle directions, confirmation and cancellation, per-type expiry, safety pins, list refresh, and failure feedback. Use temporary synthetic databases. Cover trusted recovery and a subsequent genuine pin event through integration and database tests.
- The separate review fixes remain relevant: bind retention and thorough-cleanup tooltips to their semantic resources, and normalize Windows IDs invariantly with a synthetic Turkish-culture test. Document the existing privacy consequences of protective imports and unencrypted images.
- Investigating or designing clipboard-exclusion markers for WhisperWriter or other apps is a separate follow-up; this document does not authorize an exclusion feature. Preserve privacy: do not expose clipboard contents, identifiers, paths or metadata in diagnostics or test artifacts, and do not modify real clipboard history or the user's database for tests.
- Build and test on the user's local Windows runner; cloud runner use requires separate permission. This design does not itself authorize implementation.
