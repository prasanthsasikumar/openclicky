from openclicky_linux.gestures import (
    DictationDoubleTapped, DictationGestureRecognizer, DictationPressed, DictationReleased, EscapePressed,
)


def test_a_hold_is_a_press_and_a_release_that_is_not_a_tap():
    recognizer = DictationGestureRecognizer()
    assert recognizer.dictation_key_down(10.0) == [DictationPressed()]
    assert recognizer.dictation_key_up(11.5) == [DictationReleased(was_tap=False)]


def test_auto_repeat_downs_are_ignored():
    recognizer = DictationGestureRecognizer()
    recognizer.dictation_key_down(10.0)
    assert recognizer.dictation_key_down(10.5) == []


def test_two_quick_taps_are_a_double_tap():
    recognizer = DictationGestureRecognizer()
    recognizer.dictation_key_down(10.0)
    assert recognizer.dictation_key_up(10.1) == [DictationReleased(was_tap=True)]
    recognizer.dictation_key_down(10.3)
    assert recognizer.dictation_key_up(10.4) == [DictationReleased(was_tap=True), DictationDoubleTapped()]


def test_taps_far_apart_are_not_a_double_tap():
    recognizer = DictationGestureRecognizer()
    recognizer.dictation_key_down(10.0)
    recognizer.dictation_key_up(10.1)
    recognizer.dictation_key_down(13.0)
    assert recognizer.dictation_key_up(13.1) == [DictationReleased(was_tap=True)]


def test_a_key_typed_while_held_marks_the_release():
    recognizer = DictationGestureRecognizer()
    recognizer.dictation_key_down(10.0)
    assert recognizer.other_key_down(is_escape=False) == []
    assert recognizer.dictation_key_up(10.1) == [DictationReleased(was_tap=False, key_was_pressed_during_hold=True)]


def test_escape_is_reported():
    assert DictationGestureRecognizer().other_key_down(is_escape=True) == [EscapePressed()]


# MARK: left ctrl, shared with the ask box (the default)

from openclicky_linux.gestures import AskJoined, HoldStarted, TextComposerRequested  # noqa: E402


def shared() -> DictationGestureRecognizer:
    return DictationGestureRecognizer(shares_key_with_the_ask_box=True)


def test_left_ctrl_held_on_its_own_listens_after_the_delay():
    recognizer = shared()
    assert recognizer.dictation_key_down(10.0) == [HoldStarted(1)]
    assert recognizer.hold_elapsed(1) == [DictationPressed(ask=False)]
    assert recognizer.dictation_key_up(12.0) == [DictationReleased(was_tap=False)]


def test_ctrl_c_never_listens_at_all():
    recognizer = shared()
    recognizer.dictation_key_down(10.0)
    recognizer.other_key_down(is_escape=False)  # the C, before the delay
    assert recognizer.hold_elapsed(1) == []
    assert recognizer.dictation_key_up(10.2) == []


def test_a_key_after_the_delay_makes_it_a_shortcut():
    recognizer = shared()
    recognizer.dictation_key_down(10.0)
    recognizer.hold_elapsed(1)
    recognizer.other_key_down(is_escape=False)
    assert recognizer.dictation_key_up(11.0) == [DictationReleased(was_tap=False, key_was_pressed_during_hold=True)]


def test_a_quick_release_is_no_take_and_two_of_them_open_the_ask_box():
    recognizer = shared()
    recognizer.dictation_key_down(10.0)
    assert recognizer.dictation_key_up(10.1) == []
    recognizer.dictation_key_down(10.3)
    assert recognizer.hold_elapsed(1) == []  # a stale delay from the first press does nothing
    assert recognizer.dictation_key_up(10.4) == [TextComposerRequested()]


def test_a_hold_that_listened_is_never_half_of_a_double_tap():
    recognizer = shared()
    recognizer.dictation_key_down(10.0)
    recognizer.dictation_key_up(10.1)  # a tap
    recognizer.dictation_key_down(10.2)
    recognizer.hold_elapsed(2)  # listened
    assert TextComposerRequested() not in recognizer.dictation_key_up(10.34)


def test_ctrl_and_alt_together_ask_a_question():
    recognizer = shared()
    recognizer.ask_key_down()
    recognizer.dictation_key_down(10.0)
    assert recognizer.hold_elapsed(1) == [DictationPressed(ask=True)]
    recognizer.ask_key_up()
    assert recognizer.dictation_key_up(12.0) == [DictationReleased(was_tap=False)]


def test_alt_joining_a_take_turns_it_into_a_question():
    recognizer = shared()
    recognizer.dictation_key_down(10.0)
    recognizer.hold_elapsed(1)
    assert recognizer.ask_key_down() == [AskJoined()]
    assert recognizer.ask_key_down() == []  # auto-repeat


def test_ctrl_alt_t_is_a_shortcut_not_a_question():
    recognizer = shared()
    recognizer.dictation_key_down(10.0)
    recognizer.ask_key_down()
    recognizer.other_key_down(is_escape=False)  # the T
    assert recognizer.hold_elapsed(1) == []
    assert recognizer.dictation_key_up(10.2) == []


def test_alt_between_two_taps_breaks_the_double_tap():
    recognizer = shared()
    recognizer.dictation_key_down(10.0)
    recognizer.dictation_key_up(10.1)
    recognizer.ask_key_down()
    recognizer.dictation_key_down(10.3)
    assert recognizer.dictation_key_up(10.4) == []


def test_right_ctrl_still_listens_at_once_and_alt_still_asks():
    recognizer = DictationGestureRecognizer()
    recognizer.ask_key_down()
    assert recognizer.dictation_key_down(10.0) == [DictationPressed(ask=True)]
