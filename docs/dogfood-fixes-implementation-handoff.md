<!-- dogfood-fixes-implementation-handoff.md -->
# Implementation handoff: dogfood fixes and settings integration

## Objective and authority

Implement the agreed pin-detection/import, duplicate-investigation, settings and clear-menu changes below, with regression tests. Investigate WhisperWriter interoperability and report evidence and design alternatives, but **do not implement a new exclusion mechanism without separate user approval**. This is an implementation handoff, not authorization to create or merge a PR. Preserve the existing development tooling and follow your own session's branch and Git restrictions; never modify or push the user's `main` or `master`.

The user approved this plan after a design-tree interview. Discuss any material design change or newly discovered trade-off before implementing it. Use readable, SOLID/YAGNI code; all new files begin with their filename in a comment. Use atomic Conventional Commits in Title Case, bundling feature/fix changes with their tests. Do not rewrite the feature history or build a clean upstream PR in this task unless separately asked.

## Starting point and evidence

- Repository: `JdotCarver/Flow.Launcher.Plugin.ClipboardPlus`.
- Reviewed feature is integrated on `arena/9261f4f4-flow-launcher-plugin-clipboard`; implementation source tree before this handoff is commit `526366d978b1de570fb835a98c4b753997c9cb4f`. This includes merge `27b4fa4` of reviewed feature tip `0b2cfd4f0497e81975df297cccc46e68421fdc35` and a subsequent runner-request commit. Fetch/read this source without violating your session's branch rules; verify the starting tree and coordinate with the user if it is unavailable or differs. No code changes were made while preparing this handoff.
- [Agreed feature design at the source commit](https://github.com/JdotCarver/Flow.Launcher.Plugin.ClipboardPlus/blob/526366d978b1de570fb835a98c4b753997c9cb4f/docs/deduplication-relative-time-design.md) and [synthetic measurements and trade-offs](https://github.com/JdotCarver/Flow.Launcher.Plugin.ClipboardPlus/blob/526366d978b1de570fb835a98c4b753997c9cb4f/docs/deduplication-measurements.md) provide context, not automatic future PR inclusions. The feature prefers duplicates over missing content, verifies equality rather than trusting MD5, and protects pinned records. Its existing 25-record overflow trade-off is documented there.
- The merged tree passed solution build/tests and synthetic integration checks on the user's local Windows runner: [run 41539587772](https://github.com/JdotCarver/Flow.Launcher.Plugin.ClipboardPlus/actions/runs/41539587772), request commit `526366d`. This is **not** validation of your future changes or a future cleaned PR. Actions logs have short retention and may contain runner/machine setup metadata; never put private data in them.
- Known limitations: real Windows-history integration was simulated in earlier validation; live-result refresh contention, real disk/encrypted history and sustained clipboard rates have limited measurement. Images are currently excluded from encryption even with encryption enabled: accurately disclose this security concern, but do not change encryption behavior in this task.

## 1. Windows import pinning and duplicate investigation

The user cleared plugin items, toggled Windows-history import off and on, and observed 25 imported items shown as pinned despite never intentionally pinning them. They also observed four identical-looking emoji records with the same displayed date/time in pinned items. These are user observations, not proof of a particular root cause or of content equality.

The current code has a plausible explanation: `WindowsClipboardHelper.GetClipboardData` assigns `Pinned = !pinStateTrusted || currentPinnedIds.Contains(hashId)`, and `PinnedHistorySnapshotReader` returns untrusted when certain metadata reads fail. Thus unknown state is represented as pinned in plugin records. Determine the actual failure mechanism and improve detection based on evidence; do not assume it was this path without verification or weaken safety on malformed, inaccessible, incomplete or ambiguous evidence. Investigate the full import/toggle and duplicate paths, not just the parser. Build synthetic tests for successful pinned and unpinned detection, missing/unexpected/inaccessible metadata, import with multiple unpinned and truly pinned items, and repeated identical synthetic emoji records including same timestamps and realistic distinct Windows IDs. Exercise automatic and manual cleanup as applicable, and distinguish disabled cleanup and intentionally skipped/uncertain candidates from matcher defects. Check that collision candidates are never treated as equal without verification.

Agreed fallback when an import cannot verify pin status:

- Protect the affected imported plugin records by marking them pinned, displaying the ordinary pinned marker, and persisting that protective pin. This is a conservative plugin state, **not** a change to Windows' own pins.
- Show one localized warning per affected import, not one per record, regardless of the optional success-notification setting. Convey: “Couldn't verify Windows pin status. Imported items are treated as pinned for safety, so duplicate cleanup may skip them.” Do not expose content, IDs, metadata or paths in the warning or logs.
- Do not add retroactive repair, migration or automatic reconciliation for already-imported pre-release records when verification recovers. Subsequent genuine Windows pin/unpin events must still work normally; distinguish actual new events from recovery alone. Do not add an unpin-all action.
- Keep **all genuinely pinned duplicates**, even if identical. Keep the existing exact-equality, Windows-suppression and pin protections; never delete Windows clipboard history. If pin state is unknown, prefer duplicates over loss and do not silently suppress potentially pinned Windows items.

## 2. Settings integration and tooltips

Reorder within the existing groups, not a full-panel redesign:

- Place relative-time display beside record ordering.
- Place all deduplication controls, including automatic cleanup, thorough verification, manual cleanup button/feedback and Windows suppression lifetime, together **after** the Keep text/images/files controls. Suppression lifetime belongs here because it controls deduplication. Keep Restore to default last.
- Add concise, accurate, localized tooltips on all settings and meaningful settings actions, using existing controls rather than adding help icons. Make help available on hover/focus and for disabled controls where feasible. Explain important consequences and limitations, including retention, Windows-history behavior, cleanup and the fact that images are not encrypted. Supply resources for the existing English, French, Simplified Chinese and Traditional Chinese languages; avoid inventing behavior. Preserve other settings and their semantics.

## 3. Integrate cleanup into the clear menu

- Add the existing manual duplicate-cleanup action to the configured clear-keyword menu **after** the existing clear actions, preserving their order and behavior. Make the cleanup result available in all data-source modes, including Windows-history-only, and regardless of automatic cleanup being enabled.
- Remove the separate cleanup command/keyword and its settings control; keep the settings-panel manual cleanup button. Handle existing settings files with the now-obsolete keyword safely without changing unrelated preferences or requiring user migration.
- Preserve inline progress and completion reporting in the clear-menu result: counts, skipped items and errors. Keep the launcher open and avoid a completion pop-up. Ensure async result refresh updates the right query and does not discard or reorder the original clear actions. In Windows-only mode, cleanup may suppress eligible entries within the plugin but must never delete Windows history.
- Add regression tests for the menu in both modes, custom clear keyword, obsolete settings compatibility, removal of the separate command, progress/completion behavior and unchanged existing clear actions.

## 4. WhisperWriter investigation only

User reports that dictation appears in ClipboardPlus but not their Windows clipboard history and asks whether it can be excluded. In the public [WhisperWriter input simulation at commit 69ea108998cf3c684a35959fca1be58a14bdd689](https://github.com/JdotCarver/whisper-writer/blob/69ea108998cf3c684a35959fca1be58a14bdd689/src/input_simulation.py#L94-L160), long text is temporarily written to the clipboard, pasted, and prior formats restored after a short delay; this path does not visibly set a history-exclusion marker. The user's private version may differ, and the reason Windows history did not show the item has **not** been established.

ClipboardPlus currently checks the foreground executable for excluded apps, not necessarily the clipboard writer; Windows-history imports bypass that monitor check. Do not promise that excluding `python.exe` or WhisperWriter will work. Investigate standard clipboard exclusion/monitor signals, writer attribution and temporary-write/restoration behavior, including compatibility, race conditions, privacy and the risk of dropping legitimate copies. Use synthetic data. Report evidence, limitations and candidate designs to the user; seek a separate design decision before implementing any exclusion or heuristic. Do not modify WhisperWriter as part of this handoff.

## Privacy, testing and delivery

- Do not expose clipboard contents, existing databases, settings, paths, item IDs, secrets or metadata in logs, artifacts or chat. Use synthetic inputs and temporary databases for automated tests. Do not delete or modify the user's Windows clipboard/history for testing.
- The user allowed read-only investigation of their **current** clipboard if needed, but no contents or identifying information may be logged or uploaded. Prefer synthetic tests; any real-machine diagnostic must be local-only, read-only, and emit only coarse non-sensitive status/counts, never paths, IDs, metadata or clipboard text. Do not run such a diagnostic through public Actions logs. As this is a new session and their clipboard may have changed, reconfirm permission if it has changed or the scope is uncertain; explain what will be read and where output goes first.
- The sandbox may lack .NET; build and run relevant tests and performance checks on the user's **local Windows runner**. Cloud runner requires explicit permission. Use the repository's runner guidance if present, but ensure real clipboard diagnostics are never routed to public Actions logs. Keep session runner requests/tooling separate from any eventual clean PR. If local validation is unavailable, report the limitation honestly rather than claiming it passed.
- Review and test the final patch, including localized settings, disabled-control tooltips, legacy settings, clear-menu ordering/progress, synthetic pin failures and recovery, and duplicate safety. Report exactly what was tested, outstanding risks and the WhisperWriter investigation. Follow your assigned branch rules and ask before any PR or merge.
