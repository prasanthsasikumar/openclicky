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
