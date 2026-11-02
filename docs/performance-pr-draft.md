<!-- performance-pr-draft.md -->
# 🌟 Issue this Pull Request addresses: Performance

Clipboard histories can contain many records and large images. This work investigates selected persistence and pinning paths and removes repeated work where measurements on synthetic data showed an opportunity. It does not establish the cause of the upstream memory report or claim to resolve it.

Measurements were made on a self-hosted Windows runner using generated records and images and temporary SQLite databases. No personal clipboard history, settings or existing database was used. Each production-path A/B used one warmup and five measured samples per variant, run sequentially. Numbers are scenario-specific medians on one machine, not guarantees of end-user latency.

## The Identified Bottlenecks

### 1. Bulk database insertion

Inserting a batch committed individual SQLite writes separately. A single transaction for the batch reduces transaction overhead while keeping each asset and record together; a failed batch rolls back.

### 2. Captured-image persistence

For a saved, frozen captured image, the PNG/base64 encoding used to compute its hash was generated again for persistence. The capture path can pass its already-computed encoding to the queued database write instead.

### 3. Image pinning

Pinning clones the record. For an image already frozen when its hash was calculated, reconstructing that clone needlessly re-encoded and rehashed immutable image data.

---

## 🧰 Benchmark Results

Synthetic production-path A/B on the same self-hosted Windows runner; before and after variants ran sequentially. Timings are medians of five measured samples after warmup. See the linked runs for the full measurements and the caveats below.

### Per-Component Breakdown

| Component | Before | After (optimized) | Scenario-specific speedup |
| :--- | ---: | ---: | ---: |
| **500-record bulk insertion** | 4,605.82 ms | **15.97 ms** | **~288×** |
| **Saved frozen image capture and persistence** | 200.55 ms | **117.68 ms** | **~1.70×** (~41% lower) |
| **Saved frozen image pin path** | 100.46 ms | **4.01 ms** | **~25.1×** |

### Scenario Comparisons

| Scenario and measured scope | Before (min / median / max) | After (min / median / max) |
| :--- | :--- | :--- |
| **500 synthetic text records:** actual bulk method; database setup and verification outside timer | 3,716.46 / 4,605.82 / 4,757.86 ms | 14.38 / **15.97** / 17.33 ms |
| **1024×1024 opaque noisy image:** synthetic event invoking the actual capture handler; record lock, database queue completion and forced GC included | 200.10 / 200.55 / 205.86 ms | 114.60 / **117.68** / 123.44 ms |
| **1024×1024 opaque noisy image:** actual saved-image pin method; record lock, database queue completion and forced GC included | 99.12 / 100.46 / 101.57 ms | 3.93 / **4.01** / 4.55 ms |

For pinning, median process-wide managed allocation was 28.90 MiB before and less than 0.01 MiB after. This does not measure native allocation. Image scenarios exclude real Windows clipboard extraction, UI and Windows clipboard-history integration; capture also excludes database setup and optional cache-file writing. Images are synthetic noisy bitmaps, not a representative screenshot distribution. Sequential ordering and machine load may affect results.

Runs: [bulk insertion](https://github.com/JdotCarver/Flow.Launcher.Plugin.ClipboardPlus/actions/runs/41462238701), [image capture](https://github.com/JdotCarver/Flow.Launcher.Plugin.ClipboardPlus/actions/runs/41465933231), [image pinning and full build/tests](https://github.com/JdotCarver/Flow.Launcher.Plugin.ClipboardPlus/actions/runs/41469695763). Raw workflow setup logs may contain runner metadata and may expire according to repository retention settings.

## ✨ Features

No user-facing features or settings are added. Deduplication and relative time are separate work and are not included.

## 🔧 Fixes

- **Transactional bulk insertion:** insert assets and records in a single transaction; roll back the batch if it fails, preserving existing duplicate-ID handling.
- **Captured-image encoding reuse:** pass the already-computed encoding to persistence only for frozen captured images. Mutable images retain the existing serialization path; the encoding is not stored on the history record, though queued writes may temporarily retain it.
- **Frozen-image pinning:** reuse the existing hash only when the image was already frozen when originally hashed. Mutable images, including images frozen after mutation, are rehashed. `Clone()` with a new identity remains unchanged.
- **Regression tests:** cover bulk rollback and recovery, duplicate IDs, encrypted text and images, image payload/pixel round trips, queued writes, mutable-image fallback, pin state and metadata.

**Existing security limitation:** image assets are not encrypted even when the encryption setting is enabled. This PR does not introduce or change that behavior; it merits separate review.

**Validation:** the full solution build and tests passed on the local Windows runner on the development tree. [PR agent: replace this sentence with the exact final clean-patch validation outcome before opening the draft; do not imply it has already been run.]

# Hope you like it!
_Thank you for your consideration._
### 🙏
