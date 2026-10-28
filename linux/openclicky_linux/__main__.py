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

from . import answers as answers_module
from . import applog, offline, preferences, speech_voices
from . import voice as voice_module
from .ask import AskError, Conversation, Screenshot, request_for
from .config import LinuxSettings, load_settings, save_settings
from .controller import DictationHost, DictationTakeController, FocusedWindow, OrbModel
from .formatter import format_locally, is_shortcut
from .gestures import (HOLD_DELAY_SECONDS, DictationGestureRecognizer, DictationPressed, DictationReleased, EscapePressed,
                       HandsFreeToggleRequested, HoldStarted, TextComposerRequested)
from .history import TakeAudioStore, TakeRecord
from .lanes import Lanes

# Tapped twice, the ask box; held on its own, dictation (when it is the dictation key, the default).
COMPOSER_KEY = "Control_L"
from .polish import edit as polish_edit, edit_refusal as polish_edit_refusal, polish as polish_take, polisher_for
from .polish import wants_model as polish_wants_model
from .recorder import MicrophoneRecording, RecorderUnavailable, find_recorder_command
from .session import UNTOLD_WINDOW, WINDOW_CLASS, has_xwayland, is_wayland_session, name_the_app, use_x11_windows
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
    island: Any = None  # island.Island
    welcome_tour: Any = None  # tour.WelcomeTour


class FastVoice:
    """The realtime client as the keys and the island use it: every network step off the main loop,
    one at a time, and its problems said once in the orb."""

    def __init__(self, client, is_wanted: Callable[[], bool], on_problem: Callable[[str], None]) -> None:
        self.client = client
        self.is_wanted = is_wanted
        self.on_problem = on_problem
        self.turn_open = False
        self._work = threading.Lock()

    def usable(self) -> bool:
        return self.is_wanted()

    def _in_background(self, step: Callable[[], None]) -> None:
        def run() -> None:
            with self._work:
                try:
                    step()
                except (RuntimeError, OSError) as failure:
                    applog.append(f"realtime: {failure}")
                    self.on_problem(str(failure))
        threading.Thread(target=run, daemon=True).start()

    def begin_turn(self) -> None:
        self.turn_open = True
        self._in_background(self.client.begin_push_to_talk)

    def end_turn(self) -> None:
        self.turn_open = False
        self._in_background(self.client.end_push_to_talk)

    def toggle_hands_free(self) -> None:
        turning_on = not self.client.hands_free
        applog.append(f"realtime: hands-free {'on' if turning_on else 'off'}")
        self._in_background(lambda: self.client.set_hands_free(turning_on))

    def interrupt(self) -> None:
        self.client.interrupt()


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
    from .companion import AskController, AskHost, caption_hold_seconds
    from .desktop import ClipboardPaster, OrbWindow
    from .pointer_view import PointerView
    from .screen import capture_screens, capture_screens_wayland, cut_close_up
    from .windows import KEY_NAMES, ComposerWindow, SettingsWindow, TrayIcon, pointer_icon
    from .x11 import DictationKeyListener, focused_window
    from .clipboard_history import ClipboardHistoryMonitor
    from .earcons import EarconPlayer
    from .x11 import front_app as x_front_app, mouse_position as x_mouse_position, prompt_windows as x_prompt_windows
    from . import accessibility, writing
    from .skills import FrontApp
    from .x11 import TERMINAL_WINDOW_CLASSES, LiveKeyboard, key_for as x_key_for, modifiers_held as x_modifiers_held
    from .skills import talk_skills_block

    if wayland:
        from .evdev import InputDeviceListener

    from gi.repository import Gdk, GLib, Gtk
    from Xlib import display as xdisplay

    settings = load_settings()
    xlib_display = xdisplay.Display(display_name)
    orb = OrbModel()
    # Its window follows Settings → The Orb (applied again whenever Settings is saved).
    orb_window = OrbWindow(orb, settings, resting_hint=f"hold {KEY_NAMES.get(dictation_key, dictation_key).lower()} to dictate")
    # Left ctrl is both the dictation key and the ask box's double tap: it waits before it listens.
    recognizer = DictationGestureRecognizer(shares_key_with_the_ask_box=dictation_key == COMPOSER_KEY)
    # The listener thread and the hold timer both feed the recognizer: one at a time.
    recognizer_lock = threading.Lock()
    listener_holder: dict[str, DictationKeyListener] = {}
    dictation_holder: dict = {}
    # The desktop's accessibility, when the person switched it on (Settings → Privacy): the focused
    # text box, and on Wayland the app in front's name.
    reader: dict[str, Any] = {"tracker": None}

    def start_reading_text_boxes() -> None:
        if reader["tracker"] is None and accessibility.is_enabled():
            try:
                reader["tracker"] = accessibility.FocusTracker()
            except Exception as failure:  # noqa: BLE001 - no AT-SPI: the features stay off
                applog.append(f"accessibility: not followed ({type(failure).__name__}: {failure})")

    def focused_field():
        tracker = reader["tracker"]
        return tracker.field() if tracker is not None else None

    start_reading_text_boxes()

    def wayland_window_in_front() -> FocusedWindow:
        # Wayland names no window; accessibility names the app, which is what styles and the
        # terminal check need (the window itself still cannot be told, so it stays one window).
        field = focused_field()
        if field is None or not field.app_name:
            return UNTOLD_WINDOW
        return FocusedWindow(UNTOLD_WINDOW.window_id, field.app_name, field.app_name.lower() in TERMINAL_WINDOW_CLASSES)

    window_in_front = wayland_window_in_front if wayland else (lambda: focused_window(xlib_display))
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

    def format_take(raw: str, window, nearby_text: str = "") -> str:
        """The app's style, spoken numbers, the dictionary and, when allowed, the model clean-up."""
        current = load_settings()
        space = load_space()
        style = space.style_for(window.app_class if window else None, bool(window and window.is_terminal))
        local = format_locally(raw, space, style, current.language_code)
        if is_shortcut(raw, space):
            return local
        text, _degraded = polish_take(raw, local, style, space, current, _engine_now(current),
                                      app_name=window.app_class if window else None, nearby_text=nearby_text)
        return text

    def record_take(take: dict) -> None:
        current = load_settings()
        if take_store is None or current.incognito:
            return
        window = take["window"]
        take_store.insert(TakeRecord(
            raw_text=take["raw"], formatted_text=take["text"], app_bundle_id=window.app_class if window else None,
            app_name=window.app_class if window else None, language=current.language_code, engine=_engine_now(current),
            duration_seconds=take["seconds"], paste_outcome="posted" if take["outcome"] == "moved to text box" else "leftOnPasteboard",
            mode=take.get("mode", "dictate")))

    take_audio = TakeAudioStore()

    def record_failed_take(take: dict) -> None:
        """In the history as failed, with its audio kept (when allowed) so it can be retried there."""
        current = load_settings()
        if take_store is None or current.incognito:
            return
        window = take["window"]
        record = TakeRecord(raw_text="", formatted_text="", mode=take["mode"], status="failed", failure_reason=take["reason"],
                            app_bundle_id=window.app_class if window else None, app_name=window.app_class if window else None,
                            language=current.language_code, engine=_engine_now(current), duration_seconds=take["seconds"])
        take_store.insert(record)
        if current.keep_failed_audio:
            take_audio.retain(record.id, take["wav"])

    def edit_text(selection: str, instruction: str, window) -> str:
        """Hey Clicky: the selection rewritten as the spoken instruction says, in the app's style."""
        current = load_settings()
        style = load_space().style_for(window.app_class if window else None, bool(window and window.is_terminal))
        return polish_edit(selection, instruction, style, current, _engine_now(current))

    def read_selection(done: Callable[[str | None], None]) -> None:
        # Asked of the selection's owner, answered on the main loop later: no nested loop mid-key.
        Gtk.Clipboard.get(Gdk.SELECTION_PRIMARY).request_text(lambda _clipboard, text: done(text))

    earcons = EarconPlayer()

    def live_typing_wanted(window):
        """The take's style and dictionary when it may type as it is spoken: no model will clean it
        up (the clean-up rewrites the whole take), and not a terminal or OpenClicky's own window."""
        if window is UNTOLD_WINDOW:
            # Wayland names no window; XWayland names an X11 app in front. Typed into a terminal a
            # word can be a command (less quits on "q"), so a window that cannot be told is never typed into.
            front = x_front_app(xlib_display, active_window_only=True)
            if front is None or not front.window_class:
                return None
            window = FocusedWindow(UNTOLD_WINDOW.window_id, front.window_class, front.window_class.lower() in TERMINAL_WINDOW_CLASSES)
        if window is None or window.is_terminal or (window.app_class or "").lower() == WINDOW_CLASS.lower():
            return None
        current = load_settings()
        space = load_space()
        style = space.style_for(window.app_class, window.is_terminal)
        if style.polish_with_model and polish_wants_model(current, _engine_now(current)) and polisher_for(current) is not None:
            return None
        return style, space

    live_keyboard = None if wayland else LiveKeyboard(display_name)

    def type_live(text: str) -> bool:
        if live_keyboard is not None:
            return live_keyboard.type_text(text)
        if virtual_keyboard is None:
            return False
        strokes = []
        for character in text:
            found = x_key_for(xlib_display, character)
            if found is None:
                # Not on this layout's keys: the rest of the take is pasted at the end instead.
                return False
            keycode, with_shift = found
            # X keycodes are the kernel's plus 8.
            strokes.append((keycode - 8, with_shift))
        try:
            virtual_keyboard.type_keys(strokes)
        except OSError:
            return False
        return True

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
        ask_question=lambda question: asking_holder["asking"].submit(question, asked_out_loud=True),
        format_take=format_take,
        record_take=record_take,
        record_failed_take=record_failed_take,
        read_selection=read_selection,
        paste_over_selection=lambda text, window: paster.paste(text, window, replacing_selection=True),
        focused_field=focused_field,
        edit_text=edit_text,
        edit_refusal=lambda: polish_edit_refusal(load_settings(), _engine_now(load_settings())),
        play_earcon=lambda name: earcons.play(name) if load_settings().sounds else None,
        quiet_minutes=lambda: load_settings().quiet_minutes,
        live_typing_wanted=live_typing_wanted,
        # On Wayland only OpenClicky's own keys are known (the person's ctrl or alt elsewhere is not).
        can_type_now=(lambda: not recognizer.any_modifier_down) if wayland
        else (lambda: not recognizer.any_modifier_down and not x_modifiers_held(xlib_display)),
        type_text=type_live,
        on_main_loop=lambda action: GLib.idle_add(lambda: (action(), False)[1]),
    ), orb)
    dictation_holder["dictation"] = dictation

    asking_holder: dict[str, AskController] = {}
    muted_said: list[bool] = [False]

    voice_said: list[str | None] = [None]

    def answer_voice() -> tuple[LinuxSettings, str | None]:
        """Who speaks now (Settings → Voice, read per answer), with the settings it needs."""
        current = load_settings()
        return current, speech_voices.resolve(current, voice_module.is_ready(), current.language_code)

    def begin_speaking() -> None:
        """A spoken answer starts; its pieces follow as they are written (`speak_piece`)."""
        current, choice = answer_voice()
        if voice is None or not current.speak_answers or choice is None:
            return
        if choice != voice_said[0]:
            voice_said[0] = choice
            applog.append(f"voice: {choice}")
        # Muted speakers are the person's choice: said once, so a silent answer is not a mystery.
        if not muted_said[0] and voice_module.output_is_muted():
            muted_said[0] = True
            dictation.show_notice("your sound is muted, so answers are shown, not spoken")
        voice.begin(current.language_code, current.kokoro_speaker, choice=choice, settings=current)

    def say_notice(caption: str) -> None:
        """A failure the Mac says in its recorded voice ("i couldn't reach openclicky…") is said here too."""
        name = speech_voices.notice_for(caption)
        if voice is None or name is None or not load_settings().speak_answers:
            return

        def decode_then_play() -> None:
            wav = speech_voices.recorded_notice(name)
            if wav is not None:
                GLib.idle_add(lambda: (voice.play_audio(wav, name), False)[1])

        threading.Thread(target=decode_then_play, daemon=True).start()

    pointer = PointerView(settings.pointer_presence, on_caption_finished=lambda: asking_holder["asking"].caption_finished(),
                          knows_the_mouse=not wayland)
    # The agent lane and the quick actions (see lanes.py): what answers a question by default.
    lanes = Lanes()

    def answer_question(question: str, screenshot, conversation: Conversation, extras=None, on_text=lambda _piece: None) -> str:
        # Read on the worker thread as the question goes out: its progress belongs to this question only.
        question_id = asking_holder["asking"].question_id
        return on_the_account(lambda: lanes.answer(question, screenshot, conversation, progress=lambda text: GLib.idle_add(
            lambda: (asking_holder["asking"].show_progress(text, question_id), False)[1]), extras=extras, on_text=on_text))
    def front_app_now():
        """The app in front for the skills: X11 says; on Wayland XWayland names an X11 app, and
        accessibility (when on) any app."""
        named = x_front_app(xlib_display, active_window_only=wayland)
        if named is None and wayland:
            field = focused_field()
            if field is not None and field.app_name:
                return FrontApp(window_class=field.app_name, title=field.window_title)
        return named

    def read_text_context():
        """The text a question may be about: what is highlighted, or the text box the cursor is in."""
        if reader["tracker"] is None:
            return writing.TextContext(readable=False)
        field = focused_field()
        target = writing.target_in(field)
        selected = accessibility.tidied_selection(field.selected_text) if field is not None and target is None else None
        return writing.TextContext(readable=True, target=target, selected_text=selected)

    def write_in_place(target, new_text: str) -> str:
        """The rewrite over the text it was asked about (run from the answer's worker thread: the
        box is checked and the paste made on the main loop, and the sentence handed back)."""
        finished = threading.Event()
        said: list[str] = [writing.TARGET_GONE]

        def on_main_loop() -> bool:
            said[0] = replace_now(target, new_text)
            finished.set()
            return False

        GLib.idle_add(on_main_loop)
        finished.wait(timeout=5.0)
        return said[0]

    def replace_now(target, new_text: str) -> str:
        if not new_text.strip():
            return writing.EMPTY_TEXT
        tracker = reader["tracker"]
        if tracker is None or not target.still_matches(tracker.field()):
            applog.append("write in place: the target moved, nothing pasted")
            return writing.TARGET_GONE
        if target.scope == writing.WHOLE_FIELD and not tracker.select_all():
            paster.copy_to_clipboard(new_text.strip())
            return writing.LEFT_ON_CLIPBOARD
        replacement = writing.keeping_surrounding_space(target.text, new_text)
        if not paster.paste(replacement, window_in_front(), replacing_selection=True):
            paster.copy_to_clipboard(replacement)
            return writing.LEFT_ON_CLIPBOARD
        # The person's own words one ctrl+v away, once the paste has put their clipboard back.
        GLib.timeout_add(1500, lambda: (paster.copy_to_clipboard(target.text), False)[1])
        applog.append(f"write in place: {'selection' if target.scope == writing.SELECTION else 'whole box'} "
                      f"in {target.app_name or 'an app'}, {len(target.text)} → {len(replacement)} chars")
        return writing.REPLACED

    fast_answers = [0]
    # The skills for the app in front, as fast voice's last turn began.
    realtime_skills = [""]

    def fast_voice():
        """Fast voice (realtime.py), joined to the pointer, the screen, the actions and the agent."""
        from .ask_lane import locate_pointer
        from .grounding import resolve as resolve_point
        from .realtime import RealtimeClient, ScreenContext, can_use
        from . import actions as actions_module
        from .agent import workspace_path

        def screen_now():
            """The screen with the mouse, taken on the main loop with the pointer out of sight."""
            taken: dict[str, Any] = {}
            finished = threading.Event()

            def capture() -> bool:
                try:
                    pictures = (capture_screens_wayland if wayland else capture_screens)()
                    taken["pictures"] = pictures() if callable(pictures) else pictures
                except Exception as failure:  # noqa: BLE001 - no screen this turn, said in the log
                    applog.append(f"realtime: the screen could not be taken ({type(failure).__name__})")
                pointer.show_after_screenshot()
                finished.set()
                return False

            GLib.idle_add(lambda: (pointer.hide_for_screenshot(), GLib.timeout_add(150, capture), False)[-1])
            finished.wait(timeout=10)
            pictures = taken.get("pictures")
            if not pictures:
                return None
            screen = pictures[0]
            caption = f"Screenshot of the screen with the mouse, {screen.width}x{screen.height} pixels."
            mouse = None if wayland else x_mouse_position(xlib_display)
            pointed = locate_pointer(mouse[0], mouse[1], pictures) if mouse else None
            if pointed is not None and pointed.screen_number == 1:
                caption += f" The mouse pointer is at ({pointed.x}, {pointed.y})."
            return ScreenContext(screen.png_bytes, screen.width, screen.height, caption, screen.origin_x, screen.origin_y, screen.scale)

        def point_at(screen_x: int, screen_y: int, label: str) -> None:
            GLib.idle_add(lambda: (pointer.fly_to(screen_x, screen_y), pointer.show_caption(label, hold_seconds=5.0), False)[-1])

        def run_action(name: str, arguments: dict) -> str:
            outcome = actions_module.perform(name, arguments, actions_module.live_desktop(workspace_path()))
            lanes.tasks_done += 1 if outcome.did_something else 0
            applog.append(f"realtime: quick action {name} {'did it' if outcome.did_something else 'did nothing'}")
            return outcome.sentence

        def agent_task(task: str) -> str:
            if not lanes.agent_lane_runs(load_settings()):
                return "The agent lane is not available: it needs the openclicky command-line tool and agent mode on."
            return lanes._run_agent(task, [], lambda text: GLib.idle_add(lambda: (pointer.show_caption(text[:120], None), False)[1]))

        def heard(role: str, words: str) -> None:
            if role == "assistant":
                fast_answers[0] += 1
                GLib.idle_add(lambda: (pointer.show_caption(words, hold_seconds=caption_hold_seconds(words)), False)[1])

        def instructions() -> str:
            from .realtime import DEFAULT_INSTRUCTIONS

            # Read on the main loop as the turn began (X11 and accessibility are not asked from here).
            block = realtime_skills[0]
            current = load_settings()
            language = (f"\nAlways speak and write in the language with code {current.language_code}."
                        if current.language_code and current.bare_language_code != "en" else "")
            return DEFAULT_INSTRUCTIONS + language + (f"\n\n{block}" if block else "")

        client = RealtimeClient(load_settings, screen_now, on_point_at=point_at, on_agent_task=agent_task, perform_action=run_action,
                                on_transcript=heard, instructions=instructions, resolve_point=resolve_point)
        return FastVoice(client, lambda: load_settings().fast_voice and can_use(load_settings()) and bool(load_settings().openai_api_key),
                         on_problem=lambda sentence: GLib.idle_add(lambda: (dictation.show_notice(sentence), False)[1]))

    fast = fast_voice()
    composer = ComposerWindow(on_submit=lambda question: asking_holder["asking"].submit(question))
    asking = AskController(AskHost(
        show_composer=composer.show,
        hide_composer=composer.hide,
        capture_screen=capture_screens_wayland if wayland else capture_screens,
        # Settings are read for every question: a key saved in Settings works at once.
        ask=ask or answer_question,
        run_in_background=run_in_background,
        schedule=schedule,
        on_main_loop=lambda action: GLib.idle_add(lambda: (action(), False)[1]),
        # Spoken in Kokoro's voice when it is downloaded and "Speak answers" is on (read per answer).
        prepare_voice=lambda: voice.prepare() if voice is not None and load_settings().speak_answers
        and answer_voice()[1] == speech_voices.KOKORO else None,
        say_notice=say_notice,
        speak_begin=begin_speaking,
        speak_piece=lambda piece: voice.say(piece) if voice is not None else None,
        speak_end=lambda: voice.end() if voice is not None else None,
        stop_speaking=lambda: voice.stop() if voice is not None else None,
        cancel_work=lanes.cancel,
        pause_before_sending=lambda: load_settings().pause_before_sending,
        # Wayland tells no one where the mouse is or which windows are up.
        mouse_position=(lambda: None) if wayland else (lambda: x_mouse_position(xlib_display)),
        cut_close_up=cut_close_up,
        prompt_windows=(lambda: []) if wayland else (lambda: x_prompt_windows(xlib_display)),
        # On Wayland XWayland names the app in front only while it is an X11 app.
        front_app=front_app_now,
        talk_skills=talk_skills_block,
        read_text_context=read_text_context,
        write_in_place=write_in_place,
    ), pointer)
    asking_holder["asking"] = asking

    pointer.reduce_motion = settings.reduce_motion

    island_holder: dict[str, Any] = {}

    def settings_saved(new_settings: LinuxSettings) -> None:
        pointer.set_presence(new_settings.pointer_presence)
        pointer.reduce_motion = new_settings.reduce_motion
        orb_window.apply(new_settings)
        if "island" in island_holder:
            island_holder["island"].set_glass(new_settings.glass_level)
            island_holder["island"].set_placement(new_settings.island_placement)
        start_reading_text_boxes()

    # Every OpenClicky window (Settings, the ask box) carries the pointer as its icon.
    Gtk.Window.set_default_icon(pointer_icon(64))
    settings_window = SettingsWindow(on_saved=settings_saved, on_paste_last=dictation.paste_last_take)
    words_holder: dict = {}

    def show_words() -> None:
        settings_window.show("words")

    def retry_take(take_id: str, done: Callable[[str | None], None]) -> None:
        """A failed take's audio heard again, formatted, put on the clipboard; `done` gets the
        orb's sentence (None when it worked)."""
        original = take_store.fetch(take_id) if take_store else None

        def hear_it_again() -> tuple[str, str]:
            raw = transcribe(take_audio.read(take_id))
            if not raw.strip():
                raise ValueError("didn't catch that")
            window = FocusedWindow(0, original.app_bundle_id) if original and original.app_bundle_id else None
            return raw, format_take(raw, window)

        def heard(outcome) -> None:
            if isinstance(outcome, BaseException):
                applog.append(f"take retry failed: {type(outcome).__name__}")
                done(f"couldn't retry: {outcome}")
                return
            raw, text = outcome
            take_store.complete_retried(take_id, raw, text)
            take_audio.discard(take_id)
            paster.copy_to_clipboard(text)
            dictation.show_notice("retried · copied, press ctrl+v")
            done(None)

        run_in_background(hear_it_again, heard)

    from .words_window import WordsWindow

    # "Your words" and "How it writes" are pages of the main window, as on the Mac.
    words = WordsWindow(take_store, take_audio=take_audio, retry=retry_take, embedded=True)
    settings_window.add_page("words", "Your words", words.words_page, before="orb", words="history takes search ask filter try it here retry")
    settings_window.add_page("writing", "How it writes", words.writing_page, before="orb",
                             words="dictionary names spelling shortcuts snippets styles rules apps")
    settings_window.stack.connect("notify::visible-child-name", lambda stack, _property: words.refresh()
                                  if stack.get_visible_child_name() in ("words", "writing") else None)
    words_holder["window"] = words

    if take_store is not None:
        # Kept alive by its clipboard signal; it records nothing unless Settings says so.
        ClipboardHistoryMonitor(take_store, is_enabled=lambda: load_settings().clipboard_history and not load_settings().incognito,
                                last_paste_at=lambda: paster.last_paste_at,
                                app_in_front=lambda: (window_in_front() or FocusedWindow(0)).app_class)

    from .result_card import ResultCard

    def follow_up_with_agent() -> None:
        lanes.follow_up_next = True
        asking.open_composer()

    result_card = ResultCard(on_follow_up=follow_up_with_agent)
    lanes.on_agent_finished = lambda result: GLib.idle_add(lambda: (result_card.show(result), False)[1])

    def quit_with_farewell() -> None:
        """A line in the orb under a short chime, then gone (the Mac's farewell)."""
        from .farewell import SHOWN_FOR_SECONDS, farewell

        if load_settings().sounds:
            earcons.play("goodbye")
        orb.show("done", farewell())
        GLib.timeout_add(int(SHOWN_FOR_SECONDS * 1000), lambda: (on_quit(), False)[1])

    from .controller import TakeState
    from .island import DOCKED_KEY, Island, IslandHooks, Moment, start_slow_refresh
    from .windows import shortcut_rows

    def moment_now() -> Moment:
        return Moment(dictating=dictation.state is TakeState.LISTENING and not dictation.take_asks_the_pointer,
                      dictation_working=dictation.state is TakeState.FINISHING and not dictation.take_asks_the_pointer,
                      asking_status=asking.island_status, answering=asking.is_answering,
                      speaking=voice is not None and voice.is_speaking, agent_activity=asking.agent_activity,
                      last_take=dictation.last_take_text, last_take_at=dictation.last_take_at,
                      voice_name=speech_voices.TITLES.get(voice_said[0]) if voice_said[0] else None,
                      fast_voice_state=fast.client.state, hands_free=fast.client.hands_free)

    def stop_everything() -> None:
        """The island's stop: esc's job."""
        if dictation.is_take_in_progress:
            dictation.cancel_take("cancelled")
        else:
            asking.escape()
            fast.interrupt()

    def set_docked(is_docked: bool) -> None:
        pointer.docked = is_docked
        preferences.set_values(**{DOCKED_KEY: True if is_docked else None})
        if pointer.mode == "following":
            pointer.position = pointer._follow_target()
            pointer._move_triangle()

    def set_speak_answers(is_on: bool) -> None:
        save_settings({"speak_answers": is_on})
        if not is_on and voice is not None:
            voice.stop()

    island_view = Island(IslandHooks(
        moment=moment_now, stop=stop_everything, open_settings=settings_window.show, quit=quit_with_farewell,
        # Wayland never says where the mouse is: the pointer rests and only comes out to answer.
        pointer_presence=lambda: "whenAnswering" if wayland else load_settings().pointer_presence,
        docked=lambda: pointer.docked, set_docked=set_docked,
        speak_answers=lambda: load_settings().speak_answers, set_speak_answers=set_speak_answers,
        shortcut_rows=lambda: shortcut_rows(dictation_key, load_settings().pointer_presence), copy_text=paster.copy_to_clipboard,
        follow_up_agent=follow_up_with_agent), placement=settings.island_placement, glass_level=settings.glass_level)
    island_view.take_drops()
    start_slow_refresh(island_view)
    island_holder["island"] = island_view
    set_docked(preferences.get(DOCKED_KEY) is True)

    from .tour import SETTINGS_TOUR_DONE_KEY, SettingsTour, TourHooks, WelcomeTour, tour_is_done

    def say_line(text: str) -> None:
        """A tour's line in the answering voice (nothing when answers are not spoken)."""
        current, choice = answer_voice()
        if voice is None or not current.speak_answers or choice is None:
            return
        voice.begin(current.language_code, current.kokoro_speaker, choice=choice, settings=current)
        voice.say(text)
        voice.end()

    welcome_tour = WelcomeTour(TourHooks(
        key_name=lambda: KEY_NAMES.get(dictation_key, dictation_key),
        can_ask=lambda: answers_module.answering_source(load_settings()) is not None,
        on_free_account=lambda: answers_module.answering_source(load_settings()) is not None
        and answers_module.answering_source(load_settings()).id == "openClickyAccount",
        say=say_line, stop_saying=lambda: voice.stop() if voice is not None else None,
        takes=lambda: [text for text, message in dictation.finished_takes if text and message != "asked the pointer"],
        answers_given=lambda: len(asking.conversation.exchanges) + fast_answers[0],
        tasks_done=lambda: lanes.tasks_done,
        open_settings=lambda page: settings_window.show(page)))
    settings_tour = SettingsTour(settings_window, fly_to=lambda x, y: pointer.fly_to(x, y),
                                 show_caption=lambda text, seconds: pointer.show_caption(text, hold_seconds=seconds), say=say_line,
                                 stop=lambda: (pointer.dismiss_caption(), pointer.return_to_mouse()),
                                 key_name=lambda: KEY_NAMES.get(dictation_key, dictation_key))

    def settings_opened(_window) -> None:
        # The first time Settings opens after the welcome tour, its own one-minute tour plays.
        if tour_is_done() and preferences.get(SETTINGS_TOUR_DONE_KEY) is not True and not settings_tour.is_running:
            GLib.timeout_add(600, lambda: (settings_tour.start(), False)[1])

    settings_window.window.connect("show", settings_opened)
    settings_window.on_welcome_tour = welcome_tour.start
    settings_window.on_settings_tour = settings_tour.start

    def replay_welcome() -> None:
        welcome_tour.start()

    tray = TrayIcon(on_ask=asking.open_composer, on_settings=settings_window.show, on_quit=quit_with_farewell, on_words=show_words,
                    on_open=lambda: settings_window.show("general"), on_replay_welcome=replay_welcome,
                    on_paste_last=dictation.paste_last_take, on_open_island=island_view.open,
                    docked=lambda: pointer.docked, set_docked=set_docked) if with_tray else None

    def handle(event) -> None:
        if isinstance(event, TextComposerRequested):
            asking.open_composer()
            return
        if isinstance(event, HandsFreeToggleRequested):
            if fast.usable():
                fast.toggle_hands_free()
            return
        if isinstance(event, EscapePressed) and not dictation.is_take_in_progress:
            asking.escape()
            fast.interrupt()
            welcome_tour.finish()
            settings_tour.cancel()
        # Asking out loud talks to fast voice when it can (the Mac's Realtime lane), else it is a
        # take whose words go to the pointer.
        if isinstance(event, DictationPressed) and event.ask and not dictation.is_take_in_progress and fast.usable():
            realtime_skills[0] = talk_skills_block(front_app_now())
            fast.begin_turn()
            return
        if isinstance(event, DictationReleased) and fast.turn_open:
            fast.end_turn()
            return
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
        listener_options = {"on_pointer_moved": lambda: GLib.idle_add(lambda: (pointer.mouse_moved(), island_view.mouse_moved_somewhere(),
                                                                               False)[-1]),
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
        on_edit_key=lambda is_down: deliver(recognize(lambda: recognizer.edit_key_down() if is_down else recognizer.edit_key_up())),
        # Clicks and keys, for a guided step waiting to be done (ignored unless one waits).
        on_step_input=lambda kind, position=None: None if kind == "escape" and composer.is_open_for_key_watcher else GLib.idle_add(
            lambda: (asking.step_input(kind, position), False)[1]),
        display_name=display_name,
        **listener_options,
    )
    listener_holder["listener"] = listener
    return OpenClickyApp(dictation=dictation, asking=asking, listener=listener, pointer=pointer,
                         composer=composer, settings_window=settings_window, tray=tray, take_store=take_store, island=island_view,
                         welcome_tour=welcome_tour)


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
    first_run = is_first_run()
    finished_setup = False
    if first_run or wayland_needs_permission:
        finished_setup = run_setup_window(wayland=is_wayland_session())
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

    def dictionary_keyterms() -> tuple[str, ...]:
        return tuple(term.written for term in load_space().dictionary)

    def start_take_recording():
        """The microphone (read for every take: one picked in Settings is used at once), streamed to
        AssemblyAI while it records when that is the engine."""
        current = load_settings()
        recording = MicrophoneRecording(microphone=current.microphone)
        if current.dictation_engine != "assemblyai" or not current.account_token:
            return recording
        from .streaming import StreamedRecording

        return StreamedRecording(recording, current, dictionary_keyterms(),
                                 fallback=lambda wav_bytes: on_the_account(lambda: transcribe(wav_bytes, load_settings())))
    try:
        app = wire_up(dictation_key,
                      # The microphone is read for every take: one picked in Settings is used at once.
                      start_recording=start_take_recording,
                      transcribe=lambda wav_bytes: on_the_account(lambda: transcribe(wav_bytes, load_settings(), offline_engine,
                                                                                   keyterms=dictionary_keyterms())),
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
    from .tour import DONE_KEY, tour_is_done

    if first_run and finished_setup and not tour_is_done():
        # Right after the setup, the welcome tour, once (Tray → Replay welcome… plays it again).
        GLib.timeout_add(2500, lambda: (app.welcome_tour.start(), False)[1])
    elif not first_run and not tour_is_done():
        # Set up before the tour existed: it is not pushed on them (the Mac marks earlier users done).
        from . import preferences as tour_preferences

        tour_preferences.set_values(**{DONE_KEY: True})
    from . import auto_download

    # The offline models download on their own once setup is done (not on a metered network, never
    # one the person skipped or removed).
    auto_download.start([offline.PARAKEET, voice_module.KOKORO])
    from . import updater

    def check_for_updates() -> bool:
        """Once a day, off the main loop: a newer version is said in the orb and shown in Settings."""
        if not load_settings().auto_update or not updater.is_due():
            return True

        def run() -> None:
            try:
                found = updater.check()
            except OSError as failure:
                applog.append(f"updates: not checked ({failure})")
                return
            if found is not None:
                GLib.idle_add(lambda: (app.settings_window.show_update(found),
                                       app.dictation.show_notice(f"openclicky {found.version} is ready: settings, general"), False)[-1])
        threading.Thread(target=run, daemon=True).start()
        return True

    GLib.timeout_add_seconds(60, lambda: (check_for_updates(), False)[1])
    GLib.timeout_add_seconds(3600, check_for_updates)
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
