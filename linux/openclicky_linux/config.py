"""Settings, read from the same `~/.openclicky/shell.json` the Mac app and the CLI use.

Keys read here: `openaiApiKey`, `anthropicApiKey` and `openRouterApiKey` (own keys, sent straight
to their vendor; `answerModels.openrouter` picks the OpenRouter model, as on the Mac),
`token` and `backendUrl` (an OpenClicky account, the fallback), `language` (dictation and answers;
English when unset), and the Linux-only `linuxDictationKey` (an X keysym name; `Control_L`, where
fn sits on a Mac keyboard, by default: Linux never sees fn), `linuxPointer` ("always", "whileMoving", "onShake", or
"whenAnswering": see `presence.py`), `linuxMicrophone` (a sound server source name; unset, the
system default), `linuxDictationEngine` ("auto", "offline", "openai" or "account") and
`linuxSpeakAnswers` (false: captions only), `linuxPolishWithModel` / `linuxPolishOfflineTakes` (the
model clean-up), `linuxIncognito` (no history); `kokoroSpeaker` is the Mac app's own key for the voice. `BACKEND_URL`, `OPENCLICKY_OPENAI_KEY` and `OPENCLICKY_ANTHROPIC_KEY` in the
environment win over the file, as on the Mac.

`save_settings` writes the Settings window's fields back and leaves every other key as it was: the
Mac app keeps its own (the refresh token, the voice, the answer source) in the same file.
"""

from __future__ import annotations

import json
import os
import tempfile
from dataclasses import dataclass
from pathlib import Path

HOSTED_BACKEND_URL = "https://api.openclicky.flowsxr.com"
# Left ctrl, where fn is on a Mac keyboard (the Mac app's dictation key).
DEFAULT_DICTATION_KEY = "Control_L"
POINTER_PRESENCES = ("always", "whileMoving", "onShake", "whenAnswering")


def openclicky_directory() -> Path:
    return Path(os.environ.get("OPENCLICKY_HOME", Path.home() / ".openclicky"))


@dataclass(frozen=True)
class LinuxSettings:
    openai_api_key: str | None = None
    anthropic_api_key: str | None = None
    openrouter_api_key: str | None = None
    openrouter_model: str | None = None
    account_token: str | None = None
    backend_url: str = HOSTED_BACKEND_URL
    language_code: str | None = None
    dictation_key: str = DEFAULT_DICTATION_KEY
    pointer_presence: str = "always"
    # A PulseAudio/PipeWire source name; None: the system default.
    microphone: str | None = None
    # "auto" | "offline" | "openai" | "account" (see transcriber.py).
    dictation_engine: str = "auto"
    # Answers spoken in Kokoro's voice, once it is downloaded (off: captions only).
    speak_answers: bool = True
    # Kokoro's speaker (the Mac app's `kokoroSpeaker`); None: the reply language's first.
    kokoro_speaker: int | None = None
    # "Clean up my words with AI" (the Mac's default: on), and for takes the offline engine heard
    # too (off: nothing it hears leaves the computer); incognito keeps takes out of the history.
    polish_with_model: bool = True
    polish_offline_takes: bool = False
    incognito: bool = False
    # An email account's session (`accounts.py`), as the Mac stores it.
    refresh_token: str | None = None
    token_expires_at: float | None = None
    account_email: str | None = None

    @property
    def bare_language_code(self) -> str | None:
        """"en-IN" → "en": the transcription APIs take the language alone."""
        if not self.language_code:
            return None
        return self.language_code.split("-")[0].split("_")[0].lower() or None


def _cleaned(value: object) -> str | None:
    if not isinstance(value, str):
        return None
    stripped = value.strip()
    return stripped or None


def load_settings(shell_json_path: Path | None = None, environment: dict[str, str] | None = None) -> LinuxSettings:
    path = shell_json_path or openclicky_directory() / "shell.json"
    environment = os.environ if environment is None else environment
    try:
        raw = json.loads(path.read_text(encoding="utf-8"))
        if not isinstance(raw, dict):
            raw = {}
    except (OSError, ValueError):
        raw = {}
    backend_url = _cleaned(environment.get("BACKEND_URL")) or _cleaned(raw.get("backendUrl")) or HOSTED_BACKEND_URL
    return LinuxSettings(
        openai_api_key=_cleaned(environment.get("OPENCLICKY_OPENAI_KEY")) or _cleaned(raw.get("openaiApiKey")),
        anthropic_api_key=_cleaned(environment.get("OPENCLICKY_ANTHROPIC_KEY")) or _cleaned(raw.get("anthropicApiKey")),
        openrouter_api_key=_cleaned(environment.get("OPENCLICKY_OPENROUTER_KEY")) or _cleaned(raw.get("openRouterApiKey")),
        openrouter_model=_cleaned((raw.get("answerModels") or {}).get("openrouter")) if isinstance(raw.get("answerModels"), dict) else None,
        account_token=_cleaned(raw.get("token")),
        backend_url=backend_url.rstrip("/"),
        language_code=_cleaned(raw.get("language")),
        dictation_key=_cleaned(raw.get("linuxDictationKey")) or DEFAULT_DICTATION_KEY,
        pointer_presence=raw.get("linuxPointer") if raw.get("linuxPointer") in POINTER_PRESENCES else "always",
        microphone=_cleaned(raw.get("linuxMicrophone")),
        dictation_engine=raw.get("linuxDictationEngine") if raw.get("linuxDictationEngine") in ("auto", "offline", "openai", "account") else "auto",
        speak_answers=raw.get("linuxSpeakAnswers") is not False and raw.get("linuxSpeakAnswers") != "false",
        kokoro_speaker=raw.get("kokoroSpeaker") if isinstance(raw.get("kokoroSpeaker"), int) else None,
        polish_with_model=raw.get("linuxPolishWithModel") is not False,
        polish_offline_takes=raw.get("linuxPolishOfflineTakes") is True,
        incognito=raw.get("linuxIncognito") is True,
        refresh_token=_cleaned(raw.get("refreshToken")),
        token_expires_at=float(raw["tokenExpiresAt"]) if isinstance(raw.get("tokenExpiresAt"), (int, float)) else None,
        account_email=_cleaned(raw.get("accountEmail")),
    )


# Environment variables that win over shell.json, and the Settings field each one hides.
ENVIRONMENT_OVERRIDES = {
    "OPENCLICKY_OPENAI_KEY": "OpenAI key",
    "OPENCLICKY_ANTHROPIC_KEY": "Anthropic key",
    "OPENCLICKY_OPENROUTER_KEY": "OpenRouter key",
    "BACKEND_URL": "Backend",
}


def environment_overrides(environment: dict[str, str] | None = None) -> list[str]:
    """The Settings fields an environment variable overrides right now (their names, not values)."""
    environment = os.environ if environment is None else environment
    return [field for variable, field in ENVIRONMENT_OVERRIDES.items() if _cleaned(environment.get(variable))]


# The Settings window's fields → shell.json keys. An empty field removes its key.
SETTINGS_FIELDS = {
    "openai_api_key": "openaiApiKey",
    "anthropic_api_key": "anthropicApiKey",
    "openrouter_api_key": "openRouterApiKey",
    "account_token": "token",
    "backend_url": "backendUrl",
    "language_code": "language",
    "dictation_key": "linuxDictationKey",
    "pointer_presence": "linuxPointer",
    "microphone": "linuxMicrophone",
    "dictation_engine": "linuxDictationEngine",
    "speak_answers": "linuxSpeakAnswers",
    "kokoro_speaker": "kokoroSpeaker",
    "polish_with_model": "linuxPolishWithModel",
    "polish_offline_takes": "linuxPolishOfflineTakes",
    "incognito": "linuxIncognito",
}
# Switches and what each one is when its key is absent (only the other value is written).
SWITCH_DEFAULTS = {"speak_answers": True, "polish_with_model": True, "polish_offline_takes": False, "incognito": False}


def _read_for_writing(shell_json_path: Path | None) -> tuple[Path, dict]:
    path = shell_json_path or openclicky_directory() / "shell.json"
    # A shell.json that is a link (dotfiles kept in a repository) is written where it points, and
    # stays a link.
    path = path.resolve()
    try:
        current = json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError:
        current = {}
    except ValueError as broken:
        # Never overwrite a file that could not be read: the person's other settings are in it.
        raise ValueError(f"{path} is not valid json; fix or remove it first") from broken
    if not isinstance(current, dict):
        raise ValueError(f"{path} is not a json object; fix or remove it first")
    return path, current


def _write(path: Path, current: dict) -> None:
    """Written whole to a temporary file and moved into place, readable by this user only: it holds keys."""
    path.parent.mkdir(parents=True, exist_ok=True)
    file_descriptor, temporary_path = tempfile.mkstemp(dir=path.parent, prefix=".shell-", suffix=".json")
    try:
        with os.fdopen(file_descriptor, "w", encoding="utf-8") as temporary_file:
            json.dump(current, temporary_file, indent=2)
            temporary_file.write("\n")
        os.chmod(temporary_path, 0o600)
        os.replace(temporary_path, path)
    except BaseException:
        if os.path.exists(temporary_path):
            os.unlink(temporary_path)
        raise


def save_account(token: str | None, refresh_token: str | None, expires_at: float | None, email: str | None,
                 shell_json_path: Path | None = None) -> None:
    """An email account's session (`accounts.py`), under the Mac's keys; all None signs out."""
    path, current = _read_for_writing(shell_json_path)
    for json_key, value in (("token", token), ("refreshToken", refresh_token), ("tokenExpiresAt", expires_at), ("accountEmail", email)):
        if value is None or value == "":
            current.pop(json_key, None)
        else:
            current[json_key] = value
    _write(path, current)


def save_settings(changes: dict[str, str | None], shell_json_path: Path | None = None) -> None:
    """Writes the given fields (keys of `SETTINGS_FIELDS`) into shell.json, keeping every other key."""
    path, current = _read_for_writing(shell_json_path)
    for field_name, value in changes.items():
        json_key = SETTINGS_FIELDS[field_name]
        # Not text: a switch is written only when it is not its default, the speaker is a number.
        if field_name in SWITCH_DEFAULTS:
            switched_on = value not in (False, "false", "")
            if value is None or switched_on == SWITCH_DEFAULTS[field_name]:
                current.pop(json_key, None)
            else:
                current[json_key] = switched_on
            continue
        if field_name == "kokoro_speaker":
            if isinstance(value, int) or (isinstance(value, str) and value.isdigit()):
                current[json_key] = int(value)
            else:
                current.pop(json_key, None)
            continue
        cleaned = _cleaned(value)
        # A default is no key at all: the file holds only what the person changed.
        is_default = ((field_name == "backend_url" and cleaned is not None and cleaned.rstrip("/") == HOSTED_BACKEND_URL)
                      or (field_name == "dictation_engine" and cleaned == "auto")
                      or (field_name == "dictation_key" and cleaned == DEFAULT_DICTATION_KEY))
        if cleaned is None or is_default:
            current.pop(json_key, None)
        else:
            current[json_key] = cleaned
    _write(path, current)
