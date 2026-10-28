"""Settings, read from the same `~/.openclicky/shell.json` the Mac app and the CLI use.

Keys read here, the Mac app's own: `openaiApiKey`, `anthropicApiKey`, `openRouterApiKey`,
`geminiApiKey`, `groqApiKey`, `mistralApiKey`, `xaiApiKey` and `sarvamKey` (own keys, sent straight to their
vendor), `answerSource` and `answerModels` (where answers come from, and the model per source),
`useClaudeCodePlan` / `useCodexPlan` (the plans), `token`, `refreshToken`, `tokenExpiresAt`,
`accountEmail` and `backendUrl` (an OpenClicky account, the fallback), `language` (dictation and
answers; English when unset), `speechVoice`, `elevenLabsApiKey`, `googleCloudApiKey` and
`amazonPollyCredentials` (who speaks the answers), `kokoroSpeaker` (Kokoro's speaker) and
`workspace` (the agent's folder). Linux's own: `linuxDictationKey` (an
X keysym name; `Control_L`, where fn sits on a Mac keyboard, by default: Linux never sees fn),
`linuxPointer` ("always", "whileMoving", "onShake", or "whenAnswering": see `presence.py`),
`linuxMicrophone` (a sound server source name; unset, the system default), `linuxDictationEngine`
("auto", "offline", "openai", "account", "sarvam" or "assemblyai"), `linuxSpeakAnswers` (false: captions only),
`linuxPolishWithModel` / `linuxPolishOfflineTakes` (the model clean-up), `linuxIncognito` (no
history), `linuxClaudeModel`, `linuxPauseBeforeSending`, `linuxSounds` (the tones around a take),
`linuxQuietMinutes` (a tapped take ends after that much quiet; 0: never),
`linuxKeepFailedTakeAudio` (a take an engine could not finish can be retried) and
`linuxClipboardHistory` (what is copied goes into the history too), `linuxRegion` and `linuxScript`
(see `languages.py`), `linuxOrbShows`, `linuxOrbPosition`, `linuxOrbSize`, `linuxOrbTheme` and
`linuxReduceMotion`, `linuxIslandPlacement` and `linuxGlassLevel` (the island), `linuxFastVoice`, `linuxAutoUpdate`. `BACKEND_URL`,
`OPENCLICKY_OPENAI_KEY`, `OPENCLICKY_ANTHROPIC_KEY` and `OPENCLICKY_OPENROUTER_KEY` in the
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
DICTATION_ENGINES = ("auto", "offline", "openai", "account", "sarvam", "assemblyai")
ORB_SHOWS = ("whileDictating", "always")
ORB_POSITIONS = ("bottom", "bottomLeft", "bottomRight", "top")
ORB_SIZES = ("small", "normal", "large")
QUIET_MINUTES = (1, 3, 5, 10, 0)


def openclicky_directory() -> Path:
    return Path(os.environ.get("OPENCLICKY_HOME", Path.home() / ".openclicky"))


@dataclass(frozen=True)
class LinuxSettings:
    openai_api_key: str | None = None
    anthropic_api_key: str | None = None
    openrouter_api_key: str | None = None
    openrouter_model: str | None = None
    gemini_api_key: str | None = None
    groq_api_key: str | None = None
    mistral_api_key: str | None = None
    xai_api_key: str | None = None
    # Sarvam (the Mac's `sarvamKey`): dictation in the Indian languages, and a voice.
    sarvam_api_key: str | None = None
    # Who speaks the answers (the Mac's `speechVoice`, see speech_voices.py; None: Automatic), and
    # the voice keys only a voice uses.
    speech_voice: str | None = None
    elevenlabs_api_key: str | None = None
    google_cloud_api_key: str | None = None
    amazon_polly_credentials: str | None = None
    # Settings → Answers come from (the Mac's `answerSource`; None: Automatic), the model per source
    # (`answerModels`), the plans switched on, and the Claude model (Linux's `linuxClaudeModel`).
    answer_source: str | None = None
    answer_models: dict | None = None
    use_claude_code_plan: bool = False
    use_codex_plan: bool = False
    claude_model: str | None = None
    # Settings → Privacy → Pause before sending my screen (the Mac's default: on).
    pause_before_sending: bool = True
    account_token: str | None = None
    backend_url: str = HOSTED_BACKEND_URL
    language_code: str | None = None
    dictation_key: str = DEFAULT_DICTATION_KEY
    pointer_presence: str = "always"
    # A PulseAudio/PipeWire source name; None: the system default.
    microphone: str | None = None
    # "auto" | "offline" | "openai" | "account" | "sarvam" | "assemblyai" (see transcriber.py).
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
    # Settings → Dictation, as on the Mac: the tones around a take, the minutes of quiet that end a
    # tapped take (0: never), keeping a failed take's audio to retry it, and copies in the history.
    sounds: bool = True
    quiet_minutes: int = 3
    keep_failed_audio: bool = True
    clipboard_history: bool = False
    # Settings → Dictation: India or the rest of the world (None: this computer's), and an Indian
    # language's script ("native" or "roman"); see languages.py.
    region: str | None = None
    script: str = "native"
    # Settings → The Orb: when it shows ("whileDictating", or "always", resting as a small pill),
    # where ("bottom", "bottomLeft", "bottomRight", "top"), its size and theme; and General →
    # Reduce motion (the pointer jumps, captions appear whole).
    orb_shows: str = "whileDictating"
    orb_position: str = "bottom"
    orb_size: str = "normal"
    orb_theme: str = "dark"
    reduce_motion: bool = False
    # Settings → General → Updates: checked once a day on their own.
    auto_update: bool = True
    # Settings → Clicky → Fast voice (the Mac's default: on): asking out loud talks to OpenAI
    # Realtime when there is an own OpenAI key.
    fast_voice: bool = True
    # Settings → General: where the island lives ("notch": the top of the screen, "left", "right",
    # "hidden") and how far its black melts into glass (0…1).
    island_placement: str = "notch"
    glass_level: float = 0.6
    # The agent's folder (the Mac's `workspace`; None: ~/OpenClicky), read by the CLI too.
    workspace: str | None = None
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
        gemini_api_key=_cleaned(raw.get("geminiApiKey")),
        groq_api_key=_cleaned(raw.get("groqApiKey")),
        mistral_api_key=_cleaned(raw.get("mistralApiKey")),
        xai_api_key=_cleaned(raw.get("xaiApiKey")),
        sarvam_api_key=_cleaned(raw.get("sarvamKey")),
        speech_voice=_cleaned(raw.get("speechVoice")) if _cleaned(raw.get("speechVoice")) not in ("auto", "automatic", "cloud") else None,
        elevenlabs_api_key=_cleaned(raw.get("elevenLabsApiKey")),
        google_cloud_api_key=_cleaned(raw.get("googleCloudApiKey")),
        amazon_polly_credentials=_cleaned(raw.get("amazonPollyCredentials")),
        answer_source=_cleaned(raw.get("answerSource")),
        answer_models={key: value for key, value in raw["answerModels"].items() if isinstance(value, str) and value.strip()}
        if isinstance(raw.get("answerModels"), dict) else None,
        use_claude_code_plan=raw.get("useClaudeCodePlan") is True,
        use_codex_plan=raw.get("useCodexPlan") is True,
        claude_model=_cleaned(raw.get("linuxClaudeModel")),
        pause_before_sending=raw.get("linuxPauseBeforeSending") is not False,
        openrouter_model=_cleaned((raw.get("answerModels") or {}).get("openrouter")) if isinstance(raw.get("answerModels"), dict) else None,
        account_token=_cleaned(raw.get("token")),
        backend_url=backend_url.rstrip("/"),
        language_code=_cleaned(raw.get("language")),
        dictation_key=_cleaned(raw.get("linuxDictationKey")) or DEFAULT_DICTATION_KEY,
        pointer_presence=raw.get("linuxPointer") if raw.get("linuxPointer") in POINTER_PRESENCES else "always",
        microphone=_cleaned(raw.get("linuxMicrophone")),
        dictation_engine=raw.get("linuxDictationEngine") if raw.get("linuxDictationEngine") in DICTATION_ENGINES else "auto",
        speak_answers=raw.get("linuxSpeakAnswers") is not False and raw.get("linuxSpeakAnswers") != "false",
        kokoro_speaker=raw.get("kokoroSpeaker") if isinstance(raw.get("kokoroSpeaker"), int) else None,
        polish_with_model=raw.get("linuxPolishWithModel") is not False,
        polish_offline_takes=raw.get("linuxPolishOfflineTakes") is True,
        incognito=raw.get("linuxIncognito") is True,
        sounds=raw.get("linuxSounds") is not False,
        quiet_minutes=raw["linuxQuietMinutes"] if raw.get("linuxQuietMinutes") in QUIET_MINUTES else 3,
        keep_failed_audio=raw.get("linuxKeepFailedTakeAudio") is not False,
        clipboard_history=raw.get("linuxClipboardHistory") is True,
        region=raw.get("linuxRegion") if raw.get("linuxRegion") in ("india", "world") else None,
        script="roman" if raw.get("linuxScript") == "roman" else "native",
        orb_shows=raw.get("linuxOrbShows") if raw.get("linuxOrbShows") in ORB_SHOWS else "whileDictating",
        orb_position=raw.get("linuxOrbPosition") if raw.get("linuxOrbPosition") in ORB_POSITIONS else "bottom",
        orb_size=raw.get("linuxOrbSize") if raw.get("linuxOrbSize") in ORB_SIZES else "normal",
        orb_theme="light" if raw.get("linuxOrbTheme") == "light" else "dark",
        reduce_motion=raw.get("linuxReduceMotion") is True,
        workspace=_cleaned(raw.get("workspace")),
        fast_voice=raw.get("linuxFastVoice") is not False,
        auto_update=raw.get("linuxAutoUpdate") is not False,
        island_placement=raw.get("linuxIslandPlacement") if raw.get("linuxIslandPlacement") in ("notch", "left", "right", "hidden") else "notch",
        glass_level=min(1.0, max(0.0, float(raw["linuxGlassLevel"]))) if isinstance(raw.get("linuxGlassLevel"), (int, float)) else 0.6,
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
    "gemini_api_key": "geminiApiKey",
    "groq_api_key": "groqApiKey",
    "mistral_api_key": "mistralApiKey",
    "xai_api_key": "xaiApiKey",
    "sarvam_api_key": "sarvamKey",
    "speech_voice": "speechVoice",
    "elevenlabs_api_key": "elevenLabsApiKey",
    "google_cloud_api_key": "googleCloudApiKey",
    "amazon_polly_credentials": "amazonPollyCredentials",
    "answer_source": "answerSource",
    "use_claude_code_plan": "useClaudeCodePlan",
    "use_codex_plan": "useCodexPlan",
    "claude_model": "linuxClaudeModel",
    "pause_before_sending": "linuxPauseBeforeSending",
    "polish_with_model": "linuxPolishWithModel",
    "polish_offline_takes": "linuxPolishOfflineTakes",
    "incognito": "linuxIncognito",
    "sounds": "linuxSounds",
    "quiet_minutes": "linuxQuietMinutes",
    "keep_failed_audio": "linuxKeepFailedTakeAudio",
    "clipboard_history": "linuxClipboardHistory",
    "region": "linuxRegion",
    "script": "linuxScript",
    "orb_shows": "linuxOrbShows",
    "orb_position": "linuxOrbPosition",
    "orb_size": "linuxOrbSize",
    "orb_theme": "linuxOrbTheme",
    "reduce_motion": "linuxReduceMotion",
    "workspace": "workspace",
    "fast_voice": "linuxFastVoice",
    "auto_update": "linuxAutoUpdate",
    "island_placement": "linuxIslandPlacement",
    "glass_level": "linuxGlassLevel",
}
# Switches and what each one is when its key is absent (only the other value is written).
SWITCH_DEFAULTS = {"speak_answers": True, "polish_with_model": True, "polish_offline_takes": False, "incognito": False,
                   "use_claude_code_plan": False, "use_codex_plan": False, "pause_before_sending": True, "sounds": True,
                   "keep_failed_audio": True, "clipboard_history": False, "reduce_motion": False, "fast_voice": True,
                   "auto_update": True}


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
        # Not text: a switch is written only when it is not its default; the speaker and the minutes are numbers.
        if field_name in SWITCH_DEFAULTS:
            switched_on = value not in (False, "false", "")
            if value is None or switched_on == SWITCH_DEFAULTS[field_name]:
                current.pop(json_key, None)
            else:
                current[json_key] = switched_on
            continue
        if field_name == "glass_level":
            level = round(float(value), 2) if value not in (None, "") else 0.6
            if level == 0.6:
                current.pop(json_key, None)
            else:
                current[json_key] = level
            continue
        if field_name in ("kokoro_speaker", "quiet_minutes"):
            if field_name == "quiet_minutes" and str(value) == "3":
                current.pop(json_key, None)
            elif isinstance(value, int) or (isinstance(value, str) and value.isdigit()):
                current[json_key] = int(value)
            else:
                current.pop(json_key, None)
            continue
        cleaned = _cleaned(value)
        # A default is no key at all: the file holds only what the person changed.
        is_default = ((field_name == "backend_url" and cleaned is not None and cleaned.rstrip("/") == HOSTED_BACKEND_URL)
                      or (field_name == "dictation_engine" and cleaned == "auto")
                      or (field_name == "dictation_key" and cleaned == DEFAULT_DICTATION_KEY)
                      or (field_name == "answer_source" and cleaned == "automatic")
                      or (field_name == "script" and cleaned == "native")
                      or (field_name == "speech_voice" and cleaned == "automatic")
                      or (field_name, cleaned) in (("island_placement", "notch"), ("orb_shows", "whileDictating"), ("orb_position", "bottom"), ("orb_size", "normal"),
                                                   ("orb_theme", "dark")))
        if cleaned is None or is_default:
            current.pop(json_key, None)
        else:
            current[json_key] = cleaned
    _write(path, current)
