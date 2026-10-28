"""Spoken answers: Kokoro v1.0 through sherpa-onnx, the Mac app's own offline voice
(`Kokoro/KokoroVoice.swift`), with the same model, pinned checksum and speakers. No key, nothing
sent: the answer is spoken on this computer. Or, picked in Settings → Voice (`speech_voices.py`), a
cloud voice on a key or the account, or this computer's own voice (speech-dispatcher); a cloud voice
that fails partway hands the rest to Kokoro, else to this computer's voice, as the Mac does.

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
    sha256="60703707bc05ccd48d60e1eb723f38b07d6e474795a0b6a588d7220400e0f7a4",
    size_bytes=349_886_476,
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


class SpokenChunker:
    """A streamed answer cut into the pieces it is spoken in while it is still being written: the
    Mac's `SpokenChunker.swift`. The first finished sentence goes at once; later ones are grouped
    until a piece has at least `PIECE_MINIMUM_CHARACTERS`. The answer's markers are never spoken: an
    opening [UNSURE] and the [POINT:…] tag after it (held back until the tag is complete), and any
    [NEXT]. `receive` takes the whole answer so far and returns the pieces ready now; `finish`
    returns what is left."""

    _OPENING = re.compile(r"^\s*(\[\s*unsure\s*\]\s*)?(\[POINT:[^\]]*\]\s*)?", re.IGNORECASE)
    _MARKERS = re.compile(r"\[\s*(next|unsure)\s*\]|\[POINT:[^\]]*\]", re.IGNORECASE)

    def __init__(self) -> None:
        self._handed_on = 0  # characters of the cleaned answer already given out
        self._pieces_given = 0
        self._pending_sentences: list[str] = []

    def _cleaned(self, accumulated: str) -> str | None:
        """The answer without its markers, or None while its opening could still become a tag."""
        stripped = accumulated.lstrip()
        if stripped.startswith("[") and "]" not in stripped:
            return None
        opening = self._OPENING.match(accumulated)
        rest = accumulated[opening.end():] if opening else accumulated
        if rest.lstrip().startswith("[POINT") and "]" not in rest:
            return None
        # A marker still arriving at the end is held back too.
        last_bracket = rest.rfind("[")
        if last_bracket >= 0 and "]" not in rest[last_bracket:]:
            rest = rest[:last_bracket]
        return self._MARKERS.sub("", rest)

    def receive(self, accumulated: str) -> list[str]:
        cleaned = self._cleaned(accumulated)
        if cleaned is None:
            return []
        new_text = cleaned[self._handed_on:]
        # Only finished sentences: the text up to the last sentence end followed by a space.
        last_end = max((match.end() for match in re.finditer(r"[.!?](?=\s)", new_text)), default=0)
        if not last_end:
            return []
        self._handed_on += last_end
        sentences = [sentence.strip() for sentence in re.split(r"(?<=[.!?])\s+", new_text[:last_end].strip()) if sentence.strip()]
        return self._group(sentences, final=False)

    def finish(self, accumulated: str) -> list[str]:
        cleaned = self._cleaned(accumulated) or self._MARKERS.sub("", self._OPENING.sub("", accumulated, count=1))
        rest = cleaned[self._handed_on:].strip()
        self._handed_on = len(cleaned)
        sentences = [sentence.strip() for sentence in re.split(r"(?<=[.!?])\s+", rest) if sentence.strip()]
        return self._group(sentences, final=True)

    def _group(self, sentences: list[str], final: bool) -> list[str]:
        pieces = []
        for sentence in sentences:
            if self._pieces_given == 0:
                pieces.append(sentence)
                self._pieces_given += 1
                continue
            self._pending_sentences.append(sentence)
            if len(" ".join(self._pending_sentences)) >= PIECE_MINIMUM_CHARACTERS:
                pieces.append(" ".join(self._pending_sentences))
                self._pending_sentences = []
                self._pieces_given += 1
        if final and self._pending_sentences:
            pieces.append(" ".join(self._pending_sentences))
            self._pending_sentences = []
            self._pieces_given += 1
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
        # The pieces of the answer `begin` started, waiting to become audio (None: none started).
        self._texts: queue.Queue | None = None
        # This computer's voice spoke last (speech-dispatcher is told to stop too).
        self._speaks_with_dispatcher = False
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
        if not text.strip() or not self.begin(language_code, picked_speaker):
            return False
        for piece in spoken_pieces(text):
            self.say(piece)
        self.end()
        return True

    def begin(self, language_code: str | None = None, picked_speaker: int | None = None, choice: str = "kokoro",
              settings=None) -> bool:
        """Starts a spoken answer whose pieces come later (`say`, then `end`), stopping anything
        still being said: each piece is turned into audio as it arrives, and played in order.
        `choice` is the voice (`speech_voices`), `settings` its keys."""
        from . import speech_voices

        self.stop()
        speaker = speaker_for(language_code, picked_speaker)
        player = self._player or find_player()
        if (choice == speech_voices.KOKORO and speaker is None) or (choice != speech_voices.SYSTEM and player is None):
            self._texts = None
            return False
        self._speech_id += 1
        this_speech = self._speech_id
        texts: queue.Queue = queue.Queue()
        pieces: queue.Queue = queue.Queue()
        self._texts = texts
        speaking_with = [choice]

        def audio(piece: str) -> tuple[str, bytes | None]:
            if speaking_with[0] == speech_voices.SYSTEM:
                return speech_voices.SYSTEM, None
            if speaking_with[0] == speech_voices.KOKORO:
                return "wav", self._audio_for(piece, speaker, language_code)
            return "wav", speech_voices.audio_for(speaking_with[0], piece, settings, language_code)

        def fall_back() -> bool:
            """The next voice down after a failure: Kokoro when it is ready, else this computer's."""
            if speaking_with[0] not in (speech_voices.KOKORO, speech_voices.SYSTEM) and speaker is not None and is_ready():
                speaking_with[0] = speech_voices.KOKORO
            elif speaking_with[0] != speech_voices.SYSTEM and shutil.which("spd-say"):
                speaking_with[0] = speech_voices.SYSTEM
            else:
                return False
            applog.append(f"voice: the rest goes to {speaking_with[0]}")
            return True

        def generate() -> None:
            while (piece := texts.get()) is not None:
                if this_speech != self._speech_id:
                    return
                while True:
                    try:
                        kind, wav = audio(piece)
                        pieces.put((piece, kind, wav))
                        break
                    except (OSError, RuntimeError, ValueError, speech_voices.VoiceError) as failure:
                        applog.append(f"voice: {speaking_with[0]} could not speak a piece ({failure})")
                        if not fall_back():
                            pieces.put(None)
                            return
            pieces.put(None)

        def play() -> None:
            while (item := pieces.get()) is not None:
                piece, kind, wav = item
                if this_speech != self._speech_id:
                    return
                if kind == speech_voices.SYSTEM:
                    self._run_player(speech_voices.system_voice_command(piece, language_code), this_speech, piece)
                else:
                    self._play(wav, player, this_speech, piece)

        threading.Thread(target=generate, daemon=True).start()
        threading.Thread(target=play, daemon=True).start()
        return True

    def say(self, piece: str) -> None:
        """One more piece of the answer `begin` started (nothing when it could not start)."""
        if self._texts is not None and piece.strip():
            self._texts.put(piece.strip())

    def end(self) -> None:
        """No more pieces: what was given is said to the end."""
        if self._texts is not None:
            self._texts.put(None)
            self._texts = None

    def play_audio(self, wav: bytes, label: str) -> None:
        """A recorded line (a notice), cutting off anything still being said."""
        player = self._player or find_player()
        self.stop()
        if player is None:
            return
        self._speech_id += 1
        this_speech = self._speech_id
        threading.Thread(target=lambda: self._play(wav, player, this_speech, label), daemon=True).start()

    def _run_player(self, command: list[str], this_speech: int, piece: str) -> None:
        """This computer's voice: speech-dispatcher says the piece; stopping cancels it."""
        with self._playing_lock:
            if this_speech != self._speech_id:
                return
            try:
                self._playing = subprocess.Popen(command, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            except OSError as failure:
                applog.append(f"voice: this computer's voice could not speak ({failure})")
                return
            self._speaks_with_dispatcher = True
            self.spoken.append(piece)
            playing = self._playing
        playing.wait()

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
        # A streamed answer's pieces stop coming: its threads end instead of waiting for more.
        self.end()
        with self._playing_lock:
            if self._playing is not None and self._playing.poll() is None:
                self._playing.terminate()
            self._playing = None
            if self._speaks_with_dispatcher:
                # speech-dispatcher keeps talking after its client ends: told to stop as well.
                self._speaks_with_dispatcher = False
                try:
                    subprocess.Popen(["spd-say", "--cancel"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
                except OSError:
                    pass

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
