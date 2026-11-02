<!-- optimization-report.md -->
# ClipboardPlus runtime optimization report

Working report for the eventual performance PR. Measurements use generated inputs on the user's self-hosted Windows runner; they do not read personal clipboard history, settings, or the existing plugin database. Raw GitHub Actions job-setup metadata may be visible under the repository's log retention policy. Times are not universal guarantees.

## Implemented: transactional bulk insertion

**Change:** `SqliteDatabase.AddRecordsAsync` writes assets and records in one transaction. A failed batch now rolls back as a unit; `INSERT OR IGNORE` continues to handle duplicate IDs. Single-record writes are unchanged. Commit: `31b2781`.

**Production-method A/B:** [Windows run 41462238701](https://github.com/JdotCarver/Flow.Launcher.Plugin.ClipboardPlus/actions/runs/41462238701). Same synthetic harness and source tree, except the before variant used `SqliteDatabase.cs` from `077bf9c`. Each variant warmed up once, then inserted 500 generated text records into fresh temporary disk databases five times; database creation and verification were outside the timer. Variants ran sequentially.

| Variant | Minimum | Median | Maximum |
| --- | ---: | ---: | ---: |
| Before | 3,716.46 ms | 4,605.82 ms | 4,757.86 ms |
| After | 14.38 ms | 15.97 ms | 17.33 ms |

The median is about 288 times faster for this scenario; ordering, machine load and storage can affect the ratio. This does not represent individual clipboard-copy latency. The local Windows solution build and existing tests passed, together with new tests for encrypted text, images, duplicate IDs, rollback of both tables and successful writes following a failure.

## Baseline and opportunities

- **Images:** in [run 41456954544](https://github.com/JdotCarver/Flow.Launcher.Plugin.ClipboardPlus/actions/runs/41456954544), hashing a synthetic solid 512×512 bitmap had a 1.68 ms median versus 23.31 ms for noise; the noisy case allocated 9.95 MiB managed. Reopening and materializing ten noisy 256×256 images took 75.16 ms and allocated 36.16 MiB median. Synthetic entropy is not representative of every screenshot, and the latter is not full plugin startup. Further investigation in progress.
- **Queries:** [run 41461241348](https://github.com/JdotCarver/Flow.Launcher.Plugin.ClipboardPlus/actions/runs/41461241348) used the pinned upstream Flow Launcher v2.1.4 fuzzy matcher, regular precision and no alphabet translation. For 10,000 synthetic records an all-match query took 18.23 ms median and allocated 24.62 MiB; deliberately overlapping capture-like lock acquisition waited 12.39 ms median. It excludes the live UI and clipboard event and may differ from the installed Flow version/settings.
- **Memory:** a synthetic repeated-query test showed about 0.01 MiB managed-heap growth after full GC; this does not measure native memory or exclude leaks under real use. Upstream [issue #38](https://github.com/Jack251970/Flow.Launcher.Plugin.ClipboardPlus/issues/38) reports high memory use, but its cause has not been established here.

## Security-relevant observation

`ClipboardData.EncryptData` explicitly excludes images. Enabling the encryption setting therefore does not encrypt saved image assets. This is existing behavior, not introduced by the bulk-insert change; it should be reviewed separately rather than silently changed in a performance PR.

## Open validation gaps

No personal data was used. Real-world UI latency, real clipboard events, actual Flow Launcher process/native memory, Windows clipboard-history synchronization, long-running retention, encryption/image bulk performance, and varied datasets remain unmeasured. Per-scenario methodology and limitations are documented in `tools/performance-baseline/README.md`.
