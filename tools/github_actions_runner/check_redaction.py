# tools/github_actions_runner/check_redaction.py
"""Audit a job log for identifiers that should have been redacted.

Run this against a real exported log after a round trip:

    bash tools/github_actions_runner/fetch_logs.sh RUN_ID
    python3 tools/github_actions_runner/check_redaction.py .runner-logs/RUN_ID/*.log

With no arguments it audits a built-in sample shaped like a self-hosted job, so
the workflow has something to check on every run.

Deliberately independent of redact.py. This looks for the *shapes* that must not
survive, using its own patterns rather than importing the redactor's. A checker
that shared the redactor's regexes would pass whenever both were wrong in the
same way, which is roughly how a silently empty account-name list survived
seventeen green tests.

Nothing here echoes a matched value. This tool's own output can end up in a
published log, and printing what it found would leak the very thing it exists to
catch; findings are reported by line number, pattern, and length.
"""

from __future__ import annotations

import argparse
import re
import sys
from collections.abc import Sequence
from pathlib import Path

#: Shapes that must not survive redaction. Each carries a human-readable label
#: so a finding can be acted on without printing what was found.
FORBIDDEN_PATTERNS = (
    (
        "windows-home-path",
        re.compile(rb"[A-Za-z]:\\+Users\\+(?!<user>)[^\\/\s\"']+"),
    ),
    (
        "windows-home-path-json",
        re.compile(rb"[A-Za-z]:\\{2,}Users\\{2,}(?!<user>)[^\\/\s\"']+"),
    ),
    (
        "posix-home-path",
        re.compile(rb"(?<![\w.:])/(?:home|Users)/(?!<user>)[^/\s\"']+"),
    ),
    (
        # The value must start with a non-space. Without that, backtracking lets
        # `\s*` give the space back and the class match it on its own, so a
        # correctly redacted `Runner name: <machine>` reports as a leak.
        "machine-or-runner-name",
        re.compile(
            rb"(?i)\b(?:Machine|Runner) name:\s*['\"]?"
            rb"(?!<machine>)[A-Za-z0-9._][A-Za-z0-9._ -]*"
        ),
    ),
    (
        "windows-build-number",
        re.compile(rb"\b10\.0\.\d{4,5}\b"),
    ),
    (
        "platform-string",
        re.compile(rb"(?i)\b(?:Windows|Linux|macOS|Mac OS X)-\d+(?:\.\d+)+"),
    ),
    (
        "release-and-build",
        re.compile(rb"\b\d{2}H[12]\s*\(\s*\d{4,5}\s*\)"),
    ),
)


def build_sample_log() -> bytes:
    """A log shaped like a real self-hosted Windows job, before redaction.

    Used to prove the audit catches things: this text is passed through the real
    redactor and must come out clean. The names are invented.
    """

    return (
        b"workload\tSet up job\t2026-10-27T14:00:00Z Current runner version: '2.321.0'\n"
        b"workload\tSet up job\t2026-10-27T14:00:00Z Runner name: 'DESKTOP-EXAMPLE01-session'\n"
        b"workload\tSet up job\t2026-10-27T14:00:00Z Machine name: 'DESKTOP-EXAMPLE01'\n"
        b"workload\tEnvironment\t2026-10-27T14:00:01Z USERNAME=EXAMPLEUSER\n"
        b"workload\tEnvironment\t2026-10-27T14:00:01Z USERDOMAIN=DESKTOP-EXAMPLE01\n"
        b"workload\tEnvironment\t2026-10-27T14:00:01Z "
        rb"C:\Users\EXAMPLEUSER\AppData\Local\Programs\Python\python.exe"
        b"\n"
        b"workload\tEnvironment\t2026-10-27T14:00:01Z platform: Windows-10-10.0.19045-SP0\n"
        b"workload\tUnit tests\t2026-10-27T14:00:02Z Ran 31 tests in 0.004s\n"
        b"workload\tComplete job\t2026-10-27T14:00:03Z Job succeeded\n"
    )


def audit_bytes(contents: bytes) -> list[tuple[int, str, int]]:
    """Return (line number, pattern label, match length) for every finding."""

    findings = []
    for line_number, line in enumerate(contents.splitlines(), start=1):
        for label, pattern in FORBIDDEN_PATTERNS:
            for match in pattern.finditer(line):
                findings.append((line_number, label, len(match.group(0))))
    return findings


def report(source: str, contents: bytes) -> list[tuple[int, str, int]]:
    """Audit one log and print findings without echoing what was found."""

    findings = audit_bytes(contents)
    if not findings:
        print(f"clean: {source}")
        return []

    print(f"LEAKS FOUND in {source}: {len(findings)}")
    for line_number, label, length in findings:
        print(f"  line {line_number}: {label} ({length} bytes) - value withheld")
    return findings


def parse_args(argv: Sequence[str] | None = None) -> argparse.Namespace:
    """Parse command-line arguments."""

    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "logs",
        type=Path,
        nargs="*",
        help="Log files to audit. With none, a built-in sample is used.",
    )
    return parser.parse_args(argv)


def main(argv: Sequence[str] | None = None) -> int:
    """Audit the given logs, or the built-in sample run through the redactor."""

    args = parse_args(argv)

    if not args.logs:
        # Import here so the audit of a real log never depends on the redactor
        # being importable.
        from redact import redact_bytes

        sample = build_sample_log()
        dirty = audit_bytes(sample)
        if not dirty:
            print("the sample log no longer contains anything to catch")
            return 1

        redacted = redact_bytes(sample)
        findings = report("<built-in sample, redacted>", redacted)
        if findings:
            print("The redactor let something through.", file=sys.stderr)
            return 1
        print(f"sample had {len(dirty)} identifiers before redaction, 0 after")
        return 0

    total = 0
    for path in args.logs:
        if not path.is_file():
            print(f"not a file: {path}", file=sys.stderr)
            return 2
        total += len(report(str(path), path.read_bytes()))

    if total:
        print(f"{total} finding(s) across {len(args.logs)} file(s).", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.path.insert(0, str(Path(__file__).resolve().parent))
    raise SystemExit(main())
