"""The app's log, as the Mac app keeps ~/Library/Logs/OpenClicky/app.log: what happened and how
long it took, so a "it didn't work" can be looked into. Never the words: no dictated text, no
question, no answer, no key. `$XDG_STATE_HOME/openclicky/app.log` (~/.local/state/openclicky), cut
to its last half when it passes 2 MB at launch.
"""

from __future__ import annotations

import os
import threading
import time
from pathlib import Path

MAXIMUM_BYTES = 2 * 1024 * 1024
_lock = threading.Lock()
_path: Path | None = None


def log_path() -> Path:
    state_home = Path(os.environ.get("XDG_STATE_HOME") or Path.home() / ".local" / "state")
    return state_home / "openclicky" / "app.log"


def start() -> Path | None:
    """Called once at launch: makes the folder, trims an oversized log, and remembers where it is.
    A log that cannot be written (a read-only home, XDG_STATE_HOME pointing at a file) means no
    log, never an app that does not start: None then."""
    global _path
    path = log_path()
    try:
        path.parent.mkdir(parents=True, exist_ok=True)
        if path.exists() and path.stat().st_size > MAXIMUM_BYTES:
            kept = path.read_bytes()[-MAXIMUM_BYTES // 2:]
            # From the first whole line, so the log never starts mid-line.
            path.write_bytes(kept[kept.find(b"\n") + 1:])
    except OSError:
        return None
    _path = path
    append("openclicky started")
    return path


def append(line: str) -> None:
    """One line, with the time. Does nothing before `start` (the tests, `--check`), never raises."""
    if _path is None:
        return
    stamp = time.strftime("%Y-%m-%d %H:%M:%S")
    try:
        with _lock, open(_path, "a", encoding="utf-8") as log_file:
            log_file.write(f"{stamp} {line}\n")
    except OSError:
        pass
