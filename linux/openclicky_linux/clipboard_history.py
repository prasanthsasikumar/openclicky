"""Opt-in clipboard history (Settings → Dictation), the Mac's `ClipboardHistoryMonitor`: what you
copy shows up in the history beside what you dictated. Only plain text, at most 20,000 characters,
never while incognito, never a password manager's copy (KeePassXC and KDE mark theirs with
`x-kde-passwordManagerHint`), and never a take's own paste (already in the history as the take).

The X server says when the clipboard changes hands (GTK's owner-change); on Wayland XWayland does,
as GNOME hands every copy over to it.
"""

from __future__ import annotations

import time
from typing import Callable

import gi

gi.require_version("Gtk", "3.0")
gi.require_version("Gdk", "3.0")
from gi.repository import Gdk, Gtk  # noqa: E402

from . import applog  # noqa: E402
from .history import TakeRecord, TakeStore  # noqa: E402

MAXIMUM_CHARACTERS = 20_000
# A take's paste changes the clipboard (the words, then what was there before): neither is a copy.
AFTER_A_PASTE_SECONDS = 1.5
SECRET_TARGETS = frozenset({"x-kde-passwordManagerHint"})


def should_record(text: str | None, targets: list[str], last_text: str | None, seconds_since_paste: float) -> str | None:
    """The text to keep, or None."""
    if seconds_since_paste < AFTER_A_PASTE_SECONDS or SECRET_TARGETS & set(targets):
        return None
    text = (text or "").strip()
    if not text or text == last_text:
        return None
    return text[:MAXIMUM_CHARACTERS]


class ClipboardHistoryMonitor:
    def __init__(self, take_store: TakeStore, is_enabled: Callable[[], bool], last_paste_at: Callable[[], float],
                 app_in_front: Callable[[], str | None] = lambda: None) -> None:
        self.take_store = take_store
        self.is_enabled = is_enabled
        self.last_paste_at = last_paste_at
        self.app_in_front = app_in_front
        self.clipboard = Gtk.Clipboard.get(Gdk.SELECTION_CLIPBOARD)
        self.clipboard.connect("owner-change", lambda _clipboard, _event: self._changed())

    def _changed(self) -> None:
        if not self.is_enabled():
            return
        seconds_since_paste = time.monotonic() - self.last_paste_at()
        if seconds_since_paste < AFTER_A_PASTE_SECONDS:
            return
        app = self.app_in_front()
        self.clipboard.request_targets(lambda _clipboard, atoms, *_rest: self._got_targets(
            [atom.name() for atom in atoms or []], seconds_since_paste, app))

    def _got_targets(self, targets: list[str], seconds_since_paste: float, app: str | None) -> None:
        if SECRET_TARGETS & set(targets):
            return
        self.clipboard.request_text(lambda _clipboard, text: self._got_text(text, targets, seconds_since_paste, app))

    def _got_text(self, text: str | None, targets: list[str], seconds_since_paste: float, app: str | None) -> None:
        latest = self.take_store.recent(limit=1)
        kept = should_record(text, targets, latest[0].display_text if latest else None, seconds_since_paste)
        if kept is None:
            return
        try:
            self.take_store.insert(TakeRecord(raw_text="", formatted_text=kept, mode="clipboard", app_bundle_id=app, app_name=app,
                                              engine="clipboard", paste_outcome="none"))
        except Exception as failure:  # noqa: BLE001 - a copy that is not kept must never get in the way
            applog.append(f"clipboard copy not saved to history: {type(failure).__name__}")
