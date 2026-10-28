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

    def show_caption(self, text, hold_seconds, continue_typing=False):
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
        self.extras: list = []
        self.stream = False

    def ask(self, question, screenshot, conversation, extras=None, on_text=lambda _piece: None):
        self.asked.append(question)
        self.extras.append(extras)
        if isinstance(self.answer, Exception):
            raise self.answer
        # Streamed a few letters at a time when the test asks for it, as the vendors do.
        for start in range(0, len(self.answer), 7) if self.stream else ():
            on_text(self.answer[start:start + 7])
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
    desktop.answer = AskError("i can answer that once you add your email for a free account, in settings, account.")
    asking.submit("hi")
    desktop.run_everything()
    assert pointer.calls[-1] == ("caption", "i can answer that once you add your email for a free account, in settings, account.", True)
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

    def ask(self, question, screenshot, conversation, **_streaming):
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
    asking.host.speak_piece = spoken.append
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
    asking.host.speak_piece = spoken.append
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


def test_a_streamed_answer_flies_as_soon_as_its_tag_is_complete_and_speaks_its_first_sentence_alone():
    desktop, pointer, asking = make()
    desktop.stream = True
    spoken, ends = [], []
    asking.host.speak_piece = spoken.append
    asking.host.speak_end = lambda: ends.append(True)
    flown_while_streaming = []
    original = asking._stream_piece

    def watch(question_id, piece, screenshot):
        original(question_id, piece, screenshot)
        flown_while_streaming.append(any(call[0] == "fly" for call in pointer.calls))
    asking._stream_piece = watch
    desktop.answer = ("[POINT:392,245:save button] click save, top left. it keeps everything you typed since the last time, "
                      "so nothing is lost if the app closes.")
    asking.submit("where do i save?")
    desktop.run_everything()
    # Flown long before the last piece arrived.
    assert flown_while_streaming.index(True) < len(flown_while_streaming) // 2
    assert [call for call in pointer.calls if call[0] == "fly"] == [("fly", 784, 490)]
    assert spoken[0] == "click save, top left."
    assert " ".join(spoken) == desktop.answer.split("] ", 1)[1]
    assert ends == [True]
    # The caption grew a sentence at a time and ends with the whole answer, held to be read.
    captions = [call for call in pointer.calls if call[0] == "caption"]
    assert captions[1] == ("caption", "click save, top left.", False)
    assert captions[-1] == ("caption", desktop.answer.split("] ", 1)[1], True)


# MARK: the person stays in charge (ask_lane.py)

def test_with_the_pause_on_the_screen_waits_and_esc_calls_it_off_before_anything_is_captured():
    from openclicky_linux import ask_lane
    desktop, pointer, asking = make()
    asking.host.pause_before_sending = lambda: True
    captured = []
    asking.host.capture_screen = lambda: (captured.append(True), desktop.screenshot)[1]
    asking.submit("where do i save?")
    assert pointer.calls == [("caption", ask_lane.SEND_PAUSE_CAPTION, False)]
    assert not pointer.hidden
    assert asking.escape()
    desktop.run_everything()
    assert captured == [] and desktop.asked == []
    # Without esc it goes on after the pause.
    asking.submit("where do i save?")
    desktop.run_everything()
    assert captured == [True] and desktop.asked == ["where do i save?"]


def test_an_unsure_answer_never_points_and_ends_the_guide():
    desktop, pointer, asking = make()
    desktop.stream = True
    desktop.answer = "[UNSURE] [POINT:392,245:save] i can't spot the save button. look for a disk icon, or tell me what you see. [NEXT]"
    asking.submit("where do i save?")
    desktop.run_everything()
    assert not any(call[0] == "fly" for call in pointer.calls)
    assert not asking.is_waiting_for_step
    assert pointer.calls[-1] == ("caption", "i can't spot the save button. look for a disk icon, or tell me what you see.", True)
    assert asking.conversation.exchanges[-1][1] == "i can't spot the save button. look for a disk icon, or tell me what you see."


def test_the_question_says_where_the_mouse_is_with_a_close_up_and_when_it_was_just_asked():
    from openclicky_linux import ask_lane
    desktop, pointer, asking = make()
    asking.host.mouse_position = lambda: (200, 100)
    cut = []
    asking.host.cut_close_up = lambda screen, at: (cut.append(at), b"close-up")[1]
    asking.submit("what is this?")
    desktop.run_everything()
    extras = desktop.extras[-1]
    # Half-size screenshot: the mouse is at half its desktop place in it.
    assert cut == [ask_lane.PointerInCapture(100, 50, 1)]
    assert extras.prompt_lines == (ask_lane.PointerInCapture(100, 50, 1).prompt_line,)
    assert extras.images == ((b"close-up", ask_lane.close_up_label()),)
    asking.submit("what's this??")
    desktop.run_everything()
    assert desktop.extras[-1].prompt_lines[-1] == ask_lane.REPEATED_QUESTION_LINE


def test_a_point_on_a_password_prompt_is_not_shown():
    from openclicky_linux.ask_lane import OnScreenWindow
    desktop, pointer, asking = make()
    asking.host.prompt_windows = lambda: [OnScreenWindow("gcr-prompter", 700, 450, 300, 200)]
    desktop.answer = "[POINT:392,245:unlock] type your password here."
    asking.submit("how do i unlock this?")
    desktop.run_everything()
    assert not any(call[0] == "fly" for call in pointer.calls)
    assert ("caption", "type your password here.", True) in pointer.calls


def test_a_click_well_away_from_the_point_says_so_and_asks_the_model_to_check():
    from openclicky_linux import ask_lane
    desktop, pointer, asking, timers = guided_setup([
        "[POINT:100,40:settings] open settings first. [NEXT]",
        "[POINT:100,40:settings] that was the wrong spot; settings is here. [NEXT]",
        "[POINT:none] done.",
    ])
    asking.submit("how do i turn on dark mode?")
    run_background(desktop, timers, only_delay=0.15)
    # The point was (200, 80) on the desktop; the click lands 300 px to the right.
    asking.step_input(guided.CLICK, (500, 80))
    run_background(desktop, timers, only_delay=guided.SETTLE_SECONDS_AFTER_CLICK)
    assert ("caption", ask_lane.OFF_TARGET_CAPTION, False) in pointer.calls
    assert desktop.asked[1] == guided.next_step_request("how do i turn on dark mode?", 300)
    # A click near the point is on target.
    asking.step_input(guided.CLICK, (210, 85))
    run_background(desktop, timers, only_delay=guided.SETTLE_SECONDS_AFTER_CLICK)
    assert desktop.asked[2] == guided.next_step_request("how do i turn on dark mode?")


# MARK: writing in place (writing.py)

def test_a_question_about_a_text_box_carries_its_words_and_offers_the_rewrite_tool():
    from openclicky_linux import writing
    from openclicky_linux.accessibility import FocusedField
    desktop, pointer, asking = make()
    field = FocusedField("Thunderbird", "Write: hello", True, False, "hey can u send it", None, 17)
    asking.host.read_text_context = lambda: writing.TextContext(readable=True, target=writing.target_in(field))
    written = []
    asking.host.write_in_place = lambda target, text: written.append((target.text, text)) or writing.REPLACED
    asking.open_composer()
    asking.submit("rephrase this, more polite")
    desktop.run_everything()
    extras = desktop.extras[-1]
    assert extras.prompt_lines[0].startswith("The text box the user's cursor is in in Thunderbird holds this text: «hey can u send it».")
    assert extras.instruction == writing.PROMPT_INSTRUCTION and extras.tools[0]["name"] == "write_in_place"
    assert extras.write_in_place("Could you send it, please?") == writing.REPLACED
    assert written == [("hey can u send it", "Could you send it, please?")]


def test_without_a_box_to_read_the_model_is_told_not_to_read_a_rewrite_out():
    from openclicky_linux import writing
    desktop, pointer, asking = make()
    asking.host.read_text_context = lambda: writing.TextContext(readable=False)
    asking.submit("rephrase this", asked_out_loud=True)
    desktop.run_everything()
    assert desktop.extras[-1].instruction == writing.NO_ACCESSIBILITY_PROMPT_INSTRUCTION and desktop.extras[-1].tools == ()
    asking.host.read_text_context = lambda: writing.TextContext(readable=True, selected_text="E = mc²")
    asking.submit("what is this?", asked_out_loud=True)
    desktop.run_everything()
    assert desktop.extras[-1].instruction == writing.NO_TARGET_PROMPT_INSTRUCTION
    assert desktop.extras[-1].prompt_lines[0].startswith("The user has selected (highlighted) this text on screen: «E = mc²».")


def test_a_target_is_never_a_password_box_and_must_still_be_there():
    from openclicky_linux import writing
    from openclicky_linux.accessibility import FocusedField
    password = FocusedField("Firefox", None, True, True, None, None, None)
    assert writing.target_in(password) is None
    highlighted = FocusedField("gedit", None, True, False, "one two three", (4, 7), 7)
    target = writing.target_in(highlighted)
    assert (target.scope, target.text) == (writing.SELECTION, "two")
    assert target.still_matches(highlighted)
    assert not target.still_matches(FocusedField("gedit", None, True, False, "one two three", None, 7))
    assert writing.keeping_surrounding_space("\nold text \n", " new text ") == "\nnew text \n"
