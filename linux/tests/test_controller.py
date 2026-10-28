"""The take state machine with a fake desktop: background work and timers run when the test says."""

from openclicky_linux.controller import DictationHost, DictationTakeController, FocusedWindow, OrbModel, TakeState
from openclicky_linux.gestures import DictationDoubleTapped, DictationPressed, DictationReleased, EscapePressed
from openclicky_linux.recorder import SAMPLE_RATE
from openclicky_linux.space import DictationSpace, DictionaryTerm

ONE_SECOND_OF_AUDIO = b"\x01\x00" * SAMPLE_RATE


class FakeRecording:
    def __init__(self, pcm: bytes) -> None:
        self.pcm = pcm
        self.cancelled = False

    def stop(self) -> bytes:
        return self.pcm

    def cancel(self) -> None:
        self.cancelled = True


class FakeDesktop:
    def __init__(self) -> None:
        self.window = FocusedWindow(window_id=1, app_class="gedit")
        self.transcript = "hello there"
        self.pcm = ONE_SECOND_OF_AUDIO
        self.recordings: list[FakeRecording] = []
        self.pasted: list[tuple[str, FocusedWindow | None]] = []
        self.copied: list[str] = []
        self.paste_works = True
        self.recorder_failure: Exception | None = None
        self.pending_background: list = []
        self.pending_timers: list = []
        self.space = DictationSpace()

    def start_recording(self) -> FakeRecording:
        if self.recorder_failure:
            raise self.recorder_failure
        recording = FakeRecording(self.pcm)
        self.recordings.append(recording)
        return recording

    def transcribe(self, wav_bytes: bytes) -> str:
        assert wav_bytes.startswith(b"RIFF")
        if isinstance(self.transcript, Exception):
            raise self.transcript
        return self.transcript

    def paste(self, text, window) -> bool:
        self.pasted.append((text, window))
        return self.paste_works

    def host(self) -> DictationHost:
        def run_in_background(work, done):
            self.pending_background.append((work, done))

        return DictationHost(
            start_recording=self.start_recording,
            transcribe=self.transcribe,
            focused_window=lambda: self.window,
            paste=self.paste,
            copy_to_clipboard=self.copied.append,
            load_space=lambda: self.space,
            run_in_background=run_in_background,
            schedule=lambda seconds, action: self.pending_timers.append(action),
        )

    def finish_background_work(self) -> None:
        while self.pending_background:
            work, done = self.pending_background.pop(0)
            try:
                outcome = work()
            except Exception as failure:  # noqa: BLE001
                outcome = failure
            done(outcome)


def make() -> tuple[FakeDesktop, DictationTakeController, OrbModel]:
    desktop = FakeDesktop()
    orb = OrbModel()
    return desktop, DictationTakeController(desktop.host(), orb), orb


def test_holding_the_key_pastes_the_formatted_words_into_the_window_from_the_press():
    desktop, controller, orb = make()
    desktop.space = DictationSpace(dictionary=(DictionaryTerm(written="OpenClicky", heard_as=("open clicky",)),))
    desktop.transcript = "um, try open clicky"
    controller.handle(DictationPressed())
    assert controller.state is TakeState.LISTENING
    assert orb.phase == "listening"
    controller.handle(DictationReleased(was_tap=False))
    assert controller.state is TakeState.FINISHING
    desktop.finish_background_work()
    assert desktop.pasted == [("Try OpenClicky", desktop.window)]
    assert controller.state is TakeState.IDLE
    assert (orb.phase, orb.message) == ("done", "moved to text box")
    for rest in desktop.pending_timers:
        rest()
    assert orb.phase == "idle"


def test_a_tap_keeps_listening_until_the_next_press():
    desktop, controller, _ = make()
    controller.handle(DictationPressed())
    controller.handle(DictationReleased(was_tap=True))
    assert controller.state is TakeState.LISTENING
    controller.handle(DictationPressed())
    assert controller.state is TakeState.FINISHING
    # The release of the finishing tap changes nothing.
    controller.handle(DictationReleased(was_tap=True))
    desktop.finish_background_work()
    assert [text for text, _ in desktop.pasted] == ["Hello there"]


def test_two_quick_taps_cancel_without_pasting():
    desktop, controller, orb = make()
    controller.handle(DictationPressed())
    controller.handle(DictationReleased(was_tap=True))
    controller.handle(DictationPressed())
    controller.handle(DictationReleased(was_tap=True))
    controller.handle(DictationDoubleTapped())
    assert controller.state is TakeState.IDLE
    desktop.finish_background_work()
    assert desktop.pasted == [] and desktop.copied == []
    assert (orb.phase, orb.message) == ("failed", "cancelled")


def test_escape_cancels_and_closes_the_microphone():
    desktop, controller, _ = make()
    controller.handle(DictationPressed())
    controller.handle(EscapePressed())
    assert controller.state is TakeState.IDLE
    assert desktop.recordings[0].cancelled


def test_a_cancelled_take_never_pastes_into_the_next_one():
    desktop, controller, _ = make()
    controller.handle(DictationPressed())
    controller.handle(DictationReleased(was_tap=False))
    old_work = desktop.pending_background.pop()
    controller.handle(EscapePressed())
    desktop.transcript = "new words"
    controller.handle(DictationPressed())
    # The old take's transcript arrives while the new one listens: dropped.
    work, done = old_work
    done("old words")
    assert desktop.pasted == []
    assert controller.state is TakeState.LISTENING
    controller.handle(DictationReleased(was_tap=False))
    desktop.finish_background_work()
    assert [text for text, _ in desktop.pasted] == ["New words"]


def test_a_key_used_as_a_shortcut_throws_the_take_away():
    desktop, controller, orb = make()
    controller.handle(DictationPressed())
    controller.handle(DictationReleased(was_tap=False, key_was_pressed_during_hold=True))
    assert controller.state is TakeState.IDLE
    assert desktop.pending_background == []
    assert orb.message == "cancelled, that was a shortcut"


def test_another_window_in_front_by_release_gets_nothing_the_words_are_copied():
    desktop, controller, orb = make()
    controller.handle(DictationPressed())
    desktop.window = FocusedWindow(window_id=2, app_class="firefox")
    controller.handle(DictationReleased(was_tap=False))
    desktop.finish_background_work()
    assert desktop.pasted == []
    assert desktop.copied == ["Hello there"]
    assert orb.message == "the window changed. copied, press ctrl+v"


def test_a_paste_that_could_not_be_sent_leaves_the_words_on_the_clipboard():
    desktop, controller, orb = make()
    desktop.paste_works = False
    controller.handle(DictationPressed())
    controller.handle(DictationReleased(was_tap=False))
    desktop.finish_background_work()
    assert desktop.copied == ["Hello there"]
    assert orb.message == "copied, press ctrl+v"


def test_a_very_short_take_is_not_sent_to_transcription():
    desktop, controller, orb = make()
    desktop.pcm = b"\x00\x00" * 100
    desktop.transcript = RuntimeError("must not be called")
    controller.handle(DictationPressed())
    controller.handle(DictationReleased(was_tap=False))
    desktop.finish_background_work()
    assert desktop.pasted == []
    assert (orb.phase, orb.message) == ("failed", "didn't catch that")


def test_a_transcription_failure_is_said_on_the_orb():
    desktop, controller, orb = make()
    desktop.transcript = RuntimeError("couldn't reach openai: offline")
    controller.handle(DictationPressed())
    controller.handle(DictationReleased(was_tap=False))
    desktop.finish_background_work()
    assert desktop.pasted == []
    assert (orb.phase, orb.message) == ("failed", "couldn't reach openai: offline")
    assert controller.state is TakeState.IDLE


def test_a_microphone_that_will_not_open_fails_the_take_at_once():
    desktop, controller, orb = make()
    desktop.recorder_failure = RuntimeError("no microphone recorder found")
    controller.handle(DictationPressed())
    assert controller.state is TakeState.IDLE
    assert (orb.phase, orb.message) == ("failed", "no microphone recorder found")
    # The next press tries again.
    desktop.recorder_failure = None
    controller.handle(DictationPressed())
    assert controller.state is TakeState.LISTENING


def test_a_window_that_cannot_be_told_at_press_gets_nothing_the_words_are_copied():
    desktop, controller, orb = make()
    desktop.window = None  # no window manager answer, or an X error, at press
    controller.handle(DictationPressed())
    desktop.window = FocusedWindow(window_id=9, app_class="chat")  # whatever is in front later
    controller.handle(DictationReleased(was_tap=False))
    desktop.finish_background_work()
    assert desktop.pasted == []
    assert desktop.copied == ["Hello there"]
    assert orb.message == "couldn't tell which window. copied, press ctrl+v"


def test_a_window_that_cannot_be_told_at_release_gets_nothing_either():
    desktop, controller, orb = make()
    controller.handle(DictationPressed())
    desktop.window = None
    controller.handle(DictationReleased(was_tap=False))
    desktop.finish_background_work()
    assert desktop.pasted == []
    assert desktop.copied == ["Hello there"]


def test_a_paste_that_throws_leaves_the_words_on_the_clipboard_and_the_take_ends():
    desktop, controller, orb = make()

    def broken_paste(text, window):
        raise RuntimeError("BadWindow")
    controller.host.paste = broken_paste
    controller.handle(DictationPressed())
    controller.handle(DictationReleased(was_tap=False))
    desktop.finish_background_work()
    assert controller.state is TakeState.IDLE
    assert desktop.copied == ["Hello there"]
    assert orb.message == "couldn't paste. copied, press ctrl+v"


def test_esc_handled_while_the_words_are_delivered_wins():
    desktop, controller, orb = make()

    def paste_while_esc_arrives(text, window):
        controller.handle(EscapePressed())  # what a nested main loop used to let in
        return True
    controller.host.paste = paste_while_esc_arrives
    controller.handle(DictationPressed())
    controller.handle(DictationReleased(was_tap=False))
    desktop.finish_background_work()
    assert controller.state is TakeState.IDLE
    assert orb.phase == "failed" and orb.message == "cancelled"
    assert controller.finished_takes == []


# MARK: a spoken question (left ctrl + left alt)

from openclicky_linux.gestures import AskJoined  # noqa: E402


def test_a_take_held_with_alt_goes_to_the_pointer_and_types_nothing():
    desktop, controller, orb = make()
    questions = []
    controller.host.ask_question = questions.append
    desktop.transcript = "um, where do i save this"
    controller.handle(DictationPressed(ask=True))
    assert (orb.phase, orb.message) == ("listening", "ask your question, then let go")
    controller.handle(DictationReleased(was_tap=False))
    desktop.finish_background_work()
    assert questions == ["Where do i save this"]
    assert desktop.pasted == [] and desktop.copied == []
    assert controller.state is TakeState.IDLE
    assert orb.phase == "idle"


def test_alt_joining_a_take_that_listens_turns_it_into_a_question():
    desktop, controller, orb = make()
    questions = []
    controller.host.ask_question = questions.append
    controller.handle(DictationPressed())
    controller.handle(AskJoined())
    assert orb.message == "ask your question, then let go"
    controller.handle(DictationReleased(was_tap=False))
    desktop.finish_background_work()
    assert questions == ["Hello there"] and desktop.pasted == []


def test_a_question_nobody_said_asks_nothing():
    desktop, controller, orb = make()
    questions = []
    controller.host.ask_question = questions.append
    desktop.transcript = "  "
    controller.handle(DictationPressed(ask=True))
    controller.handle(DictationReleased(was_tap=False))
    desktop.finish_background_work()
    assert questions == []
    assert (orb.phase, orb.message) == ("failed", "didn't catch that")


def test_the_next_take_is_dictation_again():
    desktop, controller, orb = make()
    questions = []
    controller.host.ask_question = questions.append
    controller.handle(DictationPressed(ask=True))
    controller.handle(DictationReleased(was_tap=False))
    desktop.finish_background_work()
    controller.handle(DictationPressed())
    controller.handle(DictationReleased(was_tap=False))
    desktop.finish_background_work()
    assert len(questions) == 1
    assert [text for text, _ in desktop.pasted] == ["Hello there"]


# MARK: Hey Clicky, tones, quiet takes, failed takes, paste last (the Mac's, ported)

def test_a_take_held_with_shift_edits_the_selection_read_at_the_press():
    from openclicky_linux.gestures import EditJoined
    desktop, controller, orb = make()
    edits = []
    controller.host.read_selection = lambda done: done("hey can u send it")
    controller.host.edit_text = lambda selection, instruction, window: (edits.append((selection, instruction, window)), "Hi, could you send it?")[1]
    desktop.transcript = "make it formal"
    controller.handle(DictationPressed(edit=True))
    assert orb.message == "say your edit, then let go"
    controller.handle(DictationReleased(was_tap=False))
    desktop.finish_background_work()
    assert edits == [("hey can u send it", "make it formal", desktop.window)]
    # The paste replaces the selection, which is still selected in the window from the press.
    assert desktop.pasted == [("Hi, could you send it?", desktop.window)]
    # With nothing selected, the last take is what is edited; shift joining a take makes it an edit.
    controller.host.read_selection = lambda done: done(None)
    controller.handle(DictationPressed())
    controller.handle(EditJoined())
    controller.handle(DictationReleased(was_tap=False))
    desktop.finish_background_work()
    assert edits[-1][0] == "Hi, could you send it?"


def test_an_edit_is_refused_before_listening_when_no_model_may_see_the_text():
    desktop, controller, orb = make()
    tones = []
    controller.host.play_earcon = tones.append
    controller.host.edit_refusal = lambda: "hey clicky needs “clean up my words with ai” on (styles)"
    controller.handle(DictationPressed(edit=True))
    assert controller.state is TakeState.IDLE and desktop.recordings == []
    assert orb.message.startswith("hey clicky needs") and tones == ["blocked"]


def test_tones_mark_a_takes_start_stop_and_end():
    desktop, controller, orb = make()
    tones = []
    controller.host.play_earcon = tones.append
    controller.handle(DictationPressed())
    controller.handle(DictationReleased(was_tap=False))
    desktop.finish_background_work()
    assert tones == ["start", "stop", "complete"]
    desktop.transcript = RuntimeError("couldn't reach openai")
    controller.handle(DictationPressed())
    controller.handle(DictationReleased(was_tap=False))
    desktop.finish_background_work()
    assert tones[-1] == "error"


def test_a_tapped_take_ends_after_minutes_of_quiet():
    import openclicky_linux.controller as controller_module
    desktop, controller, orb = make()
    controller.host.quiet_minutes = lambda: 3
    clock = [1000.0]
    original = controller_module.time.monotonic
    controller_module.time.monotonic = lambda: clock[0]
    try:
        controller.handle(DictationPressed())
        desktop.recordings[-1].current_level = lambda: 0.01
        controller.handle(DictationReleased(was_tap=True))
        clock[0] += 120
        desktop.pending_timers.pop(0)()
        assert controller.state is TakeState.LISTENING
        clock[0] += 61
        desktop.pending_timers.pop(0)()
        assert controller.state is TakeState.FINISHING
    finally:
        controller_module.time.monotonic = original


def test_a_take_the_engine_could_not_finish_keeps_its_audio():
    desktop, controller, orb = make()
    kept = []
    controller.host.record_failed_take = kept.append
    desktop.transcript = RuntimeError("couldn't reach openai")
    controller.handle(DictationPressed())
    controller.handle(DictationReleased(was_tap=False))
    desktop.finish_background_work()
    assert kept[0]["reason"] == "couldn't reach openai" and kept[0]["wav"].startswith(b"RIFF")
    assert kept[0]["window"] == desktop.window and kept[0]["mode"] == "dictate"
    assert orb.message == "couldn't reach openai"


def test_the_last_take_can_be_pasted_again():
    desktop, controller, orb = make()
    controller.paste_last_take()
    assert orb.message == "nothing dictated yet"
    controller.handle(DictationPressed())
    controller.handle(DictationReleased(was_tap=False))
    desktop.finish_background_work()
    desktop.window = FocusedWindow(window_id=2, app_class="firefox")
    controller.paste_last_take()
    assert desktop.pasted[-1] == ("Hello there", desktop.window)


def test_with_accessibility_a_password_box_is_never_typed_into_and_no_box_waits_for_one():
    from openclicky_linux.accessibility import FocusedField
    desktop, controller, orb = make()
    password = FocusedField("firefox", None, True, True, None, None, None)
    controller.host.focused_field = lambda: password
    controller.handle(DictationPressed())
    assert orb.message == "a password box: your words will be copied, not typed"
    controller.handle(DictationReleased(was_tap=False))
    desktop.finish_background_work()
    assert desktop.pasted == [] and desktop.copied == ["Hello there"]
    # Begun outside any text box: the box clicked into before the release takes the words.
    fields = [FocusedField("nautilus", None, False, False, None, None, None),
              FocusedField("gedit", None, True, False, "", None, 0)]
    controller.host.focused_field = lambda: fields.pop(0) if len(fields) > 1 else fields[0]
    controller.handle(DictationPressed())
    assert orb.message == "no text box here. click into one before you let go"
    desktop.window = FocusedWindow(window_id=2, app_class="gedit")
    controller.handle(DictationReleased(was_tap=False))
    desktop.finish_background_work()
    assert desktop.pasted[-1] == ("Hello there", desktop.window)
