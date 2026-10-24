"""`python -m openclicky_linux`: dictation, the pointer and asking by text, on X11 and Wayland.

- hold left ctrl, speak, let go: the words are typed where your cursor is
- tap left ctrl twice: ask about your screen; the pointer answers and points
- the tray icon: Ask…, Settings…, Quit

`--check` prints what would be used and exits; `settings` opens only the Settings window; `setup`
opens the first-run setup (on Wayland, the one-time permission first; `setup --remove` takes it away).
"""

from __future__ import annotations

import argparse
import signal
import sqlite3
import sys
import threading
from dataclasses import dataclass
from typing import Any, Callable

from . import applog, offline
from . import voice as voice_module
from .ask import AskError, Conversation, Screenshot, request_for
from .config import LinuxSettings, load_settings
from .controller import DictationHost, DictationTakeController, OrbModel
from .formatter import format_locally, is_shortcut
from .gestures import HOLD_DELAY_SECONDS, DictationGestureRecognizer, EscapePressed, HoldStarted, TextComposerRequested
from .history import TakeRecord
from .lanes import Lanes

# Tapped twice, the ask box; held on its own, dictation (when it is the dictation key, the default).
COMPOSER_KEY = "Control_L"
from .polish import polish as polish_take
from .recorder import MicrophoneRecording, RecorderUnavailable, find_recorder_command
from .session import UNTOLD_WINDOW, has_xwayland, is_wayland_session, name_the_app, use_x11_windows
from .space import load_space
from .transcriber import TranscriptionError, engine_for, route_for, transcribe


def _describe_setup(dictation_key: str) -> int:
    settings = load_settings()
    problems = 0
    if is_wayland_session():
        from . import inputaccess

        print(f"session: wayland{'' if has_xwayland() else ' without xwayland (openclicky needs it for its windows)'}")
        print("keyboard and typing: " + ("allowed" if inputaccess.is_granted()
                                         else "not allowed yet: run `openclicky-linux setup` once (it asks for your password)"))
        problems += 0 if inputaccess.is_granted() and has_xwayland() else 1
    else:
        print("session: x11")
    print(f"dictation key: {dictation_key} (ask by voice: hold it with Alt_L; ask by typing: tap Control_L twice)")
    try:
        print(f"recorder: {' '.join(find_recorder_command())}")
    except RecorderUnavailable as missing:
        print(f"recorder: missing ({missing})")
        problems += 1
    print(f"offline engine: {'downloaded' if offline.is_installed() else 'not downloaded'}"
          f"{'' if offline.is_engine_available() else ' (sherpa-onnx is not installed)'}")
    try:
        route = route_for(b"", settings, offline_installed=offline.is_ready())
        print("transcription: " + {"offline": "offline, on this computer", "openai": "your openai key",
                                   "backend": "openclicky account at " + settings.backend_url}[route.kind])
    except TranscriptionError as missing:
        print(f"transcription: missing ({missing})")
        problems += 1
    try:
        asking = request_for("", Screenshot(b"", 0, 0, 1.0), Conversation(), settings)
        print(f"answers: {'openclicky account at ' + settings.backend_url if asking.kind == 'backend' else 'your ' + asking.kind + ' key'}")
    except AskError as missing:
        print(f"answers: missing ({missing})")
        problems += 1
    print(f"spoken answers: {'on' if settings.speak_answers else 'off'}, voice "
          f"{'downloaded' if offline.is_installed(voice_module.KOKORO) else 'not downloaded'}"
          f"{'' if voice_module.find_player() else ' (no audio player: install pipewire or pulseaudio-utils)'}"
          f"{' (your speakers are muted)' if voice_module.output_is_muted() else ''}")
    print(f"language: {settings.language_code or 'not set (the engine detects it)'}")
    print(f"pointer: {settings.pointer_presence}")
    space = load_space()
    print(f"dictionary terms: {len(space.dictionary)}, spoken shortcuts: {len(space.shortcuts)}")
    return 1 if problems else 0


@dataclass
class OpenClickyApp:
    dictation: Any  # DictationTakeController
    asking: Any  # AskController
    listener: Any  # DictationKeyListener
    pointer: Any  # PointerView
    composer: Any  # ComposerWindow
    settings_window: Any  # SettingsWindow
    tray: Any = None  # TrayIcon, when there is one
    take_store: Any = None  # history.TakeStore, when the history can be kept


def wire_up(dictation_key: str, start_recording: Callable[[], Any], transcribe: Callable[[bytes], str],
            prepare_transcription: Callable[[], None] = lambda: None,
            voice: Any = None,
            ask: Callable[[str, Screenshot, Conversation], str] | None = None,
            display_name: str | None = None, with_tray: bool = False,
            on_quit: Callable[[], None] = lambda: None,
            wayland: bool = False, virtual_keyboard: Any = None,
            input_device_paths: Callable[[], list[str]] | None = None) -> OpenClickyApp:
    """The app's parts joined together on the GTK main loop (the key listener is not started yet).
    The X11 tests run exactly this, with a stand-in microphone, engine and model. `wayland` swaps in
    the input devices, the virtual keyboard (`virtual_keyboard`), the desktop's screenshot and the
    pointer that rests at home (see `session.py`)."""
    # The GTK modules first: they pin GTK 3 before anything imports it.
    from .companion import AskController, AskHost
    from .desktop import ClipboardPaster, OrbWindow
    from .pointer_view import PointerView
    from .screen import capture_screens, capture_screens_wayland
    from .windows import ComposerWindow, SettingsWindow, TrayIcon, pointer_icon
    from .x11 import DictationKeyListener, focused_window

    if wayland:
        from .evdev import InputDeviceListener

    from gi.repository import GLib, Gtk
    from Xlib import display as xdisplay

    settings = load_settings()
    xlib_display = xdisplay.Display(display_name)
    orb = OrbModel()
    # Kept alive by the orb, which holds its update method.
    OrbWindow(orb)
    # Left ctrl is both the dictation key and the ask box's double tap: it waits before it listens.
    recognizer = DictationGestureRecognizer(shares_key_with_the_ask_box=dictation_key == COMPOSER_KEY)
    # The listener thread and the hold timer both feed the recognizer: one at a time.
    recognizer_lock = threading.Lock()
    listener_holder: dict[str, DictationKeyListener] = {}
    dictation_holder: dict = {}
    window_in_front = (lambda: UNTOLD_WINDOW) if wayland else (lambda: focused_window(xlib_display))
    paster = ClipboardPaster(xlib_display, before_keystroke=lambda: listener_holder["listener"].ignore_keys_for(0.4),
                             on_paste_problem=lambda message: dictation_holder["dictation"].report_late_paste_problem(message),
                             keyboard=virtual_keyboard if wayland else None, focused_window_now=window_in_front)

    def run_in_background(work: Callable[[], Any], done: Callable[[Any], None]) -> None:
        def run() -> None:
            try:
                outcome = work()
            except Exception as failure:  # noqa: BLE001 - handed to the controller, which shows it
                outcome = failure
            GLib.idle_add(lambda: (done(outcome), False)[1])
        threading.Thread(target=run, daemon=True).start()

    def schedule(seconds: float, action: Callable[[], None]) -> None:
        GLib.timeout_add(int(seconds * 1000), lambda: (action(), False)[1])

    take_store = _open_take_store()

    def format_take(raw: str, window) -> str:
        """The app's style, spoken numbers, the dictionary and, when allowed, the model clean-up."""
        current = load_settings()
        space = load_space()
        style = space.style_for(window.app_class if window else None, bool(window and window.is_terminal))
        local = format_locally(raw, space, style, current.language_code)
        if is_shortcut(raw, space):
            return local
        text, _degraded = polish_take(raw, local, style, space, current, _engine_now(current),
                                      app_name=window.app_class if window else None)
        return text

    def record_take(take: dict) -> None:
        current = load_settings()
        if take_store is None or current.incognito:
            return
        window = take["window"]
        take_store.insert(TakeRecord(
            raw_text=take["raw"], formatted_text=take["text"], app_bundle_id=window.app_class if window else None,
            app_name=window.app_class if window else None, language=current.language_code, engine=_engine_now(current),
            duration_seconds=take["seconds"], paste_outcome="posted" if take["outcome"] == "moved to text box" else "leftOnPasteboard"))

    dictation = DictationTakeController(DictationHost(
        start_recording=start_recording,
        transcribe=transcribe,
        focused_window=window_in_front,
        paste=paster.paste,
        copy_to_clipboard=paster.copy_to_clipboard,
        load_space=load_space,
        run_in_background=run_in_background,
        schedule=schedule,
        prepare_transcription=prepare_transcription,
        # A spoken question goes where a typed one does: the pointer answers it, and guides.
        ask_question=lambda question: asking_holder["asking"].submit(question),
        format_take=format_take,
        record_take=record_take,
    ), orb)
    dictation_holder["dictation"] = dictation

    asking_holder: dict[str, AskController] = {}
    muted_said: list[bool] = [False]

    def speak_answer(text: str) -> None:
        current = load_settings()
        if voice is None or not current.speak_answers:
            return
        # Muted speakers are the person's choice: said once, so a silent answer is not a mystery.
        if not muted_said[0] and voice_module.output_is_muted():
            muted_said[0] = True
            dictation.show_notice("your sound is muted, so answers are shown, not spoken")
        voice.speak(text, current.language_code, current.kokoro_speaker)

    pointer = PointerView(settings.pointer_presence, on_caption_finished=lambda: asking_holder["asking"].caption_finished(),
                          knows_the_mouse=not wayland)
    # The agent lane and the quick actions (see lanes.py): what answers a question by default.
    lanes = Lanes()

    def answer_question(question: str, screenshot, conversation: Conversation) -> str:
        # Read on the worker thread as the question goes out: its progress belongs to this question only.
        question_id = asking_holder["asking"].question_id
        return on_the_account(lambda: lanes.answer(question, screenshot, conversation, progress=lambda text: GLib.idle_add(
            lambda: (asking_holder["asking"].show_progress(text, question_id), False)[1])))
    composer = ComposerWindow(on_submit=lambda question: asking_holder["asking"].submit(question))
    asking = AskController(AskHost(
        show_composer=composer.show,
        hide_composer=composer.hide,
        capture_screen=capture_screens_wayland if wayland else capture_screens,
        # Settings are read for every question: a key saved in Settings works at once.
        ask=ask or answer_question,
        run_in_background=run_in_background,
        schedule=schedule,
        # Spoken in Kokoro's voice when it is downloaded and "Speak answers" is on (read per answer).
        prepare_voice=lambda: voice.prepare() if voice is not None and load_settings().speak_answers else None,
        speak=speak_answer,
        stop_speaking=lambda: voice.stop() if voice is not None else None,
        cancel_work=lanes.cancel,
    ), pointer)
    asking_holder["asking"] = asking

    def settings_saved(new_settings: LinuxSettings) -> None:
        pointer.set_presence(new_settings.pointer_presence)

    # Every OpenClicky window (Settings, the ask box) carries the pointer as its icon.
    Gtk.Window.set_default_icon(pointer_icon(64))
    settings_window = SettingsWindow(on_saved=settings_saved)
    words_holder: dict = {}

    def show_words() -> None:
        from .words_window import WordsWindow

        if "window" not in words_holder:
            words_holder["window"] = WordsWindow(take_store)
        words_holder["window"].show()

    tray = TrayIcon(on_ask=asking.open_composer, on_settings=settings_window.show, on_quit=on_quit, on_words=show_words) if with_tray else None

    def handle(event) -> None:
        if isinstance(event, TextComposerRequested):
            asking.open_composer()
            return
        if isinstance(event, EscapePressed) and not dictation.is_take_in_progress:
            asking.escape()
        dictation.handle(event)

    def deliver(events: list) -> None:
        for event in events:
            if isinstance(event, HoldStarted):
                # The take starts only if the key is still down, on its own, when the delay is up.
                timer = threading.Timer(HOLD_DELAY_SECONDS, lambda press_id=event.press_id: deliver(recognize(
                    lambda: recognizer.hold_elapsed(press_id))))
                timer.daemon = True
                timer.start()
                continue
            GLib.idle_add(lambda event=event: (handle(event), False)[1])

    def recognize(step: Callable[[], list]) -> list:
        with recognizer_lock:
            return step()

    listener_options: dict[str, Any] = {}
    if wayland:
        # Wayland says only that the mouse moved: an answer already read then gets out of the way.
        listener_options = {"on_pointer_moved": lambda: GLib.idle_add(lambda: (pointer.mouse_moved(), False)[1]),
                            "device_paths": input_device_paths}
    listener = (InputDeviceListener if wayland else DictationKeyListener)(
        dictation_key,
        on_dictation_key=lambda is_down, at_seconds: deliver(recognize(
            lambda: recognizer.dictation_key_down(at_seconds) if is_down else recognizer.dictation_key_up(at_seconds))),
        # On the key watcher's thread, so the ask box's state is read as the key goes down: an esc
        # that closes the box only closes it.
        on_other_key_down=lambda is_escape: deliver(recognize(
            lambda: recognizer.other_key_down(is_escape and not composer.is_open_for_key_watcher))),
        composer_key_name=COMPOSER_KEY,
        on_composer_key=lambda is_down, at_seconds: deliver(recognize(
            lambda: recognizer.composer_key_down(at_seconds) if is_down else recognizer.composer_key_up(at_seconds))),
        on_ask_key=lambda is_down: deliver(recognize(lambda: recognizer.ask_key_down() if is_down else recognizer.ask_key_up())),
        # Clicks and keys, for a guided step waiting to be done (ignored unless one waits).
        on_step_input=lambda kind: None if kind == "escape" and composer.is_open_for_key_watcher else GLib.idle_add(
            lambda: (asking.step_input(kind), False)[1]),
        display_name=display_name,
        **listener_options,
    )
    listener_holder["listener"] = listener
    return OpenClickyApp(dictation=dictation, asking=asking, listener=listener, pointer=pointer,
                         composer=composer, settings_window=settings_window, tray=tray, take_store=take_store)


def on_the_account(work: Callable[[], Any]) -> Any:
    """A request that may go to the OpenClicky account: the session renewed first if it is about to
    expire, and once more if the backend still refuses it (the Mac's `BackendSignIn`)."""
    from . import accounts

    accounts.refresh_if_needed()
    try:
        return work()
    except (AskError, TranscriptionError) as failure:
        if not failure.account_refused or accounts.refresh_if_needed(force=True) != "refreshed":
            raise
        return work()


def _open_take_store():
    """The history; None (and takes still work) when its folder cannot be written."""
    from .history import TakeStore

    try:
        return TakeStore()
    except (OSError, sqlite3.Error) as failure:
        applog.append(f"history: not kept ({type(failure).__name__}: {failure})")
        return None


def _engine_now(settings: LinuxSettings) -> str:
    try:
        return engine_for(settings, offline.is_ready())
    except TranscriptionError:
        return ""


def _open_settings_only() -> int:
    # `windows` first: it pins GTK 3.
    from .windows import SettingsWindow, pointer_icon

    from gi.repository import GLib, Gtk

    Gtk.Window.set_default_icon(pointer_icon(64))
    settings_window = SettingsWindow()
    settings_window.window.connect("hide", lambda _window: Gtk.main_quit())
    GLib.unix_signal_add(GLib.PRIORITY_DEFAULT, signal.SIGINT, lambda: (Gtk.main_quit(), False)[1])
    settings_window.show()
    Gtk.main()
    return 0


def _setup(remove: bool) -> int:
    """`openclicky-linux setup`: the first-run setup window (on Wayland, the permission first);
    `--remove`: take Wayland's keyboard permission away again."""
    from . import inputaccess

    if remove:
        try:
            inputaccess.revoke()
        except inputaccess.SetupFailed as failure:
            print(f"openclicky: {failure}", file=sys.stderr)
            return 1
        print("openclicky: the keyboard permission is removed.")
        return 0
    from .onboarding import run_setup_window

    return 0 if run_setup_window(wayland=is_wayland_session()) else 1


def _start_desktop_session() -> tuple[bool, Any] | None:
    """Wayland's needs, before any window: XWayland, the one-time permission (the setup window has
    asked for it by now), and the virtual keyboard. Returns (wayland, virtual keyboard), or None
    with a sentence printed when it cannot start."""
    wayland = is_wayland_session()
    if not wayland:
        return False, None
    if not has_xwayland():
        print("openclicky needs xwayland on wayland (its pointer and orb are x11 windows, the only kind that may place "
              "itself). it is on by default in gnome, kde and sway; check that DISPLAY is set.", file=sys.stderr)
        return None
    from . import inputaccess
    from .uinput import VirtualKeyboard

    if not inputaccess.is_granted():
        print("openclicky: without the keyboard permission it cannot hear the shortcuts on wayland. "
              "run `openclicky-linux setup` when you are ready.", file=sys.stderr)
        return None
    try:
        return True, VirtualKeyboard()
    except OSError as problem:
        print(f"openclicky could not make its virtual keyboard: {problem}. run `openclicky-linux setup` again.", file=sys.stderr)
        return None


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="openclicky-linux", description="Dictation, the pointer and asking about your screen, for Linux (X11 and Wayland).")
    parser.add_argument("command", nargs="?", choices=["settings", "setup"],
                        help="settings: open only the Settings window; setup: the first-run setup (and Wayland's permission)")
    parser.add_argument("--key", help="X keysym of the dictation key (default: linuxDictationKey in shell.json, else Control_L)")
    parser.add_argument("--check", action="store_true", help="print the setup and exit")
    parser.add_argument("--remove", action="store_true", help="with setup: remove the Wayland keyboard permission")
    arguments = parser.parse_args(argv)

    settings = load_settings()
    dictation_key = arguments.key or settings.dictation_key
    if arguments.check:
        return _describe_setup(dictation_key)
    # Opened first, so the setup's own lines (the permission, the launcher) are kept too.
    log_file = applog.start()
    # GTK draws through X11 everywhere (XWayland on Wayland): only X11 lets a window place itself.
    use_x11_windows()
    name_the_app()
    if arguments.command == "setup":
        return _setup(arguments.remove)
    if arguments.command == "settings":
        return _open_settings_only()

    from . import instance

    # Another OpenClicky already runs: it opens its Settings, and this one ends (two would paste twice).
    if instance.forward_to_running("settings"):
        print("openclicky is already running: its settings are open.")
        return 0
    # Claimed now, before the setup window, so a second start meanwhile is forwarded too.
    later_starts: dict[str, Callable[[str], None]] = {"handle": lambda _request: None}
    instance.listen(lambda request: later_starts["handle"](request))
    from . import inputaccess
    from .onboarding import is_first_run, run_setup_window

    # The first run, and on Wayland whenever the permission is missing (taken away, or a new
    # computer with the same settings): the setup, before anything watches the keys.
    wayland_needs_permission = is_wayland_session() and has_xwayland() and not inputaccess.is_granted()
    if is_first_run() or wayland_needs_permission:
        run_setup_window(wayland=is_wayland_session())
    started = _start_desktop_session()
    if started is None:
        return 2
    wayland, virtual_keyboard = started

    import gi

    gi.require_version("Gtk", "3.0")
    from gi.repository import GLib, Gtk

    applog.append(f"session: {'wayland' if wayland else 'x11'}")
    from . import autostart

    # A launcher from an earlier version is rewritten: its command and window class stay current.
    if autostart.launcher_path().exists():
        try:
            autostart.install_launcher()
        except OSError as problem:
            applog.append(f"launcher: not refreshed ({problem})")
    # One helper process for the offline engine, shared by every take (it unloads itself when idle),
    # and one for the voice.
    offline_engine = offline.OfflineEngine()
    answer_voice = voice_module.Voice()
    try:
        app = wire_up(dictation_key,
                      # The microphone is read for every take: one picked in Settings is used at once.
                      start_recording=lambda: MicrophoneRecording(microphone=load_settings().microphone),
                      transcribe=lambda wav_bytes: on_the_account(lambda: transcribe(wav_bytes, load_settings(), offline_engine)),
                      prepare_transcription=lambda: offline_engine.prepare() if load_settings().dictation_engine in ("auto", "offline") else None,
                      voice=answer_voice,
                      with_tray=True, on_quit=Gtk.main_quit,
                      wayland=wayland, virtual_keyboard=virtual_keyboard)
        app.listener.start()
    except (ValueError, RuntimeError) as problem:
        # A dictation key this keyboard does not have, an X server without RECORD, or input devices
        # that cannot be read: one sentence, not a traceback.
        applog.append(f"could not start: {problem}")
        print(f"openclicky could not start: {problem}", file=sys.stderr)
        return 1
    later_starts["handle"] = lambda request: GLib.idle_add(lambda: (app.settings_window.show(), False)[1])
    from . import accounts

    def keep_signed_in() -> bool:
        # Off the main loop: renewing asks the sign-in server.
        threading.Thread(target=accounts.refresh_if_needed, daemon=True).start()
        return True

    keep_signed_in()
    GLib.timeout_add_seconds(accounts.REFRESH_CHECK_SECONDS, keep_signed_in)
    GLib.unix_signal_add(GLib.PRIORITY_DEFAULT, signal.SIGINT, lambda: (Gtk.main_quit(), False)[1])
    GLib.unix_signal_add(GLib.PRIORITY_DEFAULT, signal.SIGTERM, lambda: (Gtk.main_quit(), False)[1])
    print(f"openclicky: hold {dictation_key} and speak, let go to type it (esc cancels). hold it with left alt to ask "
          f"the pointer out loud; tap left ctrl twice to ask by typing. settings are in the tray icon. ctrl+c quits. log: {log_file or 'none (its folder could not be written)'}")
    try:
        Gtk.main()
    finally:
        app.listener.stop()
        instance.stop_listening()
        offline_engine.unload()
        answer_voice.stop()
        answer_voice.unload()
        if virtual_keyboard is not None:
            virtual_keyboard.close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
