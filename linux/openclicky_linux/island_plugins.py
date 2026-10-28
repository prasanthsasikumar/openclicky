"""The island's plugins (the Mac's `Plugins/`), each off until switched on in Settings → The island:

- **System**: CPU, memory, disk and battery (`/proc`, `/sys`), sampled every 2 s while shown.
- **Now playing**: the track any MPRIS player is playing (Spotify, Rhythmbox, Firefox, VLC…), with
  previous / play-pause / next.
- **Coding agents**: what Claude Code and Codex sessions are doing, read from their own logs
  (`~/.claude/projects/*/*.jsonl`, `~/.codex/sessions/**/rollout-*.jsonl`; nothing written, no
  network), and Claude's plan limits from the usage endpoint on Claude Code's own sign-in
  (`~/.claude/.credentials.json`; at most every 5 minutes).
- **Shelf**: files, links and notes dropped on the island, kept until dragged out or removed.

The data side is here and pure where it can be (tested with fixtures); `island.py` draws it.
"""

from __future__ import annotations

import json
import os
import shutil
import time
import urllib.error
import urllib.request
from dataclasses import dataclass, field
from datetime import datetime
from pathlib import Path

SYSTEM, MEDIA, AGENTS, SHELF = "system", "media", "agents", "shelf"
PLUGIN_TITLES = {SYSTEM: "System", MEDIA: "Now playing", AGENTS: "Coding agents", SHELF: "Shelf"}

# MARK: System


@dataclass(frozen=True)
class SystemSnapshot:
    cpu_fraction: float
    memory_used_bytes: int
    memory_total_bytes: int
    disk_free_bytes: int
    disk_total_bytes: int
    battery_percent: int | None = None
    charging: bool = False


def gigabytes(bytes_count: int) -> str:
    return f"{bytes_count / 1024 ** 3:.0f}"


def system_figures(snapshot: SystemSnapshot) -> list[tuple[str, float, str]]:
    """(name, fraction for the ring, the long figure): "cpu 23%", "7 / 16 GB", "210 GB free", "86% ⚡"."""
    figures = [("cpu", snapshot.cpu_fraction, f"{snapshot.cpu_fraction:.0%}"),
               ("memory", snapshot.memory_used_bytes / max(1, snapshot.memory_total_bytes),
                f"{gigabytes(snapshot.memory_used_bytes)} / {gigabytes(snapshot.memory_total_bytes)} GB"),
               ("disk", 1 - snapshot.disk_free_bytes / max(1, snapshot.disk_total_bytes), f"{gigabytes(snapshot.disk_free_bytes)} GB free")]
    if snapshot.battery_percent is not None:
        figures.append(("battery", snapshot.battery_percent / 100, f"{snapshot.battery_percent}%{' ⚡' if snapshot.charging else ''}"))
    return figures


def is_warning(name: str, fraction: float) -> bool:
    """Amber above 85 %; a battery at or under 20 %."""
    return fraction <= 0.2 if name == "battery" else fraction > 0.85


class SystemSampler:
    def __init__(self, proc: Path = Path("/proc"), power_supplies: Path = Path("/sys/class/power_supply")) -> None:
        self.proc = proc
        self.power_supplies = power_supplies
        self._last_cpu: tuple[int, int] | None = None

    def _cpu_fraction(self) -> float:
        """Busy time over the time since the last sample (the first sample is 0)."""
        try:
            fields = [int(value) for value in (self.proc / "stat").read_text().splitlines()[0].split()[1:]]
        except (OSError, ValueError, IndexError):
            return 0.0
        idle, total = fields[3] + (fields[4] if len(fields) > 4 else 0), sum(fields)
        last, self._last_cpu = self._last_cpu, (idle, total)
        if last is None or total <= last[1]:
            return 0.0
        return max(0.0, min(1.0, 1 - (idle - last[0]) / (total - last[1])))

    def _memory(self) -> tuple[int, int]:
        values = {}
        try:
            for line in (self.proc / "meminfo").read_text().splitlines():
                name, _, rest = line.partition(":")
                values[name] = int(rest.split()[0]) * 1024
        except (OSError, ValueError, IndexError):
            return 0, 1
        total = values.get("MemTotal", 1)
        return total - values.get("MemAvailable", total), total

    def _battery(self) -> tuple[int | None, bool]:
        for supply in sorted(self.power_supplies.glob("BAT*")):
            try:
                capacity = int((supply / "capacity").read_text().strip())
                status = (supply / "status").read_text().strip().lower()
            except (OSError, ValueError):
                continue
            return capacity, status in ("charging", "full")
        return None, False

    def sample(self) -> SystemSnapshot:
        used, total = self._memory()
        try:
            disk = shutil.disk_usage(Path.home())
            disk_free, disk_total = disk.free, disk.total
        except OSError:
            disk_free, disk_total = 0, 1
        battery, charging = self._battery()
        return SystemSnapshot(self._cpu_fraction(), used, total, disk_free, disk_total, battery, charging)


# MARK: Now playing (MPRIS)

MPRIS_PREFIX = "org.mpris.MediaPlayer2."
MPRIS_PATH = "/org/mpris/MediaPlayer2"


@dataclass(frozen=True)
class Track:
    player: str  # the bus name
    playing: bool
    title: str
    artist: str

    @property
    def line(self) -> str:
        return f"♪ {self.title}" + (f" · {self.artist}" if self.artist else "")


def track_from(player: str, status: str, metadata: dict) -> Track | None:
    title = metadata.get("xesam:title") or ""
    if not title:
        return None
    artists = metadata.get("xesam:artist") or []
    artist = ", ".join(artists) if isinstance(artists, list) else str(artists)
    return Track(player, status == "Playing", str(title), artist)


class MediaPlayers:
    """The players on the session bus, asked only while the plugin is shown or on (never launched)."""

    def _bus(self):
        from gi.repository import Gio

        return Gio.bus_get_sync(Gio.BusType.SESSION, None)

    def _call(self, name: str, interface: str, method: str, parameters=None, timeout_milliseconds: int = 1000):
        from gi.repository import Gio

        return self._bus().call_sync(name, MPRIS_PATH, interface, method, parameters, None, Gio.DBusCallFlags.NONE,
                                     timeout_milliseconds, None)

    def players(self) -> list[str]:
        from gi.repository import Gio

        try:
            names = self._bus().call_sync("org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "ListNames",
                                          None, None, Gio.DBusCallFlags.NONE, 1000, None).unpack()[0]
        except Exception:  # noqa: BLE001 - no session bus: nothing playing
            return []
        return sorted(name for name in names if name.startswith(MPRIS_PREFIX))

    def now_playing(self) -> Track | None:
        """The playing track (the first player playing, else the first that has one)."""
        from gi.repository import GLib

        found = []
        for player in self.players():
            try:
                properties = self._call(player, "org.freedesktop.DBus.Properties", "GetAll",
                                        GLib.Variant("(s)", ("org.mpris.MediaPlayer2.Player",))).unpack()[0]
            except Exception:  # noqa: BLE001 - a player that went away
                continue
            track = track_from(player, properties.get("PlaybackStatus", ""), properties.get("Metadata", {}) or {})
            if track is not None:
                found.append(track)
        return next((track for track in found if track.playing), found[0] if found else None)

    def command(self, player: str, method: str) -> None:
        """"PlayPause", "Next" or "Previous"."""
        try:
            self._call(player, "org.mpris.MediaPlayer2.Player", method)
        except Exception:  # noqa: BLE001
            pass


# MARK: Coding agents

LIVE_SECONDS = 120
RECENT_SECONDS = 600
TAIL_BYTES = 65_536
DETAIL_LENGTH = 40


@dataclass(frozen=True)
class Activity:
    kind: str  # "tool" | "thinking" | "writing" | "waiting" | "idle"
    name: str = ""
    detail: str | None = None

    @property
    def line(self) -> str:
        if self.kind == "tool":
            verb = {"edit": "editing", "write": "editing", "multiedit": "editing", "notebookedit": "editing", "apply_patch": "editing",
                    "read": "reading", "bash": "running:", "shell": "running:", "exec": "running:", "exec_command": "running:",
                    "shell_command": "running:", "grep": "searching", "glob": "searching", "webfetch": "browsing",
                    "websearch": "browsing"}.get(self.name.lower(), self.name.lower())
            if not self.detail:
                return "running" if verb == "running:" else verb
            return f"{verb} {self.detail}"
        return {"thinking": "thinking", "writing": "writing", "waiting": "waiting", "idle": "done"}[self.kind]


@dataclass(frozen=True)
class AgentSession:
    id: str
    agent: str  # "claude" | "codex"
    project: str
    title: str | None
    activity: Activity
    last_event_at: float

    def is_live(self, now: float) -> bool:
        return self.activity.kind != "idle" and now - self.last_event_at < LIVE_SECONDS


def _records(text: str) -> list[dict]:
    found = []
    for line in text.splitlines():
        try:
            value = json.loads(line)
        except ValueError:
            continue
        if isinstance(value, dict):
            found.append(value)
    return found


def _first_line(text: str, limit: int) -> str:
    lines = text.strip().splitlines()
    return lines[0][:limit] if lines else ""


def _tool_detail(file_path: str | None, command: str | None) -> str | None:
    if file_path:
        return Path(file_path).name
    if command:
        return _first_line(command, DETAIL_LENGTH).strip()
    return None


def parse_claude_tail(text: str) -> tuple[Activity, str | None, str | None]:
    """(activity, title, folder) from the tail of a Claude Code session log."""
    last_assistant, user_after, title, cwd = None, False, None, None
    for record in _records(text):
        cwd = record.get("cwd") if isinstance(record.get("cwd"), str) else cwd
        kind = record.get("type")
        if kind == "assistant":
            last_assistant, user_after = record, False
        elif kind == "user" and last_assistant is not None:
            user_after = True
        elif kind == "ai-title" and record.get("aiTitle"):
            title = record["aiTitle"]
    message = (last_assistant or {}).get("message")
    if not isinstance(message, dict):
        return Activity("waiting"), title, cwd
    if user_after:
        return Activity("waiting"), title, cwd
    if message.get("stop_reason") in ("end_turn", "stop_sequence"):
        return Activity("idle"), title, cwd
    blocks = message.get("content")
    if not isinstance(blocks, list) or not blocks or not isinstance(blocks[-1], dict):
        return Activity("waiting"), title, cwd
    last = blocks[-1]
    if last.get("type") == "tool_use":
        tool_input = last.get("input") if isinstance(last.get("input"), dict) else {}
        return Activity("tool", last.get("name") or "tool", _tool_detail(tool_input.get("file_path"), tool_input.get("command"))), title, cwd
    if last.get("type") == "thinking":
        return Activity("thinking"), title, cwd
    if last.get("type") == "text":
        return Activity("writing", detail=_first_line(last.get("text") or "", 60)), title, cwd
    return Activity("waiting"), title, cwd


def _codex_detail(arguments) -> str | None:
    try:
        value = json.loads(arguments) if isinstance(arguments, str) else None
    except ValueError:
        return None
    if not isinstance(value, dict):
        return None
    command = value.get("command")
    if isinstance(command, list):
        return " ".join(str(part) for part in command)[:DETAIL_LENGTH].strip()
    if isinstance(command, str):
        return _first_line(command, DETAIL_LENGTH).strip()
    path = value.get("path") or value.get("file_path")
    return Path(path).name if isinstance(path, str) else None


def parse_codex_tail(text: str) -> tuple[Activity, str | None, str | None]:
    """(activity, title, folder) from the tail of a Codex rollout."""
    activity, title, cwd = Activity("waiting"), None, None
    for record in _records(text):
        payload = record.get("payload")
        if not isinstance(payload, dict):
            continue
        kind, payload_type = record.get("type"), payload.get("type")
        if kind == "session_meta" and isinstance(payload.get("cwd"), str):
            cwd = payload["cwd"]
        elif kind == "response_item":
            if payload_type == "function_call":
                activity = Activity("tool", payload.get("name") or "tool", _codex_detail(payload.get("arguments")))
            elif payload_type == "custom_tool_call":
                tool_input = payload.get("input")
                activity = Activity("tool", payload.get("name") or "tool", tool_input[:DETAIL_LENGTH] if isinstance(tool_input, str) else None)
            elif payload_type == "reasoning":
                activity = Activity("thinking")
            elif payload_type == "message":
                content = payload.get("content") if isinstance(payload.get("content"), list) else []
                text_part = next((part.get("text") for part in content if isinstance(part, dict) and isinstance(part.get("text"), str)), "")
                if payload.get("role") == "assistant":
                    activity = Activity("writing", detail=_first_line(text_part, 60))
                else:
                    activity = Activity("waiting")
                    title = title or (_first_line(text_part, 60) or None)
        elif kind == "event_msg":
            if payload_type == "task_complete":
                activity = Activity("idle")
            elif payload_type == "task_started":
                activity = Activity("waiting")
    return activity, title, cwd


def _tail(path: Path) -> str:
    try:
        with path.open("rb") as log:
            log.seek(max(0, path.stat().st_size - TAIL_BYTES))
            return log.read().decode("utf-8", "replace")
    except OSError:
        return ""


def is_own_scratch_rollout(cwd: str | None) -> bool:
    """OpenClicky's own plan answers run Codex in a scratch folder: not the person's sessions."""
    return bool(cwd) and "openclicky-plan-answers" in cwd


def agent_sessions(claude_projects: Path | None = None, codex_sessions: Path | None = None, now: float | None = None) -> list[AgentSession]:
    """The sessions written in the last ten minutes, live first, then newest."""
    now = now if now is not None else time.time()
    home = Path.home()
    claude_projects = claude_projects or Path(os.environ.get("CLAUDE_CONFIG_DIR") or home / ".claude") / "projects"
    codex_sessions = codex_sessions or home / ".codex" / "sessions"
    found: list[AgentSession] = []

    def recent(paths) -> list[tuple[Path, float]]:
        recent_files = []
        for path in paths:
            try:
                modified = path.stat().st_mtime
            except OSError:
                continue
            if now - modified < RECENT_SECONDS:
                recent_files.append((path, modified))
        return recent_files

    for path, modified in recent(claude_projects.glob("*/*.jsonl")):
        activity, title, cwd = parse_claude_tail(_tail(path))
        found.append(AgentSession(str(path), "claude", Path(cwd).name if cwd else "claude", title, activity, modified))
    days = [datetime.fromtimestamp(now - offset * 86400) for offset in (0, 1)]
    for day in days:
        folder = codex_sessions / f"{day:%Y}" / f"{day:%m}" / f"{day:%d}"
        for path, modified in recent(folder.glob("rollout-*.jsonl")):
            activity, title, cwd = parse_codex_tail(_tail(path))
            if is_own_scratch_rollout(cwd):
                continue
            found.append(AgentSession(str(path), "codex", Path(cwd).name if cwd else "codex", title, activity, modified))
    found.sort(key=lambda session: (not session.is_live(now), -session.last_event_at))
    return found


def peek_line(sessions: list[AgentSession], now: float | None = None) -> str | None:
    """"claude · editing Foo.py" for the newest live session."""
    now = now if now is not None else time.time()
    live = next((session for session in sessions if session.is_live(now)), None)
    return f"{live.agent} · {live.activity.line}" if live else None


# MARK: Claude's plan limits

USAGE_ENDPOINT = "https://api.anthropic.com/api/oauth/usage"
USAGE_FETCH_SECONDS = 300


@dataclass(frozen=True)
class UsageWindow:
    fraction: float
    resets_at: float | None


def parse_usage(data: bytes) -> tuple[UsageWindow, UsageWindow] | None:
    """`five_hour` and `seven_day`, each with `utilization` (0…100) and `resets_at`."""
    try:
        value = json.loads(data)
    except ValueError:
        return None

    def window(entry) -> UsageWindow | None:
        if not isinstance(entry, dict) or not isinstance(entry.get("utilization"), (int, float)):
            return None
        resets = None
        if isinstance(entry.get("resets_at"), str):
            try:
                resets = datetime.fromisoformat(entry["resets_at"].replace("Z", "+00:00")).timestamp()
            except ValueError:
                resets = None
        return UsageWindow(min(max(entry["utilization"] / 100, 0.0), 1.0), resets)

    five, seven = window((value or {}).get("five_hour")), window((value or {}).get("seven_day"))
    return (five, seven) if five and seven else None


def reset_text(resets_at: float | None, now: float | None = None) -> str:
    """"resets 2 h 10 m", "resets 35 m", "resets soon"; nothing without a date."""
    if resets_at is None:
        return ""
    seconds = int(resets_at - (now if now is not None else time.time()))
    if seconds < 60:
        return "resets soon"
    hours, minutes = seconds // 3600, (seconds % 3600) // 60
    return f"resets {hours} h {minutes} m" if hours else f"resets {minutes} m"


def claude_access_token(path: Path | None = None) -> str | None:
    """Claude Code's own sign-in on Linux (where the Mac reads its Keychain)."""
    path = path or Path(os.environ.get("CLAUDE_CONFIG_DIR") or Path.home() / ".claude") / ".credentials.json"
    try:
        oauth = json.loads(path.read_text(encoding="utf-8")).get("claudeAiOauth") or {}
    except (OSError, ValueError, AttributeError):
        return None
    token = oauth.get("accessToken") if isinstance(oauth, dict) else None
    return token if isinstance(token, str) and token else None


def fetch_usage(token: str) -> tuple[UsageWindow, UsageWindow] | None:
    request = urllib.request.Request(USAGE_ENDPOINT, headers={"Authorization": f"Bearer {token}", "anthropic-beta": "oauth-2025-04-20",
                                                              "Accept": "application/json"})
    try:
        with urllib.request.urlopen(request, timeout=10) as response:
            return parse_usage(response.read())
    except (urllib.error.URLError, OSError, TimeoutError):
        return None


# MARK: Shelf

MAXIMUM_SHELF_ITEMS = 40


@dataclass(frozen=True)
class ShelfItem:
    kind: str  # "file" | "link" | "note"
    value: str  # a path, an address, or the note's text
    added_at: float = field(default_factory=time.time)

    @property
    def label(self) -> str:
        if self.kind == "file":
            return Path(self.value).name
        return self.value if len(self.value) <= 40 else self.value[:39] + "…"


def shelf_items_from_drop(uris: list[str] | None, text: str | None) -> list[ShelfItem]:
    """Files first, else a web link, else the text as a note (the Mac's `ShelfDropReader`)."""
    from urllib.parse import unquote, urlsplit

    files = [unquote(urlsplit(uri).path) for uri in uris or [] if uri.startswith("file://")]
    if files:
        return [ShelfItem("file", path) for path in files]
    text = (text or "").strip()
    if not text:
        return []
    if text.startswith(("http://", "https://")) and " " not in text:
        return [ShelfItem("link", text)]
    return [ShelfItem("note", text)]


class Shelf:
    """Newest first, at most 40, in `$XDG_DATA_HOME/openclicky/shelf/index.json`."""

    def __init__(self, directory: Path | None = None) -> None:
        base = Path(os.environ.get("XDG_DATA_HOME") or Path.home() / ".local" / "share")
        self.directory = directory or base / "openclicky" / "shelf"

    def items(self) -> list[ShelfItem]:
        try:
            entries = json.loads((self.directory / "index.json").read_text(encoding="utf-8"))
        except (OSError, ValueError):
            return []
        return [ShelfItem(entry["kind"], entry["value"], float(entry.get("addedAt", 0))) for entry in entries
                if isinstance(entry, dict) and entry.get("kind") in ("file", "link", "note") and isinstance(entry.get("value"), str)]

    def _write(self, items: list[ShelfItem]) -> None:
        self.directory.mkdir(parents=True, exist_ok=True, mode=0o700)
        temporary = self.directory / ".index.tmp"
        temporary.write_text(json.dumps([{"kind": item.kind, "value": item.value, "addedAt": item.added_at}
                                         for item in items[:MAXIMUM_SHELF_ITEMS]], indent=2), encoding="utf-8")
        temporary.replace(self.directory / "index.json")

    def add(self, new_items: list[ShelfItem]) -> None:
        existing = [item for item in self.items() if (item.kind, item.value) not in {(new.kind, new.value) for new in new_items}]
        self._write(new_items + existing)

    def remove(self, item: ShelfItem) -> None:
        self._write([kept for kept in self.items() if (kept.kind, kept.value) != (item.kind, item.value)])

