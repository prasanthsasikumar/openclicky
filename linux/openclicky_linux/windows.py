"""The windows people use directly: the ask box, the Settings window, and the tray icon.

- The ask box (left ctrl tapped twice, or the tray): one line, Enter sends, Esc closes. It takes the
  keyboard while open; when it closes the window manager gives the focus back.
- Settings: the fields in `~/.openclicky/shell.json` that matter on Linux, in groups (dictation, the
  voice, the pointer, privacy, the account, answers, your own keys, the agent, startup), with a line
  that says where dictation and answers will go as you type. Test asks the source answers would
  come from now (unsaved keys too) about a red square. Save writes them with `config.save_settings` (every
  other key stays). Keys show as dots. The account signs in and out at once (`account_form.py`).
- The tray icon (any system tray: Xfce, KDE, MATE, Cinnamon; GNOME needs an extension): Ask…,
  Your words…, Settings…, Quit.
"""

from __future__ import annotations

import dataclasses
import subprocess
from pathlib import Path
import threading
import time
from typing import Callable

import gi

gi.require_version("Gtk", "3.0")
gi.require_version("Gdk", "3.0")
gi.require_version("GdkX11", "3.0")
gi.require_version("Pango", "1.0")
from gi.repository import Gdk, GdkPixbuf, GdkX11, Gio, GLib, Gtk, Pango  # noqa: E402

from . import __version__, accessibility, auto_download, island, island_plugins, updater, answers, applog, autostart, languages, lanes, offline, preferences, speech_voices  # noqa: E402
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
ORB_SHOWS_CHOICES = (("whileDictating", "While I dictate"), ("always", "Always, resting as a small pill"))
ORB_POSITION_CHOICES = (("bottom", "Bottom centre"), ("bottomLeft", "Bottom left"), ("bottomRight", "Bottom right"), ("top", "Top centre"))
ORB_SIZE_CHOICES = (("small", "Small"), ("normal", "Normal"), ("large", "Large"))
ORB_THEME_CHOICES = (("dark", "Dark"), ("light", "Light"))
QUIET_CHOICES = (("1", "1 minute of quiet"), ("3", "3 minutes of quiet"), ("5", "5 minutes of quiet"),
                 ("10", "10 minutes of quiet"), ("0", "never"))
AUTOMATIC_SOURCE = "automatic"
ANSWER_SOURCE_CHOICES = ((AUTOMATIC_SOURCE, "Automatic (the first that works)"),
                         *((source.id, source.display_name) for source in answers.SOURCES))
# Whose key a source's key is, where its short name is the model's ("Claude", "Grok").
KEY_VENDORS = {"anthropic": "Anthropic", "xai": "xAI"}
# What a menu shows when shell.json has nothing for it.
CHOICE_DEFAULTS = {"island_placement": "notch", "speech_voice": "automatic", "answer_source": AUTOMATIC_SOURCE, "claude_model": answers.DEFAULT_CLAUDE_MODEL, "language_code": languages.AUTOMATIC,
                   "region": languages.detected_region(), "script": languages.NATIVE}
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
        ("Hold", (key, "Shift"), "Hey Clicky: say how to change the selected text (“make it formal”)."),
        ("Hold Left Alt, tap twice", (key,), "Hands-free on fast voice: just talk, until you do it again."),
        ("Tap twice", ("Left Ctrl",), "Ask by typing."),
        ("Press", ("Esc",), "Stop: cancels dictation, stops a spoken answer, ends a guide."),
    ]
    if pointer_presence == "onShake":
        rows.append(("Shake", ("the mouse",), "Bring the pointer back beside the mouse."))
    return rows
DICTATION_ENGINE_CHOICES = (("auto", "Automatic (offline when downloaded)"), ("offline", "Offline, on this computer"),
                            ("openai", "OpenAI (your key)"), ("account", "OpenClicky account"),
                            ("sarvam", "Sarvam (your key; the Indian languages)"),
                            ("assemblyai", "AssemblyAI, words as you speak (OpenClicky account)"))
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
    def __init__(self, on_saved: Callable[[LinuxSettings], None] = lambda _settings: None,
                 on_paste_last: Callable[[], None] = lambda: None) -> None:
        self.on_saved = on_saved
        self.on_paste_last = on_paste_last
        # The tours (set by the app; nothing in the Settings-only window).
        self.on_welcome_tour: Callable[[], None] = lambda: None
        self.on_settings_tour: Callable[[], None] = lambda: None
        self.window = Gtk.Window(title="OpenClicky")
        self.window.set_default_size(920, 680)
        self.window.connect("delete-event", lambda *_: self.window.hide() or True)
        # A test still recording when the window closes stops: the microphone is never left open.
        self.window.connect("hide", lambda _window: self._stop_microphone_test())
        self.fields: dict[str, Gtk.Widget] = {}
        self._loaded_speaker: int | None = None
        self._unset_choices: set[str] = set()
        # Every row, for the search: (page id, its words, the widget to show).
        self._searchable: list[tuple[str, str, Gtk.Widget]] = []
        self._page_titles: dict[str, str] = {}

        # The sidebar (with the search over every setting above it) and the pages beside it.
        self.stack = Gtk.Stack()
        self.stack.set_transition_type(Gtk.StackTransitionType.CROSSFADE)
        sidebar = Gtk.StackSidebar()
        sidebar.set_stack(self.stack)
        self.search = Gtk.SearchEntry()
        self.search.set_placeholder_text("search settings")
        self.search.connect("search-changed", lambda _entry: self._search())
        left = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=6)
        left.set_border_width(8)
        left.set_size_request(200, -1)
        left.pack_start(self.search, False, False, 0)
        left.pack_start(sidebar, True, True, 0)
        right = Gtk.Box(orientation=Gtk.Orientation.VERTICAL)
        right.pack_start(self.stack, True, True, 0)
        outer = Gtk.Box()
        outer.pack_start(left, False, False, 0)
        outer.pack_start(Gtk.Separator(orientation=Gtk.Orientation.VERTICAL), False, False, 0)
        outer.pack_start(right, True, True, 0)
        self.window.add(outer)

        layout = self._page("general", "General")
        self._heading(layout, "Startup")
        self.start_at_login = self._check(layout, "Start OpenClicky when I log in")
        self._heading(layout, "Updates")
        self.fields["auto_update"] = self._check(layout, "Check for updates on their own, once a day")
        updates = Gtk.Box(spacing=8)
        self.check_updates_button = Gtk.Button(label="Check now")
        self.check_updates_button.connect("clicked", lambda _button: self.check_updates())
        self.install_update_button = Gtk.Button(label="Install and restart")
        self.install_update_button.get_style_context().add_class("suggested-action")
        self.install_update_button.set_no_show_all(True)
        self.install_update_button.connect("clicked", lambda _button: self.install_update())
        updates.pack_start(self.check_updates_button, False, False, 0)
        updates.pack_start(self.install_update_button, False, False, 0)
        self._row(layout, f"OpenClicky {__version__}", updates)
        self.update_status = self._small_label("")
        layout.pack_start(self.update_status, False, False, 0)
        self.available_update = None
        self._heading(layout, "Tours")
        tours = Gtk.Box(spacing=8)
        for label, attribute in (("Welcome tour", "on_welcome_tour"), ("Settings tour", "on_settings_tour")):
            button = Gtk.Button(label=label)
            button.connect("clicked", lambda _button, attribute=attribute: getattr(self, attribute)())
            tours.pack_start(button, False, False, 0)
        self._row(layout, "Play again", tours)
        self._heading(layout, "Where OpenClicky lives")
        self.fields["island_placement"] = self._choice(layout, "The island", island.PLACEMENTS)
        self.island_detail = self._small_label("")
        layout.pack_start(self.island_detail, False, False, 0)
        self.fields["island_placement"].connect("changed", lambda combo: self.island_detail.set_text(
            island.PLACEMENT_DETAILS.get(combo.get_active_id() or "notch", "")))
        self.fields["glass_level"] = Gtk.Scale.new_with_range(Gtk.Orientation.HORIZONTAL, 0.0, 1.0, 0.05)
        self.fields["glass_level"].set_draw_value(False)
        self.fields["glass_level"].add_mark(0.0, Gtk.PositionType.BOTTOM, "all black")
        self.fields["glass_level"].add_mark(1.0, Gtk.PositionType.BOTTOM, "glass")
        self._row(layout, "Glass", self.fields["glass_level"])
        self._heading(layout, "Appearance")
        self.fields["reduce_motion"] = self._check(layout, "Reduce motion: the pointer jumps instead of flying, captions appear whole")
        self.general_box = layout

        layout = self._page("dictation", "Dictation")
        self._heading(layout, "How you dictate")
        self.fields["dictation_key"] = self._choice(layout, "Hold to dictate", DICTATION_KEY_CHOICES)
        self.fields["region"] = self._choice(layout, "Region", languages.REGIONS)
        self.fields["region"].connect("changed", lambda _combo: self._fill_languages())
        self.fields["language_code"] = self._choice(layout, "Language", ())
        self.fields["language_code"].connect("changed", lambda _combo: self._show_script_row())
        self.fields["script"] = self._choice(layout, "Script", languages.SCRIPTS)
        self.script_row = self.fields["script"].get_parent()
        self.fields["microphone"] = self._microphone_row(layout)
        self._heading(layout, "Who hears you")
        self.fields["dictation_engine"] = self._choice(layout, "Engine", DICTATION_ENGINE_CHOICES)
        self.offline_row = ModelRow(self, layout, "Offline engine", offline.PARAKEET, needs_player=False,
                                    downloaded_line="Your words never leave this computer.",
                                    not_downloaded_line="Parakeet, about {size} MB, 25 European languages, no key needed.")
        # The names the tests (and the old row) use.
        self.offline_status, self.offline_button = self.offline_row.status, self.offline_row.button
        self.start_offline_download = self.offline_row.start_download
        self._heading(layout, "Clean-up")
        self.fields["polish_with_model"] = self._check(layout, "Clean up my words with AI (punctuation, “3 PM”, your style's rules)")
        self.fields["polish_offline_takes"] = self._check(layout, "Also clean up what the offline engine heard (it then leaves this computer)")
        self._heading(layout, "Takes")
        self.fields["sounds"] = self._check(layout, "Play a short tone as a take starts and ends")
        self.fields["quiet_minutes"] = self._choice(layout, "End a tapped take after", QUIET_CHOICES)
        self.fields["keep_failed_audio"] = self._check(layout, "Keep the recording of a take that couldn't finish, to retry it")
        self.fields["clipboard_history"] = self._check(layout, "Keep what I copy in the history too")
        paste_last = Gtk.Button(label="Paste Now")
        paste_last.set_halign(Gtk.Align.START)
        paste_last.connect("clicked", lambda _button: self.on_paste_last())
        self._row(layout, "Paste my last take", paste_last)

        layout = self._page("clicky", "Clicky")
        self._heading(layout, "The pointer")
        self.fields["pointer_presence"] = self._choice(layout, "Show the pointer", POINTER_CHOICES)
        if is_wayland_session():
            # Wayland never says where the mouse is: there is nothing to follow.
            self.fields["pointer_presence"].set_sensitive(False)
            layout.pack_start(self._small_label("On Wayland the pointer rests at the top of the screen and flies out to what an "
                                                "answer is about; moving the mouse sends it back. Wayland never tells an app where "
                                                "the mouse is, so it cannot follow it."), False, False, 0)
        self._heading(layout, "Fast voice")
        self.fields["fast_voice"] = self._check(layout, "Fast voice: asking out loud talks back at once (OpenAI Realtime, with your OpenAI key)")
        layout.pack_start(self._small_label("Hold your dictation key and Left Alt to talk. Tap your dictation key twice while holding "
                                            "Left Alt for hands-free: just talk, no keys, until you do it again."), False, False, 0)
        self._heading(layout, "Answers")
        self.fields["answer_source"] = self._answer_source_row(layout)
        self.fields["claude_model"] = self._choice(layout, "Claude model", answers.CLAUDE_MODELS)
        self.fields["use_claude_code_plan"] = self._check(layout, "Use my Claude plan through Claude Code (the claude command)")
        self.fields["use_codex_plan"] = self._check(layout, "Use my ChatGPT plan through Codex (the codex command)")
        for plan in ("use_claude_code_plan", "use_codex_plan"):
            self.fields[plan].connect("toggled", lambda _button: self._update_routes())
        layout.pack_start(self._small_label("A plan answers a little slower than a key: each question starts the command afresh."), False, False, 0)
        self._heading(layout, "Agent")
        self.agent_mode = self._check(layout, "Hand bigger tasks to a Codex agent (“make a folder of my screenshots and zip it”)")
        self.agent_status = self._small_label("")
        layout.pack_start(self.agent_status, False, False, 0)
        self.fields["workspace"] = self._text(layout, "Workspace folder", "~/OpenClicky (where the agent works and saves files)")
        self.clicky_box = layout

        layout = self._page("voice", "Voice")
        self._heading(layout, "Spoken answers")
        self.fields["speak_answers"] = self._check(layout, "Speak answers")
        self.fields["speak_answers"].connect("toggled", lambda _button: self._update_routes())
        self.fields["speech_voice"] = self._speech_voice_row(layout)
        self._heading(layout, "Kokoro, on this computer")
        self.fields["kokoro_speaker"] = self._choice(layout, "Voice", tuple((str(speaker.id), speaker.label) for speaker in voice_module.SPEAKERS))
        self.voice_row = ModelRow(self, layout, "Offline voice", voice_module.KOKORO, needs_player=True,
                                  downloaded_line="Answers are spoken on this computer.",
                                  not_downloaded_line="Kokoro, about {size} MB, the Mac app's offline voice.")

        layout = self._page("orb", "The Orb")
        self._heading(layout, "The orb at the bottom of the screen")
        layout.pack_start(self._small_label("It shows each take as it goes: listening, moving your words, and where they went."), False, False, 0)
        self.fields["orb_shows"] = self._choice(layout, "Show the orb", ORB_SHOWS_CHOICES)
        self.fields["orb_position"] = self._choice(layout, "Where", ORB_POSITION_CHOICES)
        self.fields["orb_size"] = self._choice(layout, "Size", ORB_SIZE_CHOICES)
        self.fields["orb_theme"] = self._choice(layout, "Look", ORB_THEME_CHOICES)
        self.orb_box = layout

        layout = self._page("plugins", "Plugins")
        self._heading(layout, "On the island")
        layout.pack_start(self._small_label("Extras for the island, each off until you switch it on. Open the island with a click; "
                                            "the buttons along its top switch between them."), False, False, 0)
        self.plugin_switches: dict[str, Gtk.CheckButton] = {}
        plugin_lines = {island_plugins.SYSTEM: "System: CPU, memory, disk and battery",
                        island_plugins.MEDIA: "Now playing: the song playing, with previous, play and next",
                        island_plugins.AGENTS: "Coding agents: what Claude Code and Codex are doing, and your Claude plan's limits",
                        island_plugins.SHELF: "Shelf: drop files, links or text on the island to keep them, drag them out again"}
        for plugin, line in plugin_lines.items():
            self.plugin_switches[plugin] = self._check(layout, line)
        self.default_component = self._choice(layout, "Open the island on", tuple(
            (name, title) for name, title in {**island.BUILT_IN_TITLES, **island_plugins.PLUGIN_TITLES}.items()))

        layout = self._page("keyboard", "Shortcuts")
        self._heading(layout, "Every shortcut")
        # Filled from the dictation key and the pointer setting, and again whenever either changes.
        self.shortcuts_grid = Gtk.Grid(column_spacing=14, row_spacing=6)
        self.shortcuts_grid.set_margin_bottom(4)
        layout.pack_start(self.shortcuts_grid, False, False, 0)
        self._heading(layout, "On your keyboard")
        self.keyboard_sketch = KeyboardSketch()
        layout.pack_start(self.keyboard_sketch.area, False, False, 0)
        self.shortcuts_box = layout

        layout = self._page("keys", "Keys & Plans")
        self._heading(layout, "Your own keys")
        layout.pack_start(self._small_label("Keys go straight to their company from this computer, never to OpenClicky."), False, False, 0)
        self.fields["openai_api_key"] = self._text(layout, "OpenAI key", "sk-… (dictation, and answers)", secret=True)
        self.fields["anthropic_api_key"] = self._text(layout, "Anthropic key", "sk-ant-… (answers)", secret=True)
        self.fields["openrouter_api_key"] = self._text(layout, "OpenRouter key", "sk-or-… (answers)", secret=True)
        self.fields["gemini_api_key"] = self._text(layout, "Gemini key", "AIza… (answers)", secret=True)
        self.fields["xai_api_key"] = self._text(layout, "xAI key", "xai-… (answers)", secret=True)
        self.fields["mistral_api_key"] = self._text(layout, "Mistral key", "(answers)", secret=True)
        self.fields["groq_api_key"] = self._text(layout, "Groq key", "gsk_… (answers)", secret=True)
        self.fields["sarvam_api_key"] = self._text(layout, "Sarvam key", "sk_… (dictation in the Indian languages, a voice)", secret=True)
        self.fields["elevenlabs_api_key"] = self._text(layout, "ElevenLabs key", "(a voice)", secret=True)
        self.fields["google_cloud_api_key"] = self._text(layout, "Google Cloud key", "AIza… (a voice; Text-to-Speech enabled)", secret=True)
        self.fields["amazon_polly_credentials"] = self._text(layout, "Amazon Polly", "accessKeyId:secretKey[:region] (a voice)", secret=True)
        self._heading(layout, "OpenClicky's server")
        self.fields["backend_url"] = self._text(layout, "Backend", HOSTED_BACKEND_URL)

        layout = self._page("account", "Account")
        self._heading(layout, "Your OpenClicky account")
        layout.pack_start(self._small_label("A free OpenClicky account answers and transcribes when you have no key of your own. "
                                            "No password: your email, and a code when it already has an account."), False, False, 0)
        self.account_status = Gtk.Label(xalign=0)
        self.account_status.set_line_wrap(True)
        self.account_form = AccountForm(say=self._say_about_account, on_changed=self._update_routes)
        layout.pack_start(self.account_form.box, False, False, 0)
        layout.pack_start(self.account_status, False, False, 0)
        self.account_box = layout

        layout = self._page("privacy", "Privacy & Security")
        self._heading(layout, "Your screen")
        self.fields["pause_before_sending"] = self._check(layout, "Pause before sending my screen (esc calls a question off)")
        self._heading(layout, "Text boxes")
        # The desktop's own switch (a screen reader's), not a setting of OpenClicky's: read from the desktop.
        self.read_text_boxes = self._check(layout, "Read text boxes: write “rephrase this” in place, and know a text box from none")
        layout.pack_start(self._small_label("Turns on your desktop's accessibility, which screen readers use. Apps opened before "
                                            "may need reopening. Password boxes are never read."), False, False, 0)
        self._heading(layout, "Your words")
        self.fields["incognito"] = self._check(layout, "Incognito: keep no history of what I dictate")
        self._heading(layout, "Where everything is kept")
        layout.pack_start(self._small_label("Everything stays on this computer. A question sends its screenshot to the company that "
                                            "answers it; a take's audio goes to the engine that hears it (none, offline)."), False, False, 0)
        for title, folder in where_things_are():
            self._row(layout, title, self._folder_button(folder))
        delete_everything = Gtk.Button(label="Delete my history and recordings…")
        delete_everything.set_halign(Gtk.Align.START)
        delete_everything.connect("clicked", lambda _button: self._delete_history())
        layout.pack_start(delete_everything, False, False, 6)
        self.privacy_box = layout

        layout = self._page("about", "About")
        self._heading(layout, f"OpenClicky {__version__} for Linux")
        layout.pack_start(self._small_label("Dictation anywhere, and a pointer that answers about your screen. Open source, MIT."), False, False, 0)
        for title, address in (("Website", "https://openclicky.flowsxr.com"), ("Source", "https://github.com/prasanthsasikumar/openclicky")):
            link = Gtk.LinkButton(uri=address, label=address)
            link.set_halign(Gtk.Align.START)
            self._row(layout, title, link)
        self._row(layout, "The log (timings, never your words)", self._folder_button(applog.log_path().parent))
        self.about_box = layout

        self._results = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=4)
        self._results.set_border_width(18)
        results_scroller = Gtk.ScrolledWindow()
        results_scroller.add(self._results)
        self.stack.add_named(results_scroller, "search")

        foot = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=4)
        foot.set_border_width(12)
        self.routes_label = Gtk.Label(xalign=0)
        self.routes_label.set_line_wrap(True)
        foot.pack_start(self.routes_label, False, False, 0)
        self.status_label = Gtk.Label(xalign=0)
        self.status_label.set_line_wrap(True)
        foot.pack_start(self.status_label, False, False, 0)
        buttons = Gtk.Box(spacing=8)
        buttons.set_halign(Gtk.Align.END)
        close_button = Gtk.Button(label="Close")
        close_button.connect("clicked", lambda _button: self.window.hide())
        self.save_button = Gtk.Button(label="Save")
        self.save_button.get_style_context().add_class("suggested-action")
        self.save_button.connect("clicked", lambda _button: self.save())
        buttons.pack_start(close_button, False, False, 0)
        buttons.pack_start(self.save_button, False, False, 0)
        foot.pack_start(buttons, False, False, 0)
        right.pack_start(Gtk.Separator(), False, False, 0)
        right.pack_start(foot, False, False, 0)
        for widget in self.fields.values():
            if not isinstance(widget, (Gtk.CheckButton, Gtk.Scale)):
                widget.connect("changed", lambda _widget: self._update_routes())
        self.fields["dictation_key"].connect("changed", lambda _widget: self._show_shortcuts())
        self.fields["pointer_presence"].connect("changed", lambda _widget: self._show_shortcuts())
        keycaps = Gtk.CssProvider()
        keycaps.load_from_data(b".openclicky-keycap { border: 1px solid alpha(currentColor, 0.35); border-radius: 4px;"
                               b" padding: 0 6px; font-weight: 600; }")
        Gtk.StyleContext.add_provider_for_screen(Gdk.Screen.get_default(), keycaps, Gtk.STYLE_PROVIDER_PRIORITY_APPLICATION)

    # MARK: pages and the search

    def _page(self, page_id: str, title: str) -> Gtk.Box:
        """A sidebar page: its rows go in the box returned, which scrolls."""
        layout = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=6)
        layout.set_border_width(18)
        scroller = Gtk.ScrolledWindow()
        scroller.set_policy(Gtk.PolicyType.NEVER, Gtk.PolicyType.AUTOMATIC)
        scroller.add(layout)
        self.stack.add_titled(scroller, page_id, title)
        self._page_titles[page_id] = title
        self._current_page = page_id
        return layout

    def add_page(self, page_id: str, title: str, content: Gtk.Widget, before: str | None = None, words: str = "") -> None:
        """A page built elsewhere (the words, how it writes), placed before `before` in the sidebar."""
        self.stack.add_titled(content, page_id, title)
        self._page_titles[page_id] = title
        if before is not None:
            position = [child for child in self.stack.get_children()].index(self.stack.get_child_by_name(before))
            self.stack.child_set_property(content, "position", position)
        self._searchable.append((page_id, f"{title} {words}", content))
        content.show_all()

    def show_page(self, page_id: str) -> None:
        self.search.set_text("")
        self.stack.set_visible_child_name(page_id)

    @staticmethod
    def _folder_button(folder) -> Gtk.Button:
        button = Gtk.Button(label=str(folder).replace(str(Path.home()), "~"))
        button.set_halign(Gtk.Align.START)
        button.set_relief(Gtk.ReliefStyle.NONE)
        button.get_child().set_ellipsize(Pango.EllipsizeMode.MIDDLE)
        button.get_child().set_max_width_chars(48)
        button.set_tooltip_text(str(folder))
        button.connect("clicked", lambda _button: _open_folder(folder))
        return button

    def _delete_history(self) -> None:
        from .history import TakeAudioStore, TakeStore

        dialog = Gtk.MessageDialog(transient_for=self.window, modal=True, message_type=Gtk.MessageType.QUESTION,
                                   buttons=Gtk.ButtonsType.OK_CANCEL, text="Delete every take and every kept recording?")
        dialog.format_secondary_text("This cannot be undone. Your keys, dictionary, shortcuts and styles stay.")
        answer = dialog.run()
        dialog.destroy()
        if answer != Gtk.ResponseType.OK:
            return
        try:
            store = TakeStore()
            store.delete_all()
            store.close()
            TakeAudioStore().discard_all()
        except (OSError, ValueError) as failure:
            self.status_label.set_text(f"Not deleted: {failure}")
            return
        self.status_label.set_text("Your history and recordings are deleted.")

    def check_updates(self) -> None:
        """Asks GitHub for a newer Linux package, off the main loop."""
        self.check_updates_button.set_sensitive(False)
        self.update_status.set_text("Checking…")

        def run() -> None:
            try:
                found, problem = updater.check(), None
            except OSError as failure:
                found, problem = None, f"Couldn't check: {failure}"
            GLib.idle_add(lambda: (self.show_update(found, problem), False)[1])

        threading.Thread(target=run, daemon=True).start()

    def show_update(self, update, problem: str | None = None) -> None:
        self.check_updates_button.set_sensitive(True)
        self.available_update = update
        if problem:
            self.update_status.set_text(problem)
        elif update is None:
            self.update_status.set_text("You have the newest version.")
        elif not updater.is_installed_copy():
            self.update_status.set_text(f"Version {update.version} is out. This copy runs from a checkout: update it with git pull.")
        else:
            self.update_status.set_text(f"Version {update.version} is ready.")
            self.install_update_button.show()

    def install_update(self) -> None:
        update = self.available_update
        if update is None:
            return
        self.install_update_button.set_sensitive(False)
        self.update_status.set_text(f"Installing {update.version}…")

        def run() -> None:
            try:
                updater.install(update)
            except OSError as failure:
                problem = f"Not installed: {failure}"
                GLib.idle_add(lambda: (self.update_status.set_text(problem), self.install_update_button.set_sensitive(True), False)[-1])
                return
            GLib.idle_add(lambda: (updater.restart(), False)[1])

        threading.Thread(target=run, daemon=True).start()

    def _check(self, layout: Gtk.Box, label: str) -> Gtk.CheckButton:
        check = Gtk.CheckButton(label=label)
        layout.pack_start(check, False, False, 0)
        self._searchable.append((self._current_page, label, check))
        return check

    def search_results(self, query: str) -> list[tuple[str, str, Gtk.Widget]]:
        """The rows whose words hold every word of `query` (any case), page by page."""
        words = query.lower().split()
        if not words:
            return []
        return [(page_id, text, widget) for page_id, text, widget in self._searchable
                if all(word in f"{self._page_titles.get(page_id, '')} {text}".lower() for word in words)]

    def _search(self) -> None:
        query = self.search.get_text().strip()
        for child in self._results.get_children():
            self._results.remove(child)
        if not query:
            if self.stack.get_visible_child_name() == "search":
                self.stack.set_visible_child_name("general")
            return
        found = self.search_results(query)
        if not found:
            self._results.pack_start(self._small_label(f"Nothing in settings matches “{query}”."), False, False, 0)
        for page_id, text, widget in found[:40]:
            button = Gtk.Button(label=f"{self._page_titles[page_id]}  ›  {text.split('(')[0].strip()}")
            button.set_relief(Gtk.ReliefStyle.NONE)
            button.get_child().set_xalign(0)
            button.connect("clicked", lambda _button, page_id=page_id, widget=widget: self._go_to(page_id, widget))
            self._results.pack_start(button, False, False, 0)
        self._results.show_all()
        self.stack.set_visible_child_name("search")

    def _go_to(self, page_id: str, widget: Gtk.Widget) -> None:
        self.show_page(page_id)
        if widget.get_can_focus():
            widget.grab_focus()

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
        if hasattr(self, "keyboard_sketch"):
            self.keyboard_sketch.show_keys(dictation_key)

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
        self._searchable.append((self._current_page, title, layout))
        label = Gtk.Label(xalign=0)
        label.set_markup(f"<b>{GLib.markup_escape_text(title)}</b>")
        label.set_margin_top(10)
        layout.pack_start(label, False, False, 0)

    def _row(self, layout: Gtk.Box, title: str, widget: Gtk.Widget) -> None:
        self._searchable.append((self._current_page, title, widget))
        row = Gtk.Box(spacing=12)
        label = Gtk.Label(label=title, xalign=0)
        label.set_size_request(150, -1)
        row.pack_start(label, False, False, 0)
        row.pack_start(widget, True, True, 0)
        layout.pack_start(row, False, False, 0)

    def _microphone_row(self, layout: Gtk.Box) -> Gtk.ComboBoxText:
        """The microphone menu, a Test button, and under them a level meter and what the test heard."""
        combo = _ellipsized(Gtk.ComboBoxText())
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

    def _speech_voice_row(self, layout: Gtk.Box) -> Gtk.ComboBoxText:
        """Who speaks the answers, a Test button, and under them what the test found (or the free voice's allowance)."""
        combo = Gtk.ComboBoxText()
        for value in (speech_voices.AUTOMATIC, *speech_voices.AUTOMATIC_ORDER):
            combo.append(value, speech_voices.TITLES[value])
        self.test_voice_button = Gtk.Button(label="Test")
        self.test_voice_button.connect("clicked", lambda _button: self.test_voice())
        picker = Gtk.Box(spacing=8)
        picker.pack_start(combo, True, True, 0)
        picker.pack_start(self.test_voice_button, False, False, 0)
        self._row(layout, "Who speaks", picker)
        self.voice_test_result = Gtk.Label(xalign=0)
        self.voice_test_result.set_line_wrap(True)
        self.voice_test_result.set_margin_start(162)
        layout.pack_start(self.voice_test_result, False, False, 0)
        return combo

    def test_voice(self) -> None:
        """Says one sentence in the voice that would speak now (unsaved keys too)."""
        settings = self.form_settings()
        choice = speech_voices.resolve(settings, voice_module.is_ready(), settings.language_code)
        if choice is None:
            self.voice_test_result.set_text("✕ Nothing can speak yet: download Kokoro, add a voice key, or sign in.")
            return
        self.test_voice_button.set_sensitive(False)
        self.voice_test_result.set_text(f"{speech_voices.TITLES[choice]}…")
        sentence = "hi, this is how i'll sound when i answer you."

        def run() -> None:
            problem = None
            try:
                if choice == speech_voices.SYSTEM:
                    subprocess.run(speech_voices.system_voice_command(sentence, settings.language_code), timeout=20)
                elif choice == speech_voices.KOKORO:
                    test_voice = voice_module.Voice()
                    test_voice.speak(sentence, settings.language_code, settings.kokoro_speaker)
                else:
                    voice_module.Voice().play_audio(speech_voices.audio_for(choice, sentence, settings, settings.language_code), "test")
            except (speech_voices.VoiceError, OSError, subprocess.SubprocessError) as failure:
                problem = str(failure)
            GLib.idle_add(lambda: (self._show_voice_test(choice, problem), False)[1])

        threading.Thread(target=run, daemon=True).start()

    def _show_voice_test(self, choice: str, problem: str | None) -> None:
        self.test_voice_button.set_sensitive(True)
        self.voice_test_result.set_text(f"✕ {problem}" if problem else f"✓ {speech_voices.TITLES[choice]} spoke.")

    def _show_voice_allowance(self) -> None:
        """The free voice's characters left this month, when the account is what speaks."""
        settings = self.form_settings()
        if speech_voices.resolve(settings, voice_module.is_ready(), settings.language_code) != speech_voices.ACCOUNT:
            return

        def run() -> None:
            from . import accounts

            standing = accounts.billing(settings)
            used, limit = (standing or {}).get("ttsCharsMonth"), (standing or {}).get("ttsCharsLimit")
            if isinstance(used, (int, float)) and isinstance(limit, (int, float)) and limit > 0:
                line = f"The free voice has {max(0, int(limit - used)):,} of {int(limit):,} characters left this month."
                GLib.idle_add(lambda: (self.voice_test_result.set_text(line), False)[1])

        threading.Thread(target=run, daemon=True).start()

    def _answer_source_row(self, layout: Gtk.Box) -> Gtk.ComboBoxText:
        """Where answers come from, a Test button, and under them what the test found."""
        combo = Gtk.ComboBoxText()
        for value, label in ANSWER_SOURCE_CHOICES:
            combo.append(value, label)
        self.test_answers_button = Gtk.Button(label="Test")
        self.test_answers_button.connect("clicked", lambda _button: self.test_answers())
        picker = Gtk.Box(spacing=8)
        picker.pack_start(combo, True, True, 0)
        picker.pack_start(self.test_answers_button, False, False, 0)
        self._row(layout, "Answers come from", picker)
        self.answers_test_result = Gtk.Label(xalign=0)
        self.answers_test_result.set_line_wrap(True)
        self.answers_test_result.set_margin_start(162)
        layout.pack_start(self.answers_test_result, False, False, 0)
        return combo

    def form_settings(self) -> LinuxSettings:
        """The settings as the form has them now, saved or not (a key set in the environment wins, as it will)."""
        current = load_settings()
        values = self.values()
        overridden = set(environment_overrides())
        changes = {}
        for field_name, value in values.items():
            if field_name in overridden or not hasattr(current, field_name) or field_name in ("microphone", "kokoro_speaker", "quiet_minutes"):
                continue
            if isinstance(value, str):
                value = value.strip() or None
            if field_name == "answer_source" and value == AUTOMATIC_SOURCE:
                value = None
            changes[field_name] = value
        return dataclasses.replace(current, **changes)

    def test_answers(self) -> None:
        """Asks the source answers would come from now about a red square, off the main thread."""
        settings = self.form_settings()
        source = answers.answering_source(settings)
        if source is None:
            self.answers_test_result.set_text("✕ Nothing can answer yet: sign in, or add a key.")
            return
        self.test_answers_button.set_sensitive(False)
        self.answers_test_result.set_text(f"Asking {source.short_name}…")

        def run() -> None:
            verdict = answers.check(source, settings)
            GLib.idle_add(lambda: (self._show_answers_test(source, verdict), False)[1])

        threading.Thread(target=run, daemon=True).start()

    def _show_answers_test(self, source: "answers.Source", verdict: str) -> None:
        self.test_answers_button.set_sensitive(True)
        self.answers_test_result.set_text(f"{source.short_name}: {verdict}")

    def _fill_languages(self, chosen: str | None = None) -> None:
        """The languages the region offers, keeping the one picked (an option in use always stays)."""
        combo = self.fields["language_code"]
        chosen = combo.get_active_id() if chosen is None else chosen
        region = self.fields["region"].get_active_id() or languages.detected_region()
        combo.remove_all()
        for code, label in languages.language_choices(region, chosen or None):
            combo.append(code, label)
        combo.set_active_id(chosen or languages.AUTOMATIC)
        self._show_script_row()

    def _show_script_row(self) -> None:
        if "script" not in self.fields:
            return
        region = self.fields["region"].get_active_id() or languages.detected_region()
        self.script_row.set_visible(languages.offers_script(region, self.fields["language_code"].get_active_id(),
                                                            self.fields["script"].get_active_id() or languages.NATIVE))
        self.script_row.set_no_show_all(True)

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
        combo = _ellipsized(Gtk.ComboBoxText())
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
        self.answers_test_result.set_text("")
        self.voice_test_result.set_text("")
        self._unset_choices = set()
        for field_name, widget in self.fields.items():
            value = getattr(settings, field_name)
            if field_name == "microphone":
                self._fill_microphones(value)
            elif isinstance(widget, Gtk.Scale):
                widget.set_value(float(value))
            elif field_name == "language_code":
                if value is None:
                    self._unset_choices.add(field_name)
                self._fill_languages(value or languages.AUTOMATIC)
            elif isinstance(widget, Gtk.CheckButton):
                widget.set_active(bool(value))
            elif field_name == "kokoro_speaker":
                self._loaded_speaker = value
                chosen = voice_module.speaker_for(settings.language_code, value)
                widget.set_active_id(str(chosen.id if chosen else voice_module.SPEAKERS[0].id))
            elif isinstance(widget, Gtk.ComboBoxText):
                value = str(value) if isinstance(value, int) else value
                if value is None and field_name in CHOICE_DEFAULTS:
                    self._unset_choices.add(field_name)
                value = value or CHOICE_DEFAULTS.get(field_name)
                if not widget.set_active_id(value):
                    # A key picked by hand in shell.json that is not in the list is shown, not lost.
                    widget.append(value, value)
                    widget.set_active_id(value)
            elif field_name == "backend_url":
                widget.set_text("" if value == HOSTED_BACKEND_URL else value)
            else:
                widget.set_text(value or "")
        self.start_at_login.set_active(autostart.is_enabled())
        self.read_text_boxes.set_active(accessibility.is_enabled())
        on = island.enabled_plugins()
        for plugin, switch in self.plugin_switches.items():
            switch.set_active(plugin in on)
        self.default_component.set_active_id(preferences.get(island.DEFAULT_COMPONENT_KEY) or island.SHORTCUTS)
        self.account_form.refresh()
        self.account_status.set_text("")
        self.agent_mode.set_active(lanes.agent_mode_is_on())
        self._show_agent_state()
        self.offline_row.show_state()
        self.voice_row.show_state()
        self._show_voice_allowance()
        self._show_shortcuts()
        self.status_label.set_text(f"Set in the environment, which wins while it is set: {', '.join(overridden)}." if overridden else "")
        self._update_routes()

    def values(self) -> dict[str, str | None]:
        values = {}
        for field_name, widget in self.fields.items():
            if isinstance(widget, Gtk.CheckButton):
                values[field_name] = widget.get_active()
            elif isinstance(widget, Gtk.Scale):
                values[field_name] = widget.get_value()
            elif isinstance(widget, Gtk.ComboBoxText):
                values[field_name] = widget.get_active_id()
            else:
                values[field_name] = widget.get_text()
        # A menu left at what an unset key means writes nothing (the Mac's default may change).
        for field_name in self._unset_choices:
            if values.get(field_name) == CHOICE_DEFAULTS[field_name]:
                values[field_name] = None
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
        elif engine == "sarvam":
            dictation = "Sarvam, with your key" if has("sarvam_api_key") else "nowhere yet: add a Sarvam key"
        elif engine == "assemblyai":
            dictation = "AssemblyAI, through your OpenClicky account" if has("account_token") else "nowhere yet: sign in to your account"
        elif engine == "account" or (engine == "auto" and has("account_token")):
            dictation = "your OpenClicky account" if has("account_token") else "nowhere yet: sign in to your account"
        else:
            dictation = "nowhere yet: download the offline engine, add an OpenAI key, or sign in"
        source = answers.answering_source(self.form_settings())
        answered_by = ("nothing yet: sign in, or add a key" if source is None else "your OpenClicky account" if source.id == "openClickyAccount"
                       else f"your {source.short_name} plan" if source.id in ("claudeCodePlan", "codexPlan")
                       else f"your {KEY_VENDORS.get(source.id, source.short_name)} key")
        speaker = speech_voices.resolve(self.form_settings(), voice_module.is_ready(), values.get("language_code"))
        if not values.get("speak_answers"):
            spoken = "Answers are shown, not spoken."
        elif speaker == speech_voices.KOKORO:
            spoken = "Answers are spoken on this computer, in Kokoro's voice."
        elif speaker == speech_voices.SYSTEM:
            spoken = "Answers are spoken in this computer's voice; download Kokoro for a better one."
        elif speaker is not None:
            spoken = f"Answers are spoken by {speech_voices.TITLES[speaker]}."
        else:
            spoken = "Answers are shown; download the offline voice to hear them."
        self.routes_label.set_text(f"Dictation goes to {dictation}.\nAnswers come from {answered_by}.\n{spoken}")

    def save(self) -> None:
        try:
            save_settings(self.values())
            preferences.set_values(**{lanes.AGENT_MODE: None if self.agent_mode.get_active() else False})
        except (OSError, ValueError, RuntimeError) as failure:  # RuntimeError: a symlink loop
            self.status_label.set_text(f"Not saved: {failure}")
            return
        for plugin, switch in self.plugin_switches.items():
            island.set_plugin_enabled(plugin, switch.get_active())
        chosen_component = self.default_component.get_active_id()
        preferences.set_values(**{island.DEFAULT_COMPONENT_KEY: None if chosen_component == island.SHORTCUTS else chosen_component})
        if self.read_text_boxes.get_active() != accessibility.is_enabled():
            accessibility.set_enabled(self.read_text_boxes.get_active())
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

    def show(self, page_id: str | None = None) -> None:
        self.load()
        opened_afresh = not self.window.get_visible()
        present_with_focus(self.window)
        if page_id or opened_afresh:
            # Opened afresh: General, as on the Mac (the page stack shows whichever page became
            # visible first, a page added later included). Only a shown page can be picked, so
            # the window is shown first.
            self.show_page(page_id or "general")


def _ellipsized(combo: Gtk.ComboBoxText) -> Gtk.ComboBoxText:
    """A long choice (a microphone's full name) is cut short with "…" instead of widening the window."""
    for cell in combo.get_cells():
        cell.set_property("ellipsize", Pango.EllipsizeMode.END)
        cell.set_property("width-chars", 30)
    return combo


def where_things_are() -> list[tuple[str, "Path"]]:
    """The folders OpenClicky keeps things in, for the Privacy page."""
    from .config import openclicky_directory
    from .history import TakeAudioStore, default_path
    from .offline import models_directory

    return [("Keys and settings", openclicky_directory()), ("Dictionary, shortcuts, styles", openclicky_directory() / "dictation"),
            ("History", default_path().parent), ("Recordings kept to retry", TakeAudioStore().directory),
            ("Downloaded models", models_directory()), ("The log", applog.log_path().parent)]


def _open_folder(folder) -> None:
    """In the file manager, made first if it is not there yet."""
    try:
        Path(folder).mkdir(parents=True, exist_ok=True)
        Gio.AppInfo.launch_default_for_uri(Path(folder).as_uri(), None)
    except (OSError, GLib.Error) as failure:
        applog.append(f"settings: the folder could not be opened ({type(failure).__name__})")


class KeyboardSketch:
    """The bottom rows of a keyboard with OpenClicky's keys lit (the Mac's `KeyboardSketch`): the
    dictation key, left alt (ask out loud with it), shift (Hey Clicky with it), left ctrl (tapped
    twice, the ask box)."""

    # Each row: (label, width in key units, the X keysym names it stands for).
    ROWS = (
        (("Shift", 2.3, ("Shift_L",)), ("Z", 1, ()), ("X", 1, ()), ("C", 1, ()), ("V", 1, ()), ("B", 1, ()), ("N", 1, ()), ("M", 1, ()),
         (",", 1, ()), (".", 1, ()), ("/", 1, ()), ("Shift", 2.7, ("Shift_R",))),
        (("Ctrl", 1.5, ("Control_L",)), ("Super", 1.25, ("Super_L",)), ("Alt", 1.25, ("Alt_L",)), ("", 6.0, ()),
         ("Alt", 1.25, ("Alt_R",)), ("Super", 1.25, ("Super_R",)), ("Menu", 1.25, ("Menu",)), ("Ctrl", 1.25, ("Control_R",))),
    )
    KEY_HEIGHT = 34
    UNIT = 38

    def __init__(self) -> None:
        self.area = Gtk.DrawingArea()
        self.area.set_size_request(int(self.UNIT * 14.2), self.KEY_HEIGHT * 2 + 34)
        self.area.connect("draw", self._draw)
        self.lit: dict[str, str] = {}

    def lit_keys(self, dictation_key: str) -> dict[str, str]:
        """keysym → what it does, for the legend."""
        lit = {"Alt_L": "ask", "Shift_L": "edit", "Shift_R": "edit", "Control_L": "ask box"}
        lit[dictation_key] = "dictate"
        return lit

    def show_keys(self, dictation_key: str) -> None:
        self.lit = self.lit_keys(dictation_key)
        self.area.queue_draw()

    def _draw(self, area, context) -> bool:
        colour = area.get_style_context().get_color(Gtk.StateFlags.NORMAL)
        for row_index, row in enumerate(self.ROWS):
            x = 2.0
            y = 2 + row_index * (self.KEY_HEIGHT + 4)
            for label, width, keysyms in row:
                pixels = width * self.UNIT - 4
                role = next((self.lit[keysym] for keysym in keysyms if keysym in self.lit), None)
                if role:
                    context.set_source_rgba(0.93, 0.36, 0.2, 0.85 if role.startswith("dictate") else 0.4)
                    context.rectangle(x, y, pixels, self.KEY_HEIGHT)
                    context.fill()
                context.set_source_rgba(colour.red, colour.green, colour.blue, 0.45)
                context.rectangle(x + 0.5, y + 0.5, pixels, self.KEY_HEIGHT)
                context.set_line_width(1)
                context.stroke()
                context.set_source_rgba(colour.red, colour.green, colour.blue, 0.9)
                context.move_to(x + 6, y + 14)
                context.show_text(label)
                if role:
                    context.move_to(x + 6, y + self.KEY_HEIGHT - 6)
                    context.show_text(role)
                x += width * self.UNIT
        return False


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
            # Removed on purpose: it never downloads on its own again.
            auto_download.set_declined(self.asset, True)
            self.show_state()
            self.window._update_routes()
            return
        self.start_download()

    def start_download(self, asset: "offline.ModelAsset | None" = None) -> None:
        """Downloads in the background; progress and the outcome come back to the main loop."""
        auto_download.set_declined(asset or self.asset, False)
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
    """The Mac's menu bar menu: Agent mode, Open OpenClicky, Ask…, Paste last take, Your words…,
    Settings…, Replay welcome…, Quit."""

    def __init__(self, on_ask: Callable[[], None], on_settings: Callable[[], None], on_quit: Callable[[], None],
                 on_words: Callable[[], None] | None = None, on_paste_last: Callable[[], None] | None = None,
                 on_open: Callable[[], None] | None = None, on_replay_welcome: Callable[[], None] | None = None,
                 on_open_island: Callable[[], None] | None = None, docked: Callable[[], bool] | None = None,
                 set_docked: Callable[[bool], None] | None = None) -> None:
        self.menu = Gtk.Menu()
        self.agent_mode = Gtk.CheckMenuItem(label="Agent mode")
        self.agent_mode.set_active(lanes.agent_mode_is_on())
        self.agent_mode.connect("toggled", lambda item: preferences.set_values(**{lanes.AGENT_MODE: None if item.get_active() else False}))
        self.menu.append(self.agent_mode)
        self.dock = Gtk.CheckMenuItem(label="Dock the pointer")
        if docked is not None and set_docked is not None:
            self.dock.set_active(docked())
            self.dock.connect("toggled", lambda item: set_docked(item.get_active()) if item.get_active() != docked() else None)
            self.menu.append(self.dock)
        self.menu.append(Gtk.SeparatorMenuItem())
        items = ([("Open OpenClicky", on_open)] if on_open else []) + ([("Open the island", on_open_island)] if on_open_island else [])
        items += [("Ask…", on_ask)]
        items += ([("Paste last take", on_paste_last)] if on_paste_last else []) + ([("Your words…", on_words)] if on_words else [])
        items += [("Settings…", on_settings)] + ([("Replay welcome…", on_replay_welcome)] if on_replay_welcome else [])
        items += [(None, None), ("Quit", on_quit)]
        for label, action in items:
            item = Gtk.SeparatorMenuItem() if label is None else Gtk.MenuItem(label=label)
            if action is not None:
                item.connect("activate", lambda _item, action=action: action())
            self.menu.append(item)
        # Agent mode can change in Settings too: read again each time the menu opens.
        self.menu.connect("show", lambda _menu: (self.agent_mode.set_active(lanes.agent_mode_is_on()),
                                                 self.dock.set_active(docked()) if docked is not None else None))
        self.menu.show_all()
        # Gtk.StatusIcon is the X11 system tray protocol: deprecated in GTK, still what Xfce, KDE,
        # MATE and Cinnamon trays show. Where there is no tray, the shortcuts still work.
        self.icon = Gtk.StatusIcon.new_from_pixbuf(pointer_icon())
        self.icon.set_tooltip_text("OpenClicky")
        self.icon.connect("activate", lambda _icon: on_ask())
        self.icon.connect("popup-menu", lambda icon, button, time: self.menu.popup(None, None, Gtk.StatusIcon.position_menu, icon, button, time))
