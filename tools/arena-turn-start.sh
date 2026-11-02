#!/usr/bin/env bash
# tools/arena-turn-start.sh
#
# Arena turn-start sync + harness repair for this repository.
#
# Run at the START of every turn:   bash tools/arena-turn-start.sh
#
# Why this exists
# ---------------
# The Arena harness occasionally restores this checkout from a snapshot instead of
# leaving it in place. Two symptoms show up:
#   1. HEAD is reset to the base commit, so the branch looks "behind".
#   2. The working tree keeps changing after the first glance: restored files land
#      in WAVES, minutes apart, appearing and vanishing.
# Both are handled below. Nothing here touches the remote.
#
# What it does
#   1. Waits out an in-flight snapshot restore (the tree churns while it lands).
#   2. Fetches the session branch and classifies the local state:
#        - clean + in sync          -> nothing to do
#        - no remote branch yet     -> INFO only (first turn; never fetch-fail)
#        - local BEHIND             -> fast-forward (the user pushes between turns)
#        - local AHEAD              -> LEAVE HEAD ALONE (unpushed mid-turn work)
#        - HEAD == base commit      -> RE-CLONE: hard-reset to the remote tip
#        - both sides diverged      -> stop; repair by hand (steps printed inline)
#   3. Settles: requires the status hash to hold steady before trusting the tree.
#   4. Verifies the tree is whole (key files present, manifest.json parses).
#
# HISTORY / READ THIS BEFORE EDITING
# ----------------------------------
# An earlier version of this script classified
# `reset --hard` whenever HEAD != remote, which would have eaten unpushed commits
# made mid-turn. The ancestry checks below are the fix; do not simplify them away.
#
# The first version here also died on a missing tracking ref (`git rev-parse
# FETCH_HEAD` aborts under `set -e`) on the very first turn, before the session
# branch existed on the remote. Hence the explicit "no remote branch yet" branch.
#
# CHICKEN-AND-EGG: after a re-clone this file itself may be missing until the
# snapshot restore settles. If it is absent, repair by hand from the git history.

set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# --- Session identity, resolved rather than hardcoded ------------------------
# This script ships to other repositories, so it cannot name a branch or a
# commit. Both are resolved at run time, and both accept an override for when
# the resolution below cannot be trusted.
#
#   ARENA_BRANCH       the session branch
#   ARENA_BASE_COMMIT  the commit the harness resets HEAD to on a re-clone
#
# Branch resolution is deliberately narrow. Falling back to the current branch
# would make the guard in step 2 a no-op, and a guard that always passes is
# worse than no guard because it looks like it is checking something.

resolve_branch() {
    if [ -n "${ARENA_BRANCH:-}" ]; then
        printf '%s\n' "$ARENA_BRANCH"
        return 0
    fi

    # Exactly one arena/* branch on the remote is unambiguous: that is the
    # session branch.
    local candidates count current
    candidates="$(git ls-remote --heads origin 'refs/heads/arena/*' 2>/dev/null |
        sed 's|.*refs/heads/||')"
    count="$(printf '%s\n' "$candidates" | grep -c . || true)"
    case "${count:-0}" in
        1)
            printf '%s\n' "$candidates"
            return 0
            ;;
        0)
            # No session branch on the remote yet. That is the FIRST TURN of a
            # session, not an ambiguity: the branch has simply not been pushed.
            # Hard-failing here would break the one run this script is needed
            # for, so fall back to whatever is checked out and say so.
            current="$(git rev-parse --abbrev-ref HEAD 2>/dev/null)"
            if [ -n "$current" ] && [ "$current" != "HEAD" ]; then
                echo "== no arena/* branch on origin yet (first turn) — using '$current'" >&2
                printf '%s\n' "$current"
                return 0
            fi
            ;;
        *)
            echo "!! $count arena/* branches on origin; set ARENA_BRANCH to disambiguate" >&2
            ;;
    esac
    printf '%s\n' ""
}

resolve_base_commit() {
    if [ -n "${ARENA_BASE_COMMIT:-}" ]; then
        printf '%s\n' "$ARENA_BASE_COMMIT"
        return 0
    fi

    # The session branch forked from the default branch, and that fork point is
    # what a re-clone resets HEAD to. Empty is acceptable here: BASE_COMMIT only
    # selects the wording of the re-clone message, and both paths reset the same.
    #
    # Ask the remote rather than trusting refs/remotes/origin/HEAD: a clone made
    # with --branch does not set that symbolic ref, so the local copy is absent
    # exactly where this script is most likely to be run from scratch.
    local default_branch
    default_branch="$(git ls-remote --symref origin HEAD 2>/dev/null |
        awk '/^ref:/ {sub("refs/heads/", "", $2); print $2; exit}')"
    if [ -z "$default_branch" ]; then
        default_branch="$(git symbolic-ref --quiet --short refs/remotes/origin/HEAD 2>/dev/null |
            sed 's|^origin/||')"
    fi
    if [ -n "$default_branch" ]; then
        local base
        base="$(git merge-base "$BRANCH" "origin/$default_branch" 2>/dev/null || true)"
        # Degenerate case: if the remote's HEAD is the session branch itself, the
        # merge-base is the branch tip rather than a fork point. Reporting that as
        # the base commit would be worse than reporting nothing.
        if [ -n "$base" ] && [ "$base" != "$(git rev-parse "origin/$BRANCH" 2>/dev/null)" ]; then
            printf '%s\n' "$base"
        fi
    fi
}

BRANCH="$(resolve_branch)"
if [ -z "$BRANCH" ]; then
    echo "!! could not resolve the session branch; set ARENA_BRANCH and re-run" >&2
    exit 1
fi
BASE_COMMIT="$(resolve_base_commit)"

# Files whose absence means "the snapshot restore is incomplete".
# Keep this list current when load-bearing files appear.
KEY_FILES=(
    ".github/workflows/runner-request.yml"
    ".github/runner-request.toml"
    "tools/arena-turn-start.sh"
    "tools/github_actions_runner/machine_label.py"
    "tools/github_actions_runner/request_run.sh"
    "tools/github_actions_runner/watch_run.sh"
    "tools/github_actions_runner/fetch_logs.sh"
    "tools/github_actions_runner/select_runner.py"
    "tools/github_actions_runner/probe.py"
    "tools/github_actions_runner/export_logs.py"
    "tools/github_actions_runner/redact.py"
)

cd "$REPO_ROOT" || exit 1

echo "== arena turn-start ($(date +%H:%M:%S)) =="
echo "   branch=$(git rev-parse --abbrev-ref HEAD)  HEAD=$(git rev-parse --short HEAD)  dirty=$(git status --porcelain | wc -l)"

# --- 1. Is a snapshot restore still landing files? ---------------------------
# Runs BEFORE the branch guard on purpose: while files are still landing, a "dirty"
# reading (or a half-restored branch) is an artifact of the restore, not a real
# reason to refuse to do anything.
status_hash() { git status --porcelain | md5sum; }

before="$(status_hash)"
sleep 3
if [ "$before" != "$(status_hash)" ]; then
    echo "!! working tree still changing (snapshot restore in flight) — waiting 10s"
    sleep 10
fi

# --- 2. Guard: the session branch must be checked out. -----------------------
CURRENT_BRANCH="$(git rev-parse --abbrev-ref HEAD)"
if [ "$CURRENT_BRANCH" != "$BRANCH" ]; then
    echo "!! on '$CURRENT_BRANCH' but this session must run on '$BRANCH'"
    if [ -z "$(git status --porcelain)" ]; then
        echo "   -> switching back to the session branch (tree is clean)"
        git checkout "$BRANCH"
    else
        echo "   -> tree is dirty; NOT switching automatically. Resolve by hand:"
        echo "        git status --porcelain    # inspect"
        echo "        git stash                 # park it, then: git checkout $BRANCH"
        exit 1
    fi
fi

# --- 3. Fetch and classify. NEVER move HEAD when local is ahead. -------------
git fetch origin --quiet 2>/dev/null || echo "!! fetch failed (offline?) — classifying against the last known state"

if ! git ls-remote --exit-code --heads origin "$BRANCH" >/dev/null 2>&1; then
    echo "== no remote branch '$BRANCH' yet (first turn, or the user has not pushed)"
    REMOTE=""
else
    REMOTE="$(git rev-parse --verify "origin/$BRANCH" 2>/dev/null || echo "")"
fi

# A depth-1 harness re-clone truncates BOTH sides at one commit each, so
# `merge-base --is-ancestor` fails in both directions and the classifier below
# would false-alarm "DIVERGED". Unshallow first, then classify against real history.
if [ "$(git rev-parse --is-shallow-repository)" = "true" ]; then
    echo "   shallow clone detected — unshallowing before classifying"
    git fetch --unshallow origin --quiet 2>/dev/null || true
    if [ -n "$REMOTE" ]; then
        REMOTE="$(git rev-parse --verify "origin/$BRANCH" 2>/dev/null || echo "")"
    fi
fi

HEAD_NOW="$(git rev-parse HEAD)"
DIRTY="$(git status --porcelain)"

if [ -z "$REMOTE" ]; then
    echo "   local only: HEAD=$(git rev-parse --short HEAD) — nothing to sync against"
elif [ "$HEAD_NOW" = "$REMOTE" ] && [ -z "$DIRTY" ]; then
    echo "== clean and in sync with origin/$BRANCH ($(git rev-parse --short HEAD))"
elif [ "$HEAD_NOW" = "$REMOTE" ]; then
    # At the tip with a dirty tree: in-progress work or a stale snapshot, never a
    # reason to reset. The integrity check below reports anything actually broken.
    echo "!! HEAD at remote tip but tree dirty — leaving uncommitted work ALONE"
elif git merge-base --is-ancestor "$REMOTE" "$HEAD_NOW"; then
    echo "== local is AHEAD of origin/$BRANCH (unpushed commits) — leaving HEAD alone"
elif git merge-base --is-ancestor "$HEAD_NOW" "$REMOTE"; then
    if [ "$HEAD_NOW" = "$BASE_COMMIT" ]; then
        echo "!! RE-CLONE DETECTED (HEAD == base commit) — hard-resetting to the remote tip"
    else
        echo "== behind origin/$BRANCH (user pushed between turns) — fast-forwarding"
    fi
    git reset --hard "$REMOTE" >/dev/null
    # A hard reset restores tracked files only. Anything listed here is a leftover
    # from the disturbed checkout (or the user's mid-turn scribbles) — worth seeing
    # rather than silently dragging along.
    leftovers="$(git status --porcelain)"
    if [ -n "$leftovers" ]; then
        echo "   note: untracked/leftover entries after the reset:"
        echo "$leftovers" | sed 's/^/     /'
    fi
else
    echo "!! DIVERGED (both sides have unique commits) — repair by hand"
    echo "   do NOT force-push over the remote; the repair is printed below"
    # The usual cause is a re-clone arriving mid-turn (observed twice on 2026-09-29,
    # both times right after a user-facing pause): HEAD is knocked back to the base
    # commit, the next commit therefore sits on top of BASE, and the push is rejected
    # because the remote still holds the earlier commits from this same session.
    # The re-parenting recipe below is the safe repair: it keeps the local tree
    # verbatim and makes it a child of the remote tip. Verify the tree first!
    echo "   if this session's local commit was rebuilt on top of $BASE_COMMIT"
    echo "   while the remote holds your earlier commits, re-parent it (nothing is lost):"
    echo "        git diff --stat origin/$BRANCH    # confirm only your intended changes"
    echo "        git reset --soft origin/$BRANCH"
    echo "        git commit -C HEAD@{1}            # re-use the previous message"
    exit 1
fi

# --- 4. Settle: restored files can keep arriving in waves. -------------------
for round in 1 2 3; do
    before="$(status_hash)"
    sleep 3
    if [ "$before" = "$(status_hash)" ]; then
        break
    fi
    echo "!! worktree still churning (restore wave $round) — waiting"
    sleep 5
done

# --- 5. Verify the tree is whole. -------------------------------------------
sleep 2
missing=0
for file in "${KEY_FILES[@]}"; do
    if [ ! -f "$file" ]; then
        echo "!! MISSING: $file"
        missing=1
    fi
done

# Any structured file we ship must at least parse; a corrupted request is
# invisible otherwise and only fails later, on a runner, at someone's expense.
if [ -f .github/runner-request.toml ]; then
    if ! python3 -c 'import tomllib; tomllib.load(open(".github/runner-request.toml","rb"))'; then
        echo "!! .github/runner-request.toml is not valid TOML"
        missing=1
    fi
fi

if [ "$missing" -ne 0 ]; then
    echo "!! tree incomplete — inspect the MISSING lines above and restore the file(s)"
    exit 1
fi

echo "== ready: HEAD=$(git rev-parse --short HEAD) dirty=$(git status --porcelain | wc -l)"
git log --oneline -3
