"""The welcome tour (the Mac's `FirstRunTour`) and the one-minute Settings tour (`SettingsTour`).

**The welcome tour** runs once, after the first-run setup, in a card under the island: hello, then
dictate, ask, and hand over a task, each step waiting for proof that it worked (a take with words,
a spoken answer, an agent task or a quick action) and offering a skip; a note about the free
account's credits when that is what answers; then "you're all set". Each card is said in the
answering voice (the Mac's recordings name Mac keys, so Linux says its own). Tray → Replay
welcome… plays it again.

**The Settings tour** plays the first time Settings opens after the welcome tour (and from General
→ Settings tour): the pointer flies to the rows that matter, page by page, saying what each is for.
Esc, closing the window or a new question ends either.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Callable

import gi

gi.require_version("Gtk", "3.0")
gi.require_version("Gdk", "3.0")
from gi.repository import Gdk, GLib, Gtk  # noqa: E402

from . import applog, preferences  # noqa: E402

DONE_KEY = "welcomeTourDone"
SETTINGS_TOUR_DONE_KEY = "settingsTourDone"
POLL_MILLISECONDS = 500
CARD_TOP = 44


@dataclass(frozen=True)
class Card:
    step: str
    title: str
    detail: str
    keys: tuple[str, ...] = ()
    primary: str | None = None
    secondary: str | None = None


def cards(key_name: str, can_ask: bool, on_free_account: bool) -> dict[str, Card]:
    """Every card, for the dictation key in use and what can answer."""
    ask_keys = (key_name, "Left Alt")
    return {
        "meet": Card("meet", "hi, i'm clicky", "your pointer's new sidekick. i'll show you three things. it takes about a minute.",
                     primary="let's go", secondary="skip tour"),
        "dictate": Card("dictate", f"hold {key_name.lower()} and say something",
                        "try “hello openclicky, this is my first take”, then let go. your words are typed where the cursor is.",
                        keys=(key_name,), secondary="skip"),
        "talk": Card("talk", "your turn: ask me something" if can_ask else "you can ask me things too",
                     ("hold " + " and ".join(key.lower() for key in ask_keys) + " and ask “what's this?” about anything on your "
                      "screen. i answer out loud and point.") if can_ask else
                     "about anything on your screen, out loud, and i'll point at it. add your email in settings to switch it on, free.",
                     keys=ask_keys, primary="next", secondary="skip"),
        "act": Card("act", "hand me a task" if can_ask else "i can do things for you too",
                    ("hold " + " and ".join(key.lower() for key in ask_keys) + " and say “make a folder on my desktop called hello "
                     "clicky”, or “open firefox”.") if can_ask else
                    "open apps, make folders, and more, just by asking. add your email in settings to switch it on, free.",
                    keys=ask_keys, primary="next", secondary="skip"),
        "account": Card("account", "you're on a free account",
                        "it has a limited number of free credits. if you already pay for openai, claude or another ai, add that key "
                        "in settings and use it right here.", primary="add my key", secondary="continue"),
        "done": Card("done", "you're all set",
                     "hover the top of the screen any time. keys, microphone and the rest live in settings.",
                     primary="open settings", secondary="done"),
    }


def step_order(on_free_account: bool) -> list[str]:
    return ["meet", "dictate", "talk", "act", *(["account"] if on_free_account else []), "done"]


def proves_dictation(text: str) -> bool:
    """A real first take: a sentence, not one word (what silence turns into)."""
    return len(text.split()) >= 2


@dataclass
class TourHooks:
    key_name: Callable[[], str]
    can_ask: Callable[[], bool]
    on_free_account: Callable[[], bool]
    say: Callable[[str], None]
    stop_saying: Callable[[], None]
    # What has happened so far: counts that grow (takes with words, answers, tasks or quick actions).
    takes: Callable[[], list[str]]
    answers_given: Callable[[], int]
    tasks_done: Callable[[], int]
    open_settings: Callable[[str], None]


class WelcomeTour:
    def __init__(self, hooks: TourHooks, on_finished: Callable[[], None] = lambda: None) -> None:
        self.hooks = hooks
        self.on_finished = on_finished
        self.step: str | None = None
        self._baseline = (0, 0, 0)
        self._poll: int | None = None
        self.window = Gtk.Window(type=Gtk.WindowType.POPUP)
        self.window.set_keep_above(True)
        # See-through around the rounded card (with a compositor; without one the card is square).
        screen = self.window.get_screen()
        if screen.is_composited() and screen.get_rgba_visual() is not None:
            self.window.set_visual(screen.get_rgba_visual())
            self.window.set_app_paintable(True)
            self.window.get_style_context().add_class("openclicky-tour-window")
        box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=8)
        box.get_style_context().add_class("openclicky-tour")
        self.title = Gtk.Label(xalign=0)
        self.detail = Gtk.Label(xalign=0)
        self.detail.set_line_wrap(True)
        # One width for every card: wrapping lines otherwise shrink it to their shortest.
        self.detail.set_width_chars(46)
        self.detail.set_max_width_chars(46)
        self.keys = Gtk.Box(spacing=4)
        self.status = Gtk.Label(xalign=0)
        self.status.set_line_wrap(True)
        self.status.set_max_width_chars(46)
        buttons = Gtk.Box(spacing=8)
        buttons.set_halign(Gtk.Align.END)
        self.secondary = Gtk.Button()
        self.secondary.connect("clicked", lambda _button: self._secondary())
        self.primary = Gtk.Button()
        self.primary.get_style_context().add_class("suggested-action")
        self.primary.connect("clicked", lambda _button: self._primary())
        buttons.pack_start(self.secondary, False, False, 0)
        buttons.pack_start(self.primary, False, False, 0)
        for widget in (self.title, self.detail, self.keys, self.status, buttons):
            box.pack_start(widget, False, False, 0)
        self.window.add(box)
        css = Gtk.CssProvider()
        css.load_from_data(b".openclicky-tour-window { background-color: transparent; }"
                           b".openclicky-tour { background-color: rgba(18,18,20,0.97); border-radius: 16px; padding: 16px 18px; }"
                           b".openclicky-tour > label { color: #f4f1ec; }"
                           b".openclicky-tour button label { color: #1c1a18; }"
                           b".openclicky-tour button.suggested-action label { color: #ffffff; }"
                           b".openclicky-tour .openclicky-keycap { color: #f4f1ec; }")
        Gtk.StyleContext.add_provider_for_screen(Gdk.Screen.get_default(), css, Gtk.STYLE_PROVIDER_PRIORITY_APPLICATION)

    @property
    def is_running(self) -> bool:
        return self.step is not None

    def start(self) -> None:
        applog.append("tour: started")
        self._go("meet")
        if self._poll is None:
            self._poll = GLib.timeout_add(POLL_MILLISECONDS, self._watch)

    def _snapshot(self) -> tuple[int, int, int]:
        return (len(self.hooks.takes()), self.hooks.answers_given(), self.hooks.tasks_done())

    def _go(self, step: str) -> None:
        self.step = step
        self._baseline = self._snapshot()
        card = cards(self.hooks.key_name(), self.hooks.can_ask(), self.hooks.on_free_account())[step]
        self.title.set_markup(f"<b>{GLib.markup_escape_text(card.title)}</b>")
        self.detail.set_text(card.detail)
        self.status.set_text("")
        self.status.set_no_show_all(True)
        self.status.hide()
        for child in self.keys.get_children():
            self.keys.remove(child)
        for key in card.keys:
            keycap = Gtk.Label(label=key)
            keycap.get_style_context().add_class("openclicky-keycap")
            self.keys.pack_start(keycap, False, False, 0)
        self.primary.set_label(card.primary or "")
        self.primary.set_visible(bool(card.primary))
        self.primary.set_no_show_all(not card.primary)
        self.secondary.set_label(card.secondary or "")
        # Rows with nothing in them take no room, and the card shrinks to the new step.
        self.keys.set_no_show_all(not card.keys)
        self.keys.set_visible(bool(card.keys))
        self.window.resize(1, 1)
        self.window.show_all()
        self._place()
        self.hooks.stop_saying()
        self.hooks.say(f"{card.title}. {card.detail}")

    def _place(self) -> None:
        display = Gdk.Display.get_default()
        area = (display.get_primary_monitor() or display.get_monitor(0)).get_workarea()
        natural = self.window.get_preferred_size()[1]
        self.window.move(area.x + (area.width - natural.width) // 2, area.y + CARD_TOP)

    def _watch(self) -> bool:
        if self.step is None:
            self._poll = None
            return False
        takes, answers, tasks = self._snapshot()
        if self.step == "dictate" and takes > self._baseline[0]:
            words = self.hooks.takes()[-1]
            if proves_dictation(words):
                self._succeed(f"✓ heard you: “{words[:60]}{'…' if len(words) > 60 else ''}”")
            else:
                self._say_status(f"i only got “{words}”. say a whole sentence, like the one above.")
                self._baseline = (takes, answers, tasks)
        elif self.step == "talk" and answers > self._baseline[1]:
            self._succeed("✓ that's asking. i'll point whenever it helps.")
        elif self.step == "act" and tasks > self._baseline[2]:
            self._succeed("✓ done. bigger jobs go to an agent while you keep working.")
        return True

    def _say_status(self, status: str) -> None:
        self.status.set_text(status)
        self.status.set_visible(bool(status))

    def _succeed(self, status: str) -> None:
        self._say_status(status)
        step = self.step
        GLib.timeout_add(1600, lambda: (self._advance() if self.step == step else None, False)[1])

    def _advance(self) -> None:
        order = step_order(self.hooks.on_free_account())
        position = order.index(self.step) if self.step in order else len(order) - 1
        if position + 1 >= len(order):
            self.finish()
        else:
            self._go(order[position + 1])

    def _primary(self) -> None:
        if self.step == "account":
            self.hooks.open_settings("keys")
            self._advance()
        elif self.step == "done":
            self.finish()
            self.hooks.open_settings("general")
        else:
            self._advance()

    def _secondary(self) -> None:
        if self.step in ("meet", "done"):
            self.finish()
        else:
            self._advance()

    def finish(self) -> None:
        if self.step is None:
            return
        applog.append(f"tour: finished at {self.step}")
        self.step = None
        self.hooks.stop_saying()
        self.window.hide()
        preferences.set_values(**{DONE_KEY: True})
        self.on_finished()


def tour_is_done() -> bool:
    return preferences.get(DONE_KEY) is True


# MARK: the Settings tour

@dataclass(frozen=True)
class TourStop:
    page: str
    widget_name: str  # a field of the Settings window, or an attribute holding a widget
    line: str


def settings_tour_stops(key_name: str) -> list[TourStop]:
    """The Mac's narration, with the Linux keys."""
    return [
        TourStop("dictation", "region", "Here's a one-minute tour of settings. First, your region. Pick India or the rest of the world, "
                                        "and I'll only show what's useful to you."),
        TourStop("dictation", "dictation_key", "On Dictation, this is the key you hold, anywhere, to type with your voice."),
        TourStop("dictation", "language_code", "Here you choose the language you speak, and a little further down, whether AI tidies up "
                                               "your words."),
        TourStop("voice", "speech_voice", "On Voice, you choose how I sound. There's a free voice, so you don't need a key."),
        TourStop("clicky", "pointer_presence", "On Clicky, you choose when I follow your mouse. If I'm hiding, shake the mouse and I'll "
                                               "come back."),
        TourStop("words", "", "Your words keeps everything you've dictated, so you can always find it again."),
        TourStop("keyboard", "shortcuts_grid", f"Shortcuts shows every key at a glance. Hold {key_name.lower()} and left alt to talk to me."),
        TourStop("keys", "openai_api_key", "If you have your own AI key, or a Claude or ChatGPT plan, add it here. You don't need one to "
                                           "start."),
        TourStop("general", "start_at_login", "That's it! You can watch this tour again from here, any time."),
    ]


def seconds_to_say(line: str) -> float:
    """About how long a line takes to say (about 2.6 words a second), and a moment to look."""
    return max(3.0, len(line.split()) / 2.6 + 1.0)


class SettingsTour:
    def __init__(self, settings_window, fly_to: Callable[[int, int], None], show_caption: Callable[[str, float], None],
                 say: Callable[[str], None], stop: Callable[[], None], key_name: Callable[[], str]) -> None:
        self.settings_window = settings_window
        self.fly_to = fly_to
        self.show_caption = show_caption
        self.say = say
        self.stop_pointer = stop
        self.key_name = key_name
        self._stops: list[TourStop] = []
        self._timer: int | None = None
        settings_window.window.connect("hide", lambda _window: self.cancel())

    @property
    def is_running(self) -> bool:
        return bool(self._stops) or self._timer is not None

    def start(self) -> None:
        self._stops = settings_tour_stops(self.key_name())
        applog.append("settings tour: started")
        self._next()

    def cancel(self) -> None:
        if self._timer is not None:
            GLib.source_remove(self._timer)
            self._timer = None
        if self._stops:
            self._stops = []
            self.stop_pointer()

    def _next(self) -> bool:
        self._timer = None
        if not self._stops:
            preferences.set_values(**{SETTINGS_TOUR_DONE_KEY: True})
            self.stop_pointer()
            return False
        stop = self._stops.pop(0)
        self.settings_window.show_page(stop.page)
        GLib.timeout_add(250, lambda: (self._point_at(stop), False)[1])
        self._timer = GLib.timeout_add(int(seconds_to_say(stop.line) * 1000), self._next)
        return False

    def _point_at(self, stop: TourStop) -> None:
        widget = self.settings_window.fields.get(stop.widget_name) or getattr(self.settings_window, stop.widget_name, None)
        target = widget if isinstance(widget, Gtk.Widget) else self.settings_window.stack.get_visible_child()
        place = widget_on_screen(target)
        if place is not None:
            self.fly_to(*place)
        self.show_caption(stop.line, seconds_to_say(stop.line))
        self.say(stop.line)


def widget_on_screen(widget: Gtk.Widget) -> tuple[int, int] | None:
    """The middle of a widget's left part, in desktop pixels (where the pointer's tip goes)."""
    toplevel = widget.get_toplevel()
    window = toplevel.get_window() if toplevel is not None else None
    if window is None or not widget.get_realized():
        return None
    translated = widget.translate_coordinates(toplevel, 0, 0)
    if translated is None:
        return None
    _found, origin_x, origin_y = window.get_origin()
    allocation = widget.get_allocation()
    return origin_x + translated[0] + min(40, allocation.width // 2), origin_y + translated[1] + allocation.height // 2
