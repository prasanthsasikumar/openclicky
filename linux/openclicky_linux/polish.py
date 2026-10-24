"""The model clean-up of a take ("Clean up my words with AI"), the Mac's `TakePolishers.swift` and
`TakeFormatter.format` without Apple's on-device model:

1. an own OpenAI key → OpenAI chat (`gpt-4o-mini`)
2. else an own Anthropic key → Claude Haiku
3. else an own OpenRouter key → `openai/gpt-4.1-mini` there
4. else the account → the backend's `POST /v1/polish`
5. else none: the local clean-up only.

The style's rules go into the prompt (`formatter.system_prompt`). It must never hold a take up:
past `MODEL_TIMEOUT_SECONDS` the local result is pasted, and so it is when the rewrite does not
look like the same take (an answer to it, or cut short). A take heard by the offline engine is
only polished when Settings allows it: the offline engine's promise is that nothing leaves.
"""

from __future__ import annotations

import json
import urllib.error
import urllib.request
from dataclasses import dataclass

from . import applog, formatter
from .config import LinuxSettings
from .space import DictationSpace, DictationStyle

MODEL_TIMEOUT_SECONDS = 3.0
OPENAI_CHAT_URL = "https://api.openai.com/v1/chat/completions"
OPENROUTER_CHAT_URL = "https://openrouter.ai/api/v1/chat/completions"
ANTHROPIC_MESSAGES_URL = "https://api.anthropic.com/v1/messages"
OPENAI_POLISH_MODEL = "gpt-4o-mini"
OPENROUTER_POLISH_MODEL = "openai/gpt-4.1-mini"
ANTHROPIC_POLISH_MODEL = "claude-haiku-4-5"
POLISH_MAX_OUTPUT_TOKENS = 4096


@dataclass(frozen=True)
class Polisher:
    kind: str  # "openai" | "anthropic" | "openrouter" | "backend"


def polisher_for(settings: LinuxSettings) -> Polisher | None:
    if settings.openai_api_key:
        return Polisher("openai")
    if settings.anthropic_api_key:
        return Polisher("anthropic")
    if settings.openrouter_api_key:
        return Polisher("openrouter")
    if settings.account_token:
        return Polisher("backend")
    return None


def wants_model(settings: LinuxSettings, engine: str) -> bool:
    """The switch, and for the offline engine only when that was allowed too."""
    if not settings.polish_with_model:
        return False
    return settings.polish_offline_takes if engine == "offline" else True


def request_for(polisher: Polisher, system: str, take: str, settings: LinuxSettings) -> urllib.request.Request:
    if polisher.kind in ("openai", "openrouter"):
        url, key, model = ((OPENAI_CHAT_URL, settings.openai_api_key, OPENAI_POLISH_MODEL) if polisher.kind == "openai"
                           else (OPENROUTER_CHAT_URL, settings.openrouter_api_key, OPENROUTER_POLISH_MODEL))
        body = {"model": model, "temperature": 0.2, "messages": [{"role": "system", "content": system}, {"role": "user", "content": take}]}
        return urllib.request.Request(url, data=json.dumps(body).encode(), method="POST",
                                      headers={"Authorization": f"Bearer {key}", "Content-Type": "application/json"})
    if polisher.kind == "anthropic":
        body = {"model": ANTHROPIC_POLISH_MODEL,
                # An upper bound: a take cut off at max_tokens would be pasted half-finished.
                "max_tokens": min(POLISH_MAX_OUTPUT_TOKENS, len(take.encode("utf-16-le")) + 64),
                "system": [{"type": "text", "text": system, "cache_control": {"type": "ephemeral"}}],
                "messages": [{"role": "user", "content": take}]}
        return urllib.request.Request(ANTHROPIC_MESSAGES_URL, data=json.dumps(body).encode(), method="POST", headers={
            "x-api-key": settings.anthropic_api_key or "", "anthropic-version": "2023-06-01", "Content-Type": "application/json"})
    body = {"purpose": "polish", "system": system, "text": take}
    return urllib.request.Request(f"{settings.backend_url}/v1/polish", data=json.dumps(body).encode(), method="POST",
                                  headers={"Authorization": f"Bearer {settings.account_token}", "Content-Type": "application/json"})


def text_of(polisher: Polisher, answer: dict) -> str | None:
    if polisher.kind in ("openai", "openrouter"):
        choices = answer.get("choices") or []
        content = choices[0].get("message", {}).get("content") if choices and isinstance(choices[0], dict) else None
        return content if isinstance(content, str) else None
    if polisher.kind == "anthropic":
        # Half a take must never be pasted as if it were whole.
        if answer.get("stop_reason") == "max_tokens":
            return None
        text = "".join(block.get("text", "") for block in answer.get("content") or [] if isinstance(block, dict) and block.get("type") == "text")
        return text or None
    text = answer.get("text")
    return text if isinstance(text, str) else None


def polish(raw: str, local: str, style: DictationStyle, space: DictationSpace, settings: LinuxSettings, engine: str,
           app_name: str | None = None, timeout_seconds: float = MODEL_TIMEOUT_SECONDS, send=None) -> tuple[str, bool]:
    """(the text to paste, whether the clean-up was wanted but did not happen). `local` is the
    rules-only result, kept whenever the model cannot be trusted with this take."""
    if not local or not style.polish_with_model or not wants_model(settings, engine):
        return local, False
    polisher = polisher_for(settings)
    if polisher is None:
        return local, False
    system = formatter.system_prompt(style, space, app_name=app_name, language_code=settings.language_code)
    request = request_for(polisher, system, raw, settings)
    try:
        if send is not None:
            answer = send(request, timeout_seconds)
        else:
            with urllib.request.urlopen(request, timeout=timeout_seconds) as response:
                answer = json.loads(response.read().decode("utf-8"))
    except (urllib.error.URLError, OSError, TimeoutError, ValueError) as failure:
        applog.append(f"polish: {polisher.kind} failed ({type(failure).__name__}); the local clean-up went in")
        return local, True
    cleaned = formatter.unwrap_model_answer(text_of(polisher, answer if isinstance(answer, dict) else {}) or "")
    if not cleaned or not formatter.looks_like_the_same_take(cleaned, raw):
        applog.append(f"polish: {polisher.kind}'s rewrite did not look like the take; the local clean-up went in")
        return local, True
    return cleaned, False
