<!-- performance-pr-agent-handoff.md -->
# Performance PR agent handoff

## Objective

Prepare a clean branch containing only the three validated performance changes and their regression tests, review it as one patch, and create a **draft** pull request using [the PR draft](performance-pr-draft.md) as a starting point. The deduplication and relative-time implementation is being developed by another agent and is not part of this PR. Do not inspect or interfere with that agent's unfinished work.

## Branch and PR destination

The source session is `arena/9261f4f4-flow-launcher-plugin-clipboard` in `JdotCarver/Flow.Launcher.Plugin.ClipboardPlus`. Start a **new Arena session based on the target's clean `master`**, and work only on the branch assigned to that new session. Do not change, rename, create, or push another branch if the new session's instructions prohibit it. Preserve the source branch: it is the working home for the benchmarks, Windows runner bridge, reports and feature design.

As checked when this handoff was written, both `JdotCarver/Flow.Launcher.Plugin.ClipboardPlus:master` and `Jack251970/Flow.Launcher.Plugin.ClipboardPlus:master` pointed at `6abf21e6af3036f4120470183b4aa229cd33b994`. The session's base `697a20b3e5e810fdd10be3916c0b1478abe1439d` adds shared agent tooling on top of that commit, so do **not** use it as the PR base if the intention is to exclude that tooling. The user's repository is not currently reported by GitHub as a registered fork (`parent: null`). **Confirm the intended PR destination and current base/branch relationship before opening a PR**; do not assume that GitHub will allow a cross-repository PR. If needed, create the draft against the user's repository only after confirming that is desired, or explain the blocker and ask the user.

## Assemble the clean patch

Fetch the source branch without switching branches, then apply the following commits in order to the new session's branch (for example, `git fetch origin arena/9261f4f4-flow-launcher-plugin-clipboard`, then `git cherry-pick 31b2781 ab1f880 cd7f232`). Confirm the commits and ancestry before applying; resolve any conflicts carefully and stop for guidance if the base has materially changed.

1. `31b2781` — transactional bulk SQLite insertion, plus `BulkInsertTests.cs`.
2. `ab1f880` — reuse frozen captured-image encoding for queued persistence, plus `ImagePersistenceTests.cs` and the necessary test visibility declaration.
3. `cd7f232` — reuse the hash of an initially frozen image when pinning, plus `ImagePinTests.cs`.

These are the feature-and-test commits, not the intervening benchmark or Actions request commits. If replay requires a genuine production/test dependency, include the minimum necessary and explain it; do not blindly carry over the whole development branch. Preserve atomic feature-and-test commits and use Conventional Commits in Title Case. Review the final diff **against the actual PR base**, not against `697a20b`.

Expected functional diff is limited to the plugin/core production files, the three regression test files, and core `AssemblyInfo.cs` for test access. Do not include session-specific runner tooling, runner request changes, benchmark harness, generated artifacts, feature design, unrelated docs, or vendored matcher sources. Keep the performance evidence available in the PR body rather than copying session infrastructure into the PR. Do not change the existing behavior that image data is excluded from encryption.

## Review and validation

- Review all three changes together for compatibility, correctness and test coverage, including transaction rollback, duplicate IDs, saved and unsaved images, frozen versus mutable images, asynchronous write lifetime, pin/unpin metadata, and existing Windows-history behavior. Note any gap rather than claiming it was tested. Discuss any proposed production change with the user before implementing it.
- Existing Windows validation already passed: [41462238701](https://github.com/JdotCarver/Flow.Launcher.Plugin.ClipboardPlus/actions/runs/41462238701) for the bulk A/B, [41465933231](https://github.com/JdotCarver/Flow.Launcher.Plugin.ClipboardPlus/actions/runs/41465933231) for capture A/B, and [41469695763](https://github.com/JdotCarver/Flow.Launcher.Plugin.ClipboardPlus/actions/runs/41469695763) for the full solution build/tests with all three changes and pin A/B. These validate the development tree, not automatically the final clean branch. The earlier performance measurements need not be rerun merely because they exist.
- Validate the **final cleaned production/test patch** on the user's local Windows runner. If the clean branch lacks the runner bridge, coordinate a temporary, external or development-branch test setup that builds the exact cleaned source and tests, and verify its contents against the final PR patch. Do not commit the bridge or benchmark harness to the PR. The existing bridge requires a clean committed tree and creates a request commit; account for this rather than silently including that request in the PR. If exact final-patch validation cannot be arranged, report the limitation and ask the user before calling the PR validated. Obtain explicit permission before using a cloud runner.
- Use only synthetic data and temporary databases. Do not access or expose the user's real clipboard, existing database, settings, personal paths or secrets. Raw GitHub Actions setup logs may include runner/machine metadata and are subject to the user's retention policy; the user accepted that limitation. Never claim Windows-history integration, real UI latency, native-memory usage, or end-user speedups were measured when they were not.
- Confirm `git diff --check`, final diff, tests and clean status; push only the new session's assigned branch. Create a **draft** PR, not a ready-for-review PR or a merge. Adapt the accompanying draft to actual review findings, final test results and the confirmed destination. If source changes during preparation, rerun the relevant final validation and update claims.

## Evidence and context

Source report: `docs/optimization-report.md` on the source branch. Synthetic harness methodology: `tools/performance-baseline/README.md` on that branch. These are reference material, not automatic PR inclusions. Measurements use five samples after warmup and sequential before/after variants; values are medians for synthetic scenarios on one local Windows machine, not universal guarantees. The user's preferred PR structure is reproduced in the accompanying draft. No visual change was implemented, so do not fabricate an image/video.
