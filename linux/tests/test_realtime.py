"""Fast voice with a fake socket: the session, the turns, the tools, pointing and the hands-free gesture."""

import json
import threading
import time

from openclicky_linux import realtime
from openclicky_linux.config import LinuxSettings
from openclicky_linux.gestures import DictationGestureRecognizer, DictationPressed, HandsFreeToggleRequested
from openclicky_linux.realtime import RealtimeClient, ScreenContext, secret_request, session_update


class FakeSocket:
    def __init__(self):
        self.sent = []
        self.closed = False
        self.inbox = [json.dumps({"type": "session.created"})]
        self.ready = threading.Condition()

    def send_text(self, text):
        self.sent.append(json.loads(text))

    def receive(self):
        from openclicky_linux.websocket import Message
        with self.ready:
            self.ready.wait_for(lambda: self.inbox or self.closed, timeout=0.5)
            if not self.inbox:
                return None
            return Message(True, self.inbox.pop(0).encode())

    def close(self):
        self.closed = True


def client(monkeypatch, **options):
    socket = FakeSocket()
    monkeypatch.setattr(realtime.urllib.request, "urlopen", lambda request, timeout=0: _Answer({"value": "ek_1", "session": {"model": "gpt-realtime"}}))
    pointed = []
    made = RealtimeClient(lambda: LinuxSettings(openai_api_key="sk", language_code="en"),
                          lambda: ScreenContext(b"png", 1280, 800, "the screen", origin_x=1920, scale=0.5),
                          on_point_at=lambda x, y, label: pointed.append((x, y, label)), open_socket=lambda url, headers: socket, **options)
    made._play = lambda audio: None
    made._start_microphone = lambda: None
    return made, socket, pointed


class _Answer:
    def __init__(self, value):
        self.value = value

    def read(self):
        return json.dumps(self.value).encode()

    def __enter__(self):
        return self

    def __exit__(self, *_):
        return False


def test_the_session_asks_for_pcm_the_tools_and_hands_free_turns():
    update = session_update(hands_free=True, instructions="be brief", language="en")["session"]
    assert update["audio"]["input"]["format"] == {"type": "audio/pcm", "rate": 24000}
    assert update["audio"]["input"]["turn_detection"]["create_response"] is False
    assert update["audio"]["input"]["transcription"]["language"] == "en"
    names = [tool["name"] for tool in update["tools"]]
    assert names[:2] == ["send_to_agent", "point_at"] and "open_app" in names and "reveal_in_files" in names
    assert session_update(False, "x", None)["session"]["audio"]["input"]["turn_detection"] is None
    own = secret_request(LinuxSettings(openai_api_key="sk", account_token="t"), "x")
    assert own.full_url == realtime.OPENAI_SECRET_URL and json.loads(own.data)["session"]["model"] == "gpt-realtime"
    account = secret_request(LinuxSettings(account_token="t", backend_url="https://backend"), "x")
    assert account.full_url == "https://backend/agent/realtime/session" and account.get_header("Authorization") == "Bearer t"


def test_a_push_to_talk_turn_sends_the_screen_then_commits_and_asks(monkeypatch):
    made, socket, _pointed = client(monkeypatch)
    monkeypatch.setattr(realtime, "RELEASE_TAIL_SECONDS", 0)
    made.begin_push_to_talk()
    assert socket.sent[0]["type"] == "session.update" and socket.sent[-1] == {"type": "input_audio_buffer.clear"}
    made._turn_bytes = realtime.MINIMUM_TURN_BYTES
    made.end_push_to_talk()
    kinds = [event["type"] for event in socket.sent]
    assert kinds[-3:] == ["conversation.item.create", "input_audio_buffer.commit", "response.create"]
    image = socket.sent[-3]["item"]["content"][1]
    assert image["type"] == "input_image" and image["image_url"].startswith("data:image/png;base64,")
    # Released at once: no audio, no turn.
    made.begin_push_to_talk()
    made.end_push_to_talk()
    assert socket.sent[-1] == {"type": "input_audio_buffer.clear"} and made.state == "idle"


def test_point_at_lands_on_the_desktop_and_tools_ask_for_the_follow_up(monkeypatch):
    made, socket, pointed = client(monkeypatch, resolve_point=lambda guess, label, text, words, png, width, height, settings: ((100, 40), "snapped"),
                                   perform_action=lambda name, arguments: "opened Firefox.")
    made.connect()
    made._attach_screen()
    made.handle_event({"type": "response.function_call_arguments.done", "call_id": "c1", "name": "point_at",
                       "arguments": json.dumps({"x": 90, "y": 50, "label": "save", "text": "Save"})})
    deadline = time.time() + 2
    while not pointed and time.time() < deadline:
        time.sleep(0.02)
    # (100, 40) in a half-size picture of the monitor at x = 1920.
    assert pointed == [(2120, 80, "save")]
    assert socket.sent[-1]["item"] == {"type": "function_call_output", "call_id": "c1", "output": "pointing at save"}
    made.handle_event({"type": "response.function_call_arguments.done", "call_id": "c2", "name": "open_app", "arguments": '{"name": "Firefox"}'})
    assert socket.sent[-1]["item"]["output"] == "opened Firefox."
    made.handle_event({"type": "response.done", "response": {"status": "completed"}})
    assert socket.sent[-1] == {"type": "response.create"}


def test_hands_free_commits_ask_with_the_screen_and_speech_interrupts(monkeypatch):
    made, socket, _pointed = client(monkeypatch)
    made.set_hands_free(True)
    assert socket.sent[-1]["session"]["audio"]["input"]["turn_detection"]["type"] == "server_vad"
    made.handle_event({"type": "input_audio_buffer.committed"})
    assert [event["type"] for event in socket.sent[-2:]] == ["conversation.item.create", "response.create"]
    made._response_in_progress = True
    made.handle_event({"type": "input_audio_buffer.speech_started"})
    assert socket.sent[-1] == {"type": "response.cancel"}


def test_the_dictation_key_tapped_twice_with_left_alt_toggles_hands_free():
    recognizer = DictationGestureRecognizer()
    recognizer.ask_key_down()
    assert recognizer.dictation_key_down(10.0) == [DictationPressed(ask=True)]
    recognizer.dictation_key_up(10.1)
    recognizer.dictation_key_down(10.3)
    assert HandsFreeToggleRequested() in recognizer.dictation_key_up(10.4)
    shared = DictationGestureRecognizer(shares_key_with_the_ask_box=True)
    shared.ask_key_down()
    shared.dictation_key_down(20.0)
    shared.dictation_key_up(20.1)
    shared.dictation_key_down(20.3)
    assert shared.dictation_key_up(20.4) == [HandsFreeToggleRequested()]
