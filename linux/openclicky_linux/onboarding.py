"""The first-run setup: the Mac's permission cards and the start of its tour, as one window, page by
page. It runs before the app starts (so Wayland's permission is in place before the keys are
watched), again with `openclicky-linux setup`, and on Wayland whenever the permission is missing.

1. hello
2. Wayland only: **your keys** (the one-time permission, `inputaccess.py`: the Mac's Accessibility)
   and **your screen** (one screenshot through the desktop, which asks once: the Mac's Screen
   Recording); on X11 neither is needed
3. **microphone**: pick one, test it with a level meter (nothing is kept)
4. **answers**: a free account by email (6-digit code for an existing one), or your own key
5. **offline**: dictation and the voice on this computer (Parakeet and Kokoro, about 840 MB)
6. **start at login**, and OpenClicky added to the applications menu
7. **all set**: the shortcuts

Every page can be skipped but the Wayland permission, without which nothing would hear the keys.
The sentences follow the Mac's (`FirstRunTour.swift`, `PermissionPrompt.swift`), lowercase and short.
"""

from __future__ import annotations

import subprocess
import sys
import threading
import time
from typing import Callable

import gi

gi.require_version("Gtk", "3.0")
gi.require_version("Gdk", "3.0")
from gi.repository import Gdk, GLib, Gtk  # noqa: E402

from . import applog, autostart, inputaccess, offline, preferences  # noqa: E402
from .account_form import AccountForm  # noqa: E402
from . import voice as voice_module  # noqa: E402
from .config import load_settings, save_settings  # noqa: E402
from .recorder import MicrophoneRecording, RecorderUnavailable, default_microphone_name, level_of, list_microphones  # noqa: E402

WINDOW_WIDTH = 560
MICROPHONE_TEST_SECONDS = 3.0

_CSS = b"""
.openclicky-setup-title { font-size: 20pt; font-weight: 700; }
.openclicky-setup-detail { font-size: 11.5pt; }
.openclicky-setup-small { font-size: 9.5pt; opacity: 0.75; }
.openclicky-setup-good { color: #2f9e44; }
.openclicky-setup-bad { color: #e03131; }
.openclicky-dot { min-width: 8px; min-height: 8px; border-radius: 4px; background-color: alpha(currentColor, 0.2); }
.openclicky-dot.current { background-color: #f0452b; }
"""


def is_first_run() -> bool:
    return not preferences.get(preferences.SETUP_DONE, False)


def key_kind(key: str) -> str | None:
    """Which vendor a pasted key belongs to, by its prefix: "anthropic", "openrouter", "openai"."""
    key = key.strip()
    if key.startswith("sk-ant-"):
        return "anthropic"
    if key.startswith("sk-or-"):
        return "openrouter"
    if key.startswith("sk-"):
        return "openai"
    return None


KEY_FIELDS = {"anthropic": "anthropic_api_key", "openrouter": "openrouter_api_key", "openai": "openai_api_key"}


def answers_source_sentence(settings=None) -> str | None:
    """Where answers come from now, or None when nothing is set up yet (the Mac's order)."""
    settings = settings or load_settings()
    if settings.anthropic_api_key:
        return "your anthropic key"
    if settings.openrouter_api_key:
        return "your openrouter key"
    if settings.openai_api_key:
        return "your openai key"
    if settings.account_token:
        return f"your free account{' (' + settings.account_email + ')' if settings.account_email else ''}"
    return None


def run_in_background(work: Callable[[], object], done: Callable[[object], None]) -> None:
    def run() -> None:
        try:
            outcome = work()
        except Exception as failure:  # noqa: BLE001 - shown on the page
            outcome = failure
        GLib.idle_add(lambda: (done(outcome), False)[1])
    threading.Thread(target=run, daemon=True).start()


class Page:
    def __init__(self, page_id: str, title: str, detail: str, primary: str = "continue", secondary: str | None = "skip") -> None:
        self.page_id = page_id
        self.box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=10)
        self.title = Gtk.Label(label=title, xalign=0)
        self.title.set_line_wrap(True)
        self.title.get_style_context().add_class("openclicky-setup-title")
        self.detail = Gtk.Label(label=detail, xalign=0)
        self.detail.set_line_wrap(True)
        self.detail.set_max_width_chars(60)
        self.detail.get_style_context().add_class("openclicky-setup-detail")
        self.box.pack_start(self.title, False, False, 0)
        self.box.pack_start(self.detail, False, False, 0)
        self.body = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=8)
        self.body.set_margin_top(8)
        self.box.pack_start(self.body, True, True, 0)
        self.status = Gtk.Label(xalign=0)
        self.status.set_line_wrap(True)
        self.status.set_max_width_chars(60)
        self.box.pack_start(self.status, False, False, 0)
        self.primary_label = primary
        self.secondary_label = secondary
        self.primary_enabled = True

    def say(self, text: str, good: bool | None = None) -> None:
        style = self.status.get_style_context()
        style.remove_class("openclicky-setup-good")
        style.remove_class("openclicky-setup-bad")
        if good is True:
            style.add_class("openclicky-setup-good")
        elif good is False:
            style.add_class("openclicky-setup-bad")
        self.status.set_text(text)


class SetupWindow:
    """The setup, page by page. `finished` is True once it reached the end (or was skipped through),
    False when closed on Wayland's permission page without it."""

    def __init__(self, wayland: bool, on_closed: Callable[[bool], None] = lambda _finished: None) -> None:
        self.wayland = wayland
        self.on_closed = on_closed
        self.finished = False
        provider = Gtk.CssProvider()
        provider.load_from_data(_CSS)
        Gtk.StyleContext.add_provider_for_screen(Gdk.Screen.get_default(), provider, Gtk.STYLE_PROVIDER_PRIORITY_APPLICATION)
        self.window = Gtk.Window(title="Set up OpenClicky")
        self.window.set_default_size(WINDOW_WIDTH, 460)
        self.window.set_border_width(24)
        self.window.set_position(Gtk.WindowPosition.CENTER)
        self.window.connect("delete-event", lambda *_: self.close() or True)
        layout = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=14)
        self.window.add(layout)
        self.dots = Gtk.Box(spacing=6)
        layout.pack_start(self.dots, False, False, 0)
        self.stack = Gtk.Stack()
        self.stack.set_transition_type(Gtk.StackTransitionType.SLIDE_LEFT_RIGHT)
        self.stack.set_vhomogeneous(False)
        layout.pack_start(self.stack, True, True, 0)
        buttons = Gtk.Box(spacing=8)
        self.secondary_button = Gtk.Button()
        self.secondary_button.connect("clicked", lambda _button: self._secondary())
        self.primary_button = Gtk.Button()
        self.primary_button.get_style_context().add_class("suggested-action")
        self.primary_button.connect("clicked", lambda _button: self._primary())
        buttons.pack_start(self.secondary_button, False, False, 0)
        buttons.pack_end(self.primary_button, False, False, 0)
        layout.pack_end(buttons, False, False, 0)

        self.pages: list[Page] = []
        self.actions: dict[str, Callable[[], None]] = {}
        self._build_pages()
        for page in self.pages:
            self.stack.add_named(page.box, page.page_id)
            dot = Gtk.Box()
            dot.get_style_context().add_class("openclicky-dot")
            self.dots.pack_start(dot, False, False, 0)
        self.index = 0
        self._busy = False
        self._microphone_test: MicrophoneRecording | None = None
        self._downloads_cancelled = threading.Event()

    # MARK: pages

    def _build_pages(self) -> None:
        self.pages.append(Page("hello", "hi, i'm clicky",
                               "your pointer's new sidekick. hold a key and talk: i type what you say, answer questions "
                               "about your screen and point at things. this takes about a minute.",
                               primary="let's go", secondary=None))
        if self.wayland and not inputaccess.is_granted():
            page = Page("keys", "let me hear your keys",
                        "on wayland an app has to be allowed to notice the keys you hold (left ctrl) and to type your words "
                        "for you. linux asks for your password once, then never again.",
                        primary="allow", secondary="not now")
            page.body.pack_start(self._small(
                "what it allows: programs you run can read the keyboard while you are at this computer, as every program "
                "could on x11. openclicky acts only on its own keys and never keeps or sends a keystroke."), False, False, 0)
            self.pages.append(page)
            self.actions["keys"] = self._allow_keys
        if self.wayland:
            from .portal import uses_grim

            if not uses_grim():
                page = Page("screen", "let me see your screen",
                            "when you ask about your screen, a picture of it goes with the question, so i can point at "
                            "the right thing. your desktop asks once: pick every screen, then choose share. after that i "
                            "only look when you ask, for a second each time.",
                            primary="allow", secondary="skip")
                self.pages.append(page)
                self.actions["screen"] = self._allow_screen
        page = Page("microphone", "which microphone should i use?",
                    "pick it, press test and say something.", primary="continue")
        self.microphone_combo = Gtk.ComboBoxText()
        self.test_button = Gtk.Button(label="test")
        self.test_button.connect("clicked", lambda _button: self._test_microphone())
        row = Gtk.Box(spacing=8)
        row.pack_start(self.microphone_combo, True, True, 0)
        row.pack_start(self.test_button, False, False, 0)
        page.body.pack_start(row, False, False, 0)
        self.level = Gtk.LevelBar()
        self.level.set_min_value(0)
        self.level.set_max_value(1)
        page.body.pack_start(self.level, False, False, 0)
        self.pages.append(page)

        page = Page("answers", "how should i answer you?",
                    "add your email for a free account: no password, it works straight away. or paste your own "
                    "anthropic, openai or openrouter key, which goes straight to them.", primary="continue")
        self.account_form = AccountForm(say=lambda text, good, answers_page=page: answers_page.say(text, good))
        page.body.pack_start(self.account_form.box, False, False, 0)
        page.body.pack_start(self._small("or your own key:"), False, False, 0)
        self.key_entry = Gtk.Entry()
        self.key_entry.set_visibility(False)
        self.key_entry.set_placeholder_text("sk-ant-…, sk-…, sk-or-…")
        key_button = Gtk.Button(label="use this key")
        key_button.connect("clicked", lambda _button: self._use_key())
        self.key_entry.connect("activate", lambda _entry: self._use_key())
        key_row = Gtk.Box(spacing=8)
        key_row.pack_start(self.key_entry, True, True, 0)
        key_row.pack_start(key_button, False, False, 0)
        page.body.pack_start(key_row, False, False, 0)
        self.pages.append(page)

        page = Page("offline", "dictation that stays on this computer",
                    "parakeet hears you on this computer (25 european languages, english included) and kokoro speaks "
                    "answers. nothing you say leaves it. about 840 MB, once.",
                    primary="download", secondary="skip")
        self.download_progress = Gtk.ProgressBar()
        self.download_progress.set_no_show_all(True)
        page.body.pack_start(self.download_progress, False, False, 0)
        self.pages.append(page)
        self.actions["offline"] = self._download_offline

        page = Page("startup", "open me when you log in?",
                    "then left ctrl works from the moment you log in, without opening me first. i'm in your "
                    "applications menu either way.", primary="continue", secondary=None)
        self.start_at_login = Gtk.CheckButton(label="start openclicky when i log in")
        self.start_at_login.set_active(True)
        page.body.pack_start(self.start_at_login, False, False, 0)
        self.pages.append(page)

        from .windows import shortcut_rows

        page = Page("done", "you're all set",
                    "the tray icon has settings, and esc stops anything i'm doing. try these:", primary="start", secondary=None)
        grid = Gtk.Grid(column_spacing=14, row_spacing=6)
        for row_index, (how, keys, what) in enumerate(shortcut_rows(load_settings().dictation_key)):
            grid.attach(Gtk.Label(label=f"{how} {' + '.join(keys)}", xalign=0), 0, row_index, 1, 1)
            description = Gtk.Label(label=what, xalign=0)
            description.set_line_wrap(True)
            description.set_max_width_chars(40)
            grid.attach(description, 1, row_index, 1, 1)
        page.body.pack_start(grid, False, False, 0)
        self.pages.append(page)

    @staticmethod
    def _small(text: str) -> Gtk.Label:
        label = Gtk.Label(label=text, xalign=0)
        label.set_line_wrap(True)
        label.set_max_width_chars(64)
        label.get_style_context().add_class("openclicky-setup-small")
        return label

    # MARK: moving between pages

    @property
    def page(self) -> Page:
        return self.pages[self.index]

    def show(self) -> None:
        self.window.show_all()
        self._show_page()
        self.window.present()

    def _show_page(self) -> None:
        page = self.page
        self.stack.set_visible_child_name(page.page_id)
        for index, dot in enumerate(self.dots.get_children()):
            (dot.get_style_context().add_class if index == self.index else dot.get_style_context().remove_class)("current")
        self.primary_button.set_label(page.primary_label)
        self.primary_button.set_sensitive(page.primary_enabled and not self._busy)
        self.secondary_button.set_visible(page.secondary_label is not None)
        if page.secondary_label:
            self.secondary_button.set_label(page.secondary_label)
        entering = getattr(self, f"_enter_{page.page_id}", None)
        if entering:
            entering()

    def go_to(self, page_id: str) -> None:
        self.index = next(index for index, page in enumerate(self.pages) if page.page_id == page_id)
        self._show_page()

    def next_page(self) -> None:
        self._leave_page()
        if self.index == len(self.pages) - 1:
            self._finish()
            return
        self.index += 1
        self._show_page()

    def _leave_page(self) -> None:
        if self.page.page_id == "microphone":
            self._stop_microphone_test()
            chosen = self.microphone_combo.get_active_id()
            try:
                save_settings({"microphone": None if chosen in (None, "default") else chosen})
            except (OSError, ValueError) as failure:
                applog.append(f"setup: microphone not saved ({failure})")
        if self.page.page_id == "startup":
            try:
                autostart.set_enabled(self.start_at_login.get_active())
                autostart.install_launcher()
                applog.append(f"setup: start at login {'on' if self.start_at_login.get_active() else 'off'}, launcher installed")
            except OSError as failure:
                applog.append(f"setup: launcher not written ({failure})")

    def _primary(self) -> None:
        if self._busy:
            return
        action = self.actions.get(self.page.page_id)
        if action and self.page.primary_label != "continue":
            action()
        else:
            self.next_page()

    def _secondary(self) -> None:
        page_id = self.page.page_id
        if page_id == "keys":
            # Without it nothing could hear the keys on Wayland: the setup ends here.
            self.close()
            return
        if page_id == "offline" and self._busy:
            self._downloads_cancelled.set()
            return
        self.next_page()

    def _set_busy(self, busy: bool) -> None:
        self._busy = busy
        self.primary_button.set_sensitive(not busy and self.page.primary_enabled)

    def _done_with_action(self, label: str = "continue") -> None:
        self.page.primary_label = label
        self.primary_button.set_label(label)
        self._set_busy(False)

    def _finish(self) -> None:
        self.finished = True
        preferences.set_values(**{preferences.SETUP_DONE: True})
        applog.append("setup: finished")
        self.close()

    def close(self) -> None:
        self._stop_microphone_test()
        self.window.hide()
        self.on_closed(self.finished)

    # MARK: Wayland's keys and screen

    def _allow_keys(self) -> None:
        self._set_busy(True)
        self.page.say("waiting for your password…")

        def granted(outcome) -> None:
            if isinstance(outcome, Exception):
                self.page.say(f"✕ {outcome}", good=False)
                self._set_busy(False)
                return
            self.page.say("✓ done. i can hear my keys now.", good=True)
            self.page.secondary_label = None
            self.secondary_button.hide()
            self._done_with_action()
        run_in_background(inputaccess.grant, granted)

    def _allow_screen(self) -> None:
        from . import screencast
        from .portal import ScreenshotRefused, take_portal_screenshot

        self._set_busy(True)
        self.page.say("your desktop is asking…")

        def answered(outcome) -> None:
            if isinstance(outcome, (ScreenshotRefused, screencast.ScreenCastRefused)):
                self.page.say(f"✕ {outcome}. you can allow it later: ask a question and choose allow.", good=False)
            elif isinstance(outcome, Exception):
                self.page.say(f"✕ the screenshot failed: {outcome}", good=False)
            else:
                self.page.say("✓ thanks. i'll only look when you ask me something.", good=True)
            self._done_with_action()
        # The picture itself is thrown away: this only lets the desktop ask its question now, and keeps
        # the screen cast's token so later questions are not asked about.
        run_in_background(screencast.capture_monitors if screencast.is_available() else take_portal_screenshot, answered)

    # MARK: microphone

    def _enter_microphone(self) -> None:
        self.microphone_combo.remove_all()
        default_name = default_microphone_name()
        microphones = list_microphones()
        default_description = next((microphone.description for microphone in microphones if microphone.name == default_name), None)
        self.microphone_combo.append("default", f"system default ({default_description})" if default_description else "system default")
        for microphone in microphones:
            self.microphone_combo.append(microphone.name, microphone.description)
        self.microphone_combo.set_active_id(load_settings().microphone or "default")
        if not self.microphone_combo.get_active_id():
            self.microphone_combo.set_active_id("default")

    def _test_microphone(self) -> None:
        if self._microphone_test is not None:
            return
        chosen = self.microphone_combo.get_active_id()
        try:
            recording = MicrophoneRecording(microphone=None if chosen in (None, "default") else chosen)
        except (RecorderUnavailable, OSError) as problem:
            self.page.say(f"✕ {problem}", good=False)
            return
        self._microphone_test = recording
        self.test_button.set_sensitive(False)
        self.page.say("say something…")
        loudest = [0.0]
        started = time.monotonic()

        def tick() -> bool:
            if self._microphone_test is not recording:
                return False
            level = recording.current_level()
            loudest[0] = max(loudest[0], level)
            self.level.set_value(min(1.0, level * 6))
            if time.monotonic() - started < MICROPHONE_TEST_SECONDS:
                return True
            self._microphone_test = None
            self.test_button.set_sensitive(True)
            self.level.set_value(0)
            try:
                heard = recording.stop()
            except RecorderUnavailable as problem:
                self.page.say(f"✕ {problem}", good=False)
                return False
            from .windows import microphone_test_verdict

            verdict = microphone_test_verdict(max(loudest[0], level_of(heard)))
            self.page.say(verdict.lower(), good=verdict.startswith("✓"))
            return False

        GLib.timeout_add(100, tick)

    def _stop_microphone_test(self) -> None:
        if self._microphone_test is not None:
            self._microphone_test.cancel()
            self._microphone_test = None
            self.test_button.set_sensitive(True)
            self.level.set_value(0)

    # MARK: answers

    def _enter_answers(self) -> None:
        self.account_form.refresh()
        source = answers_source_sentence()
        if source:
            self.page.say(f"✓ answers come from {source}.", good=True)

    def _use_key(self) -> None:
        key = self.key_entry.get_text().strip()
        kind = key_kind(key)
        if kind is None:
            self.page.say("that doesn't look like an anthropic (sk-ant-), openai (sk-) or openrouter (sk-or-) key.", good=False)
            return
        try:
            save_settings({KEY_FIELDS[kind]: key})
        except (OSError, ValueError) as failure:
            self.page.say(f"✕ not saved: {failure}", good=False)
            return
        self.key_entry.set_text("")
        self.page.say(f"✓ saved. answers come from {answers_source_sentence()}.", good=True)

    # MARK: offline models

    def _enter_offline(self) -> None:
        if offline.is_ready() and voice_module.is_installed(voice_module.KOKORO):
            self.page.say("✓ already on this computer.", good=True)
            self.page.primary_label = "continue"
            self.primary_button.set_label("continue")

    def _download_offline(self) -> None:
        self._set_busy(True)
        self._downloads_cancelled.clear()
        self.secondary_button.set_label("cancel")
        self.download_progress.set_no_show_all(False)
        self.download_progress.show()

        def progress(done: int, total: int, what: str) -> None:
            def show() -> None:
                self.download_progress.set_fraction(min(1.0, done / total) if total else 0)
                self.page.say(f"{what}… {done // 1_000_000} of {total // 1_000_000} MB")
            GLib.idle_add(lambda: (show(), False)[1])

        def work() -> None:
            if not offline.is_engine_available():
                GLib.idle_add(lambda: (self.page.say("getting the speech engine (about 30 MB)…"), False)[1])
                install_speech_engine()
            for asset, what in ((offline.PARAKEET, "dictation"), (voice_module.KOKORO, "voice")):
                if not offline.is_installed(asset):
                    offline.download(asset, on_progress=lambda done, total, what=what: progress(done, total, what),
                                     cancelled=self._downloads_cancelled)

        def finished(outcome) -> None:
            self.download_progress.hide()
            self.secondary_button.set_label("skip")
            if isinstance(outcome, Exception):
                self.page.say(f"✕ {outcome}. you can download it later in settings.", good=False)
                self._done_with_action("continue")
                return
            self.page.say("✓ done. dictation and answers' voice now work on this computer.", good=True)
            self._done_with_action("continue")
        run_in_background(work, finished)


def install_speech_engine() -> None:
    """sherpa-onnx, the engine Parakeet and Kokoro run on, into the Python OpenClicky runs in (its own
    virtualenv): the same pinned version the Mac embeds."""
    finished = subprocess.run([sys.executable, "-m", "pip", "install", "--quiet", "sherpa-onnx==1.13.8"],
                              capture_output=True, text=True, timeout=600)
    if finished.returncode != 0:
        raise offline.OfflineEngineError(f"the speech engine could not be installed: {finished.stderr.strip()[-200:]}")
    import importlib

    importlib.invalidate_caches()


def run_setup_window(wayland: bool) -> bool:
    """Shows the setup and waits for it (its own main loop, before the app's starts). True when it was
    finished; False when closed early."""
    outcome = {"finished": False}

    def closed(finished: bool) -> None:
        outcome["finished"] = finished
        Gtk.main_quit()

    from .windows import pointer_icon

    Gtk.Window.set_default_icon(pointer_icon(64))
    window = SetupWindow(wayland=wayland, on_closed=closed)
    window.show()
    Gtk.main()
    return outcome["finished"]
