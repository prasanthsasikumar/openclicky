"""Sarvam's upload and AssemblyAI's streaming, with the network replaced."""

import io
import json
import threading
import wave

import pytest

from openclicky_linux import transcriber
from openclicky_linux.config import LinuxSettings
from openclicky_linux.recorder import SAMPLE_RATE, wav_from_pcm
from openclicky_linux.streaming import StreamedRecording, Turns, socket_url
from openclicky_linux.transcriber import TranscriptionError
from openclicky_linux.websocket import Message


def test_sarvam_gets_the_take_with_the_language_and_the_dictionary():
    settings = LinuxSettings(sarvam_api_key=" sk_x\n", dictation_engine="sarvam", language_code="hi-IN")
    route = transcriber.route_for(b"RIFFwav", settings, keyterms=("Aaditya",))
    assert route.kind == "sarvam" and route.request.full_url == transcriber.SARVAM_TRANSCRIPTION_URL
    assert route.request.get_header("Api-subscription-key") == "sk_x"
    body = route.request.data
    assert b'name="model"\r\n\r\nsaaras:v4' in body and b'name="language_code"\r\n\r\nhi-IN' in body
    assert b'name="keyterms"\r\n\r\nAaditya' in body and b"RIFFwav" in body
    unknown = transcriber.route_for(b"x", LinuxSettings(sarvam_api_key="k", dictation_engine="sarvam"))
    assert b'name="language_code"\r\n\r\nunknown' in unknown.request.data
    with pytest.raises(TranscriptionError, match="no sarvam key"):
        transcriber.engine_for(LinuxSettings(dictation_engine="sarvam"), False)


def test_a_long_take_goes_to_sarvam_in_pieces():
    pieces = transcriber.wav_pieces(wav_from_pcm(b"\x00\x00" * SAMPLE_RATE * 70), 29)
    lengths = []
    for piece in pieces:
        with wave.open(io.BytesIO(piece)) as wav_file:
            lengths.append(wav_file.getnframes() / SAMPLE_RATE)
    assert lengths == [29, 29, 12]


def test_turns_make_the_words_and_a_formatted_turn_replaces_its_draft():
    turns = Turns()
    turns.receive({"type": "Turn", "turn_order": 0, "transcript": "hello there", "end_of_turn": False})
    assert turns.text == "hello there"
    turns.receive({"type": "Turn", "turn_order": 0, "transcript": "hello there", "end_of_turn": True})
    turns.receive({"type": "Turn", "turn_order": 0, "transcript": "Hello there.", "end_of_turn": True, "turn_is_formatted": True})
    turns.receive({"type": "Turn", "turn_order": 1, "transcript": "how are", "end_of_turn": False})
    assert turns.text == "Hello there. how are"
    assert "keyterms_prompt=%5B%22Aaditya%22%5D" in socket_url("t", ("Aaditya",)) and socket_url("t").endswith("token=t")


class FakeRecording:
    def __init__(self):
        self._chunks = [b"\x01\x00" * 1600]

    def stop(self):
        return b"".join(self._chunks) + b"\x02\x00" * 1600

    def cancel(self):
        pass

    def current_level(self):
        return 0.5


class FakeSocket:
    def __init__(self, url):
        self.url = url
        self.sent = []
        self.inbox = []
        self.ready = threading.Condition()
        self.closed = False

    def send_binary(self, data):
        self.sent.append(data)

    def send_text(self, text):
        self.sent.append(json.loads(text))
        if json.loads(text) == {"type": "Terminate"}:
            with self.ready:
                self.inbox += [{"type": "Turn", "turn_order": 0, "transcript": "Hello there.", "end_of_turn": True,
                                "turn_is_formatted": True}, {"type": "Termination"}]
                self.ready.notify()

    def receive(self):
        with self.ready:
            self.ready.wait_for(lambda: self.inbox or self.closed, timeout=5)
            if not self.inbox:
                return None
            return Message(True, json.dumps(self.inbox.pop(0)).encode())

    def close(self):
        with self.ready:
            self.closed = True
            self.ready.notify()


def test_a_streamed_take_sends_its_audio_then_terminate_and_gives_the_turns():
    sockets = []
    streamed = StreamedRecording(FakeRecording(), LinuxSettings(account_token="t"), ("Aaditya",),
                                 open_socket=lambda url: sockets.append(FakeSocket(url)) or sockets[-1],
                                 fetch_token=lambda settings: "minted")
    streamed._connected.wait(2)
    pcm = streamed.stop()
    assert streamed.transcript(wav_from_pcm(pcm)) == "Hello there."
    sent = sockets[0].sent
    assert b"".join(part for part in sent if isinstance(part, bytes)) == pcm
    assert sent[-1] == {"type": "Terminate"} and "token=minted" in sockets[0].url


def test_a_socket_that_cannot_open_sends_the_take_to_the_upload():
    def refuse(_url):
        raise OSError("no network")
    uploaded = []
    streamed = StreamedRecording(FakeRecording(), LinuxSettings(account_token="t"), open_socket=refuse,
                                 fetch_token=lambda settings: "minted", fallback=lambda wav: uploaded.append(wav) or "from the upload")
    pcm = streamed.stop()
    assert streamed.transcript(wav_from_pcm(pcm)) == "from the upload" and len(uploaded) == 1


def test_a_free_accounts_audio_refused_by_the_backend_says_to_dictate_offline(monkeypatch):
    import urllib.error
    refusal = urllib.error.HTTPError("https://backend/agent/transcribe", 402, "Payment Required", {}, None)
    monkeypatch.setattr(refusal, "read", lambda *arguments: b'{"error":"not_on_plan"}')

    def refuse(request, timeout=0):
        raise refusal
    monkeypatch.setattr(transcriber.urllib.request, "urlopen", refuse)
    with pytest.raises(TranscriptionError, match="a free account dictates on this computer"):
        transcriber.transcribe(b"RIFF", LinuxSettings(account_token="t", dictation_engine="account"))
