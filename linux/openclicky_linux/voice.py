"""Spoken answers: Kokoro v1.0 through sherpa-onnx, the Mac app's own offline voice
(`Kokoro/KokoroVoice.swift`), with the same model, pinned checksum and speakers. No key, nothing
sent: the answer is spoken on this computer.

- The model (350 MB) downloads once, on request (Settings → Voice → Download), into the same
  models folder as the offline dictation engine, checked like it.
- It runs in a helper process (`voice_helper.py`), as on the Mac: loading takes a few seconds, so
  it starts as soon as a question is asked (the loading overlaps the thinking), and it ends after
  180 s without speaking, which gives its memory back.
- An answer is spoken a sentence at a time: the first as soon as it is ready, the next generated
  while the first plays (the Mac's `SpokenChunker`: the first sentence at once, then pieces of at
  least 120 characters). Esc or a new question stops it mid-word.
- Played with whatever the desktop has: `paplay` (PulseAudio, and PipeWire through pipewire-pulse),
  else `pw-play` (PipeWire alone), else `aplay` (ALSA). A player that fails is passed over for the
  next one: on Ubuntu 22.04 `pw-play` is installed (screen sharing uses PipeWire) but PipeWire has no
  speakers there ("no node available"), and every answer went unheard while it came first.
"""

from __future__ import annotations

import base64
import json
import os
import queue
import re
import shutil
import subprocess
import sys
import tempfile
import threading
from dataclasses import dataclass

from . import applog
from .offline import ModelAsset, is_engine_available, is_installed, model_folder

KOKORO = ModelAsset(
    url="https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/kokoro-multi-lang-v1_0.tar.bz2",
    sha256="c5f7e2d2caf082bc1d20fb70334a61d99d20b484500aad32e7cf84c128ea3298",
    size_bytes=349_906_910,
    folder_name="kokoro-multi-lang-v1_0",
    files=("model.onnx", "voices.bin", "tokens.txt", "espeak-ng-data", "dict", "lexicon-us-en.txt", "lexicon-zh.txt"),
)
IDLE_SECONDS_BEFORE_UNLOADING = 180.0
# The Mac's grouping: later sentences go together until a piece is at least this long.
PIECE_MINIMUM_CHARACTERS = 120
PLAYERS = (("paplay",), ("pw-play",), ("aplay", "-q"))


@dataclass(frozen=True)
class Speaker:
    id: int  # the speaker's index in the model's voice table
    name: str
    language_code: str
    accent: str

    @property
    def label(self) -> str:
        return f"{self.name}, {self.accent}"


# The Mac's list: the model has 54 speakers, these are the clearest per language.
SPEAKERS = (
    Speaker(3, "Heart", "en", "American"), Speaker(2, "Bella", "en", "American"), Speaker(6, "Nicole", "en", "American"),
    Speaker(9, "Sarah", "en", "American"), Speaker(16, "Michael", "en", "American"), Speaker(11, "Adam", "en", "American"),
    Speaker(21, "Emma", "en", "British"), Speaker(26, "George", "en", "British"), Speaker(31, "Alpha", "hi", "Hindi"),
    Speaker(33, "Omega", "hi", "Hindi"), Speaker(28, "Dora", "es", "Spanish"), Speaker(30, "Siwis", "fr", "French"),
    Speaker(35, "Sara", "it", "Italian"), Speaker(37, "Alpha", "ja", "Japanese"), Speaker(42, "Dora", "pt", "Portuguese"),
    Speaker(45, "Xiaobei", "zh", "Mandarin"),
)


def speaker_for(language_code: str | None, picked: int | None) -> Speaker | None:
    """The speaker for the reply language: the one picked if it speaks it, else the language's first;
    None when Kokoro does not speak the language."""
    language = (language_code or "en").split("-")[0].split("_")[0].lower()
    for_language = [speaker for speaker in SPEAKERS if speaker.language_code == language]
    return next((speaker for speaker in for_language if speaker.id == picked), for_language[0] if for_language else None)


def language_hint(language_code: str | None) -> str:
    """Kokoro's espeak hint; English and Mandarin go by the model's own lexicons."""
    language = (language_code or "en").split("-")[0].split("_")[0].lower()
    return {"en": "en-us", "zh": "zh", "pt": "pt-br"}.get(language, language)


def spoken_pieces(text: str) -> list[str]:
    """The answer in the pieces it is spoken in: the first sentence alone (so the voice starts
    soon), then sentences together until a piece has at least `PIECE_MINIMUM_CHARACTERS`."""
    sentences = [sentence.strip() for sentence in re.split(r"(?<=[.!?])\s+", text.strip()) if sentence.strip()]
    if not sentences:
        return []
    pieces = [sentences[0]]
    current = ""
    for sentence in sentences[1:]:
        current = f"{current} {sentence}".strip()
        if len(current) >= PIECE_MINIMUM_CHARACTERS:
            pieces.append(current)
            current = ""
    if current:
        pieces.append(current)
    return pieces


def find_player() -> tuple[str, ...] | None:
    return next((player for player in PLAYERS if shutil.which(player[0])), None)


def installed_players() -> list[tuple[str, ...]]:
    return [player for player in PLAYERS if shutil.which(player[0])]


def muted_output(pactl_mute: str, pactl_volume: str) -> bool:
    """From `pactl get-sink-mute` and `get-sink-volume`: muted, or every channel at 0 %."""
    if "yes" in pactl_mute.lower():
        return True
    levels = [part.strip() for part in pactl_volume.split("/") if part.strip().endswith("%")]
    return bool(levels) and all(level == "0%" for level in levels)


def output_is_muted() -> bool:
    """The speakers answers would play on are muted or at 0 %; False when it cannot be told."""
    if not shutil.which("pactl"):
        return False
    try:
        mute = subprocess.run(["pactl", "get-sink-mute", "@DEFAULT_SINK@"], capture_output=True, text=True, timeout=2).stdout
        volume = subprocess.run(["pactl", "get-sink-volume", "@DEFAULT_SINK@"], capture_output=True, text=True, timeout=2).stdout
    except (OSError, subprocess.SubprocessError):
        return False
    return muted_output(mute, volume.splitlines()[0] if volume else "")


def is_ready() -> bool:
    return is_installed(KOKORO) and is_engine_available() and find_player() is not None


class Voice:
    """Kokoro's helper process and the player. `speak` returns at once; the speaking happens on two
    threads (one asks the helper for each piece's audio, one plays them in order)."""

    def __init__(self, idle_seconds: float = IDLE_SECONDS_BEFORE_UNLOADING, helper_command: list[str] | None = None,
                 player: tuple[str, ...] | None = None) -> None:
        self._idle_seconds = idle_seconds
        self._helper_command = helper_command
        self._player = player
        self._helper: subprocess.Popen | None = None
        self._helper_lock = threading.Lock()
        self._idle_timer: threading.Timer | None = None
        self._speech_id = 0
        self._playing: subprocess.Popen | None = None
        self._playing_lock = threading.Lock()
        # Players that failed to play here (an installed PipeWire without speakers): not tried again.
        self._failed_players: set[str] = set()
        # What was sent to the player, in order (the tests read it).
        self.spoken: list[str] = []

    def _command(self) -> list[str]:
        return self._helper_command or [sys.executable, "-m", "openclicky_linux.voice_helper", str(model_folder(KOKORO))]

    def prepare(self) -> None:
        """Starts the helper now (a question was asked), so the voice has loaded by the answer."""
        if self._helper_command or is_installed(KOKORO):
            threading.Thread(target=self._ensure_started, daemon=True).start()

    def _ensure_started(self) -> subprocess.Popen:
        with self._helper_lock:
            if self._helper is None or self._helper.poll() is not None:
                self._helper = subprocess.Popen(self._command(), stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                                stderr=subprocess.DEVNULL, text=True)
                applog.append("voice: loading")
            if self._idle_timer is not None:
                self._idle_timer.cancel()
            self._idle_timer = threading.Timer(self._idle_seconds, self.unload)
            self._idle_timer.daemon = True
            self._idle_timer.start()
            return self._helper

    def _audio_for(self, text: str, speaker: Speaker, language_code: str | None) -> bytes:
        helper = self._ensure_started()
        request = json.dumps({"text": text, "sid": speaker.id, "lang": language_hint(language_code)}) + "\n"
        with self._helper_lock:
            assert helper.stdin is not None and helper.stdout is not None
            helper.stdin.write(request)
            helper.stdin.flush()
            line = helper.stdout.readline()
        if not line:
            raise RuntimeError("the voice stopped")
        answer = json.loads(line)
        if "error" in answer:
            raise RuntimeError(answer["error"])
        return base64.b64decode(answer["wav"])

    def speak(self, text: str, language_code: str | None = None, picked_speaker: int | None = None) -> bool:
        """Starts speaking `text` (stopping anything still being said). False when it cannot: the
        voice is not downloaded, the language is not one Kokoro speaks, or there is no player."""
        self.stop()
        speaker = speaker_for(language_code, picked_speaker)
        player = self._player or find_player()
        if speaker is None or player is None or not text.strip():
            return False
        self._speech_id += 1
        this_speech = self._speech_id
        pieces: queue.Queue = queue.Queue()

        def generate() -> None:
            for piece in spoken_pieces(text):
                if this_speech != self._speech_id:
                    return
                try:
                    pieces.put((piece, self._audio_for(piece, speaker, language_code)))
                except (OSError, RuntimeError, ValueError) as failure:
                    applog.append(f"voice: could not speak a piece ({failure})")
                    break
            pieces.put(None)

        def play() -> None:
            while (item := pieces.get()) is not None:
                piece, wav = item
                if this_speech != self._speech_id:
                    return
                self._play(wav, player, this_speech, piece)

        threading.Thread(target=generate, daemon=True).start()
        threading.Thread(target=play, daemon=True).start()
        return True

    def _players_to_try(self, player: tuple[str, ...]) -> list[tuple[str, ...]]:
        """The player given, then every other installed one, leaving out those that failed here."""
        candidates = [player] + [other for other in installed_players() if other[0] != player[0]]
        return [candidate for candidate in candidates if candidate[0] not in self._failed_players] or [player]

    def _play(self, wav: bytes, player: tuple[str, ...], this_speech: int, piece: str) -> None:
        with tempfile.NamedTemporaryFile(suffix=".wav", delete=False) as audio_file:
            audio_file.write(wav)
            path = audio_file.name
        try:
            recorded = False
            for candidate in self._players_to_try(player):
                with self._playing_lock:
                    if this_speech != self._speech_id:
                        return
                    playing = subprocess.Popen([*candidate, path], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
                    self._playing = playing
                    if not recorded:
                        self.spoken.append(piece)
                        recorded = True
                exit_status = playing.wait()
                # Stopped on purpose (esc, a new question), or played: done with this piece.
                if exit_status == 0 or this_speech != self._speech_id:
                    return
                self._failed_players.add(candidate[0])
                applog.append(f"voice: {candidate[0]} could not play (exit {exit_status}); trying the next player")
        finally:
            os.unlink(path)

    def stop(self) -> None:
        """Stops what is being said, mid-word (esc, or a new question)."""
        self._speech_id += 1
        with self._playing_lock:
            if self._playing is not None and self._playing.poll() is None:
                self._playing.terminate()
            self._playing = None

    @property
    def is_speaking(self) -> bool:
        return self._playing is not None and self._playing.poll() is None

    def unload(self) -> None:
        with self._helper_lock:
            helper, self._helper = self._helper, None
            if self._idle_timer is not None:
                self._idle_timer.cancel()
                self._idle_timer = None
        if helper is not None and helper.poll() is None:
            try:
                assert helper.stdin is not None
                helper.stdin.close()
                helper.wait(timeout=3)
            except (OSError, subprocess.TimeoutExpired):
                helper.kill()
            applog.append("voice: unloaded")
