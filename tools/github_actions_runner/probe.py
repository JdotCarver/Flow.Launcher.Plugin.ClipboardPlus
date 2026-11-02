# tools/github_actions_runner/probe.py
"""Print a non-sensitive receipt proving where the requested workload ran."""

from __future__ import annotations

import argparse
import json
import os
import platform
import sys
from collections.abc import Sequence
from pathlib import Path

import machine_label
from select_runner import RunnerRequest, load_request

EXPECTED_ENVIRONMENTS = {
    "cloud-windows": "github-hosted",
    "local-windows": "self-hosted",
}


def parse_args(argv: Sequence[str] | None = None) -> argparse.Namespace:
    """Parse command-line arguments."""

    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("request", type=Path, help="Path to runner-request.toml")
    return parser.parse_args(argv)


def build_receipt(request: RunnerRequest) -> dict[str, str]:
    """Assemble the fields that prove where the workload actually ran.

    Deliberately omits `RUNNER_NAME`. The runner name is the host: the launcher
    used to register `<computer name>-session`, and GitHub prints the same value
    in the `Set up job` banner of every self-hosted job. This receipt is
    published to the log branch, and a stable personal hostname is a better
    fingerprint than the OS build this module takes care not to print.
    `runner_environment` already answers the only question the receipt exists to
    answer -- hosted or self-hosted -- so the name bought nothing.
    """
    return {
        "request_id": request.request_id,
        "job_name": request.job_name,
        "target": request.target,
        "repository": os.environ.get("GITHUB_REPOSITORY", "unknown"),
        "commit": os.environ.get("GITHUB_SHA", "unknown"),
        "run_id": os.environ.get("GITHUB_RUN_ID", "unknown"),
        "runner_environment": os.environ.get("RUNNER_ENVIRONMENT", "unknown"),
        "operating_system": machine_label.describe_machine(),
        "architecture": platform.machine(),
        "python_version": platform.python_version(),
    }


def main(argv: Sequence[str] | None = None) -> int:
    """Validate the execution environment and print a safe, structured receipt."""

    args = parse_args(argv)
    request = load_request(args.request)
    actual_environment = os.environ.get("RUNNER_ENVIRONMENT", "unknown")
    expected_environment = EXPECTED_ENVIRONMENTS[request.target]

    problems = []
    if platform.system() != "Windows":
        problems.append(f"expected Windows, found {platform.system() or 'unknown'}")
    if actual_environment != expected_environment:
        problems.append(
            f"target {request.target!r} requires {expected_environment!r}, "
            f"found {actual_environment!r}"
        )
    if problems:
        print("Runner probe failed: " + "; ".join(problems), file=sys.stderr)
        return 1

    print(json.dumps(build_receipt(request), indent=2, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
