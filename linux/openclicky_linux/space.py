"""Your space: the styles each app is written in, the dictionary of names spelled your way, and the
spoken shortcuts that expand into saved text: the Mac app's three files under
`~/.openclicky/dictation` (`styles.json`, `dictionary.json`, `shortcuts.json`, the Mac's
`DictationSpace.swift`), so the same folder synced between computers reads the same everywhere.

Styles name their apps by the Mac's bundle ids and, here, by X11 window class (lowercased): the
seeded ones list both, as the Windows port lists exe names beside them. A terminal goes to the
developer style even when no style names it. Wayland never says which app is in front: every take
there is written in the "other apps" style.

The dictionary and shortcuts are edited in OpenClicky's Words window (or by hand, or on the Mac);
`save_words` writes the two files the way the Mac writes them (seconds since 1970, ids kept), and
never touches `styles.json`.
"""

from __future__ import annotations

import json
import os
import tempfile
import time
import uuid
from dataclasses import dataclass, field
from pathlib import Path

from .config import openclicky_directory


@dataclass(frozen=True)
class DictationStyle:
    id: str
    name: str
    rules: str
    sentence_case: bool = True
    remove_fillers: bool = True
    polish_with_model: bool = True
    # Mac bundle ids and Linux window classes (compared lowercased).
    apps: tuple[str, ...] = ()


SEEDED_STYLES = (
    DictationStyle("developer", "developer",
                   "Terse. Keep identifiers, file names, commands and code exactly as spoken; never expand or translate them. No pleasantries. Sentence case.",
                   apps=("com.apple.dt.Xcode", "com.microsoft.VSCode", "com.apple.Terminal", "com.googlecode.iterm2", "dev.zed.Zed",
                         "com.jetbrains.intellij", "code", "code-oss", "vscodium", "cursor", "zed", "dev.zed.zed", "jetbrains-idea",
                         "jetbrains-pycharm", "jetbrains-webstorm", "jetbrains-clion", "jetbrains-goland", "android-studio",
                         "sublime_text", "gnome-builder", "org.gnome.builder", "kate", "emacs", "neovide")),
    DictationStyle("work-messaging", "work messaging",
                   "Clear and quick, as a colleague writes in a chat: short sentences, friendly, no filler, no sign-off. Keep it one message.",
                   apps=("com.tinyspeck.slackmacgap", "com.microsoft.teams2", "com.microsoft.teams", "com.hnc.Discord", "us.zoom.xos",
                         "com.linear", "notion.id", "slack", "discord", "teams-for-linux", "microsoft teams - preview", "zoom", "element",
                         "mattermost", "linear", "notion-app")),
    DictationStyle("personal-messaging", "personal messaging",
                   "Casual, lowercase, the way people text: shorthand kept (idk, lemme, ngl), light punctuation, warmth kept, nothing added.",
                   sentence_case=False,
                   apps=("com.apple.MobileSMS", "net.whatsapp.WhatsApp", "org.telegram.desktop", "ru.keepcoder.Telegram", "com.facebook.archon",
                         "telegramdesktop", "telegram-desktop", "org.telegram.desktop", "signal", "whatsapp-for-linux", "whatsapp", "zapzap")),
    DictationStyle("email", "email",
                   "Composed, complete sentences with full punctuation and grammar; contractions are fine. Paragraphs where the thought changes. "
                   "Never invent a greeting or a sign-off that was not spoken.",
                   apps=("com.apple.mail", "com.microsoft.Outlook", "com.readdle.smartemail-Mac", "com.sparkmailapp.spark",
                         "thunderbird", "thunderbird-esr", "evolution", "org.gnome.evolution", "geary", "org.gnome.geary", "kmail", "mailspring")),
    DictationStyle("other", "other apps", "Light cleanup only: punctuation, capitalisation, fillers removed, your wording kept."),
)


@dataclass(frozen=True)
class DictionaryTerm:
    written: str
    heard_as: tuple[str, ...] = ()
    id: str = ""
    created_at: float = 0.0


@dataclass(frozen=True)
class SpokenShortcut:
    trigger: str
    replacement: str
    id: str = ""
    created_at: float = 0.0


@dataclass(frozen=True)
class DictationSpace:
    dictionary: tuple[DictionaryTerm, ...] = field(default_factory=tuple)
    shortcuts: tuple[SpokenShortcut, ...] = field(default_factory=tuple)
    styles: tuple[DictationStyle, ...] = SEEDED_STYLES

    def style_for(self, app_class: str | None, is_terminal: bool = False) -> DictationStyle:
        """The style for an app; the developer style for a terminal nobody named; else the style with
        no apps (other apps)."""
        if app_class:
            wanted = app_class.lower()
            claimed = next((style for style in self.styles if wanted in (app.lower() for app in style.apps)), None)
            if claimed:
                return claimed
        if is_terminal:
            developer = next((style for style in self.styles if style.id == "developer"), None)
            if developer:
                return developer
        return next((style for style in self.styles if not style.apps), self.styles[-1] if self.styles else SEEDED_STYLES[-1])


def _read_json_list(path: Path) -> list:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return []
    return value if isinstance(value, list) else []


def _styles_from(entries: list) -> tuple[DictationStyle, ...]:
    styles = []
    for entry in entries:
        if not isinstance(entry, dict) or not isinstance(entry.get("id"), str):
            continue
        seeded = next((style for style in SEEDED_STYLES if style.id == entry["id"]), None)
        apps = tuple(app for app in entry.get("appBundleIDs") or [] if isinstance(app, str))
        # A Mac-written seeded style keeps the Linux window classes this file lists for it.
        if seeded:
            apps = apps + tuple(app for app in seeded.apps if app not in apps)
        styles.append(DictationStyle(
            id=entry["id"], name=str(entry.get("name") or entry["id"]), rules=str(entry.get("rules") or ""),
            sentence_case=entry.get("sentenceCase") is not False, remove_fillers=entry.get("removeFillers") is not False,
            polish_with_model=entry.get("polishWithModel") is not False, apps=apps))
    return tuple(styles) or SEEDED_STYLES


def default_directory() -> Path:
    return openclicky_directory() / "dictation"


def load_space(directory: Path | None = None) -> DictationSpace:
    directory = directory or default_directory()
    terms = []
    for entry in _read_json_list(directory / "dictionary.json"):
        if not isinstance(entry, dict) or not isinstance(entry.get("written"), str) or not entry["written"].strip():
            continue
        heard_as = tuple(alias for alias in entry.get("heardAs") or [] if isinstance(alias, str) and alias.strip())
        terms.append(DictionaryTerm(written=entry["written"], heard_as=heard_as, id=str(entry.get("id") or ""),
                                    created_at=float(entry["createdAt"]) if isinstance(entry.get("createdAt"), (int, float)) else 0.0))
    shortcuts = []
    for entry in _read_json_list(directory / "shortcuts.json"):
        if isinstance(entry, dict) and isinstance(entry.get("trigger"), str) and isinstance(entry.get("replacement"), str):
            shortcuts.append(SpokenShortcut(trigger=entry["trigger"], replacement=entry["replacement"], id=str(entry.get("id") or ""),
                                            created_at=float(entry["createdAt"]) if isinstance(entry.get("createdAt"), (int, float)) else 0.0))
    styles_path = directory / "styles.json"
    styles = _styles_from(_read_json_list(styles_path)) if styles_path.exists() else SEEDED_STYLES
    return DictationSpace(dictionary=tuple(terms), shortcuts=tuple(shortcuts), styles=styles)


def _write_json(path: Path, value: list) -> None:
    path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    descriptor, temporary = tempfile.mkstemp(dir=path.parent, prefix=f".{path.stem}-", suffix=".json")
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8") as temporary_file:
            json.dump(value, temporary_file, indent=2, sort_keys=True, ensure_ascii=False)
        os.replace(temporary, path)
    except BaseException:
        if os.path.exists(temporary):
            os.unlink(temporary)
        raise


def new_id() -> str:
    # The Mac's UUID().uuidString: upper case.
    return str(uuid.uuid4()).upper()


def save_words(dictionary: list[DictionaryTerm], shortcuts: list[SpokenShortcut], directory: Path | None = None) -> None:
    """Writes the dictionary and the shortcuts as the Mac writes them (ids and whole seconds kept)."""
    directory = directory or default_directory()
    now = float(int(time.time()))
    _write_json(directory / "dictionary.json", [
        {"id": term.id or new_id(), "written": term.written, "heardAs": list(term.heard_as), "createdAt": term.created_at or now}
        for term in dictionary if term.written.strip()])
    _write_json(directory / "shortcuts.json", [
        {"id": shortcut.id or new_id(), "trigger": shortcut.trigger, "replacement": shortcut.replacement, "createdAt": shortcut.created_at or now}
        for shortcut in shortcuts if shortcut.trigger.strip()])


def save_styles(styles: list[DictationStyle], directory: Path | None = None) -> None:
    """Writes the styles as the Mac writes them, keeping what it keeps that Linux does not use (a
    style's tagline)."""
    directory = directory or default_directory()
    existing = {entry["id"]: entry for entry in _read_json_list(directory / "styles.json")
                if isinstance(entry, dict) and isinstance(entry.get("id"), str)}
    entries = []
    for style in styles:
        entry = dict(existing.get(style.id, {"tagline": ""}))
        entry.update(id=style.id, name=style.name, rules=style.rules, sentenceCase=style.sentence_case,
                     removeFillers=style.remove_fillers, polishWithModel=style.polish_with_model, appBundleIDs=list(style.apps))
        entries.append(entry)
    _write_json(directory / "styles.json", entries)
