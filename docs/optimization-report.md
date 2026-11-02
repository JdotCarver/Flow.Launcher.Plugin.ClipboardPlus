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

- **Images:** in [run 41456954544](https://github.com/JdotCarver/Flow.Launcher.Plugin.ClipboardPlus/actions/runs/41456954544), hashing a synthetic solid 512×512 bitmap had a 1.68 ms median versus 23.31 ms for noise; the noisy case allocated 9.95 MiB managed. Reopening and materializing ten noisy 256×256 images took 75.16 ms and allocated 36.16 MiB median. Synthetic entropy is not representative of every screenshot, and the latter is not full plugin startup. A focused encoding experiment is summarized below.
- **Queries:** [run 41461241348](https://github.com/JdotCarver/Flow.Launcher.Plugin.ClipboardPlus/actions/runs/41461241348) used the pinned upstream Flow Launcher v2.1.4 fuzzy matcher, regular precision and no alphabet translation. For 10,000 synthetic records an all-match query took 18.23 ms median and allocated 24.62 MiB; deliberately overlapping capture-like lock acquisition waited 12.39 ms median. It excludes the live UI and clipboard event and may differ from the installed Flow version/settings.
- **Memory:** a synthetic repeated-query test showed about 0.01 MiB managed-heap growth after full GC; this does not measure native memory or exclude leaks under real use. Upstream [issue #38](https://github.com/Jack251970/Flow.Launcher.Plugin.ClipboardPlus/issues/38) reports high memory use, but its cause has not been established here.

## Investigated: image encoding reuse (not implemented)

[Windows run 41464497047](https://github.com/JdotCarver/Flow.Launcher.Plugin.ClipboardPlus/actions/runs/41464497047) compared current image construction plus asset serialization with a prototype that encodes PNG/base64 once and reuses it for the same MD5 and asset payload. Five repetitions followed warmup; the inputs were generated opaque solid or random-noise bitmaps. The prototype checks encoded bytes, hash, decoded pixels and the existing image-encryption behavior. It is not a production-method A/B and excludes the clipboard monitor and database queue.

| Scenario | Current median | Reuse median | Current / reuse managed allocation |
| --- | ---: | ---: | ---: |
| 512×512 solid, hash + asset | 5.79 ms | 2.91 ms | 0.03 / 0.02 MiB |
| 512×512 noise, hash + asset | 45.11 ms | 22.75 ms | 13.24 / 7.20 MiB |
| 1024×1024 noise, hash + asset | 177.58 ms | 90.14 ms | 53.13 / 28.90 MiB |
| 512×512 noise, hash + cache file | 88.74 ms | 25.63 ms | 8.09 / 8.08 MiB |

The cache-file comparison verifies byte-identical PNGs but performs different file operations and is especially noisy (current range 43.33–95.28 ms). Reusing base64 requires decoding it back to bytes in that candidate; it did not reduce managed allocation in this case. Long-lived encoded caches, asynchronous queue retention, native memory, realistic screenshot distributions and simultaneous cache plus persistence were not measured. The current image processing path performs PNG encoding for construction, asset persistence and optional cache writing separately. Any implementation must preserve duplicate/hash semantics, database compatibility, cross-thread image lifetime and error behavior, and avoid unbounded retention.

## Security-relevant observation

`ClipboardData.EncryptData` explicitly excludes images. Enabling the encryption setting therefore does not encrypt saved image assets. This is existing behavior, not introduced by the bulk-insert change; it should be reviewed separately rather than silently changed in a performance PR.

## Open validation gaps

No personal data was used. Real-world UI latency, real clipboard events, actual Flow Launcher process/native memory, Windows clipboard-history synchronization, long-running retention, encryption/image bulk performance, and varied datasets remain unmeasured. Per-scenario methodology and limitations are documented in `tools/performance-baseline/README.md`.
