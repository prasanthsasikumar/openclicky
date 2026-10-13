"""Speech → text. Settings → Dictation → Engine (`linuxDictationEngine`) picks where it goes:

- "auto" (the default): the offline engine when it is downloaded (nothing leaves the computer),
  else the person's own OpenAI key, else the OpenClicky account, else a sentence naming what to add
- "offline": Parakeet on this computer (`offline.py`)
- "openai": the own OpenAI key straight to OpenAI (`/audio/transcriptions`, the model the backend uses)
- "account": the OpenClicky backend on the account token (`POST /agent/transcribe`, base64 JSON)

Own keys never go to the backend, as in the Mac app's `ProviderRouter`.
"""

from __future__ import annotations

import base64
import io
import json
import urllib.error
import urllib.request
import uuid
import wave
from dataclasses import dataclass

from .config import LinuxSettings

TRANSCRIPTION_MODEL = "gpt-4o-mini-transcribe"
OPENAI_TRANSCRIPTIONS_URL = "https://api.openai.com/v1/audio/transcriptions"
REQUEST_TIMEOUT_SECONDS = 30


class TranscriptionError(Exception):
    """Carries the sentence the orb shows."""


DICTATION_ENGINES = ("auto", "offline", "openai", "account")
NEEDS_AN_ENGINE = ("dictation needs an engine: download the offline one (settings, dictation, about 490 MB, "
                   "no key needed), or add an openai key or an openclicky account")


@dataclass(frozen=True)
class TranscriptionRoute:
    kind: str  # "offline" | "openai" | "backend"
    request: urllib.request.Request | None  # None for "offline"


def multipart_form(fields: dict[str, str], file_field: str, file_name: str, file_bytes: bytes, file_mime: str) -> tuple[bytes, str]:
    boundary = f"openclicky-{uuid.uuid4().hex}"
    lines: list[bytes] = []
    for name, value in fields.items():
        lines += [f"--{boundary}".encode(), f'Content-Disposition: form-data; name="{name}"'.encode(), b"", value.encode()]
    lines += [
        f"--{boundary}".encode(),
        f'Content-Disposition: form-data; name="{file_field}"; filename="{file_name}"'.encode(),
        f"Content-Type: {file_mime}".encode(),
        b"",
        file_bytes,
        f"--{boundary}--".encode(),
        b"",
    ]
    return b"\r\n".join(lines), f"multipart/form-data; boundary={boundary}"


def engine_for(settings: LinuxSettings, offline_installed: bool) -> str:
    """Which engine this take goes to: "offline", "openai" or "backend"; raises with a sentence."""
    chosen = settings.dictation_engine
    if chosen == "offline":
        if not offline_installed:
            raise TranscriptionError("the offline engine is not downloaded yet: settings, dictation, download")
        return "offline"
    if chosen == "openai":
        if not settings.openai_api_key:
            raise TranscriptionError("dictation is set to openai, but there is no openai key: add one in settings")
        return "openai"
    if chosen == "account":
        if not settings.account_token:
            raise TranscriptionError("dictation is set to the openclicky account, but you are not signed in: add a token in settings")
        return "backend"
    if offline_installed:
        return "offline"
    if settings.openai_api_key:
        return "openai"
    if settings.account_token:
        return "backend"
    raise TranscriptionError(NEEDS_AN_ENGINE)


def route_for(wav_bytes: bytes, settings: LinuxSettings, offline_installed: bool = False) -> TranscriptionRoute:
    engine = engine_for(settings, offline_installed)
    if engine == "offline":
        return TranscriptionRoute("offline", None)
    if engine == "openai":
        fields = {"model": TRANSCRIPTION_MODEL, "response_format": "json"}
        if settings.bare_language_code:
            fields["language"] = settings.bare_language_code
        body, content_type = multipart_form(fields, "file", "audio.wav", wav_bytes, "audio/wav")
        request = urllib.request.Request(OPENAI_TRANSCRIPTIONS_URL, data=body, method="POST", headers={
            "Authorization": f"Bearer {settings.openai_api_key}", "Content-Type": content_type})
        return TranscriptionRoute("openai", request)
    if engine == "backend":
        payload: dict[str, str] = {"audio": base64.b64encode(wav_bytes).decode("ascii"), "mime": "audio/wav"}
        if settings.bare_language_code:
            payload["language"] = settings.bare_language_code
        request = urllib.request.Request(f"{settings.backend_url}/agent/transcribe", data=json.dumps(payload).encode(), method="POST", headers={
            "Authorization": f"Bearer {settings.account_token}", "Content-Type": "application/json"})
        return TranscriptionRoute("backend", request)
    raise TranscriptionError(NEEDS_AN_ENGINE)


def transcribe(wav_bytes: bytes, settings: LinuxSettings, offline_engine=None) -> str:
    """`offline_engine` (an `offline.OfflineEngine`) is used when the route is offline."""
    from . import offline

    route = route_for(wav_bytes, settings, offline_installed=offline.is_ready())
    if route.kind == "offline":
        with wave.open(io.BytesIO(wav_bytes)) as wav_file:
            pcm = wav_file.readframes(wav_file.getnframes())
            sample_rate = wav_file.getframerate()
        try:
            return (offline_engine or offline.OfflineEngine()).transcribe(pcm, sample_rate)
        except offline.OfflineEngineError as failure:
            raise TranscriptionError(str(failure)) from failure
    vendor = "openai" if route.kind == "openai" else "the openclicky backend"
    try:
        with urllib.request.urlopen(route.request, timeout=REQUEST_TIMEOUT_SECONDS) as response:
            answer = json.loads(response.read().decode("utf-8"))
    except urllib.error.HTTPError as refusal:
        if refusal.code == 401:
            raise TranscriptionError(f"{vendor} refused the key or token (401). check ~/.openclicky/shell.json") from refusal
        raise TranscriptionError(f"{vendor} could not transcribe ({refusal.code})") from refusal
    except (urllib.error.URLError, TimeoutError, OSError) as failure:
        raise TranscriptionError(f"couldn't reach {vendor}: {getattr(failure, 'reason', failure)}") from failure
    except ValueError as failure:
        raise TranscriptionError(f"{vendor} sent back something that isn't json") from failure
    text = answer.get("text") if isinstance(answer, dict) else None
    return text if isinstance(text, str) else ""
