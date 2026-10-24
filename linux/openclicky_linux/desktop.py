"""GTK 3: the clipboard paste and the orb.

The paste is the Mac app's: the words go on the clipboard, the paste keystroke is posted, and the
person's own clipboard text is put back 0.6 s later (only if the clipboard still holds our words,
and only text: an image on the clipboard is replaced, not restored). On X11 the keystroke is XTEST's
ctrl+v (ctrl+shift+v in a terminal); on Wayland it is shift+insert on the virtual keyboard
(`uinput.py`), and the words go in the primary selection too, which is what terminals paste then.
On Wayland the selections are owned through `x_selection.SelectionOwner`, and the keystroke waits
until the compositor has taken the words up: sent any earlier, a Wayland app pasted the clipboard
from before. If it never does, nothing is typed and the words stay on the clipboard.

The orb is a small pill at the bottom of the screen showing the take's phase. It is a popup
(override-redirect) window: it never takes the focus, so the paste still goes to the app in front.
"""

from __future__ import annotations

import gi

gi.require_version("Gtk", "3.0")
gi.require_version("Gdk", "3.0")
from gi.repository import Gdk, GLib, Gtk  # noqa: E402

import time  # noqa: E402

from . import applog  # noqa: E402
from .controller import FocusedWindow, OrbModel  # noqa: E402
from .x11 import focused_window, send_paste_keystroke  # noqa: E402

CLIPBOARD_RESTORE_DELAY_MILLISECONDS = 600


class ClipboardPaster:
    # Reading the old clipboard normally takes milliseconds; one that takes longer than this (a hung
    # clipboard owner, GTK waits up to 30 s) is not followed by a keystroke: by then the person may be
    # typing somewhere else.
    CLIPBOARD_READ_LIMIT_SECONDS = 2.0

    def __init__(self, xlib_display, before_keystroke=lambda: None, on_paste_problem=lambda _message: None,
                 keyboard=None, focused_window_now=None) -> None:
        self._xlib_display = xlib_display
        # Wayland: the virtual keyboard (`uinput.VirtualKeyboard`) and how to read the window in
        # front (it cannot be told there); None on X11, which uses XTEST and `_NET_ACTIVE_WINDOW`.
        self._keyboard = keyboard
        self._focused_window_now = focused_window_now or (lambda: focused_window(self._xlib_display))
        # Lets the key listener ignore our own keystroke (it would look like a key typed mid-take).
        self._before_keystroke = before_keystroke
        # The take has ended by the time the keystroke goes out: a problem then is reported here, so
        # the orb can say "copied, press ctrl+v" instead of the "moved to text box" it already said.
        self._on_paste_problem = on_paste_problem
        self._clipboard = Gtk.Clipboard.get(Gdk.SELECTION_CLIPBOARD)
        self._primary = Gtk.Clipboard.get(Gdk.SELECTION_PRIMARY)
        self._paste_generation = 0
        self._selection_owner = None
        if keyboard is not None:
            from .x_selection import SelectionOwner

            self._selection_owner = SelectionOwner(Gdk.Display.get_default().get_name())

    @property
    def _pastes_with_shift_insert(self) -> bool:
        return self._keyboard is not None

    def copy_to_clipboard(self, text: str) -> None:
        if self._selection_owner is not None:
            self._selection_owner.offer(text, selections=("CLIPBOARD",))
            return
        self._clipboard.set_text(text, -1)
        # GDK buffers requests; the selection must be ours on the server before anyone asks for it.
        Gdk.Display.get_default().flush()

    def _can_send_keystroke(self) -> bool:
        return self._keyboard is not None or self._xlib_display.has_extension("XTEST")

    def _send_keystroke(self, window: FocusedWindow | None) -> bool:
        if self._keyboard is not None:
            self._keyboard.paste()
            return True
        return send_paste_keystroke(self._xlib_display, with_shift=bool(window and window.is_terminal))

    def paste(self, text: str, window: FocusedWindow | None) -> bool:
        """Puts `text` on the clipboard, posts the paste keystroke, and puts the person's own clipboard
        text back afterwards. The old clipboard is read asynchronously (`request_text`), never with
        `wait_for_text`: that runs a nested main loop, in which a queued esc or a new key press would
        be handled in the middle of a take's paste. Before the keystroke, the window in front is
        checked again; anything wrong leaves the words on the clipboard and reports it. False when
        no keystroke can be posted at all (no XTEST)."""
        if not self._can_send_keystroke():
            return False
        self._paste_generation += 1
        this_paste = self._paste_generation
        asked_at = time.monotonic()

        def paste_after_reading_the_old_clipboard(_clipboard, previous_text, previous_primary=None) -> None:
            if this_paste != self._paste_generation:
                # A newer take's paste has started: its words own the clipboard now.
                applog.append("paste: an older take's paste was overtaken; its words were not pasted")
                return
            if self._selection_owner is not None:
                self._selection_owner.offer(text, on_taken_up=lambda taken: keystroke_once_taken_up(taken, previous_text, previous_primary))
                return
            self.copy_to_clipboard(text)
            send_keystroke(previous_text, previous_primary)

        def keystroke_once_taken_up(taken: bool, previous_text, previous_primary) -> None:
            if this_paste != self._paste_generation:
                return
            if not taken:
                # Typed now, the app could paste what was on the clipboard before: never that.
                applog.append("paste: the compositor did not take up the words in time; left on the clipboard")
                self._on_paste_problem("couldn't paste. copied, press ctrl+v")
                return
            send_keystroke(previous_text, previous_primary)

        def send_keystroke(previous_text, previous_primary) -> None:
            if time.monotonic() - asked_at > self.CLIPBOARD_READ_LIMIT_SECONDS:
                self._on_paste_problem("too slow to paste. copied, press ctrl+v")
                return
            window_now = self._focused_window_now()
            if window is not None and (window_now is None or window_now.window_id != window.window_id):
                self._on_paste_problem("the window changed. copied, press ctrl+v")
                return
            self._before_keystroke()
            try:
                sent = self._send_keystroke(window)
            except Exception as failure:  # noqa: BLE001 - an X error must not lose the words
                applog.append(f"paste: the keystroke failed ({type(failure).__name__})")
                sent = False
            if not sent:
                # The words stay on the clipboard (no restore over them) and the orb says so.
                self._on_paste_problem("couldn't paste. copied, press ctrl+v")
                return

            def restore_previous_clipboard() -> bool:
                if self._selection_owner is not None:
                    owner = self._selection_owner
                    # Only while the words are still ours: anything copied since stays.
                    for selection, previous in (("CLIPBOARD", previous_text), ("PRIMARY", previous_primary)):
                        if previous is not None and owner.owns(selection) and owner.text(selection) == text:
                            owner.offer(previous, selections=(selection,))
                    return False

                def restore_if_still_ours(_clipboard, current_text) -> None:
                    # Only if nobody copied anything since: never overwrite what the person copied.
                    if previous_text is not None and current_text == text:
                        self.copy_to_clipboard(previous_text)

                def restore_primary_if_still_ours(_primary, current_text) -> None:
                    if previous_primary is not None and current_text == text:
                        self._primary.set_text(previous_primary, -1)
                        Gdk.Display.get_default().flush()
                self._clipboard.request_text(restore_if_still_ours)
                if self._pastes_with_shift_insert:
                    self._primary.request_text(restore_primary_if_still_ours)
                return False

            GLib.timeout_add(CLIPBOARD_RESTORE_DELAY_MILLISECONDS, restore_previous_clipboard)

        if self._pastes_with_shift_insert:
            # The primary selection is read first, then the clipboard, both without a nested loop.
            self._primary.request_text(lambda _primary, previous_primary: self._clipboard.request_text(
                lambda clipboard, previous_text: paste_after_reading_the_old_clipboard(clipboard, previous_text, previous_primary)))
        else:
            self._clipboard.request_text(paste_after_reading_the_old_clipboard)
        return True


_ORB_CSS = b"""
.openclicky-orb-window { background-color: transparent; }
.openclicky-orb { background-color: rgba(28, 26, 24, 0.92); border-radius: 18px; padding: 8px 16px; }
.openclicky-orb label { color: #f4f1ec; font-size: 13px; }
.openclicky-orb.listening { box-shadow: inset 0 0 0 2px #f0452b; }
.openclicky-orb.failed label { color: #ffb4a6; }
"""

_PHASE_LABELS = {"listening": "● listening", "working": "…", "done": "✓", "failed": "✕"}


class OrbWindow:
    BOTTOM_MARGIN = 72

    def __init__(self, orb: OrbModel) -> None:
        provider = Gtk.CssProvider()
        provider.load_from_data(_ORB_CSS)
        Gtk.StyleContext.add_provider_for_screen(Gdk.Screen.get_default(), provider, Gtk.STYLE_PROVIDER_PRIORITY_APPLICATION)
        self.window = Gtk.Window(type=Gtk.WindowType.POPUP)
        self.window.set_accept_focus(False)
        self.window.set_keep_above(True)
        # Round corners need a see-through window, which only a compositor can draw; without one
        # the pill is drawn square, so no white corners show behind it.
        screen = self.window.get_screen()
        if screen.is_composited() and screen.get_rgba_visual() is not None:
            self.window.set_visual(screen.get_rgba_visual())
            self.window.set_app_paintable(True)
            self.window.get_style_context().add_class("openclicky-orb-window")
        else:
            provider_square = Gtk.CssProvider()
            provider_square.load_from_data(b".openclicky-orb { border-radius: 0; }")
            Gtk.StyleContext.add_provider_for_screen(screen, provider_square, Gtk.STYLE_PROVIDER_PRIORITY_APPLICATION + 1)
        self._box = Gtk.Box()
        self._box.get_style_context().add_class("openclicky-orb")
        self.label = Gtk.Label()
        self._box.add(self.label)
        self.window.add(self._box)
        orb.listeners.append(self.update)

    def update(self, orb: OrbModel) -> None:
        style = self._box.get_style_context()
        for phase in ("listening", "working", "done", "failed"):
            style.remove_class(phase)
        if orb.phase == "idle":
            self.window.hide()
            return
        style.add_class(orb.phase)
        prefix = _PHASE_LABELS.get(orb.phase, "")
        # "● listening · release to transcribe", but "✓ moved to text box": the dot only follows a word.
        separator = " · " if prefix[-1:].isalpha() else " "
        self.label.set_text(f"{prefix}{separator}{orb.message}" if orb.message else prefix)
        # Shrinks to the new line: a window keeps its largest size unless asked for less.
        self.window.resize(1, 1)
        self.window.show_all()
        self._place_at_bottom_center()

    def _place_at_bottom_center(self) -> None:
        display = Gdk.Display.get_default()
        monitor = display.get_primary_monitor() or display.get_monitor(0)
        area = monitor.get_workarea()
        width, height = self.window.get_size()
        self.window.move(area.x + (area.width - width) // 2, area.y + area.height - height - self.BOTTOM_MARGIN)
