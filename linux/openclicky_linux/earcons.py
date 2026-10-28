"""The short tones around a take (the Mac's `DictationEarcons`): start, stop, done, blocked, error,
notify, goodbye. Synthesised here (a sine with a soft envelope, two notes for the chords), so
nothing is bundled; quiet and short, meant to be felt more than heard. Settings → Dictation →
Sounds turns them off. Played with the first sound player that is installed (`voice.PLAYERS`),
never waited on.
"""

from __future__ import annotations

import math
import os
import struct
import subprocess
import tempfile
import wave
from pathlib import Path

from .voice import find_player

SAMPLE_RATE = 44_100
VOLUME = 0.35

# Notes in Hz with their durations, played one after the other (the Mac's numbers).
NOTES = {
    "start": ((660, 0.06), (880, 0.09)),
    "stop": ((880, 0.06), (660, 0.09)),
    "complete": ((784, 0.07), (1047, 0.11)),
    "blocked": ((330, 0.12),),
    "error": ((392, 0.1), (294, 0.14)),
    "notify": ((988, 0.09),),
    # Quitting: three falling notes, a "see you" rather than an alarm.
    "goodbye": ((1047, 0.08), (784, 0.08), (523, 0.18)),
}


def earcon_pcm(name: str, sample_rate: int = SAMPLE_RATE) -> bytes:
    """The earcon's notes as 16-bit mono PCM, each with an 8 ms attack and a release to silence."""
    samples = []
    for frequency, seconds in NOTES[name]:
        count = int(seconds * sample_rate)
        attack = int(0.008 * sample_rate)
        for index in range(count):
            envelope = index / attack if index < attack else 1 - (index - attack) / max(1, count - attack)
            value = math.sin(2 * math.pi * frequency * index / sample_rate) * envelope * 0.8 * VOLUME
            samples.append(int(max(-1.0, min(1.0, value)) * 32767))
    return struct.pack(f"<{len(samples)}h", *samples)


class EarconPlayer:
    def __init__(self) -> None:
        self._files: dict[str, Path] = {}

    def _file_for(self, name: str) -> Path:
        if name not in self._files or not self._files[name].exists():
            descriptor, path = tempfile.mkstemp(prefix=f"openclicky-{name}-", suffix=".wav")
            with os.fdopen(descriptor, "wb") as file, wave.open(file, "wb") as wav_file:
                wav_file.setnchannels(1)
                wav_file.setsampwidth(2)
                wav_file.setframerate(SAMPLE_RATE)
                wav_file.writeframes(earcon_pcm(name))
            self._files[name] = Path(path)
        return self._files[name]

    def play(self, name: str) -> None:
        player = find_player()
        if player is None or name not in NOTES:
            return
        try:
            subprocess.Popen([*player, str(self._file_for(name))], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                             start_new_session=True)
        except OSError:
            pass

    def remove_files(self) -> None:
        for path in self._files.values():
            path.unlink(missing_ok=True)
        self._files.clear()
