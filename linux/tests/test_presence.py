"""The Mac app's PointerPresenceTests, ported case for case, plus the Linux-only "whenAnswering"."""

from openclicky_linux.presence import MouseShakeDetector, PointerPresenceTracker

FRAME = 1.0 / 30.0


def sweep(tracker, start_point, end_point, start, duration):
    """A straight move over `duration` seconds, sampled at 30 Hz; returns the time at its end."""
    steps = max(1, int(duration / FRAME))
    time = start
    for step in range(1, steps + 1):
        fraction = step / steps
        time = start + duration * step / steps
        tracker.update(start_point[0] + (end_point[0] - start_point[0]) * fraction,
                       start_point[1] + (end_point[1] - start_point[1]) * fraction, time)
    return time


def rest(tracker, point, start, duration):
    return sweep(tracker, point, point, start, duration)


def shake(tracker, around, start):
    """A deliberate side-to-side shake: five 120 px swings (four reversals) in about two thirds of a second."""
    time = start
    current = around
    for swing in range(5):
        target = (around[0] + (120 if swing % 2 == 0 else -120), around[1])
        time = sweep(tracker, current, target, time, 0.13)
        current = target
    return time


def test_always_stays_awake_through_a_long_rest():
    tracker = PointerPresenceTracker("always")
    assert tracker.is_awake
    rest(tracker, (100, 100), 0, 10)
    assert tracker.is_awake


def test_while_moving_rests_away_after_the_delay_and_returns_on_movement():
    tracker = PointerPresenceTracker("whileMoving", rest_delay_seconds=3)
    assert tracker.is_awake
    time = rest(tracker, (100, 100), 0, 2.5)
    assert tracker.is_awake, "still within the rest delay"
    time = rest(tracker, (100, 100), time, 1)
    assert not tracker.is_awake, "the mouse has rested past the delay"
    sweep(tracker, (100, 100), (140, 100), time, 0.1)
    assert tracker.is_awake, "any movement brings it back"


def test_while_moving_ignores_sensor_jitter():
    tracker = PointerPresenceTracker("whileMoving", rest_delay_seconds=3)
    time = rest(tracker, (100, 100), 0, 3.5)
    assert not tracker.is_awake
    sweep(tracker, (100, 100), (100.4, 100.3), time, 0.1)
    assert not tracker.is_awake, "sub-pixel drift is not movement"


def test_on_shake_starts_hidden_and_ignores_plain_movement():
    tracker = PointerPresenceTracker("onShake", rest_delay_seconds=3)
    assert not tracker.is_awake
    sweep(tracker, (0, 0), (800, 500), 0, 1)
    assert not tracker.is_awake


def test_on_shake_wakes_on_a_shake_and_rests_away_again():
    tracker = PointerPresenceTracker("onShake", rest_delay_seconds=3)
    time = shake(tracker, (400, 300), 0)
    assert tracker.is_awake, "a shake summons the pointer"
    time = sweep(tracker, (400, 300), (600, 300), time, 1)
    assert tracker.is_awake, "it follows while the mouse keeps moving"
    rest(tracker, (600, 300), time, 3.5)
    assert not tracker.is_awake, "and rests away once the mouse stops"


def test_a_slow_back_and_forth_is_not_a_shake():
    tracker = PointerPresenceTracker("onShake", rest_delay_seconds=3)
    time = 0.0
    current = (400, 300)
    for swing in range(4):
        target = (400 + (120 if swing % 2 == 0 else -120), 300)
        time = sweep(tracker, current, target, time, 0.8)
        current = target
    assert not tracker.is_awake


def test_wake_shows_the_pointer_until_the_mouse_rests():
    tracker = PointerPresenceTracker("onShake", rest_delay_seconds=3)
    tracker.wake(10)
    assert tracker.is_awake
    rest(tracker, (100, 100), 10, 3.5)
    assert not tracker.is_awake


def test_switching_to_always_wakes_immediately():
    tracker = PointerPresenceTracker("onShake", rest_delay_seconds=3)
    assert not tracker.is_awake
    tracker.presence = "always"
    tracker.update(1, 1, 0.5)
    assert tracker.is_awake


def test_only_the_shake_mode_reports_a_shake():
    shake_mode = PointerPresenceTracker("onShake", rest_delay_seconds=3)
    always_mode = PointerPresenceTracker("always", rest_delay_seconds=3)
    shake_seen = always_seen = False
    time = 0.0
    current_x = 400.0
    for swing in range(5):
        target_x = 400 + (120 if swing % 2 == 0 else -120)
        for step in range(1, 5):
            time += 0.033
            x = current_x + (target_x - current_x) * step / 4
            shake_mode.update(x, 300, time)
            always_mode.update(x, 300, time)
            shake_seen = shake_seen or shake_mode.shook_on_last_update
            always_seen = always_seen or always_mode.shook_on_last_update
        current_x = target_x
    assert shake_seen
    assert not always_seen


def _shakes(turns):
    detector = MouseShakeDetector()
    time = 0.0
    for previous, x in zip(turns, turns[1:]):
        for step in range(1, 5):
            time += 0.033
            if detector.feed(previous + (x - previous) * step / 4, 50, time):
                return True
    return False


def test_a_quick_wobble_or_three_turns_is_not_a_shake():
    assert not _shakes([100, 220, 100, 220, 100]), "three reversals used to be enough; ordinary mousing made them"
    assert not _shakes([100, 140, 100, 140, 100, 140, 100, 140]), "a 40 px jiggle is the hand, not a shake"


def test_the_shake_detector_needs_reversals_within_the_window():
    assert _shakes([100, 220, 100, 220, 100, 220])


def test_when_answering_the_mouse_never_shows_it_but_an_answer_does_not_stay_either():
    tracker = PointerPresenceTracker("whenAnswering", rest_delay_seconds=3)
    assert not tracker.is_awake
    shake(tracker, (400, 300), 0)
    assert not tracker.is_awake
    tracker.wake(5)
    assert not tracker.is_awake


def test_an_unknown_presence_is_always():
    assert PointerPresenceTracker("sometimes").presence == "always"
