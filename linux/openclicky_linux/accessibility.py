"""What the desktop's accessibility service (AT-SPI) says about the text box in front: the Mac's
`FocusedFieldReader`, `SelectedTextReader` and `WritingTarget`, for the people who switch it on
(Settings → Privacy → "Read text boxes"; it is the desktop's own switch, the one a screen reader
turns on, and off by default). With it on:

- **"rephrase this" is written in place** (`writing.py`): the highlighted text, or the whole text
  box the cursor is in, goes with the question, and the model's rewrite replaces it;
- **dictation knows a text box from no text box**, and never types into a password box;
- **the words around the cursor** help the clean-up spell names the way the box already does;
- **on Wayland the app in front has a name** (its styles, the skills, and whether it is a terminal).

The focused object is followed from AT-SPI's focus events (asking every app would take seconds).
Every call has a short timeout: an app that hangs never holds OpenClicky up. Apps started before
the switch came on may need reopening before they answer (Chromium and Electron check at start).
"""

from __future__ import annotations

from dataclasses import dataclass

import gi

from . import applog

MAXIMUM_TARGET_CHARACTERS = 6000
MAXIMUM_SELECTION_CHARACTERS = 600
NEARBY_CHARACTERS = 400
# Milliseconds an app has to answer one question.
CALL_TIMEOUT_MILLISECONDS = 250
TOOLKIT_SETTINGS = ("org.gnome.desktop.interface", "toolkit-accessibility")


def _atspi():
    gi.require_version("Atspi", "2.0")
    from gi.repository import Atspi

    return Atspi


def _a11y_status_proxy():
    from gi.repository import Gio

    bus = Gio.bus_get_sync(Gio.BusType.SESSION, None)
    return Gio.DBusProxy.new_sync(bus, Gio.DBusProxyFlags.NONE, None, "org.a11y.Bus", "/org/a11y/bus", "org.a11y.Status", None)


def is_enabled() -> bool:
    """Whether the desktop's accessibility is on (apps then answer AT-SPI)."""
    try:
        value = _a11y_status_proxy().get_cached_property("IsEnabled")
        return bool(value and value.unpack())
    except Exception:  # noqa: BLE001 - no session bus, no a11y bus: it is off
        return False


def set_enabled(enabled: bool) -> bool:
    """Switches the desktop's accessibility on or off (the a11y bus, and GNOME's own setting,
    which it keeps across logins). False when it could not."""
    from gi.repository import Gio, GLib

    changed = False
    try:
        proxy = _a11y_status_proxy()
        proxy.call_sync("org.freedesktop.DBus.Properties.Set",
                        GLib.Variant("(ssv)", ("org.a11y.Status", "IsEnabled", GLib.Variant("b", enabled))),
                        Gio.DBusCallFlags.NONE, 2000, None)
        changed = True
    except Exception as failure:  # noqa: BLE001
        applog.append(f"accessibility: the a11y bus was not switched ({type(failure).__name__})")
    try:
        schema_source = Gio.SettingsSchemaSource.get_default()
        if schema_source is not None and schema_source.lookup(TOOLKIT_SETTINGS[0], True) is not None:
            Gio.Settings.new(TOOLKIT_SETTINGS[0]).set_boolean(TOOLKIT_SETTINGS[1], enabled)
            changed = True
    except Exception as failure:  # noqa: BLE001
        applog.append(f"accessibility: the desktop setting was not changed ({type(failure).__name__})")
    applog.append(f"accessibility: switched {'on' if enabled else 'off'}" if changed else "accessibility: could not be switched")
    return changed


@dataclass(frozen=True)
class FocusedField:
    """The focused object as AT-SPI describes it."""
    app_name: str | None
    window_title: str | None
    is_editable: bool
    is_password: bool
    text: str | None  # the whole text (at most `MAXIMUM_TARGET_CHARACTERS` + 1, to tell it was longer)
    selection: tuple[int, int] | None  # character offsets of the first selection
    caret: int | None

    @property
    def selected_text(self) -> str | None:
        if self.text is None or self.selection is None or self.selection[0] == self.selection[1]:
            return None
        return self.text[self.selection[0]:self.selection[1]]

    @property
    def text_before_caret(self) -> str:
        if not self.text or self.caret is None:
            return ""
        return self.text[max(0, self.caret - NEARBY_CHARACTERS):self.caret]


def tidied_selection(text: str | None) -> str | None:
    """Whitespace collapsed and capped (the Mac's `SelectedTextReader.tidied`)."""
    collapsed = " ".join((text or "").split())
    if not collapsed:
        return None
    return collapsed if len(collapsed) <= MAXIMUM_SELECTION_CHARACTERS else collapsed[:MAXIMUM_SELECTION_CHARACTERS] + "…"


def selection_context_line(selected_text: str) -> str:
    return (f"The user has selected (highlighted) this text on screen: «{selected_text}». When they say \"this\", \"this line\" or "
            "\"this word\", they almost certainly mean the selection, answer about that text, not about whatever is under the pointer.")


class FocusTracker:
    """Follows the focused object through AT-SPI's focus events, from the main loop."""

    def __init__(self, own_process_id: int | None = None) -> None:
        import os

        Atspi = _atspi()
        Atspi.init()
        Atspi.set_timeout(CALL_TIMEOUT_MILLISECONDS, -1)
        self._own_process_id = own_process_id if own_process_id is not None else os.getpid()
        self.focused = None
        self._listener = Atspi.EventListener.new(self._on_event)
        self._listener.register("object:state-changed:focused")

    def _on_event(self, event) -> None:
        if not event.detail1:
            return
        try:
            if event.source.get_process_id() == self._own_process_id:
                return  # OpenClicky's own ask box, Settings: never the text being talked about
        except Exception:  # noqa: BLE001 - an app that went away
            return
        self.focused = event.source

    def field(self) -> FocusedField | None:
        """The focused object now, or None when nothing is known (or it went away)."""
        accessible = self.focused
        if accessible is None:
            return None
        Atspi = _atspi()
        try:
            states = accessible.get_state_set()
            if not states.contains(Atspi.StateType.FOCUSED):
                return None
            role = accessible.get_role()
            application = accessible.get_application()
            app_name = application.get_name() if application is not None else None
            window_title = _window_title(accessible, Atspi)
            is_password = role == Atspi.Role.PASSWORD_TEXT
            is_editable = states.contains(Atspi.StateType.EDITABLE)
            text, selection, caret = None, None, None
            text_interface = accessible.get_text_iface() if not is_password else None
            if text_interface is not None:
                count = Atspi.Text.get_character_count(accessible)
                text = Atspi.Text.get_text(accessible, 0, min(count, MAXIMUM_TARGET_CHARACTERS + 1))
                caret = Atspi.Text.get_caret_offset(accessible)
                if Atspi.Text.get_n_selections(accessible) > 0:
                    found = Atspi.Text.get_selection(accessible, 0)
                    selection = (found.start_offset, found.end_offset)
        except Exception as failure:  # noqa: BLE001 - a hung or departed app is simply not read
            applog.append(f"accessibility: the focused object could not be read ({type(failure).__name__})")
            return None
        return FocusedField(app_name, window_title, is_editable, is_password, text, selection, caret)

    def select_all(self) -> bool:
        """Selects the whole text of the focused box (before a rewrite replaces it)."""
        accessible = self.focused
        if accessible is None:
            return False
        Atspi = _atspi()
        try:
            count = Atspi.Text.get_character_count(accessible)
            if Atspi.Text.get_n_selections(accessible) > 0:
                return Atspi.Text.set_selection(accessible, 0, 0, count)
            return Atspi.Text.add_selection(accessible, 0, count)
        except Exception:  # noqa: BLE001
            return False


def _window_title(accessible, Atspi) -> str | None:
    """The name of the frame the object sits in."""
    current = accessible
    for _ in range(40):
        try:
            if current.get_role() in (Atspi.Role.FRAME, Atspi.Role.WINDOW, Atspi.Role.DIALOG):
                return current.get_name() or None
            current = current.get_parent()
        except Exception:  # noqa: BLE001
            return None
        if current is None:
            return None
    return None
