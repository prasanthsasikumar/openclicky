"""More than one monitor, on fake layouts (this machine's X server can only simulate one): which
picture comes first, how a point on screen N finds its way back to the desktop, what is sent."""

import json

from openclicky_linux.ask import Conversation, Screenshot, request_for
from openclicky_linux.companion import AskController, AskHost
from openclicky_linux.config import LinuxSettings
from openclicky_linux.pointing import MonitorArea, monitors_mouse_first, point_on_screens

LEFT = MonitorArea(0, 0, 1920, 1080)
RIGHT = MonitorArea(1920, 0, 2560, 1440)
BELOW = MonitorArea(0, 1080, 1920, 1080)


def picture(area: MonitorArea, scale: float, data: bytes = b"png") -> Screenshot:
    return Screenshot(png_bytes=data, width=round(area.width * scale), height=round(area.height * scale), scale=scale,
                      origin_x=area.x, origin_y=area.y)


def test_the_monitor_with_the_mouse_comes_first_then_left_to_right():
    assert monitors_mouse_first([LEFT, RIGHT], 2500, 300) == [RIGHT, LEFT]
    assert monitors_mouse_first([RIGHT, LEFT], 100, 100) == [LEFT, RIGHT]
    assert monitors_mouse_first([BELOW, LEFT, RIGHT], 50, 1500) == [BELOW, LEFT, RIGHT]


def test_a_mouse_off_every_monitor_still_gets_every_picture():
    # A gap in an uneven layout: no "primary focus", but nothing is dropped.
    assert monitors_mouse_first([RIGHT, LEFT], 1900, 1300) == [LEFT, RIGHT]


def test_a_point_on_screen_two_lands_on_that_monitor():
    # The mouse is on the right monitor, so it is screen 1; the left one is screen 2.
    screens = [picture(RIGHT, 1568 / 2560), picture(LEFT, 1568 / 1920)]
    assert point_on_screens(784, 441, None, screens) == (1920 + 1280, 720)   # the middle of the right one
    assert point_on_screens(784, 441, 1, screens) == (1920 + 1280, 720)
    assert point_on_screens(784, 441, 2, screens) == (960, 540)              # the middle of the left one
    assert point_on_screens(10, 10, 7, screens)[0] >= 1920                   # no such screen: the first


def test_a_point_is_kept_on_its_own_monitor():
    screens = [picture(LEFT, 1568 / 1920), picture(RIGHT, 1568 / 2560)]
    # Past the left monitor's right edge in its own picture: stays on the left monitor.
    assert point_on_screens(5000, 5000, 1, screens) == (1919, 1079)


def test_every_monitor_goes_with_the_question_labelled_and_the_question_last():
    screens = [picture(RIGHT, 0.6, b"right"), picture(LEFT, 0.8, b"left")]
    body = json.loads(request_for("where is the terminal?", screens, Conversation(), LinuxSettings(anthropic_api_key="k")).request.data)
    content = body["messages"][-1]["content"]
    assert [block["type"] for block in content] == ["image", "text", "image", "text"]
    assert content[1]["text"] == "screen 1 (primary focus, 1536x864 pixels)"
    assert content[3]["text"] == "screen 2 (1536x864 pixels)\n\nwhere is the terminal?"
    assert ":screenN" in body["system"]


def test_an_account_sends_at_most_two_pictures():
    screens = [picture(LEFT, 0.5), picture(RIGHT, 0.5), picture(BELOW, 0.5)]
    body = json.loads(request_for("hi", screens, Conversation(), LinuxSettings(account_token="tok")).request.data)
    assert sum(block["type"] == "image" for block in body["messages"][-1]["content"]) == 2
    own_key = json.loads(request_for("hi", screens, Conversation(), LinuxSettings(openrouter_api_key="k")).request.data)
    assert sum(block["type"] == "image_url" for block in own_key["messages"][-1]["content"]) == 3


def test_the_pointer_flies_to_the_monitor_the_answer_names():
    screens = [picture(RIGHT, 0.5), picture(LEFT, 0.5)]
    flights = []

    class Pointer:
        def hide_for_screenshot(self): pass
        def show_after_screenshot(self): pass
        def show_caption(self, text, hold_seconds): pass
        def dismiss_caption(self): pass
        def fly_to(self, screen_x, screen_y): flights.append((screen_x, screen_y))
        def return_to_mouse(self): pass

    timers, background = [], []
    asking = AskController(AskHost(
        show_composer=lambda: None, hide_composer=lambda: None, capture_screen=lambda: screens,
        ask=lambda question, pictures, conversation: "[POINT:100,50:terminal:screen2] that's your terminal, on the left.",
        run_in_background=lambda work, done: background.append((work, done)),
        schedule=lambda seconds, action: timers.append(action)), Pointer())
    asking.submit("where is my terminal?")
    timers.pop()()
    work, done = background.pop()
    done(work())
    assert flights == [(200, 100)]  # screen 2 is the left monitor, at half scale


def test_pictures_are_the_size_the_model_really_looks_at():
    import gi
    gi.require_version("Gdk", "3.0")
    from openclicky_linux.screen import picture_scale
    # The Mac's 1280 on the longest side: 1680x1050 → 1280x800 (1.02 megapixels).
    assert round(1680 * picture_scale(1680, 1050)) == 1280 and round(1050 * picture_scale(1680, 1050)) == 800
    # Never over ~1.15 megapixels, which the model would shrink again (an ultra-wide 3440x1440).
    scale = picture_scale(3440, 1440)
    assert round(3440 * scale) * round(1440 * scale) <= 1_150_000 * 1.01
    # Never enlarged.
    assert picture_scale(1024, 768) == 1.0
