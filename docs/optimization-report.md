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

## Implemented: transient captured-image encoding reuse

**Change:** for frozen images arriving through the plugin clipboard-monitor event with image persistence enabled, the PNG/base64 representation already computed for the existing hash is passed to the queued database write. It is not stored on the history record. Unfrozen images, unsaved images, Windows-history paths and optional image-cache writes retain their existing behavior. The queued operation can temporarily retain the representation until it runs; queue growth and long-term/native memory were not measured. Commit: `ab1f880`.

**Production-path A/B:** [Windows run 41465933231](https://github.com/JdotCarver/Flow.Launcher.Plugin.ClipboardPlus/actions/runs/41465933231), with five samples after warmup per variant. A synthetic monitor invokes the real event handler with an opaque noisy 1024×1024 frozen bitmap, `KeepImages` on and cache off. Each sample uses a fresh temporary SQLite database, waits for the record and database locks and verifies stored PNG bytes against the synthetic image. The before snapshot uses the three pre-change production files from `6a8203d`; the rest of the source and harness are identical. Variants ran sequentially.

| Variant | Minimum | Median | Maximum |
| --- | ---: | ---: | ---: |
| Before | 200.10 ms | 200.55 ms | 205.86 ms |
| After | 114.60 ms | 117.68 ms | 123.44 ms |

The median fell by about 41% in this scenario. Windows clipboard extraction, UI work, database setup, image-cache writing, other sizes and realistic screenshot distributions are excluded. The solution build and tests passed on the local Windows runner; new tests cover matching legacy hash and payload, pixel round-trip, image encryption behavior, an asynchronously queued write, failure recovery and the unfrozen-image fallback. The test coverage does not establish a bound on queued-memory retention.

## Image encoding prototype evidence

[Windows run 41464497047](https://github.com/JdotCarver/Flow.Launcher.Plugin.ClipboardPlus/actions/runs/41464497047) compared current image construction plus asset serialization with a prototype that encodes PNG/base64 once and reuses it for the same MD5 and asset payload. Five repetitions followed warmup; the inputs were generated opaque solid or random-noise bitmaps. The prototype checks encoded bytes, hash, decoded pixels and the existing image-encryption behavior. It is not a production-method A/B and excludes the clipboard monitor and database queue.

| Scenario | Current median | Reuse median | Current / reuse managed allocation |
| --- | ---: | ---: | ---: |
| 512×512 solid, hash + asset | 5.79 ms | 2.91 ms | 0.03 / 0.02 MiB |
| 512×512 noise, hash + asset | 45.11 ms | 22.75 ms | 13.24 / 7.20 MiB |
| 1024×1024 noise, hash + asset | 177.58 ms | 90.14 ms | 53.13 / 28.90 MiB |
| 512×512 noise, hash + cache file | 88.74 ms | 25.63 ms | 8.09 / 8.08 MiB |

The cache-file comparison verifies byte-identical PNGs but performs different file operations and is especially noisy (current range 43.33–95.28 ms). Reusing base64 requires decoding it back to bytes in that candidate; it did not reduce managed allocation in this case. Long-lived encoded caches, asynchronous queue retention, native memory, realistic screenshot distributions and simultaneous cache plus persistence were not measured. The current image processing path performs PNG encoding for construction, asset persistence and optional cache writing separately. The implemented persistence path preserves the representation and avoids storing an encoded copy in history; queue backlog and cache-file reuse remain open questions.

## Investigated: query allocation and traversal (no production change)

[Windows run 41467474061](https://github.com/JdotCarver/Flow.Launcher.Plugin.ClipboardPlus/actions/runs/41467474061) used generated plain-text histories, the plugin's actual result factory and the pinned Flow Launcher v2.1.4 matcher (regular precision, no alphabet translation). Matcher-only measurements call the matcher directly; result measurements include the synthetic host dispatch. Five measurements per traversal variant followed warmups and were alternated. Result IDs, scores and selected display fields were checked for equivalence; ordering, action execution, cancellation and preview behavior were not tested by this comparison.

| Component scenario | Serial median | Parallel median | Serial / parallel allocation |
| --- | ---: | ---: | ---: |
| 100 short records, all match | 0.19 ms | 0.10 ms | 0.21 / 0.26 MiB |
| 333 short records, all match | 0.64 ms | 0.30 ms | 0.71 / 0.84 MiB |
| 10,000 short records, all match | 33.70 ms | 8.03 ms | 21.32 / 24.33 MiB |
| 1,000 long records, all match | 5.45 ms | 0.85 ms | 2.78 / 2.96 MiB |

For 10,000 all-match records, matcher-only allocated 13.73 MiB (median); full serial result production allocated 21.32 MiB. This is an approximate component breakdown, not a controlled attribution of every byte: the host and traversal differ. Parallel traversal traded more managed allocation for lower median latency on this particular machine and dataset. Small-history differences are fractions of a millisecond and observed maxima include outliers; this is insufficient evidence to change the threshold of 333. The parallel experiment does not reproduce the complete `QueryAsync` locking or task-wrapper semantics.

The same run's actual `QueryAsync` all-match scenario at 10,000 records had a 20.16 ms median and 23.82 MiB median allocation; a deliberately overlapping capture-like record-lock wait had an 11.47 ms median. Reducing lock scope requires a safe ownership/lifetime design for mutable or disposed records and stale results, not merely copying the list. Real Flow settings, scheduling, actual clipboard events and UI latency remain unmeasured.

## Implemented: image pin/unpin hash reuse

[Windows run 41468518387](https://github.com/JdotCarver/Flow.Launcher.Plugin.ClipboardPlus/actions/runs/41468518387) measured the existing `ClipboardDataPair.TogglePinned` method on generated opaque frozen bitmaps. Five measured toggles followed a warmup; the test checked pin state, hash, ID and underlying data identity. It excludes the plugin's lock, database updates, forced GC, UI and Windows history.

| Synthetic input | Median | Median managed allocation |
| --- | ---: | ---: |
| 512×512 solid image | 2.85 ms | 0.02 MiB |
| 512×512 noisy image | 23.90 ms | 7.21 MiB |
| 1024×1024 noisy image | 91.78 ms | 28.90 MiB |
| Text control | under 0.01 ms | under 0.01 MiB |

**Change:** pin/unpin clones reuse the existing hash only if the image was frozen at the time it was originally hashed. Images frozen after possible mutation are rehashed, and `Clone()` with a new identity remains unchanged. The optimization does not retain encoded images. Commit: `cd7f232`.

**Production-path A/B:** [Windows run 41469695763](https://github.com/JdotCarver/Flow.Launcher.Plugin.ClipboardPlus/actions/runs/41469695763) used the real plugin pin method, a synthetic saved frozen 1024×1024 noisy image, and a temporary database. Five samples followed one warmup; variants were run sequentially and the before copy used pre-change `ClipboardData.cs` from `537ba8c`. The measurement includes the record lock, database queue completion and forced GC, but excludes UI and Windows clipboard history.

| Variant | Minimum | Median | Maximum | Median managed allocation |
| --- | ---: | ---: | ---: | ---: |
| Before | 99.12 ms | 100.46 ms | 101.57 ms | 28.90 MiB |
| After | 3.93 ms | 4.01 ms | 4.55 ms | under 0.01 MiB |

The solution build and tests passed on the local Windows runner. New tests check pin/unpin state and record metadata, hash identity for initially frozen images, rehashing after mutation and subsequent freezing, and `Clone()` with a new ID. This does not validate UI or Windows-history integration, native allocations, or real-world image distributions.

## Deduplication and relative time (design only)

Capture currently suppresses only a consecutive duplicate using `DataEquals` (data type and MD5); later repeats receive different record IDs. Import checks record identity rather than content. Full-history deduplication could reduce query work and persisted storage in proportion to the duplicates actually removed, but a scan and duplicate checking have costs and no performance gain is established yet. Content hashes alone are not proof of exact equality; rich text and files have representation/ordering semantics that require agreement. Pinned records, persistence, metadata, previews and Windows clipboard history make deletion and merging potentially destructive. An agreed design is recorded in [deduplication and relative time](deduplication-relative-time-design.md); neither feature has been implemented, and performance remains to be measured.

## Security-relevant observation

`ClipboardData.EncryptData` explicitly excludes images. Enabling the encryption setting therefore does not encrypt saved image assets. This is existing behavior, not introduced by the bulk-insert change; it should be reviewed separately rather than silently changed in a performance PR.

## Open validation gaps

No personal data was used. Real-world UI latency, real clipboard events, actual Flow Launcher process/native memory, Windows clipboard-history synchronization, long-running retention, encryption/image bulk performance, and varied datasets remain unmeasured. Per-scenario methodology and limitations are documented in `tools/performance-baseline/README.md`.

## Investigated, not implemented: forced collection and image startup

[Local Windows run 41712914425](https://github.com/JdotCarver/Flow.Launcher.Plugin.ClipboardPlus/actions/runs/41712914425) passed the solution build, tests and synthetic benchmarks. Both comparisons ran sequentially, one warmup and five measured samples per variant. No production source was changed. These are component experiments, not end-to-end application measurements or evidence about other proposed optimizations.

### Forced full collections

The candidate was an otherwise identical scratch build whose `GarbageCollect` method was replaced with a no-op. The benchmark invokes the real synthetic-monitor capture handler with text saving enabled, and the real pin method, waiting for queued database work. It starts with 1,001 synthetic records and a touched 64 MiB live managed array. Each sample averages 20 captures and 10 pin toggles; records and persisted pin state are checked. The database is isolated and in memory. The samples are repeated batches in one process per variant, not independent launches or individual-event tail latencies.

| Metric | Forced GC, min / median / max | Scratch no-op, min / median / max |
| --- | ---: | ---: |
| Capture and queued save, ms per operation | 0.806 / 0.846 / 0.875 | 0.064 / 0.066 / 0.073 |
| Pin toggle and queued save, ms per operation | 0.710 / 0.743 / 0.782 | 0.024 / 0.025 / 0.041 |

Final managed / working-set / private memory in MiB was 65.21 / 125.02 / 92.18 with forced GC and 67.93 / 124.07 / 85.87 without. These are single final snapshots, not peaks or a long-running memory/leak test; the two processes may collect and reserve memory at different times. The synthetic median improvements of about 0.78 ms per capture and 0.72 ms per pin warrant investigating collection placement, but do not establish user-perceived gains, safety, memory behavior under image-heavy workloads, or that every forced collection can be removed.

### Eager image materialization

The current component uses `GetAllRecordsAsync(true)` against a temporary SQLite database containing 24 independently generated opaque noisy 512×512 images. The comparison queries record metadata without loading asset payloads, then separately fetches and materializes one image. It is a deliberately narrow component prototype, **not** a functional replacement for plugin startup: it does not implement the required lazy record, UI, lifetime, concurrency, encryption, pin/cleanup, or error-handling behavior. The harness checks record count and the first image's dimensions and synthetic hash. It does not compare the entire UI, history ordering, or full startup sequence.

| Metric | Eager, min / median / max | Metadata prototype, min / median / max |
| --- | ---: | ---: |
| Database load/materialization, ms | 781.52 / 791.24 / 826.29 | 0.25 / 0.27 / 0.32 |
| First image access after load, ms | 0.00 / 0.00 / 0.00 | 28.68 / 29.91 / 30.37 |

Median managed allocation during the load was 271.65 MiB eager versus 0.01 MiB metadata-only. Median working-set and private-memory changes during load were +77.84 / +78.61 MiB eager and -0.59 / -0.59 MiB for the prototype; negative deltas reflect process/OS accounting noise, not negative memory consumption. These are load-time deltas, not retained heap or peak memory. The prototype shifts about 30 ms to the first image in this synthetic workload, and further images would add further deferred work; it does not eliminate that work. Image size, compressibility, count, cache and real startup behavior may change the result substantially. The magnitude warrants a separate design discussion before considering implementation.

Image payloads are currently excluded from encryption even when encryption is enabled. This security concern is unrelated to these experiments and was not changed.
