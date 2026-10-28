"""Every take, on this computer, in SQLite: what was heard, what was written, where it went. The Mac's
`TakeStore.swift` with the same tables and columns (so a database can be read by either), at
`$XDG_DATA_HOME/openclicky/dictation.sqlite`. Nothing in it ever leaves the computer; incognito
(Settings) keeps takes out of it, and the Words window's history can delete one or all.

A take an engine could not finish keeps its audio beside it (`TakeAudioStore`, the Mac's), so the
history can retry it once the network, the key or the credits are back: at most 100 takes and
1 GB, the oldest dropped first, deleted once retried, and none at all when Settings says so.
"""

from __future__ import annotations

import os
import sqlite3
import threading
import time
import uuid
from dataclasses import dataclass, field
from datetime import datetime, timedelta
from pathlib import Path

SCHEMA = (
    """CREATE TABLE IF NOT EXISTS takes (
        id TEXT PRIMARY KEY,
        created_at REAL NOT NULL,
        mode TEXT NOT NULL,
        status TEXT NOT NULL,
        raw_text TEXT NOT NULL,
        formatted_text TEXT NOT NULL,
        app_bundle_id TEXT,
        app_name TEXT,
        language TEXT,
        engine TEXT NOT NULL DEFAULT '',
        duration_seconds REAL NOT NULL DEFAULT 0,
        paste_outcome TEXT NOT NULL DEFAULT 'none',
        pinned INTEGER NOT NULL DEFAULT 0,
        failure_reason TEXT
    )""",
    "CREATE INDEX IF NOT EXISTS takes_created_at ON takes (created_at DESC)",
    """CREATE TABLE IF NOT EXISTS take_revisions (
        id TEXT PRIMARY KEY,
        take_id TEXT NOT NULL REFERENCES takes(id) ON DELETE CASCADE,
        created_at REAL NOT NULL,
        previous_text TEXT NOT NULL,
        new_text TEXT NOT NULL,
        editor TEXT NOT NULL
    )""",
)
COLUMNS = ("id", "created_at", "mode", "status", "raw_text", "formatted_text", "app_bundle_id", "app_name", "language", "engine",
           "duration_seconds", "paste_outcome", "pinned", "failure_reason")


def default_path() -> Path:
    base = os.environ.get("XDG_DATA_HOME") or str(Path.home() / ".local" / "share")
    return Path(base) / "openclicky" / "dictation.sqlite"


@dataclass
class TakeRecord:
    raw_text: str
    formatted_text: str
    id: str = field(default_factory=lambda: str(uuid.uuid4()).upper())
    created_at: float = field(default_factory=time.time)
    mode: str = "dictate"  # dictate | edit | clipboard
    status: str = "complete"  # complete | failed | cancelled
    app_bundle_id: str | None = None  # the window class on Linux
    app_name: str | None = None
    language: str | None = None
    engine: str = ""
    duration_seconds: float = 0.0
    paste_outcome: str = "none"  # verified | posted | leftInOrb | leftOnPasteboard | none
    pinned: bool = False
    failure_reason: str | None = None

    @property
    def display_text(self) -> str:
        return self.formatted_text or self.raw_text

    @property
    def word_count(self) -> int:
        return len(self.display_text.split())


@dataclass(frozen=True)
class TakeStats:
    words_today: int
    takes_today: int
    words_this_week: int
    takes_this_week: int
    all_time_takes: int


class TakeStore:
    def __init__(self, path: Path | None = None) -> None:
        self.path = path or default_path()
        self.path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
        # One connection, used from the main loop and the take's worker: serialised by the lock.
        self._connection = sqlite3.connect(str(self.path), check_same_thread=False, timeout=2.0)
        self._lock = threading.Lock()
        with self._lock:
            self._connection.execute("PRAGMA journal_mode = WAL")
            self._connection.execute("PRAGMA foreign_keys = ON")
            for statement in SCHEMA:
                self._connection.execute(statement)
            self._connection.commit()
        try:
            os.chmod(self.path, 0o600)
        except OSError:
            pass

    def close(self) -> None:
        with self._lock:
            self._connection.close()

    # MARK: writes

    def insert(self, take: TakeRecord) -> None:
        with self._lock:
            self._connection.execute(
                f"INSERT OR REPLACE INTO takes ({', '.join(COLUMNS)}) VALUES ({', '.join('?' * len(COLUMNS))})",
                (take.id, take.created_at, take.mode, take.status, take.raw_text, take.formatted_text, take.app_bundle_id, take.app_name,
                 take.language, take.engine, take.duration_seconds, take.paste_outcome, 1 if take.pinned else 0, take.failure_reason))
            self._connection.commit()

    def revise(self, take_id: str, new_text: str, editor: str = "history") -> None:
        """Rewrites a take's text, keeping the previous text as a revision."""
        current = self.fetch(take_id)
        if current is None:
            return
        with self._lock:
            self._connection.execute(
                "INSERT INTO take_revisions (id, take_id, created_at, previous_text, new_text, editor) VALUES (?, ?, ?, ?, ?, ?)",
                (str(uuid.uuid4()).upper(), take_id, time.time(), current.display_text, new_text, editor))
            self._connection.execute("UPDATE takes SET formatted_text = ? WHERE id = ?", (new_text, take_id))
            self._connection.commit()

    def complete_retried(self, take_id: str, raw_text: str, formatted_text: str) -> None:
        """A failed take heard again: its words, and it is complete now."""
        with self._lock:
            self._connection.execute("UPDATE takes SET status = 'complete', raw_text = ?, formatted_text = ?, failure_reason = NULL, "
                                     "paste_outcome = 'leftOnPasteboard' WHERE id = ?", (raw_text, formatted_text, take_id))
            self._connection.commit()

    def set_pinned(self, take_id: str, pinned: bool) -> None:
        with self._lock:
            self._connection.execute("UPDATE takes SET pinned = ? WHERE id = ?", (1 if pinned else 0, take_id))
            self._connection.commit()

    def delete(self, take_id: str) -> None:
        with self._lock:
            self._connection.execute("DELETE FROM takes WHERE id = ?", (take_id,))
            self._connection.commit()

    def delete_all(self) -> None:
        with self._lock:
            self._connection.execute("DELETE FROM take_revisions")
            self._connection.execute("DELETE FROM takes")
            self._connection.commit()

    # MARK: reads

    @staticmethod
    def _record(row: tuple) -> TakeRecord:
        values = dict(zip(COLUMNS, row))
        values["pinned"] = bool(values["pinned"])
        return TakeRecord(**values)

    def fetch(self, take_id: str) -> TakeRecord | None:
        with self._lock:
            row = self._connection.execute(f"SELECT {', '.join(COLUMNS)} FROM takes WHERE id = ?", (take_id,)).fetchone()
        return self._record(row) if row else None

    def recent(self, limit: int = 500, query: str | None = None, mode: str | None = None) -> list[TakeRecord]:
        """Newest first. `query` matches every word in either text, case-insensitively."""
        sql = f"SELECT {', '.join(COLUMNS)} FROM takes WHERE 1 = 1"
        arguments: list = []
        for word in (query or "").split():
            # LIKE wildcards typed into the search box are not wildcards to the person: dropped.
            pattern = "%" + word.replace("%", "").replace("_", "") + "%"
            sql += " AND (raw_text LIKE ? OR formatted_text LIKE ?)"
            arguments += [pattern, pattern]
        if mode:
            sql += " AND mode = ?"
            arguments.append(mode)
        sql += f" ORDER BY created_at DESC LIMIT {max(1, int(limit))}"
        with self._lock:
            rows = self._connection.execute(sql, arguments).fetchall()
        return [self._record(row) for row in rows]

    def matches_for(self, question: str) -> list[TakeRecord]:
        """The takes a history question is answered from: those holding every longer word of it;
        when none do, the latest (the Mac's `historyMatches`)."""
        words = [word for word in (part.strip("?!.,;:\"'“”") for part in question.split()) if len(word) > 3]
        matching = self.recent(limit=60, query=" ".join(words)) if words else []
        return [take for take in (matching or self.recent(limit=60)) if take.status == "complete"]

    def revised_take_ids(self) -> set[str]:
        with self._lock:
            return {row[0] for row in self._connection.execute("SELECT DISTINCT take_id FROM take_revisions")}

    def stats(self, now: datetime | None = None) -> TakeStats:
        now = now or datetime.now()
        start_of_today = datetime(now.year, now.month, now.day).timestamp()
        start_of_week = (datetime(now.year, now.month, now.day) - timedelta(days=6)).timestamp()
        week = [take for take in self.recent(limit=20000) if take.created_at >= start_of_week and take.mode != "clipboard" and take.status == "complete"]
        today = [take for take in week if take.created_at >= start_of_today]
        with self._lock:
            all_time = self._connection.execute("SELECT COUNT(*) FROM takes WHERE mode != 'clipboard'").fetchone()[0]
        return TakeStats(words_today=sum(take.word_count for take in today), takes_today=len(today),
                         words_this_week=sum(take.word_count for take in week), takes_this_week=len(week), all_time_takes=all_time)


class TakeAudioStore:
    MAXIMUM_TAKES = 100
    MAXIMUM_BYTES = 1_000_000_000

    def __init__(self, directory: Path | None = None) -> None:
        self.directory = directory or default_path().parent / "take-audio"

    def path_for(self, take_id: str) -> Path:
        return self.directory / f"{take_id}.wav"

    def has_audio(self, take_id: str) -> bool:
        return self.path_for(take_id).exists()

    def retain(self, take_id: str, wav: bytes) -> None:
        self.directory.mkdir(parents=True, exist_ok=True, mode=0o700)
        temporary = self.directory / f".{take_id}.tmp"
        temporary.write_bytes(wav)
        os.chmod(temporary, 0o600)
        temporary.replace(self.path_for(take_id))
        self.prune()

    def read(self, take_id: str) -> bytes:
        return self.path_for(take_id).read_bytes()

    def discard(self, take_id: str) -> None:
        self.path_for(take_id).unlink(missing_ok=True)

    def discard_all(self) -> None:
        for path in self.directory.glob("*.wav"):
            path.unlink(missing_ok=True)

    def prune(self) -> None:
        """Oldest first beyond the caps."""
        try:
            files = sorted(((path, path.stat()) for path in self.directory.glob("*.wav")), key=lambda entry: entry[1].st_mtime)
        except OSError:
            return
        total = sum(stat.st_size for _path, stat in files)
        while files and (len(files) > self.MAXIMUM_TAKES or total > self.MAXIMUM_BYTES):
            oldest, stat = files.pop(0)
            oldest.unlink(missing_ok=True)
            total -= stat.st_size
