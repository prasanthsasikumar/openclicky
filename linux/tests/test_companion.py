"""The ask controller with a fake pointer and desktop: timers and background work run when the test says."""

from openclicky_linux.ask import AskError, Screenshot
from openclicky_linux.companion import THINKING_CAPTION, AskController, AskHost


class FakePointer:
    def __init__(self) -> None:
        self.calls: list[tuple] = []
        self.hidden = False

    def hide_for_screenshot(self):
        self.hidden = True

    def show_after_screenshot(self):
        self.hidden = False

    def show_caption(self, text, hold_seconds):
        self.calls.append(("caption", text, hold_seconds is not None))

    def dismiss_caption(self):
        self.calls.append(("dismiss",))

    def fly_to(self, screen_x, screen_y):
        self.calls.append(("fly", screen_x, screen_y))

    def return_to_mouse(self):
        self.calls.append(("home",))


class FakeDesktop:
    def __init__(self) -> None:
        self.composer_open = False
        self.answer: object = "[POINT:none] hello"
        self.asked: list[str] = []
        self.timers: list = []
        self.background: list = []
        self.screenshot = Screenshot(b"png", 784, 490, 0.5)

    def ask(self, question, screenshot, conversation):
        self.asked.append(question)
        if isinstance(self.answer, Exception):
            raise self.answer
        return self.answer

    def host(self) -> AskHost:
        return AskHost(
            show_composer=lambda: setattr(self, "composer_open", True),
            hide_composer=lambda: setattr(self, "composer_open", False),
            capture_screen=lambda: self.screenshot,
            ask=self.ask,
            run_in_background=lambda work, done: self.background.append((work, done)),
            schedule=lambda seconds, action: self.timers.append(action),
        )

    def run_everything(self) -> None:
        while self.timers or self.background:
            while self.timers:
                self.timers.pop(0)()
            while self.background:
                work, done = self.background.pop(0)
                try:
                    outcome = work()
                except Exception as failure:  # noqa: BLE001
                    outcome = failure
                done(outcome)


def make():
    desktop, pointer = FakeDesktop(), FakePointer()
    return desktop, pointer, AskController(desktop.host(), pointer)


def test_a_question_shows_thinking_then_flies_to_the_point_and_shows_the_answer():
    desktop, pointer, asking = make()
    desktop.answer = "[POINT:392,245:save button] click save, top left."
    asking.open_composer()
    assert desktop.composer_open
    asking.submit("  where do i save?  ")
    assert not desktop.composer_open
    # Out of sight until the screenshot is taken, so the model never sees the pointer or a caption.
    assert pointer.hidden and pointer.calls == []
    seen_while_capturing = []
    desktop.host_capture = desktop.screenshot
    asking.host.capture_screen = lambda: (seen_while_capturing.append(pointer.hidden), desktop.screenshot)[1]
    desktop.run_everything()
    assert seen_while_capturing == [True]
    assert not pointer.hidden
    assert pointer.calls[0] == ("caption", THINKING_CAPTION, False)
    assert desktop.asked == ["where do i save?"]
    # The screenshot was half size: the point doubles on the screen.
    assert pointer.calls[1:] == [("fly", 784, 490), ("caption", "click save, top left.", True)]
    # Remembered without the tag, as on the Mac.
    assert asking.conversation.exchanges == [("where do i save?", "click save, top left.")]


def test_an_answer_without_a_point_stays_by_the_mouse():
    desktop, pointer, asking = make()
    asking.submit("what is html?")
    desktop.run_everything()
    assert ("caption", "hello", True) in pointer.calls
    assert not any(call[0] == "fly" for call in pointer.calls)


def test_an_empty_question_just_closes_the_box():
    desktop, pointer, asking = make()
    asking.open_composer()
    asking.submit("   ")
    assert not desktop.composer_open
    assert pointer.calls == []
    assert desktop.timers == []


def test_a_failure_is_said_by_the_pointer():
    desktop, pointer, asking = make()
    desktop.answer = AskError("asking needs an anthropic or openai key, or an openclicky account. add one in settings.")
    asking.submit("hi")
    desktop.run_everything()
    assert pointer.calls[-1] == ("caption", "asking needs an anthropic or openai key, or an openclicky account. add one in settings.", True)
    assert not asking.is_answering


def test_esc_while_thinking_drops_the_answer():
    desktop, pointer, asking = make()
    desktop.answer = "[POINT:10,10:x] late answer"
    asking.submit("hi")
    assert asking.escape()
    desktop.run_everything()
    assert not any(call[0] == "fly" or call[1:2] == ("late answer",) for call in pointer.calls)
    assert pointer.calls[-2:] == [("dismiss",), ("home",)]


def test_a_new_question_replaces_one_still_being_answered():
    desktop, pointer, asking = make()
    asking.submit("first")
    first_timer = desktop.timers.pop()
    desktop.answer = "[POINT:none] second answer"
    asking.submit("second")
    first_timer()  # the first question's screenshot moment comes late: ignored
    desktop.run_everything()
    assert desktop.asked == ["second"]
    assert pointer.calls[-1] == ("caption", "second answer", True)


def test_esc_with_nothing_showing_does_nothing():
    _desktop, pointer, asking = make()
    assert not asking.escape()
    assert pointer.calls == []


def test_when_the_caption_runs_out_the_pointer_goes_home():
    desktop, pointer, asking = make()
    asking.submit("hi")
    desktop.run_everything()
    asking.caption_finished()
    assert pointer.calls[-1] == ("home",)
    assert not asking.is_showing_answer


# MARK: step by step guidance

from openclicky_linux import guided  # noqa: E402


class ScriptedModel:
    """Answers in turn, recording each question."""

    def __init__(self, desktop: FakeDesktop, answers: list[str]) -> None:
        self.answers = list(answers)
        desktop.ask = self.ask  # type: ignore[method-assign]
        self.desktop = desktop

    def ask(self, question, screenshot, conversation):
        self.desktop.asked.append(question)
        return self.answers.pop(0)


def guided_setup(answers):
    desktop, pointer = FakeDesktop(), FakePointer()
    ScriptedModel(desktop, answers)
    timers: list = []
    host = desktop.host()
    # Timers keep their delay here, so a test can tell the settle wait from the idle timeout.
    host.schedule = lambda seconds, action: timers.append((seconds, action))
    asking = AskController(host, pointer)
    return desktop, pointer, asking, timers


def run_background(desktop, timers, only_delay=None):
    """Runs the timers (all, or only those with `only_delay`) and the background work they start."""
    while True:
        ready = [timer for timer in timers if only_delay is None or timer[0] == only_delay or timer[0] == 0.15]
        if not ready and not desktop.background:
            return
        for timer in ready:
            timers.remove(timer)
            timer[1]()
        while desktop.background:
            work, done = desktop.background.pop(0)
            done(work())


def test_a_guide_waits_for_a_click_then_asks_for_the_next_step_with_the_goal():
    desktop, pointer, asking, timers = guided_setup([
        "[POINT:100,40:settings] open settings first. [NEXT]",
        "[POINT:300,200:dark mode] now tick dark mode. [ next ]",
        "[POINT:none] that's it, dark mode is on.",
    ])
    asking.submit("how do i turn on dark mode?")
    run_background(desktop, timers, only_delay=0.15)
    assert asking.is_waiting_for_step
    # The marker is never shown.
    assert pointer.calls[-1] == ("caption", "open settings first.", True)
    # Typing alone is not a step.
    asking.step_input(guided.OTHER_KEY)
    assert not [timer for timer in timers if timer[0] != guided.IDLE_SECONDS_BEFORE_GIVING_UP]
    asking.step_input(guided.CLICK)
    run_background(desktop, timers, only_delay=guided.SETTLE_SECONDS_AFTER_CLICK)
    assert desktop.asked[1] == guided.next_step_request("how do i turn on dark mode?")
    assert pointer.calls[-1] == ("caption", "now tick dark mode.", True)
    asking.step_input(guided.CLICK)
    run_background(desktop, timers, only_delay=guided.SETTLE_SECONDS_AFTER_CLICK)
    assert pointer.calls[-1] == ("caption", "that's it, dark mode is on.", True)
    # The last step has no marker: the guide is over, clicks do nothing now.
    assert not asking.is_waiting_for_step
    asking.step_input(guided.CLICK)
    run_background(desktop, timers, only_delay=guided.SETTLE_SECONDS_AFTER_CLICK)
    assert len(desktop.asked) == 3


def test_a_second_quick_click_is_the_same_step():
    desktop, _pointer, asking, timers = guided_setup(["open the menu. [NEXT]", "done."])
    asking.submit("how?")
    run_background(desktop, timers, only_delay=0.15)
    asking.step_input(guided.CLICK)
    asking.step_input(guided.CLICK)  # the menu, then its item
    run_background(desktop, timers, only_delay=guided.SETTLE_SECONDS_AFTER_CLICK)
    assert len(desktop.asked) == 2


def test_typing_after_a_click_waits_for_quiet():
    desktop, _pointer, asking, timers = guided_setup(["click the address field and type it. [NEXT]", "done."])
    asking.submit("how?")
    run_background(desktop, timers, only_delay=0.15)
    asking.step_input(guided.CLICK)
    asking.step_input(guided.OTHER_KEY)
    # Only the typing wait counts now; the click's shorter wait is superseded.
    run_background(desktop, timers, only_delay=guided.SETTLE_SECONDS_AFTER_CLICK)
    assert len(desktop.asked) == 1
    run_background(desktop, timers, only_delay=guided.SETTLE_SECONDS_WHILE_TYPING)
    assert len(desktop.asked) == 2


def test_return_or_tab_finishes_a_typing_step():
    desktop, _pointer, asking, timers = guided_setup(["type your name, then press return. [NEXT]", "done."])
    asking.submit("how?")
    run_background(desktop, timers, only_delay=0.15)
    asking.step_input(guided.STEP_KEY)
    run_background(desktop, timers, only_delay=guided.SETTLE_SECONDS_AFTER_STEP_KEY)
    assert len(desktop.asked) == 2


def test_esc_ends_the_guide():
    desktop, pointer, asking, timers = guided_setup(["open settings. [NEXT]", "never asked"])
    asking.submit("how?")
    run_background(desktop, timers, only_delay=0.15)
    asking.caption_finished()  # the caption may be gone already; the guide still waits
    assert asking.is_waiting_for_step
    assert asking.escape()
    assert not asking.is_waiting_for_step
    asking.step_input(guided.CLICK)
    run_background(desktop, timers)
    assert len(desktop.asked) == 1


def test_esc_as_a_step_input_ends_the_guide_too():
    desktop, _pointer, asking, timers = guided_setup(["open settings. [NEXT]"])
    asking.submit("how?")
    run_background(desktop, timers, only_delay=0.15)
    asking.step_input(guided.ESCAPE)
    assert not asking.is_waiting_for_step


def test_two_minutes_of_nothing_ends_the_guide():
    desktop, _pointer, asking, timers = guided_setup(["open settings. [NEXT]"])
    asking.submit("how?")
    run_background(desktop, timers, only_delay=0.15)
    run_background(desktop, timers, only_delay=guided.IDLE_SECONDS_BEFORE_GIVING_UP)
    assert not asking.is_waiting_for_step


def test_a_new_question_ends_the_guide_and_starts_afresh():
    desktop, _pointer, asking, timers = guided_setup(["open settings. [NEXT]", "paris."])
    asking.submit("how do i turn on dark mode?")
    run_background(desktop, timers, only_delay=0.15)
    asking.submit("what is the capital of france?")
    run_background(desktop, timers, only_delay=0.15)
    assert not asking.is_waiting_for_step
    assert asking.guide_goal == "what is the capital of france?"


def test_a_runaway_guide_stops_after_fifteen_steps():
    desktop, _pointer, asking, timers = guided_setup([f"step {number}. [NEXT]" for number in range(1, 30)])
    asking.submit("how?")
    run_background(desktop, timers, only_delay=0.15)
    for _ in range(40):
        asking.step_input(guided.CLICK)
        run_background(desktop, timers, only_delay=guided.SETTLE_SECONDS_AFTER_CLICK)
    assert len(desktop.asked) == guided.MAXIMUM_STEPS
    assert not asking.is_waiting_for_step


# The Mac's GuidedStepsTests, ported.

def test_the_marker_is_found_however_it_is_written():
    assert guided.contains_marker("click the paperclip. [NEXT]")
    assert guided.contains_marker("click the paperclip. [ next ]")
    assert not guided.contains_marker("that's the next button.")
    assert guided.removing_markers("[POINT:1,2:clip] click the paperclip. [NEXT]\n") == "[POINT:1,2:clip] click the paperclip."


def test_a_trailing_point_tag_before_the_marker_still_points():
    from openclicky_linux.pointing import parse_pointing
    result = parse_pointing(guided.removing_markers("click the paperclip at the top [POINT:612,88:attach] [NEXT]"))
    assert (result.x, result.y, result.text) == (612, 88, "click the paperclip at the top")


def test_click_return_or_tab_finish_a_step_typing_alone_does_not_esc_ends():
    assert guided.reaction(guided.CLICK, False) == guided.Reaction("ask for next step", guided.SETTLE_SECONDS_AFTER_CLICK)
    assert guided.reaction(guided.STEP_KEY, False) == guided.Reaction("ask for next step", guided.SETTLE_SECONDS_AFTER_STEP_KEY)
    assert guided.reaction(guided.OTHER_KEY, False) == guided.Reaction("ignore")
    assert guided.reaction(guided.OTHER_KEY, True) == guided.Reaction("postpone", guided.SETTLE_SECONDS_WHILE_TYPING)
    assert guided.reaction(guided.ESCAPE, True) == guided.Reaction("end guide")


def test_the_next_step_request_repeats_the_goal():
    request = guided.next_step_request("how do i attach a file")
    assert request.startswith(guided.NEXT_STEP_REQUEST_OPENING)
    assert "how do i attach a file" in request
    assert "—" not in request


def test_the_ask_prompt_explains_the_marker():
    from openclicky_linux.ask import SYSTEM_PROMPT
    assert "[NEXT]" in SYSTEM_PROMPT and guided.NEXT_STEP_REQUEST_OPENING in SYSTEM_PROMPT


def test_an_answer_that_is_only_a_tag_never_leaves_an_empty_turn_in_the_conversation():
    desktop, pointer, asking = make()
    desktop.answer = "[POINT:10,20:save] [NEXT]"
    asking.submit("where do i save?")
    desktop.run_everything()
    question, remembered = asking.conversation.exchanges[0]
    assert remembered.strip(), "an empty assistant turn makes Anthropic refuse every later question"
    assert remembered == "(pointed at save)"


# MARK: spoken answers

def test_an_answer_is_spoken_and_esc_or_a_new_question_stops_it():
    desktop, pointer, asking = make()
    spoken, stops, prepared = [], [], []
    asking.host.speak = spoken.append
    asking.host.stop_speaking = lambda: stops.append(True)
    asking.host.prepare_voice = lambda: prepared.append(True)
    desktop.answer = "[POINT:392,245:save] click save, top left."
    asking.submit("where do i save?")
    assert prepared == [True]  # the voice loads while the answer is thought about
    desktop.run_everything()
    assert spoken == ["click save, top left."]  # without the tag
    stops_before = len(stops)
    asking.escape()
    assert len(stops) == stops_before + 1
    asking.submit("and then?")
    assert len(stops) == stops_before + 2


def test_a_failure_or_an_empty_answer_is_not_spoken():
    desktop, pointer, asking = make()
    spoken = []
    asking.host.speak = spoken.append
    desktop.answer = "[POINT:10,20:save]"
    asking.submit("where?")
    desktop.run_everything()
    from openclicky_linux.ask import AskError
    desktop.answer = AskError("couldn't reach openrouter.")
    asking.submit("again?")
    desktop.run_everything()
    assert spoken == []


def test_on_wayland_the_screenshot_is_taken_off_the_main_thread_with_the_pointer_still_hidden():
    desktop, pointer, asking = make()
    desktop.answer = "[POINT:392,245:save button] click save."
    hidden_while_the_desktop_took_it = []

    def take_it_in_the_background():
        hidden_while_the_desktop_took_it.append(pointer.hidden)
        return [desktop.screenshot]
    # Wayland's capture hands back a function, which goes to the background like the question does.
    asking.host.capture_screen = lambda: take_it_in_the_background
    asking.submit("where do i save?")
    desktop.run_everything()
    assert hidden_while_the_desktop_took_it == [True]
    assert pointer.calls[0] == ("caption", THINKING_CAPTION, False)
    assert ("fly", 784, 490) in pointer.calls


def test_on_wayland_a_refused_screenshot_says_so_and_asks_nothing():
    desktop, pointer, asking = make()

    def refused():
        raise RuntimeError("the desktop refused the screenshot")
    asking.host.capture_screen = lambda: refused
    asking.submit("where do i save?")
    desktop.run_everything()
    assert desktop.asked == []
    assert not pointer.hidden
    assert pointer.calls == [("caption", "couldn't see the screen: the desktop refused the screenshot", True)]


def test_a_screenshot_finished_after_esc_is_dropped():
    desktop, pointer, asking = make()
    asking.host.capture_screen = lambda: (lambda: [desktop.screenshot])
    asking.submit("where do i save?")
    while desktop.timers:
        desktop.timers.pop(0)()
    asking.escape()
    desktop.run_everything()
    assert desktop.asked == []
