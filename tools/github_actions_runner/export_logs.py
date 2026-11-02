# tools/github_actions_runner/export_logs.py
"""Export completed job logs to a dedicated git branch the Arena harness can fetch."""

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
from collections.abc import Sequence
from pathlib import Path
from typing import Any

# Same directory as this script, which is how it is invoked from the workflow.
from redact import redact_bytes

LOG_BRANCH = "actions-logs"

# How many runs stay published on the log branch. Without a ceiling the branch
# grows by one directory per run forever, and every published log is a durable
# copy of data that GitHub itself would have expired after 90 days.
RUN_RETENTION = 10
ANSI_CSI_PATTERN = re.compile(rb"\x1b\[[0-?]*[ -/]*[@-~]")
ANSI_OSC_PATTERN = re.compile(rb"\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)")


def normalize_log_bytes(contents: bytes) -> bytes:
    """Make a job log readable and safe to publish.

    Terminal control sequences are stripped, line endings normalized, and the
    signed-in account name replaced with `<user>`: these logs are republished to a
    public branch and a Windows log names the account in nearly every path.
    """

    contents = ANSI_OSC_PATTERN.sub(b"", contents)
    contents = ANSI_CSI_PATTERN.sub(b"", contents)
    contents = contents.replace(b"\r\n", b"\n").replace(b"\r", b"\n")
    return redact_bytes(contents)


def run_command(
    arguments: Sequence[str],
    *,
    cwd: Path | None = None,
    stdout: Any = None,
    check: bool = True,
) -> subprocess.CompletedProcess[str]:
    """Run a command without a shell and never include credentials in its arguments."""

    return subprocess.run(
        arguments,
        cwd=cwd,
        stdout=stdout,
        stderr=subprocess.PIPE,
        text=True,
        check=check,
    )


def github_api(endpoint: str) -> dict[str, Any]:
    """Read one GitHub API object using the workflow's GH_TOKEN."""

    result = run_command(["gh", "api", endpoint], stdout=subprocess.PIPE, check=True)
    return json.loads(result.stdout)


def safe_name(value: str) -> str:
    """Convert a job name to a portable filename component."""

    normalized = re.sub(r"[^A-Za-z0-9._-]+", "-", value).strip("-.")
    return normalized[:80] or "job"


def describe_error(error: BaseException) -> str:
    """Include useful API diagnostics without publishing signed download URLs."""

    if isinstance(error, subprocess.CalledProcessError) and error.stderr:
        details = error.stderr.strip()
    else:
        details = str(error)
    return re.sub(r"https?://\S+", "<redacted-url>", details)


def print_workflow_error(message: str) -> None:
    """Emit a sanitized error annotation readable through the checks API."""

    escaped = message.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")
    print(f"::error::{escaped}", file=sys.stderr)


def write_redacted(destination: Path, filename: str, contents: bytes) -> None:
    """Write one published file through the same redaction the job logs get.

    `run.json` carries `head_branch` and a job URL per job, and
    `runner-request.toml` is whatever the request author wrote. Both land on a
    branch that is world-readable on a public repository, so neither may skip
    the pass every job log goes through.
    """

    (destination / filename).write_bytes(normalize_log_bytes(contents))


def job_log_filename(job: dict[str, Any], position: int) -> str:
    """Build a stable, collision-free filename for a job log."""

    return f"{position:02d}-{safe_name(job['name'])}-{job['id']}.log"


def should_export_job(job: dict[str, Any], exporter_name: str) -> bool:
    """Only completed jobs can have downloadable logs; the exporter is still running."""

    return (
        job.get("status") == "completed"
        and job.get("conclusion") != "skipped"
        and job.get("name") != exporter_name
    )


def write_run_metadata(
    destination: Path,
    run: dict[str, Any],
    jobs: list[dict[str, Any]],
    request_path: Path,
) -> None:
    """Write metadata that lets an agent identify the source commit and job results."""

    metadata = {
        "capture_note": (
            "Status is captured while Export logs is still in progress; "
            "use the final workflow conclusion for that job's result."
        ),
        "id": run.get("id"),
        "name": run.get("name"),
        "url": run.get("html_url"),
        "event": run.get("event"),
        "head_branch": run.get("head_branch"),
        "head_sha": run.get("head_sha"),
        "status": run.get("status"),
        "conclusion": run.get("conclusion"),
        "jobs": [
            {
                "id": job.get("id"),
                "name": job.get("name"),
                "status": job.get("status"),
                "conclusion": job.get("conclusion"),
                "url": job.get("html_url"),
            }
            for job in jobs
        ],
    }
    write_redacted(
        destination,
        "run.json",
        (json.dumps(metadata, indent=2, sort_keys=True) + "\n").encode("utf-8"),
    )
    if request_path.is_file():
        write_redacted(destination, "runner-request.toml", request_path.read_bytes())


def download_logs(
    repository: str,
    run_id: int,
    exporter_name: str,
    request_path: Path,
    staging_directory: Path,
) -> list[str]:
    """Download every completed planner/workload log and return any error messages."""

    run = github_api(f"repos/{repository}/actions/runs/{run_id}")
    jobs_response = github_api(f"repos/{repository}/actions/runs/{run_id}/jobs?per_page=100")
    jobs = jobs_response.get("jobs", [])
    write_run_metadata(staging_directory, run, jobs, request_path)

    errors = []
    for position, job in enumerate(jobs, start=1):
        if not should_export_job(job, exporter_name):
            continue
        destination = staging_directory / job_log_filename(job, position)
        try:
            with destination.open("wb") as log_file:
                run_command(
                    [
                        "gh",
                        "api",
                        "--allow-escape-sequences",
                        f"repos/{repository}/actions/jobs/{job['id']}/logs",
                    ],
                    stdout=log_file,
                    check=True,
                )
            destination.write_bytes(normalize_log_bytes(destination.read_bytes()))
        except (OSError, subprocess.CalledProcessError) as error:
            message = f"Could not download log for job {job.get('name')!r}: {describe_error(error)}"
            destination.unlink(missing_ok=True)
            errors.append(message)
            print(message, file=sys.stderr)

    if errors:
        (staging_directory / "export-errors.txt").write_text(
            "\n".join(errors) + "\n",
            encoding="utf-8",
        )
    return errors


def configure_git_repository(directory: Path, repository: str) -> None:
    """Prepare an isolated repository with gh-managed, non-persisted credentials."""

    run_command(["git", "init"], cwd=directory)
    run_command(["git", "config", "user.name", "github-actions[bot]"], cwd=directory)
    run_command(
        ["git", "config", "user.email", "41898282+github-actions[bot]@users.noreply.github.com"],
        cwd=directory,
    )
    run_command(["git", "config", "commit.gpgsign", "false"], cwd=directory)
    run_command(["gh", "auth", "setup-git"], cwd=directory)
    run_command(
        ["git", "remote", "add", "origin", f"https://github.com/{repository}.git"],
        cwd=directory,
    )


def check_out_log_branch(directory: Path) -> None:
    """Check out the existing log branch, or start an orphan branch on first export."""

    remote_check = run_command(
        ["git", "ls-remote", "--exit-code", "--heads", "origin", LOG_BRANCH],
        cwd=directory,
        check=False,
    )
    if remote_check.returncode == 0:
        run_command(["git", "fetch", "origin", LOG_BRANCH], cwd=directory)
        run_command(["git", "checkout", "-B", "logs", "FETCH_HEAD"], cwd=directory)
    elif remote_check.returncode == 2:
        run_command(["git", "checkout", "--orphan", "logs"], cwd=directory)
    else:
        raise RuntimeError(f"Could not inspect {LOG_BRANCH}: {remote_check.stderr.strip()}")


def commit_and_push(directory: Path, run_id: int) -> None:
    """Publish one run directory, rebasing if another exporter pushes concurrently."""

    run_command(["git", "add", "--all"], cwd=directory)
    staged_changes = run_command(["git", "diff", "--cached", "--quiet"], cwd=directory, check=False)
    if staged_changes.returncode == 0:
        print(f"Run {run_id} logs are already published.")
        return

    run_command(["git", "commit", "-m", f"Actions logs: run {run_id}"], cwd=directory)
    for attempt in range(1, 6):
        push = run_command(
            ["git", "push", "origin", f"HEAD:{LOG_BRANCH}"],
            cwd=directory,
            check=False,
        )
        if push.returncode == 0:
            return
        if attempt == 5:
            raise RuntimeError(f"Could not push {LOG_BRANCH}: {push.stderr.strip()}")

        print(f"Log branch moved concurrently; rebasing (attempt {attempt}/5).")
        run_command(["git", "fetch", "origin", LOG_BRANCH], cwd=directory)
        run_command(["git", "rebase", "FETCH_HEAD"], cwd=directory)
        time.sleep(attempt)


def published_run_ids(directory: Path) -> list[int]:
    """List the run ids currently published on the log branch, newest first.

    Directory names are run ids, which GitHub assigns monotonically, so sorting
    the names numerically is a chronological ordering without needing timestamps.
    Names that are not integers are ignored rather than raising: the branch is
    written by this exporter, but a stray directory should not break publishing.
    """

    runs_root = directory / "runs"
    if not runs_root.is_dir():
        return []

    ids = []
    for entry in runs_root.iterdir():
        if entry.is_dir() and entry.name.isdigit():
            ids.append(int(entry.name))
    return sorted(ids, reverse=True)


def prune_old_runs(directory: Path, keep: int = RUN_RETENTION) -> list[int]:
    """Delete published runs beyond the newest `keep`, returning what was removed."""

    removed = []
    for run_id in published_run_ids(directory)[keep:]:
        shutil.rmtree(directory / "runs" / str(run_id))
        removed.append(run_id)
    return removed


def publish_logs(
    staging_directory: Path,
    repository: str,
    run_id: int,
    work_directory: Path,
) -> None:
    """Copy one run's logs into an isolated checkout and push the log branch."""

    configure_git_repository(work_directory, repository)
    check_out_log_branch(work_directory)
    destination = work_directory / "runs" / str(run_id)
    if destination.exists():
        shutil.rmtree(destination)
    destination.parent.mkdir(parents=True, exist_ok=True)
    shutil.copytree(staging_directory, destination)

    pruned = prune_old_runs(work_directory)
    if pruned:
        print(f"Retention: removed {len(pruned)} run(s) beyond the newest {RUN_RETENTION}.")

    commit_and_push(work_directory, run_id)


def parse_args(argv: Sequence[str] | None = None) -> argparse.Namespace:
    """Parse command-line arguments."""

    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--exporter-name", default="Export logs")
    parser.add_argument(
        "--request",
        type=Path,
        default=Path(".github/runner-request.toml"),
    )
    return parser.parse_args(argv)


def main(argv: Sequence[str] | None = None) -> int:
    """Export logs for the current run without modifying the source checkout."""

    args = parse_args(argv)
    repository = os.environ.get("GITHUB_REPOSITORY")
    run_id_text = os.environ.get("GITHUB_RUN_ID")
    if not repository or not run_id_text or not run_id_text.isdigit():
        print("GITHUB_REPOSITORY and a numeric GITHUB_RUN_ID are required.", file=sys.stderr)
        return 2
    if not os.environ.get("GH_TOKEN"):
        print("GH_TOKEN must contain the workflow's GITHUB_TOKEN.", file=sys.stderr)
        return 2

    run_id = int(run_id_text)
    try:
        with tempfile.TemporaryDirectory(prefix="actions-log-export-") as temporary_directory:
            temporary_path = Path(temporary_directory)
            staging_directory = temporary_path / "staging"
            work_directory = temporary_path / "publish"
            staging_directory.mkdir()
            work_directory.mkdir()
            errors = download_logs(
                repository,
                run_id,
                args.exporter_name,
                args.request,
                staging_directory,
            )
            publish_logs(staging_directory, repository, run_id, work_directory)
    except (OSError, RuntimeError, subprocess.CalledProcessError) as error:
        print_workflow_error(f"Log export failed: {describe_error(error)}")
        return 1

    if errors:
        print_workflow_error("Logs were published with download errors; see export-errors.txt.")
        return 1
    print(f"Published run {run_id} logs to the {LOG_BRANCH} branch.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
