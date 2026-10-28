"""Speech → text. Settings → Dictation → Engine (`linuxDictationEngine`) picks where it goes:

- "auto" (the default): the offline engine when it is downloaded (nothing leaves the computer),
  else the person's own OpenAI key, else the OpenClicky account, else a sentence naming what to add
- "offline": Parakeet on this computer (`offline.py`)
- "openai": the own OpenAI key straight to OpenAI (`/audio/transcriptions`, the model the backend uses)
- "account": the OpenClicky backend on the account token (`POST /agent/transcribe`, base64 JSON)
- "sarvam": Sarvam's Saaras (`saaras:v4`) on the person's own Sarvam key (`sarvamKey`), for the
  Indian languages; a take longer than Sarvam's thirty seconds goes in pieces
- "assemblyai": AssemblyAI streaming on a token the account mints (`streaming.py`): words arrive
  while the key is held, which is what live typing needs; anything it cannot do goes to the
  account's upload instead

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
    """Carries the sentence the orb shows; `account_refused` as `ask.AskError`'s."""

    def __init__(self, message: str, account_refused: bool = False) -> None:
        super().__init__(message)
        self.account_refused = account_refused


DICTATION_ENGINES = ("auto", "offline", "openai", "account", "sarvam", "assemblyai")
SARVAM_TRANSCRIPTION_URL = "https://api.sarvam.ai/speech-to-text"
SARVAM_TRANSCRIPTION_MODEL = "saaras:v4"
SARVAM_MAXIMUM_SECONDS = 29
MAXIMUM_KEYTERMS = 50
# A free account dictates on this computer (the Mac's `hearsOnDevice`): the backend answers 402
# "not_on_plan" to its audio.
FREE_ACCOUNT_DICTATES_HERE = ("a free account dictates on this computer, not through openclicky: pick the offline engine in "
                              "settings, dictation")
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
    """Which engine this take goes to: "offline", "openai", "backend", "sarvam" or "assemblyai";
    raises with a sentence."""
    chosen = settings.dictation_engine
    if chosen == "offline":
        if not offline_installed:
            raise TranscriptionError("the offline engine is not downloaded yet: settings, dictation, download")
        return "offline"
    if chosen == "openai":
        if not settings.openai_api_key:
            raise TranscriptionError("dictation is set to openai, but there is no openai key: add one in settings")
        return "openai"
    if chosen == "sarvam":
        if not settings.sarvam_api_key:
            raise TranscriptionError("dictation is set to sarvam, but there is no sarvam key: add one in settings")
        return "sarvam"
    if chosen == "assemblyai":
        if not settings.account_token:
            raise TranscriptionError("assemblyai needs an openclicky account: sign in, in settings")
        return "assemblyai"
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


def sarvam_request(wav_bytes: bytes, settings: LinuxSettings, keyterms: tuple[str, ...] = ()) -> urllib.request.Request:
    fields = [("model", SARVAM_TRANSCRIPTION_MODEL), ("language_code", settings.language_code or "unknown")]
    fields += [("keyterms", term) for term in keyterms[:MAXIMUM_KEYTERMS]]
    boundary = f"openclicky-{uuid.uuid4().hex}"
    body = b"".join(f'--{boundary}\r\nContent-Disposition: form-data; name="{name}"\r\n\r\n{value}\r\n'.encode() for name, value in fields)
    body += (f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="take.wav"\r\nContent-Type: audio/wav\r\n\r\n'.encode()
             + wav_bytes + f"\r\n--{boundary}--\r\n".encode())
    # A pasted key often brings a newline, and an untrimmed one is refused like a wrong one.
    return urllib.request.Request(SARVAM_TRANSCRIPTION_URL, data=body, method="POST", headers={
        "api-subscription-key": (settings.sarvam_api_key or "").strip(), "Content-Type": f"multipart/form-data; boundary={boundary}"})


def wav_pieces(wav_bytes: bytes, seconds: int) -> list[bytes]:
    """The take cut into WAVs of at most `seconds` each."""
    with wave.open(io.BytesIO(wav_bytes)) as wav_file:
        rate, width, channels = wav_file.getframerate(), wav_file.getsampwidth(), wav_file.getnchannels()
        frames = wav_file.readframes(wav_file.getnframes())
    step = rate * width * channels * seconds
    pieces = []
    for start in range(0, max(len(frames), 1), step):
        buffer = io.BytesIO()
        with wave.open(buffer, "wb") as piece:
            piece.setnchannels(channels)
            piece.setsampwidth(width)
            piece.setframerate(rate)
            piece.writeframes(frames[start:start + step])
        pieces.append(buffer.getvalue())
    return pieces


def route_for(wav_bytes: bytes, settings: LinuxSettings, offline_installed: bool = False, keyterms: tuple[str, ...] = ()) -> TranscriptionRoute:
    engine = engine_for(settings, offline_installed)
    if engine == "offline":
        return TranscriptionRoute("offline", None)
    if engine == "sarvam":
        return TranscriptionRoute("sarvam", sarvam_request(wav_bytes, settings, keyterms))
    if engine == "assemblyai":
        # Streamed while the key was held (`streaming.py`); a take that reaches here is uploaded.
        engine = "backend"
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


def transcribe(wav_bytes: bytes, settings: LinuxSettings, offline_engine=None, keyterms: tuple[str, ...] = ()) -> str:
    """`offline_engine` (an `offline.OfflineEngine`) is used when the route is offline; `keyterms`
    (the dictionary's words) help Sarvam spell names."""
    from . import offline

    route = route_for(wav_bytes, settings, offline_installed=offline.is_ready(), keyterms=keyterms)
    if route.kind == "sarvam":
        return " ".join(part for part in (_send(sarvam_request(piece, settings, keyterms), "sarvam", "transcript")
                                          for piece in wav_pieces(wav_bytes, SARVAM_MAXIMUM_SECONDS)) if part).strip()
    if route.kind == "offline":
        with wave.open(io.BytesIO(wav_bytes)) as wav_file:
            pcm = wav_file.readframes(wav_file.getnframes())
            sample_rate = wav_file.getframerate()
        try:
            return (offline_engine or offline.OfflineEngine()).transcribe(pcm, sample_rate)
        except offline.OfflineEngineError as failure:
            raise TranscriptionError(str(failure)) from failure
    return _send(route.request, "openai" if route.kind == "openai" else "the openclicky backend", "text")


def _send(request: urllib.request.Request, vendor: str, text_field: str) -> str:
    try:
        with urllib.request.urlopen(request, timeout=REQUEST_TIMEOUT_SECONDS) as response:
            answer = json.loads(response.read().decode("utf-8"))
    except urllib.error.HTTPError as refusal:
        if refusal.code == 402 and b"not_on_plan" in (refusal.read() or b""):
            raise TranscriptionError(FREE_ACCOUNT_DICTATES_HERE) from refusal
        if refusal.code in (401, 403):
            raise TranscriptionError(f"{vendor} refused the key or token ({refusal.code}). check it in settings",
                                     account_refused=vendor == "the openclicky backend") from refusal
        if refusal.code in (402, 429) and vendor == "sarvam":
            raise TranscriptionError("sarvam is busy or out of credits for this key") from refusal
        raise TranscriptionError(f"{vendor} could not transcribe ({refusal.code})") from refusal
    except (urllib.error.URLError, TimeoutError, OSError) as failure:
        raise TranscriptionError(f"couldn't reach {vendor}: {getattr(failure, 'reason', failure)}") from failure
    except ValueError as failure:
        raise TranscriptionError(f"{vendor} sent back something that isn't json") from failure
    text = answer.get(text_field) if isinstance(answer, dict) else None
    return text.strip() if isinstance(text, str) else ""
