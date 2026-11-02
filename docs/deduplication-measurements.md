<!-- deduplication-measurements.md -->
# Deduplication measurements

## Local Windows run 41498753934

The local self-hosted runner built and tested the solution and ran the synthetic
Release benchmark on 2026-11-02. No real clipboard, user database, settings or
cache was read. The [run](https://github.com/JdotCarver/Flow.Launcher.Plugin.ClipboardPlus/actions/runs/41498753934)
contains the exported sanitized measurements.

The planner measurements are wall-clock milliseconds: three measured repetitions
after one warmup. Inputs and hashes are constructed before timing. Text records
contain 512 repeated characters plus a synthetic key, with 500 distinct contents.
The image sample contains four identical, frozen 1024×1024 noisy synthetic
bitmaps. Best effort skips image verification; thorough serializes and compares
image representations. The expected candidate count is checked on every run.

| Scenario | Min / median / max (ms) |
| --- | ---: |
| 1,000 text, best effort | 0.74 / 0.77 / 3.83 |
| 1,000 text, thorough | 1.01 / 1.01 / 1.09 |
| 10,000 text, best effort | 5.19 / 5.70 / 13.50 |
| 10,000 text, thorough | 4.69 / 4.72 / 6.90 |
| Four noisy images, best effort | 0.01 / 0.01 / 0.01 |
| Four noisy images, thorough | 557.51 / 569.42 / 569.86 |

The existing independent synthetic plugin scenarios measured median 8.58 ms
for an empty 10,000-record query and 12.35 ms capture lock wait during a query.
These are baseline figures, **not** A/B measurements with cleanup active.

This is a planner-only comparison, not an end-to-end cleanup benchmark. It does
not include snapshot creation, database transactions, cache scans, Windows
history, revalidation, memory allocations or capture/query contention while
cleanup is running. The small sample and specific synthetic content cannot set
safe limits for real histories. In particular, the image results support keeping
thorough verification off responsiveness-critical paths; they do not establish
a size threshold. No best-effort numeric limits have been selected. Measure the
integrated capture, query and cleanup paths and review those results before
finalizing them.

## Text-size follow-up 41502142937

A second [local run](https://github.com/JdotCarver/Flow.Launcher.Plugin.ClipboardPlus/actions/runs/41502142937)
measured 20 synthetic text records in ten duplicate pairs. Each pair contains
separately allocated strings, so the comparison cannot succeed by reference
identity alone. As above, input construction and hashing are outside timing,
with three measured repetitions after one warmup.

| Approximate characters per record | Min / median / max (ms) |
| --- | ---: |
| 4,096 | 0.02 / 0.02 / 0.02 |
| 65,536 | 0.06 / 0.06 / 0.08 |
| 1,048,576 | 1.07 / 1.09 / 1.20 |

These measurements do not include database verification or concurrent capture
and query work, so they are not sufficient to finalize a best-effort threshold.
The same run measured four noisy images at 569.41 ms median for thorough
verification, reinforcing that this work must not hold a responsiveness-critical
lock.

## Verified in-memory database transactions 41503177038

A [local run](https://github.com/JdotCarver/Flow.Launcher.Plugin.ClipboardPlus/actions/runs/41503177038)
measured the real saved-record verification/deletion method against isolated
in-memory SQLite. Each sample creates its own database and populates synthetic,
unencrypted text before timing, then times sequential transactions. Each
transaction re-reads and compares both records and removes the duplicate and
asset. Three measured repetitions follow one warmup.

| Sample | Total min / median / max (ms) | Median per pair (ms) |
| --- | ---: | ---: |
| 50 pairs, 4,096 characters | 4.21 / 4.46 / 6.81 | 0.09 |
| 20 pairs, 65,536 characters | 6.41 / 6.90 / 9.72 | 0.35 |
| 4 pairs, 1,048,576 characters | 22.43 / 23.98 / 24.22 | 6.00 |

These totals are not tail latency or a real disk benchmark. They omit encryption,
snapshotting, images, cache, Windows history, and concurrent query/capture;
they also do not measure lock hold times in an integrated cleanup. Do not treat
them as proof that any threshold is safe on other machines.

## Integrated synthetic contention, 2026-11-02

Local self-hosted Windows run [41522950466](https://github.com/JdotCarver/Flow.Launcher.Plugin.ClipboardPlus/actions/runs/41522950466) built and passed the tests and benchmark. A synthetic plugin instance used private in-memory SQLite, 250 saved duplicate text pairs of 4,094 characters, four 1024×1024 noisy synthetic images, and 20 concurrent synthetic captures and no-match queries. The monitor was not started; no real clipboard, user database, settings, or cache was accessed. Capture includes the existing forced-GC path. All 20 capture/query iterations overlapped cleanup, but these are one-shot measurements, not a statistical A/B study.

| Mode | Total with captures/queries | Capture median / maximum | Query median / maximum | Removed / skipped |
| --- | ---: | ---: | ---: | ---: |
| Best effort (4,096-character cap, no images) | 88.15 ms | 1.97 / 2.59 ms | 0.85 / 1.40 ms | 250 / 3 |
| Thorough | 3,196.82 ms | 1.88 / 4.08 ms | 0.86 / 1.46 ms | 253 / 0 |

After reviewing these results, the maintainer approved retaining the conservative 4,096-unit best-effort limit and skipping images. This is not a universal responsiveness guarantee. They exclude real disk and encryption, different hardware and contention patterns; capture/query maxima are from only 20 samples, and the two modes do not form randomized repeated A/B trials. The thorough run demonstrates why expensive image verification remains background work. Windows-history behavior was simulated, not tested against the user's Windows history.

## Review follow-up: opt-out cache capture and contention

In local self-hosted Windows run [41530121545](https://github.com/JdotCarver/Flow.Launcher.Plugin.ClipboardPlus/actions/runs/41530121545), a synthetic 1024×1024 noisy image captured with automatic cleanup disabled took 182.12 ms initially and 180.55 ms on a filename collision. The same synthetic image's individual safe and legacy cache-file writes took 84.41 ms and 81.62 ms respectively. Full capture includes image hashing and the asynchronous provenance-save dispatch; the isolated writes do not. This is a tiny, non-randomized sample, uses a synthetic settings API and temporary directory, and does not measure real settings I/O. It is not evidence of a general performance difference. The maintainer chose to keep non-overwriting files and provenance even when automatic cleanup is off, to support safe manual cleanup.

With the same synthetic 250-pair contention scenario as above, best effort took 135.66 ms overall, with capture median/max 2.06/5.10 ms and query median/max 0.99/14.37 ms; thorough took 3,263.30 ms, with capture median/max 1.92/5.06 ms and query median/max 1.15/1.58 ms. The 20 queries and captures overlapped both runs. These remain one-shot, in-memory measurements, not a real-disk or encrypted-history benchmark.

## Saved image candidate selection and bounded overflow

Local self-hosted Windows run [41536361628](https://github.com/JdotCarver/Flow.Launcher.Plugin.ClipboardPlus/actions/runs/41536361628) used private in-memory SQLite with synthetic 256×256 noisy images. Each scenario ran once for warm-up and three measured times. This measures metadata selection and candidate reconstruction, **not** full duplicate verification or cleanup; setup and image insertion are excluded. A concurrent cache-reference query approximates database-queue delay, not disk writes or capture latency.

| Unique images | Scenario | Reconstructed | Selection median / max | Concurrent query median / max |
| --- | --- | ---: | ---: | ---: |
| 40 | All records loaded | 0 | 0.10 / 0.19 ms | 0.09 / 0.09 ms |
| 40 | Two duplicates unloaded | 2 | 13.83 / 14.72 ms | 0.05 / 0.05 ms |
| 160 | All records loaded | 0 | 0.18 / 0.31 ms | 0.11 / 0.16 ms |
| 160 | Eight duplicates unloaded | 8 | 54.18 / 57.15 ms | 0.09 / 0.22 ms |

The synthetic capacity regression used a limit of two, one protected pinned Windows-origin item, and 60 more simulated Windows captures. At the provisional 25-record overflow bound the loaded list remained at 27; once cleanup could verify the snapshot, it trimmed to two while retaining the pinned item. A separate skipped-image scenario showed unrelated captures did not accumulate beyond the one slot reserved for that unresolved pair. At the bound, oldest-unpinned eviction can lose unsaved history. The maintainer approved retaining the 25-record bound after reviewing these results. These tests do not determine an optimal bound or cover real Windows history, real disk, sustained physical clipboard rates, or pinned-only histories that exceed the limit.
