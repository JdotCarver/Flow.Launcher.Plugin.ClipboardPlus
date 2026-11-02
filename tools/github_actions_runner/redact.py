# tools/github_actions_runner/redact.py
"""Strip personal identifiers out of job logs before they are published.

The exporter republishes whole job logs to the `actions-logs` branch. On a public
repository that branch is world-readable, and a Windows job log is full of the
signed-in account and the machine: `C:\\Users\\<name>\\.gitconfig`, the Python
install path under `AppData`, `USERNAME=<name>`, and the `Machine name:` line
GitHub itself prints for every self-hosted job.

Two rules shape this module.

1. **Nothing here may know what it is looking for.** An earlier version read the
   account name from `USERNAME`/`USER`. That was a silent no-op: the exporter
   runs on a GitHub-hosted Linux runner, where `USERNAME` is unset and `USER` is
   the generic `runner`, so the bare-name scrub never ran in production while its
   tests -- which passed the name in explicitly -- stayed green. Worse, a
   redactor that hardcodes its target leaks the target. So names are *harvested*
   from the log's own text at runtime instead.

2. **The replacements are deliberately dumb and total.** They must never depend
   on a particular machine's setup, because the log is published before anyone
   can inspect it.
"""

from __future__ import annotations

import os
import re
from typing import Iterable, Sequence

#: Where a Windows log names an account: `C:\Users\name`, `C:\\Users\\name`,
#: `C:/Users/name`, and the `\\?\C:\Users\name` long-path form.
WINDOWS_BACKSLASH_ACCOUNT = re.compile(rb"(?i)([A-Z]:\\+Users\\+)([^\\/\s\"']+)")
WINDOWS_FORWARD_ACCOUNT = re.compile(rb"(?i)([A-Z]:/+Users/+)([^\\/\s\"']+)")

#: The POSIX equivalents, which also appear in logs from Ubuntu planner jobs. A
#: colon is excluded from the lookbehind so that a `C:/Users/name` path, already
#: redacted above, is not rewritten a second time.
POSIX_ACCOUNT = re.compile(rb"(?<![\w.:])(/(?:home|Users)/)([^/\s\"']+)")

ACCOUNT_NAME = b"<user>"

#: GitHub prints these two lines in the `Set up job` banner of every self-hosted
#: job, so the machine's own name reaches the log before any of our code runs. A
#: downloaded log prefixes each line with job, step, and timestamp, so neither
#: pattern is anchored to the start of a line. The value runs to the end of the
#: line because a runner name is allowed to contain spaces.
RUNNER_NAME_LINE = re.compile(rb"(?i)Runner name:\s*([^\r\n\t]+)")
MACHINE_NAME_LINE = re.compile(rb"(?i)Machine name:\s*([^\r\n\t]+)")

MACHINE_NAME = b"<machine>"

#: The launcher names its runner `<machine>-session`, so a log can carry the
#: machine name glued to that suffix with no bare occurrence anywhere. Harvest
#: the prefix as well, or `USERDOMAIN=<machine>` survives on a log whose only
#: other mention was the runner name.
RUNNER_SESSION_SUFFIX = re.compile(rb"(?i)-session$")

#: Operating-system versions. A build number says exactly which patch level a
#: machine is on, which is the first thing an attacker checks against a list of
#: known vulnerabilities. The family (`Windows 10`) is what a log needs in order
#: to be readable; the number is not.
#:
#: `platform.platform()` on the owner's machine prints
#: `Windows-10-10.0.XXXXX-SP0`, which leaks twice over (build and service pack).
WINDOWS_PLATFORM = re.compile(rb"(?i)\bWindows-(\d+(?:\.\d+)?)-\d+(?:\.\d+)*-SP\d+")
WINDOWS_BUILD = re.compile(rb"(?i)\b10\.0\.\d{4,5}\b")
WINDOWS_BUILD_LABEL = re.compile(rb"(?i)\b(build)\s+\d{4,5}\b")
WINDOWS_RELEASE = re.compile(rb"(?i)\b(\d{2})H([12])\b")
#: A release id followed by its build in brackets: `22H2 (19045)`.
WINDOWS_RELEASE_BUILD = re.compile(rb"(?i)\b(\d{2}H[12])\s*\(?\s*\d{4,5}\s*\)?")
MACOS_PLATFORM = re.compile(rb"(?i)\b(macOS|Mac OS X)-(\d+(?:\.\d+)*)(?:-[A-Za-z0-9_]+)?")
LINUX_PLATFORM = re.compile(rb"(?i)\bLinux-(\d+\.\d+(?:\.\d+)*)(?:-[A-Za-z0-9_\-.]+)?")
DARWIN_KERNEL = re.compile(rb"(?i)\bDarwin Kernel Version \d+(?:\.\d+)*")

OS_FAMILY_TAG = b"<version>"

#: `Windows 10` / `Windows 11`. Kept by default so timings stay interpretable;
#: set REDACT_OS_FAMILY=1 to remove the family as well.
WINDOWS_FAMILY = re.compile(rb"(?i)\bWindows (?:10|11)\b")
WINDOWS_HYPHEN_FAMILY = re.compile(rb"(?i)\bWindows-\d+(?:\.\d+)*(?=-)")

#: Names that are simultaneously ordinary words or path components. Scrubbing
#: them as bare words would rewrite text such as `C:\Users` into `C:\<user>s`,
#: and the path patterns above have already removed them where they matter.
#: The machine entries are the same problem for hostnames: a box called
#: `workstation` would have that word rewritten throughout an ordinary log.
GENERIC_NAMES = frozenset(
    {
        "user",
        "users",
        "home",
        "root",
        "runner",
        "github",
        "administrator",
        "public",
        "desktop",
        "laptop",
        "computer",
        "workstation",
        "localhost",
        "unknown",
    }
)


def _is_scrubbable(name: bytes) -> bool:
    """Whether a harvested name is specific enough to scrub as a bare word.

    A one- or two-character name matches ordinary words in a log, a generic one
    damages path text the patterns above already handle, and anything wrapped in
    angle brackets is one of our own placeholders -- evidence that this log has
    already been through here.
    """
    if len(name) < 3 or b"<" in name or b">" in name:
        return False
    return name.decode("utf-8", "replace").lower() not in GENERIC_NAMES


def _scrub_word(contents: bytes, name: bytes, replacement: bytes) -> bytes:
    """Replace every whole-word occurrence of one harvested name."""
    return re.sub(
        rb"(?<![A-Za-z0-9])" + re.escape(name) + rb"(?![A-Za-z0-9])",
        replacement,
        contents,
        flags=re.IGNORECASE,
    )


def _unquote(value: bytes) -> bytes:
    """Drop the quotes and padding GitHub wraps a runner or machine name in."""
    return value.strip().strip(b"'\"").strip()


def _harvest_account_names(contents: bytes) -> Iterable[bytes]:
    """Account names this log reveals in its own home-directory paths.

    This is the only reliable source available to the exporter: it runs on a
    GitHub-hosted Linux runner and cannot see the Windows workload's
    environment, so `USERNAME` is not readable from here.
    """
    found = set()
    for pattern in (WINDOWS_BACKSLASH_ACCOUNT, WINDOWS_FORWARD_ACCOUNT, POSIX_ACCOUNT):
        for match in pattern.finditer(contents):
            found.add(match.group(2))
    return {name for name in found if _is_scrubbable(name)}


def _harvest_machine_names(contents: bytes) -> Iterable[bytes]:
    """Machine names this log reveals in GitHub's `Set up job` banner."""
    found = set()
    for pattern in (RUNNER_NAME_LINE, MACHINE_NAME_LINE):
        for match in pattern.finditer(contents):
            name = _unquote(match.group(1))
            if not name:
                continue
            found.add(name)
            found.add(RUNNER_SESSION_SUFFIX.sub(b"", name))
    return {name for name in found if name and _is_scrubbable(name)}


def _account_names(explicit: Sequence[str] | None = None) -> Iterable[bytes]:
    """Names supplied by the caller or readable from this process's environment.

    Kept as a secondary source only. On the exporter it yields nothing, which is
    exactly why `_harvest_account_names` exists; it helps when a caller already
    knows a name worth scrubbing.
    """
    candidates = list(explicit or ())
    for variable in ("USERNAME", "USER", "LOGNAME"):
        value = os.environ.get(variable)
        if value:
            candidates.append(value)

    seen = set()
    for name in candidates:
        encoded = name.encode("utf-8", "replace")
        if encoded.lower() in seen or not _is_scrubbable(encoded):
            continue
        seen.add(encoded.lower())
        yield encoded


def _mask_family() -> bool:
    """Whether the OS family should go too (REDACT_OS_FAMILY=1)."""
    return os.environ.get("REDACT_OS_FAMILY", "").strip().lower() in ("1", "true", "yes")


def _redact_os_versions(contents: bytes) -> bytes:
    """Replace exact OS versions with the family name and a marker.

    Order matters: the compound `platform.platform()` form is rewritten first,
    so the bare build number inside it is never matched on its own.
    """
    contents = WINDOWS_PLATFORM.sub(rb"Windows-\1-" + OS_FAMILY_TAG, contents)
    contents = WINDOWS_RELEASE_BUILD.sub(rb"\1 (<version>)", contents)
    contents = WINDOWS_BUILD.sub(b"10.0." + OS_FAMILY_TAG, contents)
    contents = WINDOWS_BUILD_LABEL.sub(rb"\1 " + OS_FAMILY_TAG, contents)
    contents = WINDOWS_RELEASE.sub(b"<release>", contents)
    if _mask_family():
        # Both spellings: `Windows 10` and the hyphenated `Windows-10` left by
        # the platform string above.
        contents = WINDOWS_HYPHEN_FAMILY.sub(b"Windows", contents)
        contents = WINDOWS_FAMILY.sub(b"Windows <version>", contents)
    contents = MACOS_PLATFORM.sub(rb"\1-" + OS_FAMILY_TAG, contents)
    contents = LINUX_PLATFORM.sub(rb"Linux-" + OS_FAMILY_TAG, contents)
    contents = DARWIN_KERNEL.sub(b"Darwin Kernel Version <version>", contents)
    return contents


def _redact_machine_name_banner(contents: bytes) -> bytes:
    """Blank GitHub's `Runner name:` and `Machine name:` lines.

    The values are harvested before this runs, so their other occurrences go
    too; replacing the line itself keeps the banner readable without naming the
    host.
    """
    contents = MACHINE_NAME_LINE.sub(b"Machine name: " + MACHINE_NAME, contents)
    contents = RUNNER_NAME_LINE.sub(b"Runner name: " + MACHINE_NAME, contents)
    return contents


def redact_bytes(contents: bytes, account_names: Sequence[str] | None = None) -> bytes:
    """Replace the account and machine identifiers in a log with placeholders."""
    # Harvest BEFORE substituting anything: the paths and the banner are the only
    # places the real names are still readable.
    harvested_accounts = _harvest_account_names(contents)
    harvested_machines = _harvest_machine_names(contents)

    contents = WINDOWS_BACKSLASH_ACCOUNT.sub(rb"\1" + ACCOUNT_NAME, contents)
    contents = WINDOWS_FORWARD_ACCOUNT.sub(rb"\1" + ACCOUNT_NAME, contents)
    contents = POSIX_ACCOUNT.sub(rb"\1" + ACCOUNT_NAME, contents)
    contents = _redact_machine_name_banner(contents)

    contents = _redact_os_versions(contents)

    for name in harvested_machines:
        contents = _scrub_word(contents, name, MACHINE_NAME)
    for name in (*harvested_accounts, *_account_names(account_names)):
        contents = _scrub_word(contents, name, ACCOUNT_NAME)

    return contents
