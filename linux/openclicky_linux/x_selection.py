"""Wayland: the clipboard and the primary selection, owned through XWayland so the moment the
compositor has taken them up can be seen.

OpenClicky's windows are X11 ones, so its clipboard is XWayland's, which the compositor copies
across to Wayland apps. That copy is not instant: a paste keystroke sent straight after setting the
clipboard reached a native Wayland app before the compositor had the new text, and the app pasted
whatever was on the clipboard before (live-tested on GNOME 42). So the selections are owned here,
with python-xlib, and every request for them is answered here: the compositor's first request
(TARGETS, to learn what can be pasted) is the sign it now offers our words to Wayland apps, and only
then is the keystroke sent (`offer(..., on_taken_up=…)`; after `TAKE_UP_TIMEOUT_SECONDS` it is sent
anyway and the log says so). Everything runs on the GTK main loop (the X connection is watched with
an io watch), so no thread touches the connection.
"""

from __future__ import annotations

import time
from typing import Callable

from gi.repository import GLib
from Xlib import X, Xatom, display as xdisplay
from Xlib.protocol import event as xevent

from . import applog

TAKE_UP_TIMEOUT_SECONDS = 0.5
# After the compositor's TARGETS request is answered, a moment for it to make the selection its own.
AFTER_TAKE_UP_SECONDS = 0.03
# The clipboard is what must be taken up (pasted early, an app would paste the one from before); the
# primary selection, which terminals paste, gets this long more, since a compositor without it
# never asks for it at all.
PRIMARY_GRACE_SECONDS = 0.15
TEXT_TARGETS = ("UTF8_STRING", "text/plain;charset=utf-8", "STRING", "TEXT", "text/plain")


class SelectionOwner:
    def __init__(self, display_name: str | None = None) -> None:
        self._display = xdisplay.Display(display_name)
        screen = self._display.screen()
        self._window = screen.root.create_window(-10, -10, 1, 1, 0, X.CopyFromParent, X.InputOnly, X.CopyFromParent,
                                                 event_mask=X.PropertyChangeMask)
        self._atom = self._display.intern_atom
        self._targets = self._atom("TARGETS")
        self._timestamp_atom = self._atom("TIMESTAMP")
        self._selections = {name: self._atom(name) for name in ("CLIPBOARD", "PRIMARY")}
        self._text: dict[int, bytes] = {}
        self._owned_at: dict[int, int] = {}
        # Selections offered and not yet asked for, with what to do once they all are.
        self._waiting: tuple[set[int], Callable[[bool], None], int] | None = None
        self._wait_generation = 0
        # The offer whose clipboard was taken up: its timeout no longer gives up on it.
        self._taken_up_generation = 0
        GLib.io_add_watch(self._display.fileno(), GLib.PRIORITY_DEFAULT, GLib.IO_IN, self._on_readable)

    def _server_time(self) -> int:
        """A real X timestamp (CurrentTime is refused for ownership by some clients)."""
        self._window.change_property(self._timestamp_atom, Xatom.INTEGER, 32, [0])
        self._display.flush()
        deadline = time.monotonic() + 0.5
        while time.monotonic() < deadline:
            while self._display.pending_events():
                event = self._display.next_event()
                if event.type == X.PropertyNotify and event.window.id == self._window.id:
                    return event.time
                self._handle(event)
            time.sleep(0.002)
        return X.CurrentTime

    def offer(self, text: str, selections: tuple[str, ...] = ("CLIPBOARD", "PRIMARY"),
              on_taken_up: Callable[[bool], None] | None = None, timeout_seconds: float = TAKE_UP_TIMEOUT_SECONDS) -> None:
        """Owns the selections with `text`. `on_taken_up(True)` once another client (the compositor)
        has asked for each of them; `on_taken_up(False)` after the timeout."""
        timestamp = self._server_time()
        atoms = {self._selections[name] for name in selections}
        for atom in atoms:
            self._text[atom] = text.encode("utf-8")
            self._window.set_selection_owner(atom, timestamp)
            self._owned_at[atom] = timestamp
        self._display.flush()
        self._wait_generation += 1
        if on_taken_up is None:
            self._waiting = None
            return
        generation = self._wait_generation
        self._waiting = (set(atoms), on_taken_up, generation)

        def give_up() -> bool:
            if self._waiting is not None and self._waiting[2] == generation and self._taken_up_generation != generation:
                self._waiting = None
                on_taken_up(False)
            return False
        GLib.timeout_add(int(timeout_seconds * 1000), give_up)

    def _taken_up(self, after_seconds: float) -> None:
        """Calls the waiting `on_taken_up(True)` after `after_seconds`, unless a newer offer came."""
        _waiting_for, on_taken_up, generation = self._waiting
        self._taken_up_generation = generation

        def call() -> bool:
            if self._waiting is not None and self._waiting[2] == generation:
                self._waiting = None
                on_taken_up(True)
            return False
        GLib.timeout_add(int(after_seconds * 1000), call)

    def owns(self, selection: str = "CLIPBOARD") -> bool:
        return self._selections[selection] in self._text

    def text(self, selection: str = "CLIPBOARD") -> str | None:
        data = self._text.get(self._selections[selection])
        return data.decode("utf-8") if data is not None else None

    # MARK: answering requests

    def _on_readable(self, _fd, _condition) -> bool:
        while self._display.pending_events():
            self._handle(self._display.next_event())
        return True

    def _handle(self, event) -> None:
        if event.type == X.SelectionClear:
            # Someone else copied something: their text is the clipboard now.
            self._text.pop(event.atom, None)
            return
        if event.type != X.SelectionRequest:
            return
        atom = event.selection
        target = event.target
        property_atom = event.property or target
        data = self._text.get(atom)
        answered = False
        if data is not None:
            if target == self._targets:
                targets = [self._targets, self._timestamp_atom] + [self._atom(name) for name in TEXT_TARGETS]
                event.requestor.change_property(property_atom, Xatom.ATOM, 32, targets)
                answered = True
            elif target == self._timestamp_atom:
                event.requestor.change_property(property_atom, Xatom.INTEGER, 32, [self._owned_at.get(atom, 0)])
                answered = True
            elif target in [self._atom(name) for name in TEXT_TARGETS]:
                payload = data if target != Xatom.STRING else data.decode("utf-8").encode("latin-1", "replace")
                event.requestor.change_property(property_atom, target, 8, payload)
                answered = True
        notify = xevent.SelectionNotify(time=event.time, requestor=event.requestor, selection=atom, target=target,
                                        property=property_atom if answered else X.NONE)
        event.requestor.send_event(notify)
        self._display.flush()
        if answered and self._waiting is not None and event.requestor.id != self._window.id:
            waiting_for, _on_taken_up, _generation = self._waiting
            waiting_for.discard(atom)
            if not waiting_for:
                self._taken_up(AFTER_TAKE_UP_SECONDS)
            elif waiting_for == {self._selections["PRIMARY"]}:
                self._taken_up(PRIMARY_GRACE_SECONDS)
        if data is not None and not answered:
            applog.append(f"clipboard: a request for {self._display.get_atom_name(target)} was refused")
