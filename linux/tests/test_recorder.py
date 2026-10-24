"""The recorder with a shell command standing in for parecord: it writes known bytes, then waits
to be stopped, the way a real recorder streams until SIGINT."""

import io
import time
import wave

import pytest

from openclicky_linux.recorder import MicrophoneRecording, RecorderUnavailable, pcm_seconds, wav_from_pcm

# 3 bytes: the odd one must be dropped (a signal can land mid-sample).
STREAMS_THEN_WAITS = ("sh", "-c", "printf 'abc'; exec sleep 30")
DIES_AT_ONCE = ("sh", "-c", "echo 'no such device' >&2; exit 1")


def test_stop_returns_whole_samples_of_what_was_recorded():
    recording = MicrophoneRecording(STREAMS_THEN_WAITS)
    time.sleep(0.2)
    started = time.monotonic()
    assert recording.stop() == b"ab"
    assert time.monotonic() - started < 2


def test_a_recorder_that_dies_says_why():
    recording = MicrophoneRecording(DIES_AT_ONCE)
    time.sleep(0.2)
    with pytest.raises(RecorderUnavailable, match="no such device"):
        recording.stop()


def test_cancel_ends_the_recorder():
    recording = MicrophoneRecording(STREAMS_THEN_WAITS)
    recording.cancel()
    assert recording._process.poll() is not None


def test_the_wav_is_16_khz_mono_16_bit():
    pcm = b"\x01\x00" * 16_000
    assert pcm_seconds(pcm) == 1.0
    with wave.open(io.BytesIO(wav_from_pcm(pcm))) as wav_file:
        assert (wav_file.getframerate(), wav_file.getnchannels(), wav_file.getsampwidth(), wav_file.getnframes()) == (16_000, 1, 2, 16_000)


# MARK: picking and testing the microphone

from openclicky_linux.recorder import (  # noqa: E402
    Microphone, find_recorder_command, level_of, microphones_from_pactl_json,
)

PACTL_SOURCES = [  # the shape `pactl -f json list sources` gives (PipeWire 1.4, pactl 17)
    {"name": "alsa_input.usb-Blue_Yeti-00.analog-stereo", "description": "Yeti Stereo Microphone Analog Stereo",
     "properties": {"device.class": "sound"}},
    {"name": "alsa_output.pci-0000_00_1f.3.analog-stereo.monitor", "description": "Monitor of Built-in Audio",
     "properties": {"device.class": "monitor"}},
    {"name": "alsa_input.pci-0000_00_1f.3.analog-stereo", "description": "Built-in Audio Analog Stereo", "properties": {}},
    {"name": "bluez_output.AA_BB.1.monitor", "description": "Monitor of Headphones"},  # marked by its name only
    "not a source",
]


def test_the_microphones_are_listed_by_name_without_the_speaker_monitors():
    assert microphones_from_pactl_json(PACTL_SOURCES) == [
        Microphone("alsa_input.usb-Blue_Yeti-00.analog-stereo", "Yeti Stereo Microphone Analog Stereo"),
        Microphone("alsa_input.pci-0000_00_1f.3.analog-stereo", "Built-in Audio Analog Stereo"),
    ]


def test_a_picked_microphone_goes_to_parecord(monkeypatch):
    monkeypatch.setattr("shutil.which", lambda name: f"/usr/bin/{name}")
    assert find_recorder_command("alsa_input.usb-Blue_Yeti-00.analog-stereo")[-1] == "--device=alsa_input.usb-Blue_Yeti-00.analog-stereo"
    assert not any(argument.startswith("--device") for argument in find_recorder_command(None))


def test_arecord_ignores_a_picked_microphone_rather_than_failing(monkeypatch):
    monkeypatch.setattr("shutil.which", lambda name: "/usr/bin/arecord" if name == "arecord" else None)
    assert find_recorder_command("alsa_input.usb-Blue_Yeti-00.analog-stereo")[0] == "arecord"
    assert not any("--device" in argument for argument in find_recorder_command("x"))


def test_loudness():
    assert level_of(b"") == 0.0
    assert level_of(b"\x00\x00" * 1600) == 0.0
    assert abs(level_of((16384).to_bytes(2, "little", signed=True) * 1600) - 0.5) < 0.001


def test_the_test_says_what_it_heard():
    import gi
    gi.require_version("Gtk", "3.0")
    from openclicky_linux.windows import microphone_test_verdict
    assert microphone_test_verdict(0.0001).startswith("✕ Heard nothing at all")
    assert "virtual machine" in microphone_test_verdict(0.0)
    assert microphone_test_verdict(0.005).startswith("✓ It hears something, but quietly")
    assert microphone_test_verdict(0.1) == "✓ Heard you. This microphone works."


def test_parecord_hands_over_audio_in_small_fragments_so_a_short_take_is_not_empty():
    # At its default (about two seconds a fragment) a take under two seconds came back empty.
    from openclicky_linux.recorder import RECORDER_COMMANDS

    assert "--latency-msec=30" in RECORDER_COMMANDS[0]
