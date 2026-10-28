"""Live typing: settled words, phrases, the final line-up, and a take that types as it is spoken."""

from openclicky_linux import live_typing
from openclicky_linux.controller import DictationHost, DictationTakeController, FocusedWindow, OrbModel, TakeState
from openclicky_linux.gestures import DictationPressed, DictationReleased
from openclicky_linux.live_typing import StableWordCommitter, align, text_to_type
from openclicky_linux.recorder import SAMPLE_RATE
from openclicky_linux.space import SEEDED_STYLES, DictationSpace, DictionaryTerm

OTHER = SEEDED_STYLES[-1]


def test_a_word_settles_once_it_is_clear_of_the_tail_and_unchanged():
    committer = StableWordCommitter()
    assert committer.observe("hello", 0.0) == []
    assert committer.observe("hello there", 0.1) == []
    # "hello" has stood 0.3 s and "there" is the tail.
    assert committer.observe("hello there", 0.35) == ["hello"]
    # A revision restarts a word's clock; a pause lets the tail go too.
    assert committer.observe("hello their friend", 0.4) == []
    assert committer.observe("hello their friend", 0.75, person_paused=True) == ["their", "friend"]
    # A partial that rewrote typed words commits nothing more.
    assert committer.observe("goodbye", 2.0) == []


def test_a_possible_shortcut_and_a_split_alias_are_held_back():
    shortcut = StableWordCommitter(take_start_triggers=[["my", "sign", "off"]])
    assert shortcut.observe("my sign", 0.0) == [] and shortcut.observe("my sign", 1.0, person_paused=True) == []
    # Past every trigger: the words that settled long ago go at once.
    assert shortcut.observe("my sign up page", 2.0) == ["my", "sign"]
    assert shortcut.observe("my sign up page", 3.0) == ["up"]
    alias = StableWordCommitter(protected_phrases=[["aditya", "shatriya"]])
    alias.observe("call aditya now", 0.0)
    assert alias.observe("call aditya now", 1.0) == ["call"]


def test_phrases_are_spaced_cased_and_spelled_your_way():
    space = DictationSpace(dictionary=(DictionaryTerm(written="OpenClicky", heard_as=("open clicky",)),))
    assert text_to_type(["um", "try", "open", "clicky."], OTHER, space, "") == "Try OpenClicky."
    assert text_to_type(["it", "works"], OTHER, space, "Try OpenClicky.") == " It works"
    assert text_to_type(["now,", "really"], OTHER, space, "It works") == " now, really"
    assert text_to_type([","], OTHER, space, "word") == ","
    assert live_typing.contains_control_characters("a\nb") and not live_typing.contains_control_characters("नमस्ते‍")


def test_the_final_transcript_lines_up_against_what_was_typed():
    assert align("hello there my friend".split(), ["hello", "there"]) == live_typing.Alignment(2, True)
    # Rewritten on the way ("twenty five" → "25"): the trailing words are found near the end.
    assert align("I have 25 apples and pears".split(), "I have twenty five apples".split()).remaining(
        "I have 25 apples and pears".split()) == ["and", "pears"]
    # Nothing lines up: the rest after the common start is left for the person.
    assert align("goodbye world".split(), ["hello", "there"]) == live_typing.Alignment(0, False)


class StreamingRecording:
    def __init__(self):
        self.on_live_words = lambda _words: None

    def stop(self):
        return b"\x01\x00" * SAMPLE_RATE

    def cancel(self):
        pass

    def transcript(self, _wav):
        return "Hello there my friend."


def make(can_type=True):
    typed, copied, timers, background = [], [], [], []
    window = FocusedWindow(1, "gedit")
    recording = StreamingRecording()
    host = DictationHost(
        start_recording=lambda: recording, transcribe=lambda wav: "unused", focused_window=lambda: window,
        paste=lambda text, window: True, copy_to_clipboard=copied.append, load_space=DictationSpace,
        run_in_background=lambda work, done: background.append((work, done)), schedule=lambda seconds, action: timers.append(action),
        live_typing_wanted=lambda window: (OTHER, DictationSpace()), can_type_now=lambda: can_type,
        type_text=lambda text: typed.append(text) or True)
    controller = DictationTakeController(host, OrbModel())
    return controller, recording, typed, copied, background


def finish(background):
    while background:
        work, done = background.pop(0)
        done(work())


def test_a_tapped_take_types_as_it_is_spoken_and_types_the_rest_on_release():
    controller, recording, typed, copied, background = make()
    controller.handle(DictationPressed())
    controller.handle(DictationReleased(was_tap=True))
    controller._live.committer.stable_seconds = 0.0
    recording.on_live_words("hello there my")
    assert typed == ["Hello there"]
    controller.handle(DictationPressed())  # the second tap ends it
    finish(background)
    assert typed == ["Hello there", " my friend."] and copied == []
    assert controller.finished_takes[-1] == ("Hello there my friend.", "moved to text box")
    assert controller.state is TakeState.IDLE


def test_while_a_modifier_is_held_nothing_is_typed_and_a_take_that_typed_nothing_pastes():
    controller, recording, typed, copied, background = make(can_type=False)
    controller.handle(DictationPressed())
    controller._live.committer.stable_seconds = 0.0
    recording.on_live_words("hello there my")
    assert typed == []
    controller.handle(DictationReleased(was_tap=False))
    finish(background)
    assert controller.finished_takes[-1] == ("Hello there my friend.", "moved to text box") and typed == []
