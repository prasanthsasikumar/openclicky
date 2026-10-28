"""The language OpenClicky hears and answers in, the region, and the script, as the Mac offers them
(`DictationLanguage`, `ReplyLanguage`, `AppRegion`, `DictationScript`). Linux keeps one language
for both (`language` in shell.json, the Mac's own key): dictation is pinned to it and every answer
is in it; unset, the engine detects what was said and answers are in English.

- **Region** (`linuxRegion`): India, or the rest of the world. India offers the Indian languages,
  the script choice and Sarvam; elsewhere they are clutter. Until someone picks, it follows the
  computer: India when its time zone is India's. An option already in use always stays offered.
- **Script** (`linuxScript`): an Indian-language take written in its own script ("नमस्ते") or in
  roman letters ("namaste"), the way people type on a phone; it goes into the clean-up's prompt.
"""

from __future__ import annotations

import os
import time
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class Language:
    code: str
    name: str
    native_name: str

    @property
    def is_indian(self) -> bool:
        """One of the Indian languages (English counts as India's only in its en-IN form)."""
        return self.code.endswith("-IN") and self.code != "en-IN"

    @property
    def label(self) -> str:
        return self.name if self.name == self.native_name else f"{self.native_name} ({self.name})"


LANGUAGES = (
    Language("en-IN", "English", "English"),
    Language("hi-IN", "Hindi", "हिन्दी"),
    Language("ta-IN", "Tamil", "தமிழ்"),
    Language("te-IN", "Telugu", "తెలుగు"),
    Language("kn-IN", "Kannada", "ಕನ್ನಡ"),
    Language("ml-IN", "Malayalam", "മലയാളം"),
    Language("bn-IN", "Bengali", "বাংলা"),
    Language("mr-IN", "Marathi", "मराठी"),
    Language("gu-IN", "Gujarati", "ગુજરાતી"),
    Language("pa-IN", "Punjabi", "ਪੰਜਾਬੀ"),
    Language("od-IN", "Odia", "ଓଡ଼ିଆ"),
    # Offered outside India: English as written there, the European languages every engine hears,
    # and the Mac's other answer languages.
    Language("en-US", "English (US)", "English (US)"),
    Language("en-GB", "English (UK)", "English (UK)"),
    Language("es-ES", "Spanish", "Español"),
    Language("fr-FR", "French", "Français"),
    Language("de-DE", "German", "Deutsch"),
    Language("it-IT", "Italian", "Italiano"),
    Language("pt-BR", "Portuguese", "Português"),
    Language("ja-JP", "Japanese", "日本語"),
    Language("ko-KR", "Korean", "한국어"),
    Language("zh-CN", "Chinese", "中文"),
)
LANGUAGES_BY_CODE = {language.code: language for language in LANGUAGES}
AUTOMATIC = ""
INDIA, WORLD = "india", "world"
REGIONS = ((INDIA, "India"), (WORLD, "Rest of the world"))
NATIVE, ROMAN = "native", "roman"
SCRIPTS = ((NATIVE, "Native · नमस्ते, आप कैसे हैं?"), (ROMAN, "Roman · namaste, aap kaise hain?"))


def detected_region(time_zone: str | None = None) -> str:
    """India when this computer's time zone is India's, else the rest of the world."""
    if time_zone is None:
        time_zone = os.environ.get("TZ") or _system_time_zone()
    return INDIA if time_zone in ("Asia/Kolkata", "Asia/Calcutta") else WORLD


def _system_time_zone() -> str:
    try:
        return Path("/etc/timezone").read_text(encoding="utf-8").strip()
    except OSError:
        pass
    try:
        return os.path.realpath("/etc/localtime").split("zoneinfo/", 1)[1]
    except (OSError, IndexError):
        return time.tzname[0]


def language_choices(region: str, current: str | None) -> list[tuple[str, str]]:
    """(code, label) for the picker: automatic first, the region's languages, and `current` always."""
    offered = [language for language in LANGUAGES if (language.code.endswith("-IN") if region == INDIA else not language.code.endswith("-IN"))]
    choices = [(AUTOMATIC, "Automatic (the engine detects it; answers in English)")]
    choices += [(language.code, language.label) for language in offered]
    if current and current not in {code for code, _label in choices}:
        known = LANGUAGES_BY_CODE.get(current)
        choices.append((current, known.label if known else current))
    return choices


def offers_script(region: str, language_code: str | None, script: str) -> bool:
    """The script row shows in India, or wherever an Indian language or roman letters are in use."""
    known = LANGUAGES_BY_CODE.get(language_code or "")
    return region == INDIA or bool(known and known.is_indian) or script == ROMAN


def script_line(language_code: str | None, script: str) -> str | None:
    """The clean-up prompt's line about the language and its script (the Mac's `TakeFormatter`)."""
    if language_code:
        bare = language_code.split("-")[0].split("_")[0].lower()
        known = LANGUAGES_BY_CODE.get(language_code)
        name = known.name if known else language_code
        how = "roman letters (latin script), the way people type it on a phone" if script == ROMAN else "its native script"
        return f"The language is {name} ({bare}). Write it in {how}."
    if script == ROMAN:
        return "If the speech is in an Indian language, write it in roman letters (latin script)."
    return None
