"""Skills in the talk prompt, the Mac's `SkillFile`, `AppSkillMatcher`, `SkillPromptBuilder` and
`SkillLibraryStore` (and the Windows port of each), on the same files:

- **app-teaching skills** (`app-skills/<id>/SKILL.md` in the repository, shipped inside the package,
  plus `~/.openclicky/app-skills/<id>/SKILL.md`, which win over a bundled one with the same id): the
  one for the app in front, or the browser's site, goes into the question's prompt (at most 4,000
  characters). Their `apps:` name Mac bundle ids; `LINUX_APPS` says which window classes are the
  same app here (only apps that run on Linux: Chrome, VS Code, Slack, DaVinci Resolve). A browser's
  site is found in its window title (Linux tells no app a browser's address).
- **the person's skill library** (`~/.openclicky/skills/library/<id>/SKILL.md`, switched on with
  `openclicky skills activate <id>` or here): the active talk skills go in first, whole skills only,
  at most 6,000 characters. `active/` holds one link per active skill, which Codex loads, as the
  command-line tool keeps it (`agent/src/skillsLibrary.ts`).
"""

from __future__ import annotations

import json
import os
import re
import shutil
import time
from dataclasses import dataclass, field
from pathlib import Path

from . import applog
from .config import openclicky_directory

APP_SKILL_BUDGET = 4000
ACTIVE_SKILL_BUDGET = 6000

# Window classes (lowercased WM_CLASS) → the bundle id an app skill names for the same app.
LINUX_APPS = {
    "google-chrome": "com.google.Chrome", "google-chrome-beta": "com.google.Chrome", "chromium": "com.google.Chrome",
    "chromium-browser": "com.google.Chrome",
    "code": "com.microsoft.VSCode", "code - insiders": "com.microsoft.VSCode", "vscodium": "com.microsoft.VSCode",
    "slack": "com.tinyspeck.slackmacGap",
    "resolve": "com.blackmagic-design.DaVinciResolve",
}
BROWSER_WINDOW_CLASSES = frozenset({
    "google-chrome", "google-chrome-beta", "chromium", "chromium-browser", "firefox", "firefox-esr", "navigator",
    "brave-browser", "microsoft-edge", "vivaldi-stable", "opera", "epiphany", "org.gnome.epiphany", "librewolf",
})


@dataclass(frozen=True)
class SkillFile:
    id: str
    name: str
    description: str
    apps: tuple[str, ...] = ()
    sites: tuple[str, ...] = ()
    surfaces: frozenset[str] = frozenset({"talk", "agent"})
    integration: str | None = None
    body: str = ""

    @property
    def is_for_talk(self) -> bool:
        return "talk" in self.surfaces


_FRONTMATTER = re.compile(r"^---\r?\n([\s\S]*?)\r?\n---\r?\n?([\s\S]*)$")
_KEY_VALUE = re.compile(r"^([A-Za-z_][\w-]*):\s*(.*)$")


def _strip_quotes(value: str) -> str:
    if len(value) >= 2 and value[0] == value[-1] and value[0] in "\"'":
        return value[1:-1]
    return value


def _parse_list(value: str) -> tuple[str, ...]:
    """`[a, "b", 'c']` (or a bare comma list) → ("a", "b", "c")."""
    inner = value.strip(" \t")
    inner = inner[1:] if inner.startswith("[") else inner
    inner = inner[:-1] if inner.endswith("]") else inner
    return tuple(item for item in (_strip_quotes(part.strip(" \t")) for part in inner.split(",")) if item)


def parse_skill(markdown: str, skill_id: str) -> SkillFile | None:
    """One SKILL.md; None without frontmatter, a name or a description."""
    match = _FRONTMATTER.match(markdown.removeprefix("﻿"))
    if not match:
        return None
    fields = {}
    for line in re.split(r"\r?\n", match.group(1)):
        key_value = _KEY_VALUE.match(line)
        if key_value:
            fields[key_value.group(1)] = _strip_quotes(key_value.group(2).strip(" \t"))
    if not fields.get("name") or not fields.get("description"):
        return None
    return SkillFile(
        id=skill_id, name=fields["name"], description=fields["description"],
        apps=_parse_list(fields.get("apps", "")), sites=_parse_list(fields.get("sites", "")),
        surfaces=frozenset(_parse_list(fields["surfaces"])) if "surfaces" in fields else frozenset({"talk", "agent"}),
        integration=fields.get("integration") or None, body=match.group(2).strip())


def load_skills(directory: Path) -> list[SkillFile]:
    """Every `<directory>/<id>/SKILL.md`, sorted by id; folders starting with `_` are templates."""
    try:
        entries = sorted(entry for entry in directory.iterdir() if entry.is_dir() and not entry.name.startswith("_"))
    except OSError:
        return []
    skills = []
    for entry in entries:
        try:
            markdown = (entry / "SKILL.md").read_text(encoding="utf-8")
        except (OSError, UnicodeDecodeError):
            continue
        skill = parse_skill(markdown, entry.name)
        if skill is not None:
            skills.append(skill)
    return skills


# MARK: the app in front


@dataclass(frozen=True)
class FrontApp:
    window_class: str | None = None
    title: str | None = None
    url_host: str | None = None  # when it could be read (never, without accessibility)

    @property
    def is_browser(self) -> bool:
        return (self.window_class or "").lower() in BROWSER_WINDOW_CLASSES


def _host_matches(host: str, site: str) -> bool:
    site = site.lower()
    return host == site or host.endswith("." + site)


def _best_site_match(skills: list[SkillFile], matches) -> SkillFile | None:
    """The skill whose longest matching site is the longest overall; ties keep list order."""
    best, best_length = None, -1
    for skill in skills:
        matching = [site for site in skill.sites if matches(site)]
        if matching and max(map(len, matching)) > best_length:
            best, best_length = skill, max(map(len, matching))
    return best


def match_app_skill(front: FrontApp, skills: list[SkillFile]) -> SkillFile | None:
    """A site skill for the address, then (browsers only) one named in the window title, then the
    app's own skill (its window class, or the Mac bundle id it stands for)."""
    if front.url_host:
        host = front.url_host.lower()
        by_site = _best_site_match(skills, lambda site: _host_matches(host, site))
        if by_site is not None:
            return by_site
    if front.is_browser and front.title:
        title = front.title.lower()
        by_title = _best_site_match(skills, lambda site: site.lower() in title)
        if by_title is not None:
            return by_title
    if front.window_class:
        names = {front.window_class.lower()}
        if front.window_class.lower() in LINUX_APPS:
            names.add(LINUX_APPS[front.window_class.lower()].lower())
        return next((skill for skill in skills if any(app.lower() in names for app in skill.apps)), None)
    return None


def _truncated(text: str, budget: int) -> str:
    return text if len(text) <= budget else text[:max(budget - 1, 0)] + "…"


def prompt_block(active_skills: list[SkillFile], app_skill: SkillFile | None, front: FrontApp | None,
                 active_budget: int = ACTIVE_SKILL_BUDGET, app_budget: int = APP_SKILL_BUDGET) -> str:
    """The person's active talk skills, then the teaching notes for the app in front."""
    sections = []
    remaining = active_budget
    for skill in active_skills:
        if not skill.is_for_talk:
            continue
        section = f"## Skill: {skill.name}\n{skill.body}"
        # Whole skills only: a truncated style guide is worse than none.
        if len(section) > remaining:
            applog.append(f"skills: skipping \"{skill.name}\" ({len(section)} chars over the talk budget)")
            continue
        remaining -= len(section)
        sections.append(section)
    if app_skill is not None:
        heading = f"## The app in front: {app_skill.name}"
        details = [detail for detail in ((front.window_class if front else None), (front.url_host if front else None)) if detail]
        if details:
            heading += f" ({', '.join(details)})"
        sections.append(f"{heading}\n{_truncated(app_skill.body, app_budget)}")
    return "\n\n".join(sections)


# MARK: where the skills are


def bundled_app_skills_directories() -> list[Path]:
    """The skills shipped inside the package, or the repository's own folder when run from a checkout."""
    package = Path(__file__).resolve().parent
    return [package / "app_skills", package.parent.parent / "app-skills"]


def user_skills_directory() -> Path:
    return openclicky_directory() / "skills"


def load_app_skills() -> list[SkillFile]:
    """Bundled skills, then `~/.openclicky/app-skills`, a later folder's skill winning by id."""
    by_id: dict[str, SkillFile] = {}
    for directory in [*bundled_app_skills_directories(), openclicky_directory() / "app-skills"]:
        for skill in load_skills(directory):
            by_id[skill.id] = skill
    return [by_id[skill_id] for skill_id in sorted(by_id)]


@dataclass
class SkillLibrary:
    """The person's skill library, read and written as the command-line tool does."""
    directory: Path = field(default_factory=user_skills_directory)

    @property
    def library_directory(self) -> Path:
        return self.directory / "library"

    @property
    def active_directory(self) -> Path:
        return self.directory / "active"

    @property
    def activations_path(self) -> Path:
        return self.directory / "activations.json"

    def skills(self) -> list[SkillFile]:
        return load_skills(self.library_directory)

    def active_ids(self) -> list[str]:
        try:
            active = json.loads(self.activations_path.read_text(encoding="utf-8")).get("active")
        except (OSError, ValueError, AttributeError):
            return []
        return [skill_id for skill_id in active if isinstance(skill_id, str)] if isinstance(active, list) else []

    def active_talk_skills(self) -> list[SkillFile]:
        active = set(self.active_ids())
        return [skill for skill in self.skills() if skill.id in active and skill.is_for_talk]

    def set_active(self, skill_id: str, on: bool) -> None:
        ids = [existing for existing in self.active_ids() if existing != skill_id] + ([skill_id] if on else [])
        self.directory.mkdir(parents=True, exist_ok=True)
        payload = {"active": list(dict.fromkeys(ids)), "updatedAt": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())}
        # Atomic: a torn write must never read back as "nothing active".
        temporary = self.directory / f".activations.{os.getpid()}.{time.time_ns()}.tmp"
        temporary.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
        temporary.replace(self.activations_path)
        self.sync_active_directory()

    def sync_active_directory(self) -> None:
        """`active/` holds exactly one link per activated, existing skill (the tool's `syncActiveDir`)."""
        existing_ids = {skill.id for skill in self.skills()}
        wanted = [skill_id for skill_id in self.active_ids() if skill_id in existing_ids]
        self.active_directory.mkdir(parents=True, exist_ok=True)
        for entry in self.active_directory.iterdir():
            if entry.name not in wanted:
                if entry.is_symlink() or entry.is_file():
                    entry.unlink(missing_ok=True)
                else:
                    shutil.rmtree(entry, ignore_errors=True)
        for skill_id in wanted:
            link, target = self.active_directory / skill_id, self.library_directory / skill_id
            if link.is_symlink() and Path(os.path.realpath(link)) == Path(os.path.realpath(target)):
                continue
            if link.is_symlink() or link.exists():
                link.unlink() if link.is_symlink() else shutil.rmtree(link, ignore_errors=True)
            try:
                link.symlink_to(target, target_is_directory=True)
            except FileExistsError:
                pass  # the tool linked it first


def talk_skills_block(front: FrontApp | None, library: SkillLibrary | None = None) -> str:
    """The skills that apply to this question: the person's active ones, and the app in front's."""
    library = library or SkillLibrary()
    app_skill = match_app_skill(front, [skill for skill in load_app_skills() if skill.is_for_talk]) if front else None
    if app_skill is not None:
        applog.append(f"skills: app skill \"{app_skill.name}\" for {front.window_class or '?'}")
    return prompt_block(library.active_talk_skills(), app_skill, front)
