"""Offline dictation: which engine a take goes to, the model's download (checked, cancellable,
never half-installed), the helper process, and, where the real model is downloaded, real speech."""

import functools
import hashlib
import http.server
import io
import sys
import tarfile
import threading
import time
import wave
from pathlib import Path

import pytest

from openclicky_linux import offline
from openclicky_linux.config import LinuxSettings
from openclicky_linux.transcriber import TranscriptionError, engine_for

# MARK: which engine


def test_auto_prefers_this_computer_then_the_openai_key_then_the_account():
    keys = LinuxSettings(openai_api_key="sk", account_token="tok")
    assert engine_for(keys, offline_installed=True) == "offline"
    assert engine_for(keys, offline_installed=False) == "openai"
    assert engine_for(LinuxSettings(account_token="tok"), offline_installed=False) == "backend"
    with pytest.raises(TranscriptionError, match="download the offline one"):
        engine_for(LinuxSettings(), offline_installed=False)


def test_a_chosen_engine_is_used_even_when_another_would_do_and_says_what_it_misses():
    keys = LinuxSettings(openai_api_key="sk", account_token="tok", dictation_engine="openai")
    assert engine_for(keys, offline_installed=True) == "openai"
    assert engine_for(LinuxSettings(account_token="tok", dictation_engine="account"), offline_installed=True) == "backend"
    with pytest.raises(TranscriptionError, match="not downloaded yet"):
        engine_for(LinuxSettings(openai_api_key="sk", dictation_engine="offline"), offline_installed=False)
    with pytest.raises(TranscriptionError, match="no openai key"):
        engine_for(LinuxSettings(dictation_engine="openai"), offline_installed=True)


# MARK: the download

def tiny_model_archive() -> bytes:
    """A stand-in for the 487 MB archive: the same folder and files, a few bytes each."""
    buffer = io.BytesIO()
    with tarfile.open(fileobj=buffer, mode="w:bz2") as archive:
        for name in offline.PARAKEET.files + ("test_wavs/en.wav",):
            data = f"stand-in {name}".encode()
            info = tarfile.TarInfo(f"{offline.PARAKEET.folder_name}/{name}")
            info.size = len(data)
            archive.addfile(info, io.BytesIO(data))
    return buffer.getvalue()


@pytest.fixture
def local_server(tmp_path):
    """Serves tmp_path/served over http on a free port, for the length of a test."""
    served = tmp_path / "served"
    served.mkdir()
    handler = functools.partial(http.server.SimpleHTTPRequestHandler, directory=str(served))
    handler.log_message = lambda *_arguments: None
    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    yield served, f"http://127.0.0.1:{server.server_address[1]}"
    server.shutdown()


def stand_in_asset(base_url: str, archive: bytes, sha256: str | None = None) -> offline.ModelAsset:
    return offline.ModelAsset(url=f"{base_url}/model.tar.bz2", sha256=sha256 or hashlib.sha256(archive).hexdigest(),
                              size_bytes=len(archive), folder_name=offline.PARAKEET.folder_name, files=offline.PARAKEET.files)


def test_the_model_is_downloaded_checked_and_unpacked(local_server, tmp_path, monkeypatch):
    monkeypatch.setenv("XDG_DATA_HOME", str(tmp_path / "data"))
    served, base_url = local_server
    archive = tiny_model_archive()
    (served / "model.tar.bz2").write_bytes(archive)
    asset = stand_in_asset(base_url, archive)
    progress = []
    assert not offline.is_installed(asset)
    offline.download(asset, on_progress=lambda done, total: progress.append((done, total)))
    assert offline.is_installed(asset)
    assert progress[-1] == (len(archive), len(archive))
    # Nothing but the model is left in the models folder (no archive, no working folder).
    assert [path.name for path in offline.models_directory().iterdir()] == [asset.folder_name]
    offline.remove(asset)
    assert not offline.is_installed(asset)


def test_a_download_that_does_not_match_its_checksum_is_thrown_away(local_server, tmp_path, monkeypatch):
    monkeypatch.setenv("XDG_DATA_HOME", str(tmp_path / "data"))
    served, base_url = local_server
    archive = tiny_model_archive()
    (served / "model.tar.bz2").write_bytes(archive)
    with pytest.raises(offline.OfflineEngineError, match="checksum"):
        offline.download(stand_in_asset(base_url, archive, sha256="0" * 64))
    assert list(offline.models_directory().iterdir()) == []


def test_a_cancelled_download_leaves_nothing(local_server, tmp_path, monkeypatch):
    monkeypatch.setenv("XDG_DATA_HOME", str(tmp_path / "data"))
    served, base_url = local_server
    archive = tiny_model_archive() + bytes(3 << 20)  # big enough to be read in several pieces
    (served / "model.tar.bz2").write_bytes(archive)
    cancelled = threading.Event()
    with pytest.raises(offline.OfflineEngineError, match="cancelled"):
        offline.download(stand_in_asset(base_url, archive), on_progress=lambda _done, _total: cancelled.set(), cancelled=cancelled)
    assert list(offline.models_directory().iterdir()) == []


def test_a_refused_download_says_so(local_server, tmp_path, monkeypatch):
    monkeypatch.setenv("XDG_DATA_HOME", str(tmp_path / "data"))
    _served, base_url = local_server
    with pytest.raises(offline.OfflineEngineError, match="download failed"):
        offline.download(stand_in_asset(base_url, b"x"))  # nothing served at that address: 404


# MARK: the helper process

STAND_IN_HELPER = """
import json, sys
for line in sys.stdin:
    request = json.loads(line)
    sys.stdout.write(json.dumps({"text": f"heard {len(request['pcm'])} base64 characters at {request['rate']} Hz"}) + "\\n")
    sys.stdout.flush()
"""


def test_the_helper_answers_each_take_and_ends_when_unloaded(tmp_path, monkeypatch):
    monkeypatch.setattr(offline, "is_installed", lambda asset=offline.PARAKEET: True)
    monkeypatch.setattr(offline, "is_engine_available", lambda: True)
    engine = offline.OfflineEngine(helper_command=[sys.executable, "-c", STAND_IN_HELPER])
    assert engine.transcribe(b"\x00\x00" * 3) == "heard 8 base64 characters at 16000 Hz"
    assert engine.transcribe(b"\x00\x00" * 3, sample_rate=24000).endswith("24000 Hz")  # the same helper, still running
    assert engine.is_running
    engine.unload()
    assert not engine.is_running


def test_the_helper_ends_on_its_own_when_idle(monkeypatch):
    monkeypatch.setattr(offline, "is_installed", lambda asset=offline.PARAKEET: True)
    monkeypatch.setattr(offline, "is_engine_available", lambda: True)
    engine = offline.OfflineEngine(idle_seconds=0.3, helper_command=[sys.executable, "-c", STAND_IN_HELPER])
    engine.transcribe(b"\x00\x00")
    time.sleep(1.0)
    assert not engine.is_running


def test_a_helper_that_dies_is_reported_not_hung(monkeypatch):
    monkeypatch.setattr(offline, "is_installed", lambda asset=offline.PARAKEET: True)
    monkeypatch.setattr(offline, "is_engine_available", lambda: True)
    engine = offline.OfflineEngine(helper_command=[sys.executable, "-c", "import sys; sys.exit(3)"])
    with pytest.raises(offline.OfflineEngineError, match="stopped"):
        engine.transcribe(b"\x00\x00")


def test_without_the_model_it_says_where_to_get_it(tmp_path, monkeypatch):
    monkeypatch.setenv("XDG_DATA_HOME", str(tmp_path))
    monkeypatch.setattr(offline, "is_engine_available", lambda: True)
    with pytest.raises(offline.OfflineEngineError, match="settings, dictation, download"):
        offline.OfflineEngine().transcribe(b"\x00\x00")


# MARK: the real model, where it is downloaded

real_model = pytest.mark.skipif(not offline.is_ready(), reason="the offline model is not downloaded (Settings → Dictation → Download)")


def sample(name: str) -> tuple[bytes, int]:
    with wave.open(str(offline.model_folder() / "test_wavs" / name)) as wav_file:
        return wav_file.readframes(wav_file.getnframes()), wav_file.getframerate()


@real_model
def test_the_real_model_transcribes_real_speech():
    engine = offline.OfflineEngine()
    try:
        assert engine.transcribe(*sample("en.wav")) == "Ask not what your country can do for you, ask what you can do for your country."
        assert engine.transcribe(*sample("de.wav")) == "Alles hat ein Ende, nur die Wurst hat zwei."
    finally:
        engine.unload()
