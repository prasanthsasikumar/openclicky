"""`python -m openclicky_linux`: dictation, the pointer and asking by text.

- hold right ctrl, speak, let go: the words are typed where your cursor is
- tap left ctrl twice: ask about your screen; the pointer answers and points
- the tray icon: Ask…, Settings…, Quit

`--check` prints what would be used and exits; `settings` opens only the Settings window.
"""

from __future__ import annotations

import argparse
import signal
import sys
import threading
from dataclasses import dataclass
from typing import Any, Callable

from . import applog, offline
from . import voice as voice_module
from .ask import AskError, Conversation, Screenshot, ask as ask_model, request_for
from .config import LinuxSettings, load_settings
from .controller import DictationHost, DictationTakeController, OrbModel
from .gestures import HOLD_DELAY_SECONDS, DictationGestureRecognizer, EscapePressed, HoldStarted, TextComposerRequested

# Tapped twice, the ask box; held on its own, dictation (when it is the dictation key, the default).
COMPOSER_KEY = "Control_L"
from .recorder import MicrophoneRecording, RecorderUnavailable, find_recorder_command
from .space import load_space
from .transcriber import TranscriptionError, route_for, transcribe
from .x11 import is_wayland_session


def _describe_setup(dictation_key: str) -> int:
    settings = load_settings()
    problems = 0
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
          f"{'' if voice_module.find_player() else ' (no audio player: install pipewire or pulseaudio-utils)'}")
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


def wire_up(dictation_key: str, start_recording: Callable[[], Any], transcribe: Callable[[bytes], str],
            prepare_transcription: Callable[[], None] = lambda: None,
            voice: Any = None,
            ask: Callable[[str, Screenshot, Conversation], str] | None = None,
            display_name: str | None = None, with_tray: bool = False,
            on_quit: Callable[[], None] = lambda: None) -> OpenClickyApp:
    """The app's parts joined together on the GTK main loop (the key listener is not started yet).
    The X11 tests run exactly this, with a stand-in microphone, engine and model."""
    # The GTK modules first: they pin GTK 3 before anything imports it.
    from .companion import AskController, AskHost
    from .desktop import ClipboardPaster, OrbWindow
    from .pointer_view import PointerView
    from .screen import capture_screens
    from .windows import ComposerWindow, SettingsWindow, TrayIcon, pointer_icon
    from .x11 import DictationKeyListener, focused_window

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
    paster = ClipboardPaster(xlib_display, before_keystroke=lambda: listener_holder["listener"].ignore_keys_for(0.4),
                             on_paste_problem=lambda message: dictation_holder["dictation"].report_late_paste_problem(message))

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

    dictation = DictationTakeController(DictationHost(
        start_recording=start_recording,
        transcribe=transcribe,
        focused_window=lambda: focused_window(xlib_display),
        paste=paster.paste,
        copy_to_clipboard=paster.copy_to_clipboard,
        load_space=load_space,
        run_in_background=run_in_background,
        schedule=schedule,
        prepare_transcription=prepare_transcription,
        # A spoken question goes where a typed one does: the pointer answers it, and guides.
        ask_question=lambda question: asking_holder["asking"].submit(question),
    ), orb)
    dictation_holder["dictation"] = dictation

    asking_holder: dict[str, AskController] = {}
    pointer = PointerView(settings.pointer_presence, on_caption_finished=lambda: asking_holder["asking"].caption_finished())
    composer = ComposerWindow(on_submit=lambda question: asking_holder["asking"].submit(question))
    asking = AskController(AskHost(
        show_composer=composer.show,
        hide_composer=composer.hide,
        capture_screen=capture_screens,
        # Settings are read for every question: a key saved in Settings works at once.
        ask=ask or (lambda question, screenshot, conversation: ask_model(question, screenshot, conversation, load_settings())),
        run_in_background=run_in_background,
        schedule=schedule,
        # Spoken in Kokoro's voice when it is downloaded and "Speak answers" is on (read per answer).
        prepare_voice=lambda: voice.prepare() if voice is not None and load_settings().speak_answers else None,
        speak=lambda text: voice.speak(text, load_settings().language_code, load_settings().kokoro_speaker)
        if voice is not None and load_settings().speak_answers else None,
        stop_speaking=lambda: voice.stop() if voice is not None else None,
    ), pointer)
    asking_holder["asking"] = asking

    def settings_saved(new_settings: LinuxSettings) -> None:
        pointer.set_presence(new_settings.pointer_presence)

    # Every OpenClicky window (Settings, the ask box) carries the pointer as its icon.
    Gtk.Window.set_default_icon(pointer_icon(64))
    settings_window = SettingsWindow(on_saved=settings_saved)
    tray = TrayIcon(on_ask=asking.open_composer, on_settings=settings_window.show, on_quit=on_quit) if with_tray else None

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

    listener = DictationKeyListener(
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
    )
    listener_holder["listener"] = listener
    return OpenClickyApp(dictation=dictation, asking=asking, listener=listener, pointer=pointer,
                         composer=composer, settings_window=settings_window, tray=tray)


def _refuse_wayland() -> bool:
    if is_wayland_session():
        print("openclicky for linux needs an X11 session for now: wayland does not let an app watch a key, "
              "paste into another app or float a window over it. pick the X11 (Xorg) session at login.", file=sys.stderr)
        return True
    return False


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


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="openclicky-linux", description="Dictation, the pointer and asking by text, for Linux (X11).")
    parser.add_argument("command", nargs="?", choices=["settings"], help="settings: open only the Settings window")
    parser.add_argument("--key", help="X keysym of the dictation key (default: linuxDictationKey in shell.json, else Control_R)")
    parser.add_argument("--check", action="store_true", help="print the setup and exit")
    arguments = parser.parse_args(argv)

    settings = load_settings()
    dictation_key = arguments.key or settings.dictation_key
    if arguments.check:
        return _describe_setup(dictation_key)
    if _refuse_wayland():
        return 2
    if arguments.command == "settings":
        return _open_settings_only()

    import gi

    gi.require_version("Gtk", "3.0")
    from gi.repository import GLib, Gtk

    log_file = applog.start()
    # One helper process for the offline engine, shared by every take (it unloads itself when idle),
    # and one for the voice.
    offline_engine = offline.OfflineEngine()
    answer_voice = voice_module.Voice()
    try:
        app = wire_up(dictation_key,
                  # The microphone is read for every take: one picked in Settings is used at once.
                  start_recording=lambda: MicrophoneRecording(microphone=load_settings().microphone),
                      transcribe=lambda wav_bytes: transcribe(wav_bytes, load_settings(), offline_engine),
                  prepare_transcription=lambda: offline_engine.prepare() if load_settings().dictation_engine in ("auto", "offline") else None,
                  voice=answer_voice,
                      with_tray=True, on_quit=Gtk.main_quit)
        app.listener.start()
    except (ValueError, RuntimeError) as problem:
        # A dictation key this keyboard does not have, or an X server without RECORD: one sentence,
        # not a traceback.
        applog.append(f"could not start: {problem}")
        print(f"openclicky could not start: {problem}", file=sys.stderr)
        return 1
    GLib.unix_signal_add(GLib.PRIORITY_DEFAULT, signal.SIGINT, lambda: (Gtk.main_quit(), False)[1])
    GLib.unix_signal_add(GLib.PRIORITY_DEFAULT, signal.SIGTERM, lambda: (Gtk.main_quit(), False)[1])
    print(f"openclicky: hold {dictation_key} and speak, let go to type it (esc cancels). hold it with left alt to ask "
          f"the pointer out loud; tap left ctrl twice to ask by typing. settings are in the tray icon. ctrl+c quits. log: {log_file or 'none (its folder could not be written)'}")
    try:
        Gtk.main()
    finally:
        app.listener.stop()
        offline_engine.unload()
        answer_voice.stop()
        answer_voice.unload()
    return 0


if __name__ == "__main__":
    sys.exit(main())
