"""Fast voice (the Mac's `RealtimeVoiceClient`): OpenAI Realtime over one WebSocket, so OpenClicky
talks back at once instead of transcribing, asking and speaking in turn. Settings → Clicky → Fast
voice; it needs an own OpenAI key (the client secret is minted at OpenAI) or the account (the
backend's `/agent/realtime/session` mints it).

- **push to talk**: holding the ask keys opens the microphone; on release the screen goes along,
  the audio is committed and an answer is asked for;
- **hands-free**: the server decides the turns (its voice detection); the screen goes with each;
- **a typed question** (the ask box) goes in as text, with the screen;
- the model's tools: `point_at` (the pointer flies to the element, `grounding.py` puts it on the
  element), the quick actions (`actions.py`), and `send_to_agent` (the agent lane).

Audio is 16-bit 24 kHz mono both ways: the microphone through parecord (or arecord), the voice
through pacat (or aplay). Without echo cancellation the model would hear itself, so the microphone
is not sent while it speaks (and a moment after); esc, or a new turn, stops it at once.

A socket is kept while it is used: reused if it is under 50 minutes old (OpenAI ends sessions at
60) and was heard from in the last minute, else made anew.
"""

from __future__ import annotations

import base64
import json
import shutil
import subprocess
import threading
import time
import urllib.error
import urllib.request
from dataclasses import dataclass
from typing import Any, Callable

from . import actions, applog
from .config import LinuxSettings
from .recorder import RecorderUnavailable, find_recorder_command
from .websocket import WebSocket, WebSocketError

SAMPLE_RATE = 24_000
REALTIME_MODEL = "gpt-realtime"
OPENAI_SECRET_URL = "https://api.openai.com/v1/realtime/client_secrets"
SOCKET_URL = "wss://api.openai.com/v1/realtime?model={model}"
MAXIMUM_SOCKET_AGE_SECONDS = 50 * 60
HEARD_FROM_WITHIN_SECONDS = 60
# Released: the microphone stays open this long so the last word is not cut off.
RELEASE_TAIL_SECONDS = 0.4
# Less audio than this is no turn (a tap, or a release before the microphone opened).
MINIMUM_TURN_BYTES = int(SAMPLE_RATE * 2 * 0.2)
SEND_EVERY_SECONDS = 0.1
# After the voice stops, the microphone stays muted this long (the room's echo).
ECHO_TAIL_SECONDS = 0.35

DEFAULT_INSTRUCTIONS = (
    "You are OpenClicky, a friendly, fast voice assistant on the user's Linux computer. Keep spoken replies short (one or two "
    "sentences). Every request comes with a screenshot of the user's current screen (with the pointer position noted when it is "
    "known): \"this\", \"here\", \"that\" refer to what is on screen. Look at the screenshot and answer about it directly; never say "
    "you cannot see the screen and never mention a camera. Answer quick questions yourself. When the user asks how to do something, "
    "where something is, or what to click, point at it with the point_at tool while you explain, one call per step, in order, with "
    "the element's exact on-screen text when it has any, and keep speaking between calls. Do not point for general questions or "
    "things they are obviously already looking at. Do simple local things yourself with the fast tools (open_app, open_url, "
    "create_folder, reveal_in_files, set_volume, media_control) and then say the one sentence they give back. For anything bigger "
    "(creating or editing files or code, running commands, using integrations, research, multi-step tasks), first say one short "
    "sentence acknowledging it, then call the send_to_agent tool with a clear, self-contained task, and afterwards tell the user in "
    "one sentence what happened. Never pretend work was done without the tool. Do not read file paths aloud character by character.")

SEND_TO_AGENT_TOOL = {
    "type": "function", "name": "send_to_agent",
    "description": "Hand a task that requires doing work (files, code, commands, apps, research) to the OpenClicky agent. Returns a short result summary.",
    "parameters": {"type": "object", "properties": {"task": {"type": "string", "description": "A clear, self-contained description of what to do."}},
                   "required": ["task"]}}
POINT_AT_TOOL = {
    "type": "function", "name": "point_at",
    "description": ("Fly the on-screen cursor buddy to a UI element in the attached screenshot and show a short label. Use it while you "
                    "explain: one call per step, in order, as you say each step. Coordinates are pixels in the screenshot, origin "
                    "top-left; the buddy snaps to the element's visible text near them, so always pass that text when the element has any."),
    "parameters": {"type": "object", "properties": {
        "x": {"type": "integer", "description": "Horizontal pixel in the screenshot, from the left edge."},
        "y": {"type": "integer", "description": "Vertical pixel in the screenshot, from the top edge."},
        "label": {"type": "string", "description": "1-3 words naming the element, e.g. 'export button'"},
        "text": {"type": "string", "description": ("The text written on the element itself (button caption, menu item, link, tab title), "
                                                   "copied exactly as it appears on screen. Empty string for icon-only elements.")}},
        "required": ["x", "y", "label", "text"]}}


def realtime_tools() -> list[dict]:
    """send_to_agent, point_at and the quick actions, in Realtime's (flat) function shape."""
    quick = [{"type": "function", "name": tool["name"], "description": tool["description"], "parameters": tool["input_schema"]}
             for tool in actions.tool_definitions()]
    return [SEND_TO_AGENT_TOOL, POINT_AT_TOOL, *quick]


def session_update(hands_free: bool, instructions: str, language: str | None, voice: str | None = None) -> dict:
    """The session as OpenClicky wants it (GA Realtime shape)."""
    audio_input: dict[str, Any] = {"format": {"type": "audio/pcm", "rate": SAMPLE_RATE},
                                   # Pinned, like the reply: unpinned, English audio came back as Chinese.
                                   "transcription": {"model": "gpt-4o-mini-transcribe", "language": language or "en"}}
    # Hands-free: the server finds the turns; the screen goes up when it commits one, then the answer
    # is asked for (so create_response is off).
    audio_input["turn_detection"] = ({"type": "server_vad", "silence_duration_ms": 600, "create_response": False, "interrupt_response": True}
                                     if hands_free else None)
    audio_output: dict[str, Any] = {"format": {"type": "audio/pcm", "rate": SAMPLE_RATE}}
    if voice:
        audio_output["voice"] = voice
    return {"type": "session.update", "session": {"type": "realtime", "instructions": instructions, "tools": realtime_tools(),
                                                  "tool_choice": "auto", "audio": {"input": audio_input, "output": audio_output}}}


def secret_request(settings: LinuxSettings, instructions: str) -> urllib.request.Request:
    """An own OpenAI key mints at OpenAI; the account at the backend (never both)."""
    if settings.openai_api_key:
        body = {"expires_after": {"anchor": "created_at", "seconds": 600},
                "session": {"type": "realtime", "model": REALTIME_MODEL, "instructions": instructions}}
        return urllib.request.Request(OPENAI_SECRET_URL, data=json.dumps(body).encode(), method="POST", headers={
            "Authorization": f"Bearer {settings.openai_api_key}", "Content-Type": "application/json"})
    return urllib.request.Request(f"{settings.backend_url}/agent/realtime/session", data=json.dumps({"instructions": instructions}).encode(),
                                  method="POST", headers={"Authorization": f"Bearer {settings.account_token}", "Content-Type": "application/json"})


def can_use(settings: LinuxSettings) -> bool:
    return bool(settings.openai_api_key or settings.account_token)


@dataclass
class ScreenContext:
    """The screenshot a turn carries, and what `point_at` maps onto."""
    png: bytes
    width: int
    height: int
    caption: str
    # Picture pixels → desktop: the screenshot's place and scale.
    origin_x: int = 0
    origin_y: int = 0
    scale: float = 1.0

    def to_desktop(self, x: float, y: float) -> tuple[int, int]:
        return round(self.origin_x + x / self.scale), round(self.origin_y + y / self.scale)


class RealtimeClient:
    def __init__(self, settings_reader: Callable[[], LinuxSettings], screen: Callable[[], ScreenContext | None],
                 on_point_at: Callable[[int, int, str], None] = lambda _x, _y, _label: None,
                 on_agent_task: Callable[[str], str] = lambda _task: "The agent lane is not available in this session.",
                 perform_action: Callable[[str, dict], str] | None = None,
                 on_transcript: Callable[[str, str], None] = lambda _role, _text: None,
                 on_state: Callable[[str], None] = lambda _state: None,
                 instructions: Callable[[], str] = lambda: DEFAULT_INSTRUCTIONS,
                 resolve_point: Callable[..., tuple[tuple[float, float], str]] | None = None,
                 open_socket: Callable[[str, dict], WebSocket] = lambda url, headers: WebSocket(url, headers=headers)) -> None:
        self.settings_reader = settings_reader
        self.screen = screen
        self.on_point_at = on_point_at
        self.on_agent_task = on_agent_task
        self.perform_action = perform_action
        self.on_transcript = on_transcript
        self.on_state = on_state
        self.instructions = instructions
        self.resolve_point = resolve_point
        self.open_socket = open_socket
        self.hands_free = False
        self.state = "idle"  # idle | connecting | listening | thinking | speaking
        self._socket: WebSocket | None = None
        self._connected_at = 0.0
        self._heard_at = 0.0
        self._lock = threading.Lock()
        self._microphone: subprocess.Popen | None = None
        self._forwarding = False
        self._turn_bytes = 0
        self._player: subprocess.Popen | None = None
        self._speaking_until = 0.0
        self._response_in_progress = False
        self._needs_continuation = False
        self._screen: ScreenContext | None = None
        self._last_user_words: str | None = None
        self._pointing = threading.Lock()  # point_at calls fly one after another, in order
        self._sent_instructions = ""

    # MARK: the socket

    def _set_state(self, state: str) -> None:
        self.state = state
        self.on_state(state)

    def _usable(self) -> bool:
        now = time.monotonic()
        return (self._socket is not None and not self._socket.closed and now - self._connected_at < MAXIMUM_SOCKET_AGE_SECONDS
                and now - self._heard_at < HEARD_FROM_WITHIN_SECONDS)

    def connect(self) -> None:
        """A socket, reused when it is fresh, else made (raises RuntimeError with a sentence)."""
        with self._lock:
            if self._usable():
                return
            if self._socket is not None:
                self._socket.close()
            settings = self.settings_reader()
            if not can_use(settings):
                raise RuntimeError("fast voice needs an openai key or an openclicky account")
            self._set_state("connecting")
            instructions = self.instructions()
            try:
                with urllib.request.urlopen(secret_request(settings, instructions), timeout=15) as response:
                    secret = json.loads(response.read())
            except urllib.error.HTTPError as refusal:
                raise RuntimeError(f"{'openai' if settings.openai_api_key else 'the openclicky backend'} refused fast voice ({refusal.code})") from refusal
            except (urllib.error.URLError, OSError, ValueError) as failure:
                raise RuntimeError("couldn't reach fast voice. check your internet") from failure
            value = secret.get("value") or (secret.get("client_secret") or {}).get("value")
            if not value:
                raise RuntimeError("fast voice got no session")
            model = (secret.get("session") or {}).get("model") or REALTIME_MODEL
            try:
                socket = self.open_socket(SOCKET_URL.format(model=model), {"Authorization": f"Bearer {value}"})
                first = socket.receive()  # session.created: the socket only counts once OpenAI spoke
            except (WebSocketError, OSError) as failure:
                raise RuntimeError(f"fast voice could not connect ({failure})") from failure
            if first is None:
                raise RuntimeError("fast voice closed at once")
            self._socket = socket
            self._connected_at = self._heard_at = time.monotonic()
            self._send(session_update(self.hands_free, instructions, settings.bare_language_code))
            self._sent_instructions = instructions
            threading.Thread(target=self._receive, args=(socket,), daemon=True).start()
            applog.append(f"realtime: connected ({model})")
            self._set_state("idle")

    def _send(self, event: dict) -> None:
        socket = self._socket
        if socket is None:
            raise RuntimeError("fast voice is not connected")
        socket.send_text(json.dumps(event))

    def close(self) -> None:
        self.stop_listening()
        self._stop_player()
        with self._lock:
            if self._socket is not None:
                self._socket.close()
                self._socket = None
        self._set_state("idle")

    def _refresh_instructions(self) -> None:
        """The skills for the app in front change the instructions: sent only when they changed."""
        text = self.instructions()
        if text != self._sent_instructions:
            self._send(session_update(self.hands_free, text, self.settings_reader().bare_language_code))
            self._sent_instructions = text

    # MARK: the microphone

    def _start_microphone(self) -> None:
        if self._microphone is not None and self._microphone.poll() is None:
            return
        try:
            command = find_recorder_command(self.settings_reader().microphone, rate=SAMPLE_RATE)
        except RecorderUnavailable as missing:
            raise RuntimeError(str(missing)) from missing
        self._microphone = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
        threading.Thread(target=self._read_microphone, args=(self._microphone,), daemon=True).start()

    def _read_microphone(self, microphone: subprocess.Popen) -> None:
        chunk_bytes = int(SAMPLE_RATE * 2 * SEND_EVERY_SECONDS)
        while (audio := microphone.stdout.read(chunk_bytes)):
            # Half duplex: what the speakers play is not sent back (no echo cancellation here).
            if not self._forwarding or time.monotonic() < self._speaking_until:
                continue
            try:
                self._send({"type": "input_audio_buffer.append", "audio": base64.b64encode(audio).decode("ascii")})
                self._turn_bytes += len(audio)
            except (RuntimeError, WebSocketError, OSError):
                return

    def stop_listening(self) -> None:
        self._forwarding = False
        if self._microphone is not None and self._microphone.poll() is None:
            self._microphone.terminate()
        self._microphone = None

    # MARK: turns

    def begin_push_to_talk(self) -> None:
        """The ask keys went down: whatever is being said stops, the microphone opens."""
        self.connect()
        self.interrupt()
        self._send({"type": "input_audio_buffer.clear"})
        self._turn_bytes = 0
        self._forwarding = True
        self._start_microphone()
        self._set_state("listening")

    def end_push_to_talk(self) -> None:
        """Released: a moment more for the last word, then the screen, the audio and the question."""
        time.sleep(RELEASE_TAIL_SECONDS)
        self._forwarding = False
        self.stop_listening()
        if self._turn_bytes < MINIMUM_TURN_BYTES:
            # No audio, no turn: an empty commit made the model invent a request.
            self._send({"type": "input_audio_buffer.clear"})
            applog.append(f"realtime: turn not sent ({self._turn_bytes} bytes of audio)")
            self._set_state("idle")
            return
        self._set_state("thinking")
        self._refresh_instructions()
        self._attach_screen()
        self._send({"type": "input_audio_buffer.commit"})
        self._send({"type": "response.create"})

    def send_text(self, text: str) -> None:
        """A typed turn: the words go in with the screen, then the answer is asked for."""
        self.connect()
        self.interrupt()
        self._last_user_words = text
        self._set_state("thinking")
        self._refresh_instructions()
        self._attach_screen()
        self._send({"type": "conversation.item.create", "item": {"type": "message", "role": "user",
                                                                 "content": [{"type": "input_text", "text": text}]}})
        self._send({"type": "response.create"})

    def set_hands_free(self, on: bool) -> None:
        """Always listening (the server finds the turns), or back to push to talk."""
        self.hands_free = on
        self.connect()
        self._send(session_update(on, self.instructions(), self.settings_reader().bare_language_code))
        if on:
            self._forwarding = True
            self._start_microphone()
            self._set_state("listening")
        else:
            self.stop_listening()
            self._set_state("idle")

    def interrupt(self) -> None:
        """Esc, or a new turn: the answer stops being said and generated."""
        self._stop_player()
        if self._response_in_progress:
            try:
                self._send({"type": "response.cancel"})
            except (RuntimeError, WebSocketError, OSError):
                pass
            self._response_in_progress = False
        self._speaking_until = 0.0

    def _attach_screen(self) -> None:
        context = self.screen()
        if context is None:
            applog.append("realtime: no screen context")
            return
        self._screen = context
        self._send({"type": "conversation.item.create", "item": {"type": "message", "role": "user", "content": [
            {"type": "input_text", "text": f"[Screen context, not spoken] {context.caption}"},
            {"type": "input_image", "image_url": "data:image/png;base64," + base64.b64encode(context.png).decode("ascii")}]}})

    # MARK: the voice

    def _player_command(self) -> list[str] | None:
        if shutil.which("pacat"):
            return ["pacat", "--raw", "--format=s16le", f"--rate={SAMPLE_RATE}", "--channels=1", "--latency-msec=60"]
        if shutil.which("aplay"):
            return ["aplay", "-q", "-t", "raw", "-f", "S16_LE", "-r", str(SAMPLE_RATE), "-c", "1"]
        return None

    def _play(self, audio: bytes) -> None:
        if self._player is None or self._player.poll() is not None:
            command = self._player_command()
            if command is None:
                return
            self._player = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        try:
            self._player.stdin.write(audio)
            self._player.stdin.flush()
        except (BrokenPipeError, OSError):
            self._player = None
            return
        # The microphone stays muted until this audio (and a moment of room echo) has played.
        now = time.monotonic()
        self._speaking_until = max(self._speaking_until, now) + len(audio) / (SAMPLE_RATE * 2) + ECHO_TAIL_SECONDS
        if self.state != "speaking":
            self._set_state("speaking")

    def _stop_player(self) -> None:
        if self._player is not None and self._player.poll() is None:
            self._player.kill()
        self._player = None

    @property
    def is_speaking(self) -> bool:
        return time.monotonic() < self._speaking_until

    # MARK: what the server says

    def _receive(self, socket: WebSocket) -> None:
        try:
            while (message := socket.receive()) is not None:
                self._heard_at = time.monotonic()
                try:
                    event = json.loads(message.text)
                except ValueError:
                    continue
                self.handle_event(event)
        except (WebSocketError, OSError) as failure:
            applog.append(f"realtime: disconnected ({type(failure).__name__})")
        finally:
            if self._socket is socket:
                self._socket = None
                self._forwarding = False
                self._set_state("idle")

    def handle_event(self, event: dict) -> None:
        kind = event.get("type", "")
        if kind == "input_audio_buffer.speech_started":
            self.interrupt()
            self._set_state("listening")
        elif kind == "conversation.item.input_audio_transcription.completed":
            words = (event.get("transcript") or "").strip()
            if words:
                self._last_user_words = words
                self.on_transcript("user", words)
        elif kind == "input_audio_buffer.committed" and self.hands_free:
            self._set_state("thinking")
            self._refresh_instructions()
            self._attach_screen()
            self._send({"type": "response.create"})
        elif kind == "response.created":
            self._response_in_progress = True
        elif kind in ("response.output_audio.delta", "response.audio.delta"):
            if event.get("delta"):
                self._play(base64.b64decode(event["delta"]))
        elif kind in ("response.output_audio_transcript.done", "response.audio_transcript.done"):
            words = (event.get("transcript") or "").strip()
            if words:
                self.on_transcript("assistant", words)
        elif kind == "response.function_call_arguments.done":
            self._tool_call(event)
        elif kind == "response.done":
            self._response_in_progress = False
            status = (event.get("response") or {}).get("status")
            if self._needs_continuation and status != "cancelled":
                # Tool outputs went in during this response: the follow-up is asked for once.
                self._needs_continuation = False
                self._send({"type": "response.create"})
                return
            self._needs_continuation = False
            if not self.hands_free:
                threading.Timer(max(0.0, self._speaking_until - time.monotonic()), lambda: self._set_state("idle")
                                if not self._response_in_progress and self.state == "speaking" else None).start()
            elif self.state != "speaking":
                self._set_state("listening")
        elif kind == "error":
            message = (event.get("error") or {}).get("message") or "an error"
            applog.append(f"realtime: error ({message[:120]})")

    def _tool_call(self, event: dict) -> None:
        call_id, name = event.get("call_id") or "", event.get("name") or ""
        try:
            arguments = json.loads(event.get("arguments") or "{}")
        except ValueError:
            arguments = {}
        if not isinstance(arguments, dict):
            arguments = {}
        if name == "point_at":
            output = self._point_at(arguments)
        elif name == "send_to_agent":
            task = arguments.get("task") if isinstance(arguments.get("task"), str) else ""
            applog.append("realtime: agent task")
            output = self.on_agent_task(task)
        elif self.perform_action is not None:
            output = self.perform_action(name, arguments)
        else:
            output = f"unknown tool {name}"
        self._send({"type": "conversation.item.create", "item": {"type": "function_call_output", "call_id": call_id,
                                                                 "output": str(output)[:4000]}})
        self._needs_continuation = True

    def _point_at(self, arguments: dict) -> str:
        def number(value) -> float | None:
            try:
                parsed = float(value)
            except (TypeError, ValueError):
                return None
            return parsed if parsed == parsed and abs(parsed) != float("inf") else None

        x, y = number(arguments.get("x")), number(arguments.get("y"))
        label = (arguments.get("label") or "").strip() or "here"
        text = (arguments.get("text") or "").strip()
        context = self._screen
        if x is None or y is None:
            return "invalid coordinates: point_at needs finite integer x and y"
        if context is None:
            return "no screenshot is attached to this turn"
        user_words = self._last_user_words

        def resolve_and_fly() -> None:
            with self._pointing:
                where, how = (x, y), "guess"
                if self.resolve_point is not None:
                    try:
                        where, how = self.resolve_point((x, y), label, text, user_words, context.png, context.width, context.height,
                                                        self.settings_reader())
                    except Exception as failure:  # noqa: BLE001 - a failed snap keeps the model's guess
                        applog.append(f"point_at: not resolved ({type(failure).__name__})")
                screen_x, screen_y = context.to_desktop(*where)
                applog.append(f"point_at ({x:.0f}, {y:.0f}) {how}")
                self.on_point_at(screen_x, screen_y, label)

        threading.Thread(target=resolve_and_fly, daemon=True).start()
        return f"pointing at {label}"
