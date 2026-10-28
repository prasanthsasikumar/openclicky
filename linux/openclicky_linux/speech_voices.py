"""Who speaks the answers (the Mac's `SpeechVoices.swift`, Settings → Voice): the free voice on an
account, a voice key (OpenAI, ElevenLabs, Google Cloud, Google Gemini, Amazon Polly, Sarvam), Kokoro
on this computer, or this computer's own voice (speech-dispatcher). Automatic takes the first that
can speak, own keys first; a picked voice without its key falls back to this computer's. Every cloud
request goes straight to its vendor; none of these keys is ever sent to the OpenClicky backend.
Request building is pure, so it is tested without a network.

Cloud voices answer in MP3 (Gemini and Sarvam in WAV): GStreamer turns MP3 into WAV here, so every
piece plays through the same players as Kokoro's (`voice.py`).

The notices the Mac recorded in its answering voice (`RecordedVoice`, "i couldn't reach
openclicky…") ship with the package and play when the same thing happens here.
"""

from __future__ import annotations

import base64
import datetime
import hashlib
import hmac
import io
import json
import re
import shutil
import urllib.error
import urllib.parse
import urllib.request
import wave
from dataclasses import dataclass
from pathlib import Path

from . import applog
from .config import LinuxSettings

AUTOMATIC, ACCOUNT, OPENAI, ELEVENLABS, GOOGLE, GEMINI, POLLY, SARVAM, KOKORO, SYSTEM = (
    "automatic", "account", "openai", "elevenlabs", "google", "gemini", "polly", "sarvam", "kokoro", "mac")
TITLES = {AUTOMATIC: "Automatic", ACCOUNT: "Free voice (ElevenLabs)", OPENAI: "OpenAI", ELEVENLABS: "ElevenLabs (your key)",
          GOOGLE: "Google Cloud", GEMINI: "Google Gemini", POLLY: "Amazon Polly", SARVAM: "Sarvam", KOKORO: "Kokoro (on this computer)",
          SYSTEM: "This computer (speech-dispatcher)"}
# Own keys first (the person chose to pay for them), then Kokoro once downloaded, then the free
# voice, then this computer. Gemini last of the keys: about four seconds a sentence.
AUTOMATIC_ORDER = (OPENAI, ELEVENLABS, GOOGLE, POLLY, SARVAM, GEMINI, KOKORO, ACCOUNT, SYSTEM)
REQUEST_TIMEOUT_SECONDS = 30

OPENAI_SPEECH_URL = "https://api.openai.com/v1/audio/speech"
OPENAI_SPEECH_MODEL, OPENAI_SPEECH_VOICE = "gpt-4o-mini-tts", "marin"
# Sarah, a premade voice: the free tier refuses library voices.
ELEVENLABS_VOICE_ID, ELEVENLABS_MODEL = "EXAVITQu4vr4xnSDxMaL", "eleven_flash_v2_5"
GOOGLE_SPEECH_URL = "https://texttospeech.googleapis.com/v1/text:synthesize"
GEMINI_SPEECH_MODEL, GEMINI_VOICE = "gemini-3.8-flash-tts", "Kore"
SARVAM_SPEECH_URL, SARVAM_SPEECH_MODEL = "https://api.sarvam.ai/text-to-speech", "bulbul:v3"
SARVAM_LANGUAGES = {"en": "en-IN", "hi": "hi-IN", "bn": "bn-IN", "gu": "gu-IN", "kn": "kn-IN", "ml": "ml-IN", "mr": "mr-IN",
                    "od": "od-IN", "or": "od-IN", "pa": "pa-IN", "ta": "ta-IN", "te": "te-IN"}
POLLY_VOICES = {"en": "Joanna", "hi": "Kajal", "fr": "Lea", "de": "Vicki", "es": "Lucia", "it": "Bianca", "ja": "Kazuha", "ko": "Seoyeon",
                "pt": "Camila", "zh": "Zhiyu"}


class VoiceError(Exception):
    pass


def bare_language(language_code: str | None) -> str:
    return (language_code or "en").split("-")[0].split("_")[0].lower()


@dataclass(frozen=True)
class AWSCredentials:
    """Amazon Polly's credentials as one pasted line: "accessKeyId:secretKey[:region]"."""
    access_key_id: str
    secret_access_key: str
    region: str = "us-east-1"

    @staticmethod
    def parse(stored: str | None) -> "AWSCredentials | None":
        parts = [part.strip() for part in (stored or "").strip().split(":")]
        if len(parts) not in (2, 3) or not parts[0] or not parts[1]:
            return None
        return AWSCredentials(parts[0], parts[1], parts[2] if len(parts) == 3 and parts[2] else "us-east-1")


def can_speak(choice: str, settings: LinuxSettings, kokoro_ready: bool, language_code: str | None) -> bool:
    language = bare_language(language_code)
    if choice == ACCOUNT:
        return bool(settings.account_token)
    if choice == OPENAI:
        return bool(settings.openai_api_key)
    if choice == ELEVENLABS:
        return bool(settings.elevenlabs_api_key)
    if choice == GOOGLE:
        return bool(settings.google_cloud_api_key)
    if choice == GEMINI:
        return bool(settings.gemini_api_key)
    if choice == POLLY:
        return AWSCredentials.parse(settings.amazon_polly_credentials) is not None and language in POLLY_VOICES
    if choice == SARVAM:
        return bool(settings.sarvam_api_key) and language in SARVAM_LANGUAGES
    if choice == KOKORO:
        from .voice import speaker_for

        return kokoro_ready and speaker_for(language_code, None) is not None
    if choice == SYSTEM:
        return shutil.which("spd-say") is not None
    return False


def resolve(settings: LinuxSettings, kokoro_ready: bool, language_code: str | None) -> str | None:
    """The voice that speaks now: the one picked if it can, else this computer's; Automatic, the
    first that can. None when nothing can speak at all."""
    picked = settings.speech_voice or AUTOMATIC
    candidates = AUTOMATIC_ORDER if picked == AUTOMATIC else (picked, SYSTEM)
    return next((choice for choice in candidates if can_speak(choice, settings, kokoro_ready, language_code)), None)


def offered(settings: LinuxSettings, language_code: str | None) -> list[str]:
    """What the picker offers: Automatic, every voice with a key or the account, Kokoro, this
    computer, and the one picked even when its key is gone."""
    picked = settings.speech_voice or AUTOMATIC
    return [choice for choice in (AUTOMATIC, *AUTOMATIC_ORDER)
            if choice in (AUTOMATIC, KOKORO, SYSTEM, picked) or can_speak(choice, settings, False, language_code)]


# MARK: requests


def _json_request(url: str, body: dict, headers: dict[str, str]) -> urllib.request.Request:
    return urllib.request.Request(url, data=json.dumps(body).encode(), method="POST", headers={"Content-Type": "application/json", **headers})


def elevenlabs_body(text: str) -> dict:
    """The body for an own ElevenLabs key and for the account's free voice (the backend relays it)."""
    return {"text": text, "model_id": ELEVENLABS_MODEL, "voice_settings": {"stability": 0.5, "similarity_boost": 0.75}}


def google_voice(language: str) -> dict:
    """The region each language is spoken in, and a Neural2 voice where one is chosen."""
    named = {"en": ("en-US", "en-US-Neural2-F"), "hi": ("hi-IN", "hi-IN-Neural2-A")}
    regions = {"zh": "cmn-CN", "pt": "pt-BR", "ja": "ja-JP", "ko": "ko-KR"}
    if language in named:
        return {"languageCode": named[language][0], "name": named[language][1]}
    if language in ("bn", "gu", "kn", "ml", "mr", "ta", "te", "pa"):
        return {"languageCode": f"{language}-IN"}
    return {"languageCode": regions.get(language, f"{language}-{language.upper()}")}


def speech_request(choice: str, text: str, settings: LinuxSettings, language_code: str | None,
                   now: datetime.datetime | None = None) -> urllib.request.Request:
    language = bare_language(language_code)
    if choice == OPENAI:
        return _json_request(OPENAI_SPEECH_URL, {"model": OPENAI_SPEECH_MODEL, "voice": OPENAI_SPEECH_VOICE, "input": text, "response_format": "mp3"},
                             {"Authorization": f"Bearer {settings.openai_api_key}", "Accept": "audio/mpeg"})
    if choice == ELEVENLABS:
        return _json_request(f"https://api.elevenlabs.io/v1/text-to-speech/{ELEVENLABS_VOICE_ID}?output_format=mp3_44100_128",
                             elevenlabs_body(text), {"xi-api-key": (settings.elevenlabs_api_key or "").strip(), "Accept": "audio/mpeg"})
    if choice == ACCOUNT:
        return _json_request(f"{settings.backend_url}/tts", elevenlabs_body(text),
                             {"Authorization": f"Bearer {settings.account_token}", "Accept": "audio/mpeg"})
    if choice == GOOGLE:
        return _json_request(GOOGLE_SPEECH_URL, {"input": {"text": text}, "voice": google_voice(language), "audioConfig": {"audioEncoding": "MP3"}},
                             {"X-Goog-Api-Key": (settings.google_cloud_api_key or "").strip()})
    if choice == GEMINI:
        return _json_request(f"https://generativelanguage.googleapis.com/v1beta/models/{GEMINI_SPEECH_MODEL}:generateContent",
                             {"contents": [{"parts": [{"text": text}]}],
                              "generationConfig": {"responseModalities": ["AUDIO"],
                                                   "speechConfig": {"voiceConfig": {"prebuiltVoiceConfig": {"voiceName": GEMINI_VOICE}}}}},
                             {"x-goog-api-key": (settings.gemini_api_key or "").strip()})
    if choice == SARVAM:
        return _json_request(SARVAM_SPEECH_URL, {"text": text, "target_language_code": SARVAM_LANGUAGES[language], "model": SARVAM_SPEECH_MODEL},
                             {"api-subscription-key": (settings.sarvam_api_key or "").strip()})
    if choice == POLLY:
        credentials = AWSCredentials.parse(settings.amazon_polly_credentials)
        if credentials is None or language not in POLLY_VOICES:
            raise VoiceError("Amazon Polly needs credentials in settings")
        request = _json_request(f"https://polly.{credentials.region}.amazonaws.com/v1/speech",
                                {"Engine": "neural", "OutputFormat": "mp3", "Text": text, "VoiceId": POLLY_VOICES[language]}, {})
        sign_aws_v4(request, credentials, "polly", now or datetime.datetime.now(datetime.timezone.utc))
        return request
    raise VoiceError(f"no request for the {choice} voice")


def sign_aws_v4(request: urllib.request.Request, credentials: AWSCredentials, service: str, now: datetime.datetime) -> None:
    """AWS Signature Version 4 for one request with a body (the Mac's `AWSSignatureV4`)."""
    parsed = urllib.parse.urlsplit(request.full_url)
    amz_date, date_stamp = now.strftime("%Y%m%dT%H%M%SZ"), now.strftime("%Y%m%d")
    request.add_header("X-Amz-Date", amz_date)
    headers = {"host": parsed.netloc}
    headers.update({name.lower(): value.strip() for name, value in request.header_items()})
    signed_names = sorted(headers)
    canonical_headers = "".join(f"{name}:{headers[name]}\n" for name in signed_names)
    query = "&".join(sorted(parsed.query.split("&"))) if parsed.query else ""
    canonical_request = "\n".join([request.get_method(), parsed.path or "/", query, canonical_headers, ";".join(signed_names),
                                   hashlib.sha256(request.data or b"").hexdigest()])
    scope = f"{date_stamp}/{credentials.region}/{service}/aws4_request"
    string_to_sign = "\n".join(["AWS4-HMAC-SHA256", amz_date, scope, hashlib.sha256(canonical_request.encode()).hexdigest()])
    key = f"AWS4{credentials.secret_access_key}".encode()
    for part in (date_stamp, credentials.region, service, "aws4_request"):
        key = hmac.new(key, part.encode(), hashlib.sha256).digest()
    signature = hmac.new(key, string_to_sign.encode(), hashlib.sha256).hexdigest()
    request.add_header("Authorization", f"AWS4-HMAC-SHA256 Credential={credentials.access_key_id}/{scope}, "
                                        f"SignedHeaders={';'.join(signed_names)}, Signature={signature}")


def decode_answer(choice: str, data: bytes) -> bytes:
    """The audio in a vendor's answer: the body itself, or base64 inside JSON."""
    if choice == GOOGLE:
        content = json.loads(data).get("audioContent")
        return base64.b64decode(content) if content else b""
    if choice == SARVAM:
        audios = json.loads(data).get("audios") or []
        return base64.b64decode(audios[0]) if audios else b""
    if choice == GEMINI:
        parts = (((json.loads(data).get("candidates") or [{}])[0].get("content") or {}).get("parts")) or []
        inline = next((part["inlineData"] for part in parts if isinstance(part, dict) and "inlineData" in part), None)
        if not inline:
            return b""
        audio = base64.b64decode(inline.get("data", ""))
        mime = (inline.get("mimeType") or "").lower()
        if "l16" in mime or "pcm" in mime:
            # Raw 16-bit PCM (little-endian, whatever the label says), wrapped as WAV.
            rate = int(match.group(1)) if (match := re.search(r"rate=(\d+)", mime)) else 24000
            return pcm_to_wav(audio, rate)
        return audio
    return data


def audio_for(choice: str, text: str, settings: LinuxSettings, language_code: str | None, send=None) -> bytes:
    """One piece of an answer in a cloud voice, as WAV; raises VoiceError with what went wrong."""
    request = speech_request(choice, text, settings, language_code)
    try:
        if send is not None:
            data = send(request)
        else:
            with urllib.request.urlopen(request, timeout=REQUEST_TIMEOUT_SECONDS) as response:
                data = response.read()
    except urllib.error.HTTPError as refusal:
        raise VoiceError(f"{TITLES[choice]} refused ({refusal.code})") from refusal
    except (urllib.error.URLError, OSError, TimeoutError) as failure:
        raise VoiceError(f"couldn't reach {TITLES[choice]}") from failure
    try:
        audio = decode_answer(choice, data)
    except (ValueError, KeyError, IndexError, AttributeError) as failure:
        raise VoiceError(f"{TITLES[choice]}'s answer had no audio in it") from failure
    if not audio:
        raise VoiceError(f"{TITLES[choice]}'s answer had no audio in it")
    return as_wav(audio)


def pcm_to_wav(pcm: bytes, rate: int) -> bytes:
    buffer = io.BytesIO()
    with wave.open(buffer, "wb") as wav_file:
        wav_file.setnchannels(1)
        wav_file.setsampwidth(2)
        wav_file.setframerate(rate)
        wav_file.writeframes(pcm)
    return buffer.getvalue()


def as_wav(audio: bytes) -> bytes:
    """WAV as it is; MP3 decoded by GStreamer (the players here read WAV only)."""
    if audio[:4] == b"RIFF":
        return audio
    gi = __import__("gi")
    gi.require_version("Gst", "1.0")
    from gi.repository import Gst

    Gst.init(None)
    pipeline = Gst.parse_launch("appsrc name=source ! decodebin ! audioconvert ! audioresample ! "
                                "audio/x-raw,format=S16LE,channels=1,rate=24000 ! appsink name=sink sync=false")
    source, sink = pipeline.get_by_name("source"), pipeline.get_by_name("sink")
    pipeline.set_state(Gst.State.PLAYING)
    source.emit("push-buffer", Gst.Buffer.new_wrapped(audio))
    source.emit("end-of-stream")
    pcm = bytearray()
    while (sample := sink.emit("try-pull-sample", 10 * Gst.SECOND)) is not None:
        buffer = sample.get_buffer()
        pcm += buffer.extract_dup(0, buffer.get_size())
    failure = pipeline.get_bus().pop_filtered(Gst.MessageType.ERROR)
    pipeline.set_state(Gst.State.NULL)
    if failure is not None or not pcm:
        raise VoiceError("the voice's audio could not be decoded")
    return pcm_to_wav(bytes(pcm), 24000)


def system_voice_command(text: str, language_code: str | None) -> list[str]:
    """This computer's voice (speech-dispatcher), waiting until the piece has been said."""
    return ["spd-say", "--wait", "--language", bare_language(language_code), "--", text]


# MARK: the recorded notices


RECORDED_FOLDER = Path(__file__).resolve().parent / "recorded_voice"
# What happened → the Mac's recording of it (said in the answering voice, with no key or network).
NOTICES = {
    "no internet": "notice-no-internet",
    "no screen": "notice-no-screen",
    "empty answer": "notice-empty-answer",
    "stopped partway": "notice-stopped-partway",
    "went wrong": "notice-went-wrong",
    "realtime unavailable": "notice-realtime-unavailable",
}


def notice_for(caption: str) -> str | None:
    """Which recorded notice fits a failure's caption, if one does."""
    lowered = caption.lower()
    if lowered.startswith("couldn't see the screen"):
        return NOTICES["no screen"]
    if lowered.startswith("couldn't reach"):
        return NOTICES["no internet"]
    if "answered with no text" in lowered:
        return NOTICES["empty answer"]
    if "stopped partway" in lowered:
        return NOTICES["stopped partway"]
    return None


def recorded_notice(name: str) -> bytes | None:
    """A notice's audio as WAV, or None when it is not shipped."""
    path = RECORDED_FOLDER / f"{name}.mp3"
    try:
        return as_wav(path.read_bytes())
    except (OSError, VoiceError) as failure:
        applog.append(f"recorded voice: {name} not played ({type(failure).__name__})")
        return None
