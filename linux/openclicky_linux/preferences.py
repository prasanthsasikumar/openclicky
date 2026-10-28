"""The app's own state that is not a setting the Mac shares (the Mac keeps it in UserDefaults, Windows
in preferences.json): whether the first-run setup and the tour are done, and the like. One JSON
object at `$XDG_CONFIG_HOME/openclicky/preferences.json`, written atomically; unreadable is empty.
"""

from __future__ import annotations

import json
import os
import tempfile
from pathlib import Path
from typing import Any

SETUP_DONE = "setupDone"
TOUR_STEP = "tourStep"
TOUR_DONE = "tourDone"


def preferences_path() -> Path:
    base = os.environ.get("XDG_CONFIG_HOME") or str(Path.home() / ".config")
    return Path(base) / "openclicky" / "preferences.json"


def load() -> dict[str, Any]:
    try:
        value = json.loads(preferences_path().read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return {}
    return value if isinstance(value, dict) else {}


def get(key: str, default: Any = None) -> Any:
    return load().get(key, default)


def set_values(**values: Any) -> None:
    """Sets (or, with None, removes) the given keys and keeps the rest."""
    current = load()
    for key, value in values.items():
        if value is None:
            current.pop(key, None)
        else:
            current[key] = value
    path = preferences_path()
    path.parent.mkdir(parents=True, exist_ok=True)
    file_descriptor, temporary_path = tempfile.mkstemp(dir=path.parent, prefix=".preferences-", suffix=".json")
    try:
        with os.fdopen(file_descriptor, "w", encoding="utf-8") as temporary_file:
            json.dump(current, temporary_file, indent=2)
        os.replace(temporary_path, path)
    except BaseException:
        if os.path.exists(temporary_path):
            os.unlink(temporary_path)
        raise
