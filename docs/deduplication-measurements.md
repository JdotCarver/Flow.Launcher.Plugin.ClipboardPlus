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
