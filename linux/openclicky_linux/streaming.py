"""AssemblyAI streaming dictation (the Mac's `AssemblyAIStreamingTranscriptionProvider`): words arrive
while the key is held. The account mints a short-lived token (`POST /transcribe-token` on the
backend, account only: it is metered like any model call); the take's audio goes over AssemblyAI's
v3 socket (16 kHz PCM, formatted turns, `u3-rt-pro`, the dictionary as key terms) as it is
recorded, a tenth of a second at a time; on release the rest is sent, then `Terminate`, and the
turns heard make the take's words.

`StreamedRecording` wraps the microphone's recording, so the take controller sees a recording like
any other: `stop()` gives the audio, `transcript(wav)` the words (or, when the socket failed, the
account's upload of the same audio), `on_live_words` hears the words so far.
"""

from __future__ import annotations

import json
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
from typing import Callable

from . import applog
from .config import LinuxSettings
from .recorder import SAMPLE_RATE, SAMPLE_WIDTH_BYTES
from .transcriber import FREE_ACCOUNT_DICTATES_HERE, TranscriptionError
from .websocket import WebSocket, WebSocketError

SOCKET_URL = "wss://streaming.assemblyai.com/v3/ws"
SEND_EVERY_SECONDS = 0.1
# AssemblyAI takes 50 ms to 1 s of audio per message.
MAXIMUM_CHUNK_BYTES = SAMPLE_RATE * SAMPLE_WIDTH_BYTES
FINAL_WAIT_SECONDS = 4.0


def token_request(settings: LinuxSettings) -> urllib.request.Request:
    return urllib.request.Request(f"{settings.backend_url}/transcribe-token", data=b"{}", method="POST", headers={
        "Authorization": f"Bearer {settings.account_token}", "Content-Type": "application/json"})


def socket_url(token: str, keyterms: tuple[str, ...] = ()) -> str:
    query = [("sample_rate", str(SAMPLE_RATE)), ("encoding", "pcm_s16le"), ("format_turns", "true"), ("speech_model", "u3-rt-pro")]
    terms = [term.strip() for term in keyterms if term.strip()]
    if terms:
        query.append(("keyterms_prompt", json.dumps(terms)))
    query.append(("token", token))
    return f"{SOCKET_URL}?{urllib.parse.urlencode(query)}"


class Turns:
    """The turns heard so far, in order: a finished turn keeps its (formatted) words, the one under
    way shows its words until it finishes."""

    def __init__(self) -> None:
        self.finished: dict[int, str] = {}
        self.active_order: int | None = None
        self.active_text = ""

    def receive(self, message: dict) -> bool:
        """True when it was a turn message (the words may have changed)."""
        if str(message.get("type", "")).lower() != "turn":
            return False
        text = (message.get("transcript") or "").strip()
        order = message.get("turn_order")
        if not isinstance(order, int):
            order = self.active_order if self.active_order is not None else max(self.finished, default=-1) + 1
        if message.get("end_of_turn") or message.get("turn_is_formatted"):
            # The formatted turn replaces the unformatted one with the same order.
            if text and (message.get("turn_is_formatted") or order not in self.finished):
                self.finished[order] = text
            self.active_order, self.active_text = None, ""
        else:
            self.active_order, self.active_text = order, text
        return True

    @property
    def text(self) -> str:
        parts = [self.finished[order] for order in sorted(self.finished)]
        if self.active_text and self.active_order not in self.finished:
            parts.append(self.active_text)
        return " ".join(parts).strip()


class StreamedRecording:
    def __init__(self, recording, settings: LinuxSettings, keyterms: tuple[str, ...] = (),
                 fallback: Callable[[bytes], str] | None = None, open_socket: Callable[[str], WebSocket] = WebSocket,
                 fetch_token: Callable[[LinuxSettings], str] | None = None) -> None:
        self.recording = recording
        self.on_live_words: Callable[[str], None] = lambda _words: None
        self._settings = settings
        self._keyterms = keyterms
        self._fallback = fallback
        self._open_socket = open_socket
        self._fetch_token = fetch_token or _fetch_token
        self._turns = Turns()
        self._socket: WebSocket | None = None
        self._failure: Exception | None = None
        self._sent_bytes = 0
        # The sender thread and the release both send audio: one at a time, each byte once.
        self._send_lock = threading.Lock()
        self._stopped = threading.Event()
        self._terminated = threading.Event()
        self._connected = threading.Event()
        threading.Thread(target=self._run, daemon=True).start()

    def current_level(self) -> float:
        return self.recording.current_level()

    def _run(self) -> None:
        try:
            self._socket = self._open_socket(socket_url(self._fetch_token(self._settings), self._keyterms))
        except (WebSocketError, OSError, TranscriptionError, ValueError) as failure:
            self._failure = failure
            applog.append(f"assemblyai: no socket ({type(failure).__name__}: {str(failure)[:120]}); the take will be uploaded")
            self._terminated.set()
            return
        self._connected.set()
        threading.Thread(target=self._read, daemon=True).start()
        while not self._stopped.is_set():
            self._send_new_audio()
            time.sleep(SEND_EVERY_SECONDS)

    def _send_new_audio(self, pcm: bytes | None = None) -> None:
        audio = pcm if pcm is not None else b"".join(self.recording._chunks)
        audio = audio[: len(audio) - len(audio) % SAMPLE_WIDTH_BYTES]
        with self._send_lock:
            try:
                while self._sent_bytes < len(audio):
                    piece = audio[self._sent_bytes:self._sent_bytes + MAXIMUM_CHUNK_BYTES]
                    self._socket.send_binary(piece)
                    self._sent_bytes += len(piece)
            except (WebSocketError, OSError) as failure:
                self._failure = self._failure or failure

    def _read(self) -> None:
        try:
            while (message := self._socket.receive()) is not None:
                if not message.is_text:
                    continue
                try:
                    decoded = json.loads(message.text)
                except ValueError:
                    continue
                kind = str(decoded.get("type", "")).lower()
                if self._turns.receive(decoded) and self._turns.text:
                    self.on_live_words(self._turns.text)
                elif kind == "termination":
                    break
                elif kind == "error":
                    self._failure = TranscriptionError(f"assemblyai: {decoded.get('error') or decoded.get('message') or 'an error'}")
                    break
        except (WebSocketError, OSError) as failure:
            self._failure = self._failure or failure
        finally:
            self._terminated.set()

    def stop(self) -> bytes:
        pcm = self.recording.stop()
        self._stopped.set()
        if self._connected.wait(timeout=2.0) and self._socket is not None and self._failure is None:
            self._send_new_audio(pcm)
            try:
                self._socket.send_text(json.dumps({"type": "Terminate"}))
            except (WebSocketError, OSError) as failure:
                self._failure = failure
        return pcm

    def cancel(self) -> None:
        self._stopped.set()
        self.recording.cancel()
        if self._socket is not None:
            self._socket.close()

    def transcript(self, wav: bytes) -> str:
        """The take's words: the turns AssemblyAI heard, or the account's upload when the socket failed."""
        self._terminated.wait(timeout=FINAL_WAIT_SECONDS)
        if self._socket is not None:
            self._socket.close()
        words = self._turns.text
        if words and self._failure is None:
            return words
        if self._fallback is None:
            raise TranscriptionError(str(self._failure or "assemblyai heard nothing"))
        if self._failure is not None:
            applog.append(f"assemblyai: {type(self._failure).__name__}; the take went to the upload instead")
        return self._fallback(wav) if self._failure is not None else words


def _fetch_token(settings: LinuxSettings) -> str:
    try:
        with urllib.request.urlopen(token_request(settings), timeout=10) as response:
            answer = json.loads(response.read().decode("utf-8"))
    except urllib.error.HTTPError as refusal:
        if refusal.code == 402 and b"not_on_plan" in (refusal.read() or b""):
            raise TranscriptionError(FREE_ACCOUNT_DICTATES_HERE) from refusal
        raise TranscriptionError(f"the openclicky backend gave no assemblyai token ({refusal.code})",
                                 account_refused=refusal.code == 401) from refusal
    except (urllib.error.URLError, OSError, ValueError) as failure:
        raise TranscriptionError(f"couldn't reach the openclicky backend: {failure}") from failure
    token = answer.get("token") if isinstance(answer, dict) else None
    if not isinstance(token, str) or not token:
        raise TranscriptionError("the openclicky backend gave no assemblyai token")
    return token
