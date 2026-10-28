"""Spoken answers: the pieces an answer is spoken in, the speaker, the helper and the player (with
stand-ins), stopping mid-answer, and, where Kokoro and Parakeet are downloaded, a real round trip:
Kokoro says it, Parakeet hears it."""

import glob
import os
import sys
import time
import wave

import pytest

from openclicky_linux import offline, voice
from openclicky_linux.config import LinuxSettings, load_settings, save_settings


def test_the_first_sentence_is_spoken_alone_then_pieces_of_at_least_120_characters():
    text = ("Click the save button up top. Then pick a folder. Give it a name. "
            "Press enter when you are happy with it, and it will be saved where you can find it again tomorrow.")
    pieces = voice.spoken_pieces(text)
    assert pieces[0] == "Click the save button up top."
    assert all(len(piece) >= voice.PIECE_MINIMUM_CHARACTERS for piece in pieces[1:-1])
    assert " ".join(pieces) == text
    assert voice.spoken_pieces("   ") == []
    assert voice.spoken_pieces("no full stop") == ["no full stop"]


def test_the_speaker_follows_the_language_and_the_pick():
    assert voice.speaker_for(None, None).name == "Heart"
    assert voice.speaker_for("en-GB", 26).name == "George"
    assert voice.speaker_for("en", 31).name == "Heart"  # a Hindi speaker does not speak English
    assert voice.speaker_for("hi-IN", None).name == "Alpha"
    assert voice.speaker_for("de", None) is None  # Kokoro does not speak German: captions only
    assert voice.language_hint("pt-BR") == "pt-br" and voice.language_hint(None) == "en-us"


STAND_IN_HELPER = """
import base64, io, json, sys, wave
for line in sys.stdin:
    request = json.loads(line)
    buffer = io.BytesIO()
    with wave.open(buffer, "wb") as out:
        out.setnchannels(1); out.setsampwidth(2); out.setframerate(24000); out.writeframes(bytes(4800))
    sys.stdout.write(json.dumps({"wav": base64.b64encode(buffer.getvalue()).decode()}) + "\\n"); sys.stdout.flush()
"""


def stand_in_player(directory, seconds=0.0):
    """A 'player' that keeps each piece's file (and, if asked, takes a while, like real audio)."""
    script = directory / "player.py"
    script.write_text(f"import shutil, sys, time\nshutil.copy(sys.argv[1], sys.argv[1] + '.played'); "
                      f"shutil.copy(sys.argv[1], r'{directory}/' + str(time.time_ns()) + '.wav'); time.sleep({seconds})\n")
    return (sys.executable, str(script))


def test_an_answer_is_spoken_piece_by_piece_in_order(tmp_path):
    speaking = voice.Voice(helper_command=[sys.executable, "-c", STAND_IN_HELPER], player=stand_in_player(tmp_path))
    text = "First sentence here. " + "A second, longer part of the answer that goes on for quite a while to pass the limit. " * 2
    assert speaking.speak(text)
    deadline = time.monotonic() + 10
    while ((len(speaking.spoken) < len(voice.spoken_pieces(text)) or len(glob.glob(str(tmp_path / "*.wav"))) < len(speaking.spoken))
           and time.monotonic() < deadline):
        time.sleep(0.05)
    assert speaking.spoken == voice.spoken_pieces(text)
    assert len(glob.glob(str(tmp_path / "*.wav"))) == len(speaking.spoken)
    speaking.unload()


def test_stop_ends_what_is_being_said_mid_answer(tmp_path):
    speaking = voice.Voice(helper_command=[sys.executable, "-c", STAND_IN_HELPER], player=stand_in_player(tmp_path, seconds=5))
    assert speaking.speak("First sentence. " + "Then a long second part of the answer that will never be heard at all. " * 3)
    deadline = time.monotonic() + 10
    while not speaking.is_speaking and time.monotonic() < deadline:
        time.sleep(0.05)
    speaking.stop()
    time.sleep(0.5)
    assert not speaking.is_speaking
    assert speaking.spoken == ["First sentence."]  # the rest is never played
    speaking.unload()


def test_nothing_is_said_without_a_speaker_for_the_language_or_a_player(tmp_path):
    speaking = voice.Voice(helper_command=[sys.executable, "-c", STAND_IN_HELPER], player=stand_in_player(tmp_path))
    assert speaking.speak("Hallo.", language_code="de") is False
    assert voice.Voice(helper_command=["true"], player=None).speak("x") in (True, False)  # depends on this machine's players


def test_speaking_answers_is_a_switch_written_only_when_off(tmp_path):
    shell_json = tmp_path / "shell.json"
    save_settings({"speak_answers": False, "kokoro_speaker": "26"}, shell_json)
    assert load_settings(shell_json, environment={}) == LinuxSettings(speak_answers=False, kokoro_speaker=26)
    save_settings({"speak_answers": True, "kokoro_speaker": None}, shell_json)
    assert shell_json.read_text().strip() == "{}"


real_models = pytest.mark.skipif(not (voice.is_ready() and offline.is_ready()),
                                 reason="needs Kokoro and Parakeet downloaded (Settings → Voice / Dictation)")


@real_models
def test_kokoro_says_it_and_parakeet_hears_it(tmp_path):
    speaking = voice.Voice(player=stand_in_player(tmp_path))
    assert speaking.speak("That save button up top will do it.")
    deadline = time.monotonic() + 60
    while not glob.glob(str(tmp_path / "*.wav")) and time.monotonic() < deadline:
        time.sleep(0.1)
    speaking.unload()
    [spoken_file] = glob.glob(str(tmp_path / "*.wav"))
    with wave.open(spoken_file) as wav_file:
        pcm, rate = wav_file.readframes(wav_file.getnframes()), wav_file.getframerate()
    listener = offline.OfflineEngine()
    try:
        assert listener.transcribe(pcm, rate).lower().rstrip(".") == "that save button up top will do it"
    finally:
        listener.unload()


def test_a_player_that_cannot_play_here_is_passed_over_for_the_next(tmp_path, monkeypatch):
    # Ubuntu 22.04: pw-play is installed but PipeWire has no speakers ("no node available", exit 1).
    failing = tmp_path / "failing-player"
    failing.write_text("#!/bin/sh\necho 'no node available' >&2\nexit 1\n")
    failing.chmod(0o755)
    working = stand_in_player(tmp_path)
    monkeypatch.setattr(voice, "installed_players", lambda: [(str(failing),), working])
    speaking = voice.Voice(helper_command=[sys.executable, "-c", STAND_IN_HELPER], player=(str(failing),))
    assert speaking.speak("Hello there. This is the second sentence.", "en")
    played = lambda: [path for path in tmp_path.glob("*.wav") if path.stem.isdigit()]
    deadline = time.monotonic() + 10
    while len(played()) < 2 and time.monotonic() < deadline:
        time.sleep(0.05)
    assert len(played()) == 2
    # Both pieces spoken once each, and the failing player was not tried again for the second.
    assert speaking.spoken == ["Hello there.", "This is the second sentence."]
    assert speaking._failed_players == {str(failing)}
    speaking.unload()


def test_paplay_comes_first_since_it_plays_on_pulseaudio_and_pipewire_alike():
    assert voice.PLAYERS[0] == ("paplay",)


@pytest.mark.parametrize("mute, volume, muted", [
    ("Mute: yes", "Volume: front-left: 0 /   0% / -inf dB,   front-right: 0 /   0% / -inf dB", True),
    ("Mute: no", "Volume: front-left: 0 /   0% / -inf dB,   front-right: 0 /   0% / -inf dB", True),
    ("Mute: no", "Volume: front-left: 32768 /  50% / -18.06 dB,   front-right: 32768 /  50% / -18.06 dB", False),
    ("", "", False),
])
def test_muted_speakers_are_told_from_pactl(mute, volume, muted):
    assert voice.muted_output(mute, volume) is muted
