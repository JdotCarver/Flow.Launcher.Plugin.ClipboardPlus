---
# SKILL.md
name: handoff
description: Prepare an evidence-based, user-approved handoff for a new agent session to assemble a clean pull request from selected work while preserving the development branch and its tooling. Use when the user asks for an agent handoff, PR handoff, or instructions for a separate PR-preparation session.
version: "1.0.0"
---

# Agent handoff for a clean pull request

Use this skill to prepare a **handoff document**, not to assemble the PR yourself. A new session uses the document to review and transfer the approved work onto its own permitted branch. Keep the current development branch and its tooling intact. The user's `main` or `master` is treated as an upstream copy: do not modify or push to it. Respect the active session's branch, Git, privacy, and approval rules even if this document suggests a workflow that is not permitted there.

## 1. Investigate the source and destination

Read the user's request and project instructions, then inspect the current branch and working tree, repository/remotes, relevant history and diffs, and available documentation and test evidence. Use Git and GitHub tools as appropriate. Do not inspect unfinished work belonging to another agent unless the user authorizes it. Do not modify the working tree, switch branches, create a branch, push, or open a PR as part of this investigation.

Establish and record, with full commit SHAs where useful:

- The source repository, branch and commit; whether there are uncommitted changes; and the proposed PR destination and base. Check which base is actually upstream-equivalent. Do not assume that the current session's base, fork `master`, upstream `master`, or GitHub fork relationship are interchangeable. Check the current facts, not a previous handoff's facts.
- Candidate commits and their actual changed files. Identify production changes, associated tests, necessary dependencies, and commits that mix relevant and unrelated changes. Check whether the selected commits can be replayed in order against the intended base; if not, describe a safe alternative without silently expanding scope. Do not treat cherry-picking as guaranteed to succeed.
- Session-specific tooling, generated artifacts, local runner configuration, unrelated features and other changes proposed for exclusion. Check the diff against the actual intended base, including tooling that predates the current session but is absent from upstream.
- Relevant reports, design documents, benchmark methodology and test results; identify the exact code or tree each validates and known gaps. Do not re-run tests just because they exist, or imply a development-branch run validates a different final patch.

If access or evidence is missing, say what is unknown and include a verification step. If the intended PR destination or ownership is unclear, ask rather than guess. Never expose secrets, user data, sensitive paths or private logs. Link only to material the intended reader can access; warn if Actions logs are subject to retention or contain machine metadata.

## 2. Propose the handoff and obtain approval

Before writing the handoff, present the candidate scope: intended base/destination, exact commits or changes and tests to include, dependencies and exclusions, documents/evidence to reference, validation already completed versus what remains, and what the new agent is authorized to do. Identify ambiguities and trade-offs explicitly.

Use `ask_user` to ask the user to confirm or revise **what to include and exclude**. The first option should be the recommended scope. Use custom responses for changes. Ask additional questions if destination, dirty changes, mixed commits, or authority to create a draft PR remain unresolved. If `ask_user` is unavailable, ask in chat and wait. Do not write or commit the handoff until the user approves the scope. If their answer changes the proposal materially, revise it and obtain confirmation again.

A request for a handoff does not by itself authorize the current agent to make a PR, switch branches, cherry-pick, merge, or change the underlying feature. Distinguish instructions for the **new** agent from actions performed now. Do not include a PR body or prescribe a draft format unless the user requests one.

## 3. Write the handoff document

After approval, create a concise Markdown file in the workspace (normally `docs/<topic>-pr-agent-handoff.md`, or another user-specified location). Start the file with its filename in an HTML comment. If the target already exists, do not overwrite unrelated work; choose a distinct name or ask. The handoff must stand alone for a fresh session, without relying on invisible conversation memory. Include:

1. **Objective and deliverables:** selected scope and whether the new agent should only prepare a branch or also create a draft PR. Do not assume permission to create or merge a PR.
2. **Source and base:** repository, source branch and pinned source SHA, confirmed destination and base, and how to fetch the source without switching the new session away from its assigned branch. Include the expected state of dirty or uncommitted work, if any; never pretend uncommitted work is fetchable.
3. **Assembly:** exact commits, logical order, purpose, tests, prerequisites and exclusions. Give commands only if they are appropriate for the new session's permitted branch and the verified repository state. Tell the new agent to inspect the final diff against the actual PR base, preserve atomic feature-and-test commits, and follow its own branch rules. If a commit mixes scope, explain the required selective handling rather than blindly replaying it.
4. **Evidence and references:** usable links to reports and methodology at a pinned source commit, not just relative paths that will be missing on the clean branch. Include meaningful test/benchmark run links, what was measured, limitations and any known risk or security caveat. Separate evidence from assumptions; do not invent measurements. Verify the link's repository, path and commit, and flag access or retention limitations. References are context, not automatic PR inclusions.
5. **Review and verification:** review the clean patch for behavior, test coverage and compatibility; identify previously completed tests and whether the exact final patch has been tested. Give a safe validation approach consistent with the user's local/cloud preferences and privacy requirements. If runner tooling is excluded, describe how to validate without committing it, and require an explicit limitation if exact-patch validation cannot be arranged. Any new code change requires appropriate discussion/approval and renewed relevant validation.
6. **Finish:** check the final diff and Git status, follow the new session's allowed push/PR rules, and report blockers and actual outcomes. Ask for user direction if the destination, access or safe validation is unresolved. Never instruct the agent to modify the upstream copy or to bypass session branch restrictions.

Use the project's commit conventions if the user wants the handoff committed. Otherwise do not assume a commit or push is wanted. Present the completed file and give a short copyable prompt telling the user how to start the new session and where to find the handoff. Since the new session may start from a clean base that does not contain this file, include a verified source-branch/commit URL or a safe fetch-and-read instruction; a workspace-relative link alone is not sufficient.

## Guardrails

- Do not inspect personal data to make the handoff more concrete. Use synthetic data where testing requires inputs, and respect the user's runner and network constraints.
- Do not transfer session-only tools into the PR unless the user explicitly approves them as part of scope. Do not remove those tools from the development branch merely to make a PR clean.
- Do not assert that `main` or `master` is safe to push to, that the repositories are a GitHub fork network, that a cross-repository PR is possible, or that referenced logs are permanent without checking.
- Do not silently include other agents' unfinished work, make unrelated fixes, fabricate results, or treat a draft PR as authorization to merge.
