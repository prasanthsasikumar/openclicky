"""The microphone, through the recorder every desktop already has: `parecord` (PulseAudio, and
PipeWire through pipewire-pulse) or `arecord` (ALSA). Raw 16 kHz mono 16-bit PCM on stdout,
which is what the transcription APIs want once it has a WAV header. A command-line recorder
instead of a Python audio library: nothing to compile, and the desktop's own default input is used.
"""

from __future__ import annotations

import array
import io
import json
import math
import shutil
import signal
import subprocess
import threading
import wave
from dataclasses import dataclass

SAMPLE_RATE = 16_000
SAMPLE_WIDTH_BYTES = 2
CHANNELS = 1

# parecord's latency matters: left at its default, the sound server hands over recorded audio in
# fragments of about two seconds and drops the unfinished one when the recorder stops, so a take
# under two seconds came back empty ("didn't catch that") and longer ones lost their end (found on a
# real microphone, 2026-10-23). 30 ms fragments lose nothing.
RECORDER_COMMANDS: tuple[tuple[str, ...], ...] = (
    ("parecord", "--raw", f"--rate={SAMPLE_RATE}", f"--channels={CHANNELS}", "--format=s16le", "--latency-msec=30"),
    ("arecord", "-q", "-t", "raw", "-f", "S16_LE", "-r", str(SAMPLE_RATE), "-c", str(CHANNELS)),
)


class RecorderUnavailable(Exception):
    pass


def find_recorder_command(microphone: str | None = None) -> tuple[str, ...]:
    """The recorder to run; `microphone` is a PulseAudio/PipeWire source name from
    `list_microphones` (None: the system default). Only parecord can pick one; arecord, the ALSA
    fallback, always uses the default."""
    for command in RECORDER_COMMANDS:
        if shutil.which(command[0]):
            if microphone and command[0] == "parecord":
                return command + (f"--device={microphone}",)
            return command
    raise RecorderUnavailable("no microphone recorder found. install pulseaudio-utils (parecord) or alsa-utils (arecord)")


@dataclass(frozen=True)
class Microphone:
    name: str  # the source name the recorder is given
    description: str  # what the person reads ("Built-in Audio Analog Stereo")


def microphones_from_pactl_json(sources: list) -> list[Microphone]:
    """The inputs in `pactl -f json list sources`, without the "Monitor of …" sources: those
    record what the speakers play, not a microphone."""
    microphones = []
    for source in sources:
        if not isinstance(source, dict) or not isinstance(source.get("name"), str):
            continue
        properties = source.get("properties") or {}
        if properties.get("device.class") == "monitor" or source["name"].endswith(".monitor"):
            continue
        microphones.append(Microphone(source["name"], source.get("description") or source["name"]))
    return microphones


def list_microphones() -> list[Microphone]:
    """Every microphone the sound server (PulseAudio, or PipeWire through pipewire-pulse) knows;
    empty without pactl. Asked again each time Settings opens, so a headset plugged in shows up."""
    if not shutil.which("pactl"):
        return []
    try:
        listed = subprocess.run(["pactl", "-f", "json", "list", "sources"], capture_output=True, text=True, timeout=5)
        return microphones_from_pactl_json(json.loads(listed.stdout))
    except (OSError, subprocess.SubprocessError, ValueError):
        return []


def default_microphone_name() -> str | None:
    if not shutil.which("pactl"):
        return None
    try:
        answer = subprocess.run(["pactl", "get-default-source"], capture_output=True, text=True, timeout=5).stdout.strip()
    except (OSError, subprocess.SubprocessError):
        return None
    return answer or None


def level_of(pcm: bytes) -> float:
    """How loud 16-bit PCM is, 0…1 (root mean square). Normal speech near the microphone is
    roughly 0.02–0.3; a microphone that hears nothing stays near 0.0001."""
    samples = array.array("h", pcm[: len(pcm) - len(pcm) % SAMPLE_WIDTH_BYTES])
    if not samples:
        return 0.0
    return math.sqrt(sum(sample * sample for sample in samples) / len(samples)) / 32768


def pcm_seconds(pcm: bytes) -> float:
    return len(pcm) / (SAMPLE_RATE * SAMPLE_WIDTH_BYTES * CHANNELS)


def wav_from_pcm(pcm: bytes) -> bytes:
    buffer = io.BytesIO()
    with wave.open(buffer, "wb") as wav_file:
        wav_file.setnchannels(CHANNELS)
        wav_file.setsampwidth(SAMPLE_WIDTH_BYTES)
        wav_file.setframerate(SAMPLE_RATE)
        wav_file.writeframes(pcm)
    return buffer.getvalue()


class MicrophoneRecording:
    """One take's audio: started at press, stopped (or cancelled) at release."""

    def __init__(self, command: tuple[str, ...] | None = None, microphone: str | None = None) -> None:
        self._process = subprocess.Popen(command or find_recorder_command(microphone), stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        self._chunks: list[bytes] = []
        # The pipe is drained all the time: a full pipe would stall the recorder mid-take.
        self._reader = threading.Thread(target=self._drain, daemon=True)
        self._reader.start()

    def _drain(self) -> None:
        assert self._process.stdout is not None
        for chunk in iter(lambda: self._process.stdout.read(4096), b""):
            self._chunks.append(chunk)

    def current_level(self) -> float:
        """How loud the last tenth of a second was (for a level meter), 0…1."""
        recent = b"".join(self._chunks[-1:])[-SAMPLE_RATE * SAMPLE_WIDTH_BYTES // 10:]
        return level_of(recent)

    def _end_process(self) -> None:
        if self._process.poll() is None:
            # SIGINT lets the recorder flush what it has; SIGKILL only if it ignores that.
            self._process.send_signal(signal.SIGINT)
            try:
                self._process.wait(timeout=1.0)
            except subprocess.TimeoutExpired:
                self._process.kill()
                self._process.wait()
        self._reader.join(timeout=1.0)

    def stop(self) -> bytes:
        """The PCM heard so far. Raises when the recorder died on its own (no microphone, refused)."""
        exited_early = self._process.poll() is not None
        self._end_process()
        pcm = b"".join(self._chunks)
        if exited_early and not pcm:
            error_output = (self._process.stderr.read() if self._process.stderr else b"").decode(errors="replace").strip()
            raise RecorderUnavailable(f"the microphone recorder stopped: {error_output or 'no output'}")
        # Whole samples only: a signal can land mid-sample.
        return pcm[: len(pcm) - len(pcm) % SAMPLE_WIDTH_BYTES]

    def cancel(self) -> None:
        self._end_process()
        self._chunks.clear()
