"""Updates (the Mac's Sparkle, Windows' Velopack): a new Linux package on GitHub Releases is found,
checked and installed into OpenClicky's own virtualenv, then OpenClicky starts again.

- Checked once a day (Settings → General → Check for updates on their own; on by default), or with
  "Check now". The releases are listed through GitHub's API; one carries a Linux package when it
  has `openclicky_linux-<version>-py3-none-any.whl` and `SHA256SUMS-linux.txt` (the
  `linux-release` workflow attaches both).
- The wheel is downloaded, its SHA-256 checked against the release's sums, and installed with the
  virtualenv's own pip. Nothing is installed that does not match.
- Only an installed copy updates itself (`install.sh`'s `~/.local/openclicky`); a copy run from a
  checkout says so, and `git pull` updates it.
"""

from __future__ import annotations

import hashlib
import json
import os
import re
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
from dataclasses import dataclass
from pathlib import Path

from . import __version__, applog, preferences

RELEASES_URL = "https://api.github.com/repos/prasanthsasikumar/openclicky/releases?per_page=15"
CHECK_EVERY_SECONDS = 24 * 3600
LAST_CHECK_KEY = "lastUpdateCheck"
WHEEL_PATTERN = re.compile(r"^openclicky_linux-(\d+(?:\.\d+)*)-py3-none-any\.whl$")
SUMS_NAME = "SHA256SUMS-linux.txt"


@dataclass(frozen=True)
class Update:
    version: str
    wheel_url: str
    wheel_name: str
    sums_url: str
    notes: str = ""


def version_tuple(version: str) -> tuple[int, ...]:
    return tuple(int(part) for part in re.findall(r"\d+", version))


def newest_update(releases: list, current: str = __version__) -> Update | None:
    """The newest release with a Linux package newer than `current` (drafts and pre-releases skipped)."""
    best: Update | None = None
    for release in releases:
        if not isinstance(release, dict) or release.get("draft") or release.get("prerelease"):
            continue
        assets = {asset.get("name"): asset.get("browser_download_url") for asset in release.get("assets") or [] if isinstance(asset, dict)}
        sums = assets.get(SUMS_NAME)
        for name, url in assets.items():
            match = WHEEL_PATTERN.match(name or "")
            if not match or not sums or not url:
                continue
            version = match.group(1)
            if version_tuple(version) > version_tuple(current) and (best is None or version_tuple(version) > version_tuple(best.version)):
                best = Update(version, url, name, sums, (release.get("body") or "")[:600])
    return best


def expected_sha256(sums_text: str, file_name: str) -> str | None:
    """`sha256sum` output: "<hex>  ./<name>" or "<hex>  <name>"."""
    for line in sums_text.splitlines():
        parts = line.split()
        if len(parts) == 2 and parts[1].lstrip("*").removeprefix("./") == file_name and re.fullmatch(r"[0-9a-f]{64}", parts[0]):
            return parts[0]
    return None


def is_installed_copy() -> bool:
    """Running from install.sh's virtualenv (never a checkout's)."""
    return Path(sys.prefix).resolve() == (Path.home() / ".local" / "openclicky").resolve()


def _get(url: str, timeout: float = 20) -> bytes:
    request = urllib.request.Request(url, headers={"Accept": "application/vnd.github+json", "User-Agent": f"openclicky-linux/{__version__}"})
    with urllib.request.urlopen(request, timeout=timeout) as response:
        return response.read()


def check(fetch=_get) -> Update | None:
    """The update there is, or None (raises OSError when GitHub cannot be reached)."""
    preferences.set_values(**{LAST_CHECK_KEY: time.time()})
    try:
        releases = json.loads(fetch(RELEASES_URL))
    except ValueError as failure:
        raise OSError("github's answer could not be read") from failure
    update = newest_update(releases if isinstance(releases, list) else [])
    applog.append(f"updates: {'version ' + update.version + ' is out' if update else 'up to date'} (this is {__version__})")
    return update


def is_due(now: float | None = None) -> bool:
    last = preferences.get(LAST_CHECK_KEY)
    return not isinstance(last, (int, float)) or (now or time.time()) - last >= CHECK_EVERY_SECONDS


def install(update: Update, fetch=_get, run=subprocess.run) -> None:
    """Downloads, checks and installs the wheel into this virtualenv; raises OSError with a sentence."""
    if not is_installed_copy():
        raise OSError("this copy runs from a checkout: update it with git pull")
    expected = expected_sha256(fetch(update.sums_url).decode("utf-8", "replace"), update.wheel_name)
    if expected is None:
        raise OSError("the release does not list a checksum for its linux package")
    wheel = fetch(update.wheel_url, timeout=120)
    if hashlib.sha256(wheel).hexdigest() != expected:
        raise OSError("the downloaded package did not match its checksum, so it was not installed")
    with tempfile.TemporaryDirectory(prefix="openclicky-update-") as folder:
        path = Path(folder) / update.wheel_name
        path.write_bytes(wheel)
        finished = run([sys.executable, "-m", "pip", "install", "--quiet", "--upgrade", str(path)], capture_output=True, text=True, timeout=600)
    if finished.returncode != 0:
        raise OSError(f"the update could not be installed: {(finished.stderr or '').strip()[-200:]}")
    applog.append(f"updates: installed {update.version}")


def restart() -> None:
    """OpenClicky starts again as the new version (the same command, the same arguments)."""
    os.execv(sys.executable, [sys.executable, "-m", "openclicky_linux", *sys.argv[1:]])
