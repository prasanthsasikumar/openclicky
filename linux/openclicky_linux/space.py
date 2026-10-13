"""The dictionary and spoken shortcuts, from the same files the Mac app keeps under
`~/.openclicky/dictation` (`dictionary.json`, `shortcuts.json`). Read-only here: the Mac app's
window is where they are edited, and the files sync however the person syncs that folder.
"""

from __future__ import annotations

import json
from dataclasses import dataclass, field
from pathlib import Path

from .config import openclicky_directory


@dataclass(frozen=True)
class DictionaryTerm:
    written: str
    heard_as: tuple[str, ...] = ()


@dataclass(frozen=True)
class SpokenShortcut:
    trigger: str
    replacement: str


@dataclass(frozen=True)
class DictationSpace:
    dictionary: tuple[DictionaryTerm, ...] = field(default_factory=tuple)
    shortcuts: tuple[SpokenShortcut, ...] = field(default_factory=tuple)


def _read_json_list(path: Path) -> list:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return []
    return value if isinstance(value, list) else []


def load_space(directory: Path | None = None) -> DictationSpace:
    directory = directory or openclicky_directory() / "dictation"
    terms = []
    for entry in _read_json_list(directory / "dictionary.json"):
        if not isinstance(entry, dict) or not isinstance(entry.get("written"), str) or not entry["written"].strip():
            continue
        heard_as = tuple(alias for alias in entry.get("heardAs") or [] if isinstance(alias, str) and alias.strip())
        terms.append(DictionaryTerm(written=entry["written"], heard_as=heard_as))
    shortcuts = []
    for entry in _read_json_list(directory / "shortcuts.json"):
        if isinstance(entry, dict) and isinstance(entry.get("trigger"), str) and isinstance(entry.get("replacement"), str):
            shortcuts.append(SpokenShortcut(trigger=entry["trigger"], replacement=entry["replacement"]))
    return DictationSpace(dictionary=tuple(terms), shortcuts=tuple(shortcuts))
