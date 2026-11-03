<!-- settings-invalid-records-and-image-dedup-design.md -->
# Settings, invalid records and image deduplication: agreed design

Status: **design confirmed; not implemented**. The settings grouping is a starting point to review and potentially revise after seeing it in use. Implementation requires separate authorization.

## Reported image duplicate and investigation

A user reported that two apparently duplicate images imported from Windows history remained visible in ClipboardPlus even after thorough cleanup. Pin detection for entries had improved after the prior fix. The cause of this particular case is **not established**: do not assume that the images differ in encoding, that pin state is trusted for either item, or that any other specific condition applies.

Investigate using synthetic images and temporary databases, covering actual import, automatic thorough and manual cleanup, Windows suppression, pin trust, frozen and unavailable images, stored versus temporary records, and refresh. Preserve privacy; do not read, log, export, or modify the user's actual clipboard contents, history, database, paths, identifiers, settings or metadata. Any live diagnostic requires a separately approved safe plan. Cleanup must never modify Windows clipboard history: verified eligible Windows duplicates are hidden only in the ClipboardPlus view, consistent with the existing suppression lifetime and pin protections.

## Image relationships and preview explanations

- Keep the existing exact-representation deduplication safety boundary: when images have exactly the same verified pixels but their stored representations differ, **keep both** rather than delete or suppress one merely because the pixels match. Do not use approximate visual-similarity matching.
- In the image preview details footer, alongside Source, Date and Type, briefly explain verified relationships and verified reasons an image was retained, including relevant pin or other protection where established. The requested tone is concise, for example: “Deduplication: same pixels as another item from [date/time]; stored image data differs.” Show a specific cause such as metadata or encoding only when actually verified; the plugin's conversions may not retain enough information to determine the original cause.
- Use the related item's displayed date and time, not internal identifiers. Timestamps need not uniquely identify records: use wording such as “another item from …” rather than implying uniqueness. With multiple verified relationships or reasons, show one relevant example and indicate additional matches without crowding the footer.
- Show relationships only after exact pixel equality is verified. Omit uncertain comparisons rather than implying a duplicate was established. Do not infer a cause from a failed or skipped comparison.
- Compute explanations as part of actual cleanup passes, not by launching additional image comparisons whenever a preview opens. Keep them in memory for the current session, revalidate or discard them when records or settings change, and do not persist comparison metadata. Preserve existing protections for pinned and differently persisted records. Details remain visible only in the user's UI; do not log image data or internal identifiers.

## Invalid records

The existing upstream manual action has a broad notion of invalidity: for example, an unavailable file may be temporarily inaccessible, and the action may include pinned entries. Keep this manual action and its existing scope, but require confirmation before it proceeds. Warn that pinned records and entries for temporarily unavailable files may be permanently removed; cancellation changes nothing.

Add a separate, **conservative automatic check once at startup**, enabled by default without a new setting. It must not run as a recurring background agent or as a separate check after each capture or import. Process both loaded temporary records and saved plugin database records, including saved records outside the loaded list; never modify Windows history. Only unpinned, conclusively verified empty text is eligible:

- Plain text: the verified value is zero-length, not merely unreadable or whitespace.
- Rich text: both the RTF payload and its plain-text companion are verified and zero-length.

Do not treat unreadable, undecryptable, missing, malformed, uncertain or temporarily unavailable data as proven empty. In particular, do not automatically remove missing-file entries or unreadable images. Revalidate pin status and emptiness before removal and update the loaded list and database consistently. On failure or uncertainty, preserve affected records, avoid a user notification and private diagnostics, and retry on a future startup. Use synthetic records and temporary databases to verify pins, unreadable data, file availability, both rich-text fields, unloaded saved records, races and failures.

## Settings layout and verification mode

Use Explorer-style labelled expandable sections. Initially expand General and collapse the other sections. Proposed groups: General; Windows history; Copying; Storage and retention (including image cache); Duplicate cleanup; Excluded apps. Keep Restore defaults outside the groups. Preserve existing settings, accessibility, localized text and behaviors; the grouping is provisional until the user reviews it in action.

Replace the automatic thorough-verification checkbox with a dropdown offering **Best effort** and **Thorough verification**. Keep it visible but disabled while automatic consolidation is off. Preserve the existing stored mode for upgrades and the existing default for new installations; changing the presentation must not silently change cleanup behavior. Retain the user's settings-wording edits. Minimally clarify that changing pin-expiry protection in either direction immediately runs retention cleanup, that expired unpinned records can be deleted, and that pins are protected when the box is checked.

## Review and validation

Before implementation, inspect the actual synthetic failure path and agree on any material trade-offs not resolved above. Cover comparison correctness without relying on a hash as proof; matching pixels with differing representation; equal representations; pinned, saved and temporary records; timestamp collisions; stale session explanations; disabled or best-effort cleanup; and preview responsiveness. Test manual confirmation and cancellation, conservative startup cleanup and failure handling, settings migration and disabled dropdown behavior, localization, keyboard accessibility and layout. Use the user's local Windows runner under the existing safety constraints; do not use cloud runners without explicit permission. No WhisperWriter exclusion feature is authorized by this design.
