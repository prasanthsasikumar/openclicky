"""Offline dictation: NVIDIA Parakeet TDT 0.6B v3 (int8) through sherpa-onnx, the Mac app's offline
engine for Macs before macOS 26 (`Parakeet/ParakeetDictation.swift`), with the same model, the same
pinned checksum and the same settings. No key, and the audio never leaves the computer. 25 European
languages, English included, with its own punctuation; no Indian languages.

- The model (487 MB) is downloaded once, on request (Settings → Dictation → Download), checked
  against its SHA-256 while it arrives, and unpacked into `$XDG_DATA_HOME/openclicky/models`.
- It runs in a helper process (`offline_helper.py`), as on the Mac: sherpa-onnx gives a model's
  memory back only when its process ends. The helper starts when a take starts (the model loads
  while the person speaks), answers each take on one JSON line, and ends after 180 s without one.
"""

from __future__ import annotations

import base64
import hashlib
import json
import os
import shutil
import subprocess
import sys
import tarfile
import tempfile
import threading
import urllib.request
from dataclasses import dataclass
from pathlib import Path
from typing import Callable

from . import applog


@dataclass(frozen=True)
class ModelAsset:
    url: str
    sha256: str
    size_bytes: int
    folder_name: str
    files: tuple[str, ...]


PARAKEET = ModelAsset(
    url="https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8.tar.bz2",
    sha256="5793d0fd397c5778d2cf2126994d58e9d56b1be7c04d13c7a15bb1b4eafb16bf",
    size_bytes=487_170_055,
    folder_name="sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8",
    files=("encoder.int8.onnx", "decoder.int8.onnx", "joiner.int8.onnx", "tokens.txt"),
)
IDLE_SECONDS_BEFORE_UNLOADING = 180.0


class OfflineEngineError(Exception):
    """Carries the sentence shown to the person."""


def models_directory() -> Path:
    # OPENCLICKY_MODELS_DIR: the tests give the downloaded models to a run with its own data folder.
    if os.environ.get("OPENCLICKY_MODELS_DIR"):
        return Path(os.environ["OPENCLICKY_MODELS_DIR"])
    data_home = Path(os.environ.get("XDG_DATA_HOME") or Path.home() / ".local" / "share")
    return data_home / "openclicky" / "models"


def model_folder(asset: ModelAsset = PARAKEET) -> Path:
    return models_directory() / asset.folder_name


def is_installed(asset: ModelAsset = PARAKEET) -> bool:
    folder = model_folder(asset)
    # Files, or folders (Kokoro's espeak-ng-data and dict).
    return all((folder / name).exists() for name in asset.files)


def is_ready(asset: ModelAsset = PARAKEET) -> bool:
    """Downloaded, and sherpa-onnx is there to run it: what "auto" dictation checks."""
    return is_installed(asset) and is_engine_available()


def is_engine_available() -> bool:
    """sherpa-onnx is installed (it is an optional dependency: `pip install openclicky-linux[offline]`)."""
    try:
        import sherpa_onnx  # noqa: F401
    except ImportError:
        return False
    return True


def installed_size_bytes(asset: ModelAsset = PARAKEET) -> int:
    folder = model_folder(asset)
    return sum(path.stat().st_size for path in folder.rglob("*") if path.is_file()) if folder.exists() else 0


def download(asset: ModelAsset = PARAKEET, on_progress: Callable[[int, int], None] = lambda _done, _total: None,
             cancelled: threading.Event | None = None) -> None:
    """Downloads, checks and unpacks the model. Raises `OfflineEngineError` (with a sentence) on a
    refused download, a wrong checksum, too little space or a cancel; nothing half-done is left."""
    cancelled = cancelled or threading.Event()
    directory = models_directory()
    directory.mkdir(parents=True, exist_ok=True)
    # The archive and its unpacked copy need about twice its size while it is being installed.
    if shutil.disk_usage(directory).free < asset.size_bytes * 3:
        raise OfflineEngineError(f"not enough free space: the offline engine needs about {asset.size_bytes * 3 // 1_000_000} MB while it installs")
    working = Path(tempfile.mkdtemp(prefix=".download-", dir=directory))
    try:
        archive = working / "model.tar.bz2"
        digest = hashlib.sha256()
        received = 0
        try:
            with urllib.request.urlopen(asset.url, timeout=60) as response, open(archive, "wb") as archive_file:
                total = int(response.headers.get("Content-Length") or asset.size_bytes)
                while chunk := response.read(1 << 20):
                    if cancelled.is_set():
                        raise OfflineEngineError("download cancelled")
                    archive_file.write(chunk)
                    digest.update(chunk)
                    received += len(chunk)
                    on_progress(received, total)
        except (OSError, ValueError) as failure:
            raise OfflineEngineError(f"the download failed: {getattr(failure, 'reason', failure)}") from failure
        if digest.hexdigest() != asset.sha256:
            raise OfflineEngineError("the download did not match its checksum, so it was thrown away. try again")
        unpacked = working / "unpacked"
        with tarfile.open(archive, "r:bz2") as model_archive:
            # Only plain files and folders, only inside the destination (the "data" filter).
            model_archive.extractall(unpacked, filter="data")
        source = unpacked / asset.folder_name
        if not all((source / name).exists() for name in asset.files):
            raise OfflineEngineError("the download is missing model files")
        destination = model_folder(asset)
        if destination.exists():
            shutil.rmtree(destination)
        source.rename(destination)
        applog.append(f"offline model {asset.folder_name}: installed ({installed_size_bytes(asset) // 1_000_000} MB)")
    finally:
        shutil.rmtree(working, ignore_errors=True)


def remove(asset: ModelAsset = PARAKEET) -> None:
    shutil.rmtree(model_folder(asset), ignore_errors=True)
    applog.append(f"offline model {asset.folder_name}: removed")


class OfflineEngine:
    """The helper process that holds the model. Safe to call from any thread (one take at a time)."""

    def __init__(self, asset: ModelAsset = PARAKEET, idle_seconds: float = IDLE_SECONDS_BEFORE_UNLOADING,
                 helper_command: list[str] | None = None) -> None:
        self._asset = asset
        self._idle_seconds = idle_seconds
        self._helper_command = helper_command
        self._process: subprocess.Popen | None = None
        self._lock = threading.Lock()
        self._idle_timer: threading.Timer | None = None

    def _command(self) -> list[str]:
        return self._helper_command or [sys.executable, "-m", "openclicky_linux.offline_helper", str(model_folder(self._asset))]

    def prepare(self) -> None:
        """Starts the helper now (a take began), so the model has loaded by the time it is needed."""
        if not is_installed(self._asset):
            return
        threading.Thread(target=self._ensure_started, daemon=True).start()

    def _ensure_started(self) -> subprocess.Popen:
        with self._lock:
            if self._process is None or self._process.poll() is not None:
                self._process = subprocess.Popen(self._command(), stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                                 stderr=subprocess.DEVNULL, text=True)
                applog.append("offline engine: loading")
            self._restart_idle_timer()
            return self._process

    def _restart_idle_timer(self) -> None:
        if self._idle_timer is not None:
            self._idle_timer.cancel()
        self._idle_timer = threading.Timer(self._idle_seconds, self.unload)
        self._idle_timer.daemon = True
        self._idle_timer.start()

    def transcribe(self, pcm: bytes, sample_rate: int = 16000) -> str:
        """Mono 16-bit PCM (the app records 16 kHz) → the words."""
        if not is_engine_available():
            raise OfflineEngineError("the offline engine needs sherpa-onnx: pip install sherpa-onnx")
        if not is_installed(self._asset):
            raise OfflineEngineError("the offline engine is not downloaded yet: settings, dictation, download")
        process = self._ensure_started()
        request = json.dumps({"pcm": base64.b64encode(pcm).decode("ascii"), "rate": sample_rate}) + "\n"
        with self._lock:
            try:
                assert process.stdin is not None and process.stdout is not None
                process.stdin.write(request)
                process.stdin.flush()
                # The first answer after a start also waits for the model to load (about a second).
                line = process.stdout.readline()
            except (OSError, ValueError) as failure:
                raise OfflineEngineError("the offline engine stopped. try again") from failure
            self._restart_idle_timer()
        if not line:
            raise OfflineEngineError("the offline engine stopped. try again")
        answer = json.loads(line)
        if "error" in answer:
            raise OfflineEngineError(f"the offline engine could not transcribe: {answer['error']}")
        return answer.get("text", "")

    def unload(self) -> None:
        """Ends the helper, which gives all of the model's memory back."""
        with self._lock:
            process, self._process = self._process, None
            if self._idle_timer is not None:
                self._idle_timer.cancel()
                self._idle_timer = None
        if process is not None and process.poll() is None:
            try:
                assert process.stdin is not None
                process.stdin.close()  # the helper ends when its input does
                process.wait(timeout=3)
            except (OSError, subprocess.TimeoutExpired):
                process.kill()
            applog.append("offline engine: unloaded")

    @property
    def is_running(self) -> bool:
        return self._process is not None and self._process.poll() is None
