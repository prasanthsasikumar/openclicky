"""The windows people use directly: the ask box, the Settings window, and the tray icon.

- The ask box (left ctrl tapped twice, or the tray): one line, Enter sends, Esc closes. It takes the
  keyboard while open; when it closes the window manager gives the focus back.
- Settings: the fields in `~/.openclicky/shell.json` that matter on Linux, in groups (dictation, the
  voice, the pointer, the account, your own keys, the agent, startup), with a line that says where
  dictation and answers will go as you type. Save writes them with `config.save_settings` (every
  other key stays). Keys show as dots. The account signs in and out at once (`account_form.py`).
- The tray icon (any system tray: Xfce, KDE, MATE, Cinnamon; GNOME needs an extension): Ask…,
  Your words…, Settings…, Quit.
"""

from __future__ import annotations

import threading
import time
from typing import Callable

import gi

gi.require_version("Gtk", "3.0")
gi.require_version("Gdk", "3.0")
gi.require_version("GdkX11", "3.0")
from gi.repository import Gdk, GdkPixbuf, GdkX11, GLib, Gtk  # noqa: E402

from . import autostart, lanes, offline, preferences  # noqa: E402
from .account_form import AccountForm  # noqa: E402
from . import voice as voice_module  # noqa: E402
from .recorder import MicrophoneRecording, RecorderUnavailable, default_microphone_name, level_of, list_microphones  # noqa: E402
from .config import HOSTED_BACKEND_URL, LinuxSettings, environment_overrides, load_settings, save_settings  # noqa: E402
from .presence import PRESENCE_LABELS  # noqa: E402
from .session import is_wayland_session  # noqa: E402
from .pointer_view import POINTER_OUTLINE, POINTER_HEIGHT, POINTER_WIDTH  # noqa: E402
from .x11 import polygon_rows  # noqa: E402

DICTATION_KEY_CHOICES = (("Control_L", "left ctrl (where fn is on a Mac)"), ("Control_R", "right ctrl"), ("Alt_R", "right alt"),
                         ("Super_R", "right super"), ("Menu", "menu key"))
POINTER_CHOICES = tuple(PRESENCE_LABELS.items())
KEY_NAMES = {"Control_L": "Left Ctrl", "Control_R": "Right Ctrl", "Alt_L": "Left Alt", "Alt_R": "Right Alt",
             "Super_R": "Right Super", "Menu": "Menu"}


def shortcut_rows(dictation_key: str, pointer_presence: str = "always") -> list[tuple[str, tuple[str, ...], str]]:
    """What each key combination does, as Settings shows it: (how, keys, what it does). Follows the
    dictation key picked (left ctrl, shared with the ask box, cannot also be tapped to dictate)."""
    key = KEY_NAMES.get(dictation_key, dictation_key)
    rows = [("Hold", (key,), "Dictate: speak, then let go. Your words are typed where the cursor is.")]
    if dictation_key != "Control_L":
        rows.append(("Tap, then tap again", (key,), "Dictate without holding the key."))
    rows += [
        ("Hold", (key, "Left Alt"), "Ask out loud: the pointer answers, shows you where, and guides you step by step."),
        ("Tap twice", ("Left Ctrl",), "Ask by typing."),
        ("Press", ("Esc",), "Stop: cancels dictation, stops a spoken answer, ends a guide."),
    ]
    if pointer_presence == "onShake":
        rows.append(("Shake", ("the mouse",), "Bring the pointer back beside the mouse."))
    return rows
DICTATION_ENGINE_CHOICES = (("auto", "Automatic (offline when downloaded)"), ("offline", "Offline, on this computer"),
                            ("openai", "OpenAI (your key)"), ("account", "OpenClicky account"))
MICROPHONE_DEFAULT_ID = "default"
MICROPHONE_TEST_SECONDS = 3.0
# Below this the test heard nothing: room noise on a working microphone is above it, a microphone
# that passes no sound at all (muted, or a virtual machine's with nothing behind it) stays far below.
MICROPHONE_SILENCE_LEVEL = 0.002


def microphone_test_verdict(loudest: float) -> str:
    if loudest < MICROPHONE_SILENCE_LEVEL:
        return ("✕ Heard nothing at all. Pick another microphone, or check it is not muted in your sound "
                "settings. In a virtual machine, the host has to pass the microphone through.")
    if loudest < 0.01:
        return "✓ It hears something, but quietly. Speak closer, or raise its volume in your sound settings."
    return "✓ Heard you. This microphone works."


def present_with_focus(window: Gtk.Window) -> None:
    """Shows the window and asks for the keyboard with the X server's own time: opened from a key
    watched through RECORD there is no GTK event, and without a time window managers refuse focus."""
    window.show_all()
    gdk_window = window.get_window()
    timestamp = GdkX11.x11_get_server_time(gdk_window) if isinstance(gdk_window, GdkX11.X11Window) else Gdk.CURRENT_TIME
    window.present_with_time(timestamp)


class ComposerWindow:
    WIDTH = 520
    TOP_MARGIN = 120

    def __init__(self, on_submit: Callable[[str], None]) -> None:
        # Read by the key watcher's thread when esc arrives (often before GTK has handled that esc
        # itself), so an esc that closes this box is not also taken for "dismiss the answer" or
        # "end the guide". A plain bool, set only on the main thread.
        self.is_open_for_key_watcher = False
        self.window = Gtk.Window(title="Ask OpenClicky")
        self.window.set_decorated(False)
        self.window.set_keep_above(True)
        self.window.set_skip_taskbar_hint(True)
        self.window.set_type_hint(Gdk.WindowTypeHint.DIALOG)
        self.window.set_default_size(self.WIDTH, -1)
        self.entry = Gtk.Entry()
        self.entry.set_placeholder_text("ask about your screen…  (enter sends, esc closes)")
        # The placeholder hides as soon as the box has the keyboard, which it has from the start: the
        # same words stay under it.
        hint = Gtk.Label(label="ask about anything on your screen · enter sends · esc closes", xalign=0)
        hint.get_style_context().add_class("dim-label")
        layout = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=6)
        layout.set_margin_start(10)
        layout.set_margin_end(10)
        layout.set_margin_top(10)
        layout.set_margin_bottom(8)
        layout.pack_start(self.entry, False, False, 0)
        layout.pack_start(hint, False, False, 0)
        self.window.add(layout)
        self.entry.connect("activate", lambda entry: on_submit(entry.get_text()))
        self.window.connect("key-press-event", self._on_key)
        self.window.connect("delete-event", lambda *_: self.hide() or True)

    def _on_key(self, _window, event) -> bool:
        if event.keyval == Gdk.KEY_Escape:
            self.hide()
            return True
        return False

    def show(self) -> None:
        self.is_open_for_key_watcher = True
        self.entry.set_text("")
        # On the monitor the mouse is on, not centred across all of them (the seam between two).
        display = Gdk.Display.get_default()
        _screen, mouse_x, mouse_y = display.get_default_seat().get_pointer().get_position()[0:3]
        monitor = display.get_monitor_at_point(mouse_x, mouse_y).get_geometry()
        self.window.move(monitor.x + (monitor.width - self.WIDTH) // 2, monitor.y + self.TOP_MARGIN)
        present_with_focus(self.window)
        self.entry.grab_focus()

    def hide(self) -> None:
        self.is_open_for_key_watcher = False
        self.window.hide()

    @property
    def is_open(self) -> bool:
        return self.window.get_visible()


class SettingsWindow:
    def __init__(self, on_saved: Callable[[LinuxSettings], None] = lambda _settings: None) -> None:
        self.on_saved = on_saved
        self.window = Gtk.Window(title="OpenClicky Settings")
        self.window.set_default_size(560, -1)
        self.window.set_border_width(18)
        self.window.connect("delete-event", lambda *_: self.window.hide() or True)
        # A test still recording when the window closes stops: the microphone is never left open.
        self.window.connect("hide", lambda _window: self._stop_microphone_test())
        layout = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=6)
        # Settings scrolls once it is taller than the screen; Close and Save stay in sight below it.
        self.scroller = Gtk.ScrolledWindow()
        self.scroller.set_policy(Gtk.PolicyType.NEVER, Gtk.PolicyType.AUTOMATIC)
        self.scroller.set_propagate_natural_height(True)
        self.scroller.add(layout)
        outer = Gtk.Box(orientation=Gtk.Orientation.VERTICAL)
        outer.pack_start(self.scroller, True, True, 0)
        self.window.add(outer)
        self.fields: dict[str, Gtk.Widget] = {}
        self._loaded_speaker: int | None = None

        self._heading(layout, "Shortcuts")
        # Filled from the dictation key and the pointer setting, and again whenever either changes.
        self.shortcuts_grid = Gtk.Grid(column_spacing=14, row_spacing=6)
        self.shortcuts_grid.set_margin_bottom(4)
        layout.pack_start(self.shortcuts_grid, False, False, 0)
        self._heading(layout, "Dictation")
        self.fields["dictation_key"] = self._choice(layout, "Hold to dictate", DICTATION_KEY_CHOICES)
        self.fields["language_code"] = self._text(layout, "Language", "blank: the engine detects it (en, hi-IN, ta…)")
        self.fields["microphone"] = self._microphone_row(layout)
        self.fields["dictation_engine"] = self._choice(layout, "Engine", DICTATION_ENGINE_CHOICES)
        self.offline_row = ModelRow(self, layout, "Offline engine", offline.PARAKEET, needs_player=False,
                                    downloaded_line="Your words never leave this computer.",
                                    not_downloaded_line="Parakeet, about {size} MB, 25 European languages, no key needed.")
        # The names the tests (and the old row) use.
        self.offline_status, self.offline_button = self.offline_row.status, self.offline_row.button
        self.start_offline_download = self.offline_row.start_download
        self.fields["polish_with_model"] = Gtk.CheckButton(label="Clean up my words with AI (punctuation, “3 PM”, your style's rules)")
        layout.pack_start(self.fields["polish_with_model"], False, False, 0)
        self.fields["polish_offline_takes"] = Gtk.CheckButton(label="Also clean up what the offline engine heard (it then leaves this computer)")
        layout.pack_start(self.fields["polish_offline_takes"], False, False, 0)
        self.fields["incognito"] = Gtk.CheckButton(label="Incognito: keep no history of what I dictate")
        layout.pack_start(self.fields["incognito"], False, False, 0)
        self._heading(layout, "Voice")
        self.fields["speak_answers"] = Gtk.CheckButton(label="Speak answers (on this computer, in Kokoro's voice)")
        self.fields["speak_answers"].connect("toggled", lambda _button: self._update_routes())
        layout.pack_start(self.fields["speak_answers"], False, False, 0)
        self.fields["kokoro_speaker"] = self._choice(layout, "Voice", tuple((str(speaker.id), speaker.label) for speaker in voice_module.SPEAKERS))
        self.voice_row = ModelRow(self, layout, "Offline voice", voice_module.KOKORO, needs_player=True,
                                  downloaded_line="Answers are spoken on this computer.",
                                  not_downloaded_line="Kokoro, about {size} MB, the Mac app's offline voice.")
        self._heading(layout, "Pointer")
        self.fields["pointer_presence"] = self._choice(layout, "Show the pointer", POINTER_CHOICES)
        if is_wayland_session():
            # Wayland never says where the mouse is: there is nothing to follow.
            self.fields["pointer_presence"].set_sensitive(False)
            layout.pack_start(self._small_label("On Wayland the pointer rests at the top of the screen and flies out to what an "
                                                "answer is about; moving the mouse sends it back. Wayland never tells an app where "
                                                "the mouse is, so it cannot follow it."), False, False, 0)
        self._heading(layout, "Account")
        layout.pack_start(self._small_label("A free OpenClicky account answers and transcribes when you have no key of your own. "
                                            "No password: your email, and a code when it already has an account."), False, False, 0)
        self.account_status = Gtk.Label(xalign=0)
        self.account_status.set_line_wrap(True)
        self.account_form = AccountForm(say=self._say_about_account, on_changed=self._update_routes)
        layout.pack_start(self.account_form.box, False, False, 0)
        layout.pack_start(self.account_status, False, False, 0)
        self._heading(layout, "Your own keys")
        self.fields["openai_api_key"] = self._text(layout, "OpenAI key", "sk-… (dictation, and answers without an Anthropic key)", secret=True)
        self.fields["anthropic_api_key"] = self._text(layout, "Anthropic key", "sk-ant-… (answers)", secret=True)
        self.fields["openrouter_api_key"] = self._text(layout, "OpenRouter key", "sk-or-… (answers, when there is no Anthropic key)", secret=True)
        self.fields["backend_url"] = self._text(layout, "Backend", HOSTED_BACKEND_URL)

        self._heading(layout, "Agent")
        self.agent_mode = Gtk.CheckButton(label="Hand bigger tasks to a Codex agent (“make a folder of my screenshots and zip it”)")
        layout.pack_start(self.agent_mode, False, False, 0)
        self.agent_status = self._small_label("")
        layout.pack_start(self.agent_status, False, False, 0)

        self._heading(layout, "Startup")
        self.start_at_login = Gtk.CheckButton(label="Start OpenClicky when I log in")
        layout.pack_start(self.start_at_login, False, False, 0)

        self.routes_label = Gtk.Label(xalign=0)
        self.routes_label.set_line_wrap(True)
        self.routes_label.set_margin_top(12)
        layout.pack_start(self.routes_label, False, False, 0)
        self.status_label = Gtk.Label(xalign=0)
        layout.pack_start(self.status_label, False, False, 0)

        buttons = Gtk.Box(spacing=8)
        buttons.set_halign(Gtk.Align.END)
        buttons.set_margin_top(12)
        close_button = Gtk.Button(label="Close")
        close_button.connect("clicked", lambda _button: self.window.hide())
        self.save_button = Gtk.Button(label="Save")
        self.save_button.get_style_context().add_class("suggested-action")
        self.save_button.connect("clicked", lambda _button: self.save())
        buttons.pack_start(close_button, False, False, 0)
        buttons.pack_start(self.save_button, False, False, 0)
        outer.pack_start(buttons, False, False, 0)
        for widget in self.fields.values():
            if not isinstance(widget, Gtk.CheckButton):
                widget.connect("changed", lambda _widget: self._update_routes())
        self.fields["dictation_key"].connect("changed", lambda _widget: self._show_shortcuts())
        self.fields["pointer_presence"].connect("changed", lambda _widget: self._show_shortcuts())
        keycaps = Gtk.CssProvider()
        keycaps.load_from_data(b".openclicky-keycap { border: 1px solid alpha(currentColor, 0.35); border-radius: 4px;"
                               b" padding: 0 6px; font-weight: 600; }")
        Gtk.StyleContext.add_provider_for_screen(Gdk.Screen.get_default(), keycaps, Gtk.STYLE_PROVIDER_PRIORITY_APPLICATION)

    # MARK: building

    def _show_shortcuts(self) -> None:
        for child in self.shortcuts_grid.get_children():
            self.shortcuts_grid.remove(child)
        dictation_key = self.fields["dictation_key"].get_active_id() or "Control_L"
        presence = self.fields["pointer_presence"].get_active_id() if "pointer_presence" in self.fields else "always"
        self.shortcut_lines = []
        for row_index, (how, keys, what) in enumerate(shortcut_rows(dictation_key, presence)):
            combination = Gtk.Box(spacing=4)
            # Keycaps keep their own height beside a description that wraps onto two lines.
            combination.set_valign(Gtk.Align.START)
            combination.pack_start(Gtk.Label(label=how, xalign=0), False, False, 0)
            for key_index, key in enumerate(keys):
                if key_index:
                    combination.pack_start(Gtk.Label(label="+"), False, False, 0)
                keycap = Gtk.Label(label=key)
                keycap.get_style_context().add_class("openclicky-keycap")
                combination.pack_start(keycap, False, False, 0)
            description = Gtk.Label(label=what, xalign=0)
            description.set_valign(Gtk.Align.START)
            description.set_line_wrap(True)
            description.set_max_width_chars(44)
            self.shortcuts_grid.attach(combination, 0, row_index, 1, 1)
            self.shortcuts_grid.attach(description, 1, row_index, 1, 1)
            self.shortcut_lines.append(f"{how} {' + '.join(keys)}: {what}")
        self.shortcuts_grid.show_all()

    @staticmethod
    def _small_label(text: str) -> Gtk.Label:
        label = Gtk.Label(label=text, xalign=0)
        label.set_line_wrap(True)
        label.set_max_width_chars(70)
        label.get_style_context().add_class("dim-label")
        return label

    def _say_about_account(self, text: str, good: bool | None) -> None:
        self.account_status.set_text(text)

    def _show_agent_state(self) -> None:
        from . import agent

        if agent.is_tool_installed():
            self.agent_status.set_text("The openclicky command-line tool is installed: the gate sends tasks to it, questions to the pointer.")
        else:
            self.agent_status.set_text("Needs the openclicky command-line tool and Codex (see docs/setup.md, “Run the agent”). "
                                       "Without them, quick actions still work: open an app or a site, make or show a folder, "
                                       "the volume, play and pause.")

    def _heading(self, layout: Gtk.Box, title: str) -> None:
        label = Gtk.Label(xalign=0)
        label.set_markup(f"<b>{GLib.markup_escape_text(title)}</b>")
        label.set_margin_top(10)
        layout.pack_start(label, False, False, 0)

    def _row(self, layout: Gtk.Box, title: str, widget: Gtk.Widget) -> None:
        row = Gtk.Box(spacing=12)
        label = Gtk.Label(label=title, xalign=0)
        label.set_size_request(150, -1)
        row.pack_start(label, False, False, 0)
        row.pack_start(widget, True, True, 0)
        layout.pack_start(row, False, False, 0)

    def _microphone_row(self, layout: Gtk.Box) -> Gtk.ComboBoxText:
        """The microphone menu, a Test button, and under them a level meter and what the test heard."""
        combo = Gtk.ComboBoxText()
        self.test_microphone_button = Gtk.Button(label="Test")
        self.test_microphone_button.connect("clicked", lambda _button: self.test_microphone())
        picker = Gtk.Box(spacing=8)
        picker.pack_start(combo, True, True, 0)
        picker.pack_start(self.test_microphone_button, False, False, 0)
        self._row(layout, "Microphone", picker)
        self.microphone_level = Gtk.LevelBar()
        self.microphone_level.set_min_value(0)
        self.microphone_level.set_max_value(1)
        self.microphone_test_result = Gtk.Label(xalign=0)
        self.microphone_test_result.set_line_wrap(True)
        meter = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=4)
        meter.set_margin_start(162)
        meter.pack_start(self.microphone_level, False, False, 0)
        meter.pack_start(self.microphone_test_result, False, False, 0)
        layout.pack_start(meter, False, False, 0)
        self._microphone_test = None
        return combo

    def _fill_microphones(self, chosen: str | None) -> None:
        """Asked of the sound server each time Settings opens, so a headset plugged in since shows up."""
        combo = self.fields["microphone"]
        combo.remove_all()
        default_name = default_microphone_name()
        microphones = list_microphones()
        default_description = next((microphone.description for microphone in microphones if microphone.name == default_name), None)
        combo.append(MICROPHONE_DEFAULT_ID, f"System default ({default_description})" if default_description else "System default")
        for microphone in microphones:
            combo.append(microphone.name, microphone.description)
        if chosen and not any(microphone.name == chosen for microphone in microphones):
            # Picked before and not connected now: kept, and said so, rather than silently dropped.
            combo.append(chosen, f"{chosen} (not connected)")
        combo.set_active_id(chosen or MICROPHONE_DEFAULT_ID)

    def test_microphone(self, seconds: float = MICROPHONE_TEST_SECONDS) -> None:
        """Records for a few seconds from the chosen microphone, shows how loud it is as it goes,
        then says whether it heard anything. Nothing is kept or sent."""
        if self._microphone_test is not None:
            return
        chosen = self.fields["microphone"].get_active_id()
        microphone = None if chosen in (None, MICROPHONE_DEFAULT_ID) else chosen
        try:
            recording = MicrophoneRecording(microphone=microphone)
        except (RecorderUnavailable, OSError) as problem:
            self.microphone_test_result.set_text(f"✕ {problem}")
            return
        self._microphone_test = recording
        self.test_microphone_button.set_sensitive(False)
        self.microphone_test_result.set_text("Say something…")
        loudest = [0.0]
        started = time.monotonic()

        def tick() -> bool:
            if self._microphone_test is not recording:
                return False
            level = recording.current_level()
            loudest[0] = max(loudest[0], level)
            # Speech sits low on a linear scale; scaled up so talking fills most of the bar.
            self.microphone_level.set_value(min(1.0, level * 6))
            if time.monotonic() - started < seconds:
                return True
            self._finish_microphone_test(recording, loudest[0])
            return False

        GLib.timeout_add(100, tick)

    def _finish_microphone_test(self, recording, loudest: float) -> None:
        self._microphone_test = None
        self.test_microphone_button.set_sensitive(True)
        self.microphone_level.set_value(0)
        try:
            heard = recording.stop()
        except RecorderUnavailable as problem:
            self.microphone_test_result.set_text(f"✕ {problem}")
            return
        loudest = max(loudest, level_of(heard))
        self.microphone_test_result.set_text(microphone_test_verdict(loudest))

    def _stop_microphone_test(self) -> None:
        if self._microphone_test is not None:
            self._microphone_test.cancel()
            self._microphone_test = None
            self.test_microphone_button.set_sensitive(True)
            self.microphone_level.set_value(0)

    def _text(self, layout: Gtk.Box, title: str, placeholder: str, secret: bool = False) -> Gtk.Entry:
        entry = Gtk.Entry()
        entry.set_placeholder_text(placeholder)
        entry.set_visibility(not secret)
        self._row(layout, title, entry)
        return entry

    def _choice(self, layout: Gtk.Box, title: str, choices: tuple[tuple[str, str], ...]) -> Gtk.ComboBoxText:
        combo = Gtk.ComboBoxText()
        for value, label in choices:
            combo.append(value, label)
        self._row(layout, title, combo)
        return combo

    # MARK: values

    def load(self) -> None:
        # The file's own values: a key or backend given in the environment (OPENCLICKY_*_KEY,
        # BACKEND_URL) is never shown here, so Save never writes it into shell.json, which the
        # Mac app reads too. The window says when the environment wins.
        settings = load_settings(environment={})
        overridden = environment_overrides()
        self.microphone_test_result.set_text("")
        for field_name, widget in self.fields.items():
            value = getattr(settings, field_name)
            if field_name == "microphone":
                self._fill_microphones(value)
            elif isinstance(widget, Gtk.CheckButton):
                widget.set_active(bool(value))
            elif field_name == "kokoro_speaker":
                self._loaded_speaker = value
                chosen = voice_module.speaker_for(settings.language_code, value)
                widget.set_active_id(str(chosen.id if chosen else voice_module.SPEAKERS[0].id))
            elif isinstance(widget, Gtk.ComboBoxText):
                if not widget.set_active_id(value):
                    # A key picked by hand in shell.json that is not in the list is shown, not lost.
                    widget.append(value, value)
                    widget.set_active_id(value)
            elif field_name == "backend_url":
                widget.set_text("" if value == HOSTED_BACKEND_URL else value)
            else:
                widget.set_text(value or "")
        self.start_at_login.set_active(autostart.is_enabled())
        self.account_form.refresh()
        self.account_status.set_text("")
        self.agent_mode.set_active(lanes.agent_mode_is_on())
        self._show_agent_state()
        self.offline_row.show_state()
        self.voice_row.show_state()
        self._show_shortcuts()
        self.status_label.set_text(f"Set in the environment, which wins while it is set: {', '.join(overridden)}." if overridden else "")
        self._update_routes()

    def values(self) -> dict[str, str | None]:
        values = {}
        for field_name, widget in self.fields.items():
            if isinstance(widget, Gtk.CheckButton):
                values[field_name] = widget.get_active()
            elif isinstance(widget, Gtk.ComboBoxText):
                values[field_name] = widget.get_active_id()
            else:
                values[field_name] = widget.get_text()
        # "System default" is no key at all in shell.json.
        if values.get("microphone") == MICROPHONE_DEFAULT_ID:
            values["microphone"] = None
        # The voice is written only when it was picked: `kokoroSpeaker` is the Mac app's own key too,
        # and the language's own first speaker is what an unset one means anyway.
        default_speaker = voice_module.speaker_for(values.get("language_code"), None)
        if self._loaded_speaker is None and default_speaker and values.get("kokoro_speaker") == str(default_speaker.id):
            values["kokoro_speaker"] = None
        return values

    def _update_routes(self) -> None:
        values = self.values()
        # A key set in the environment counts too: it is what the app uses while it is set.
        from_environment = load_settings()

        def has(field_name: str) -> bool:
            if field_name == "account_token":
                # The account form writes it straight into shell.json.
                return bool(from_environment.account_token)
            return bool((values.get(field_name) or "").strip()) or bool(getattr(from_environment, field_name, None) and
                                                                      field_name.endswith("_api_key"))
        engine = values.get("dictation_engine") or "auto"
        offline_ready = offline.is_ready()
        if engine == "offline" or (engine == "auto" and offline_ready):
            dictation = "this computer (offline)" if offline_ready else "nowhere yet: download the offline engine"
        elif engine == "openai" or (engine == "auto" and has("openai_api_key")):
            dictation = "your OpenAI key" if has("openai_api_key") else "nowhere yet: add an OpenAI key"
        elif engine == "account" or (engine == "auto" and has("account_token")):
            dictation = "your OpenClicky account" if has("account_token") else "nowhere yet: sign in to your account"
        else:
            dictation = "nowhere yet: download the offline engine, add an OpenAI key, or sign in"
        answers = ("your Anthropic key" if has("anthropic_api_key") else "your OpenRouter key" if has("openrouter_api_key")
                   else "your OpenAI key" if has("openai_api_key")
                   else "your OpenClicky account" if has("account_token") else "nothing yet: sign in, or add a key")
        if not values.get("speak_answers"):
            spoken = "Answers are shown, not spoken."
        elif voice_module.is_ready():
            spoken = "Answers are spoken on this computer."
        else:
            spoken = "Answers are shown; download the offline voice to hear them."
        self.routes_label.set_text(f"Dictation goes to {dictation}.\nAnswers come from {answers}.\n{spoken}")

    def save(self) -> None:
        try:
            save_settings(self.values())
            preferences.set_values(**{lanes.AGENT_MODE: None if self.agent_mode.get_active() else False})
        except (OSError, ValueError, RuntimeError) as failure:  # RuntimeError: a symlink loop
            self.status_label.set_text(f"Not saved: {failure}")
            return
        try:
            # Only when the box changed: an entry the person edited (arguments, a delay) is kept.
            if self.start_at_login.get_active() != autostart.is_enabled():
                autostart.set_enabled(self.start_at_login.get_active())
        except OSError as failure:
            self.status_label.set_text(f"Saved, but starting at login could not be changed: {failure}")
            self.on_saved(load_settings())
            return
        overridden = environment_overrides()
        self.status_label.set_text("Saved. A new dictation key takes effect when OpenClicky restarts."
                                   + (f"\nSet in the environment, which wins while it is set: {', '.join(overridden)}." if overridden else ""))
        self.on_saved(load_settings())

    def show(self) -> None:
        self.load()
        # As tall as it needs, up to the screen less room for panels and the title bar.
        display = Gdk.Display.get_default()
        _screen, mouse_x, mouse_y = display.get_default_seat().get_pointer().get_position()[0:3]
        monitor_height = display.get_monitor_at_point(mouse_x, mouse_y).get_workarea().height
        self.scroller.set_max_content_height(max(300, monitor_height - 160))
        present_with_focus(self.window)


class ModelRow:
    """A model's row in Settings (the offline engine, the offline voice): its state, and the one
    action that makes sense now: Download (with its size), Cancel while downloading, or Remove."""

    def __init__(self, window: "SettingsWindow", layout: Gtk.Box, title: str, asset: "offline.ModelAsset", needs_player: bool,
                 downloaded_line: str, not_downloaded_line: str) -> None:
        self.window = window
        self.asset = asset
        self.needs_player = needs_player
        self.downloaded_line = downloaded_line
        self.not_downloaded_line = not_downloaded_line
        self.status = Gtk.Label(xalign=0)
        self.status.set_line_wrap(True)
        self.button = Gtk.Button()
        self.button.connect("clicked", lambda _button: self._clicked())
        row = Gtk.Box(spacing=8)
        row.pack_start(self.status, True, True, 0)
        row.pack_start(self.button, False, False, 0)
        window._row(layout, title, row)
        self.progress = Gtk.ProgressBar()
        self.progress.set_margin_start(162)
        self.progress.set_no_show_all(True)
        layout.pack_start(self.progress, False, False, 0)
        self._cancelled: threading.Event | None = None

    def show_state(self, message: str | None = None) -> None:
        if self._cancelled is not None:
            return  # a download in progress shows its own progress
        if not offline.is_engine_available():
            self.status.set_text(message or "Needs sherpa-onnx: pip install 'openclicky-linux[offline]'")
            self.button.set_label("Download")
            self.button.set_sensitive(False)
        elif offline.is_installed(self.asset):
            missing_player = self.needs_player and voice_module.find_player() is None
            self.status.set_text(message or (f"Downloaded ({offline.installed_size_bytes(self.asset) // 1_000_000} MB). "
                                             + ("No audio player found: install pipewire or pulseaudio-utils." if missing_player
                                                else self.downloaded_line)))
            self.button.set_label("Remove")
            self.button.set_sensitive(True)
        else:
            self.status.set_text(message or "Not downloaded. " + self.not_downloaded_line.format(size=self.asset.size_bytes // 1_000_000))
            self.button.set_label("Download")
            self.button.set_sensitive(True)
        self.progress.hide()

    def _clicked(self) -> None:
        if self._cancelled is not None:
            self._cancelled.set()
            return
        if offline.is_installed(self.asset):
            offline.remove(self.asset)
            self.show_state()
            self.window._update_routes()
            return
        self.start_download()

    def start_download(self, asset: "offline.ModelAsset | None" = None) -> None:
        """Downloads in the background; progress and the outcome come back to the main loop."""
        cancelled = threading.Event()
        self._cancelled = cancelled
        self.button.set_label("Cancel")
        self.progress.set_fraction(0)
        self.progress.show()
        self.status.set_text("Downloading…")

        def progress(done: int, total: int) -> None:
            GLib.idle_add(lambda: (self._show_progress(done, total), False)[1])

        def run() -> None:
            try:
                offline.download(asset or self.asset, on_progress=progress, cancelled=cancelled)
                outcome = None
            except offline.OfflineEngineError as failure:
                outcome = str(failure)
            GLib.idle_add(lambda: (self._finished(outcome), False)[1])

        threading.Thread(target=run, daemon=True).start()

    def _show_progress(self, done: int, total: int) -> None:
        if self._cancelled is None:
            return
        self.progress.set_fraction(min(1.0, done / total) if total else 0)
        self.status.set_text(f"Downloading… {done // 1_000_000} of {total // 1_000_000} MB")

    def _finished(self, problem: str | None) -> None:
        self._cancelled = None
        self.show_state(f"Not downloaded: {problem}." if problem else None)
        self.window._update_routes()


def pointer_icon(size: int = 22) -> GdkPixbuf.Pixbuf:
    """The tray icon: the pointer's own triangle, drawn pixel by pixel (no cairo bridge needed)."""
    scale = (size - 4) / max(POINTER_WIDTH, POINTER_HEIGHT)
    outline = [(3 + x * scale, 2 + y * scale) for x, y in POINTER_OUTLINE]
    pixels = bytearray(size * size * 4)
    for x, y, width, _height in polygon_rows(outline, size, size):
        for column in range(x, x + width):
            offset = (y * size + column) * 4
            pixels[offset:offset + 4] = bytes((0xF0, 0x45, 0x2B, 0xFF))
    return GdkPixbuf.Pixbuf.new_from_bytes(GLib.Bytes.new(bytes(pixels)), GdkPixbuf.Colorspace.RGB, True, 8, size, size, size * 4)


class TrayIcon:
    def __init__(self, on_ask: Callable[[], None], on_settings: Callable[[], None], on_quit: Callable[[], None],
                 on_words: Callable[[], None] | None = None) -> None:
        self.menu = Gtk.Menu()
        items = [("Ask…", on_ask)] + ([("Your words…", on_words)] if on_words else []) + [("Settings…", on_settings), (None, None), ("Quit", on_quit)]
        for label, action in items:
            item = Gtk.SeparatorMenuItem() if label is None else Gtk.MenuItem(label=label)
            if action is not None:
                item.connect("activate", lambda _item, action=action: action())
            self.menu.append(item)
        self.menu.show_all()
        # Gtk.StatusIcon is the X11 system tray protocol: deprecated in GTK, still what Xfce, KDE,
        # MATE and Cinnamon trays show. Where there is no tray, the shortcuts still work.
        self.icon = Gtk.StatusIcon.new_from_pixbuf(pointer_icon())
        self.icon.set_tooltip_text("OpenClicky")
        self.icon.connect("activate", lambda _icon: on_ask())
        self.icon.connect("popup-menu", lambda icon, button, time: self.menu.popup(None, None, Gtk.StatusIcon.position_menu, icon, button, time))
