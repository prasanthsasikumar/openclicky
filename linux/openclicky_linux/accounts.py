"""A free OpenClicky account by email, as on the Mac (`OpenClickyAuthSession.swift`): no password.

- `start(email)`: a new email is signed in at once as a guest (unconfirmed; the email holds a
  6-digit code to confirm it); an email that already has an account gets a 6-digit code instead.
- `submit_code(email, code)`: signs in with that code.
- `confirm(code)`: a guest confirms its email with the code from the confirmation mail.
- `refresh_if_needed()`: the access token is renewed 10 minutes before it expires, through the
  sign-in server the backend names (`GET /auth/config`); a refusal signs out.
- `sign_out()`.

The session is kept where the Mac keeps it, in shell.json (`token`, `refreshToken`,
`tokenExpiresAt`, `accountEmail`), so the CLI and every other part read the same account. This
computer is told apart by a salted hash of `/etc/machine-id` (the Mac hashes its hardware UUID the
same way): the id itself never leaves it, and reinstalling never refills the starter allowance.
The sentences are the Mac's.
"""

from __future__ import annotations

import hashlib
import json
import threading
import time
import urllib.error
import urllib.request
from dataclasses import dataclass
from pathlib import Path

from . import applog
from .config import load_settings, save_account

REQUEST_TIMEOUT_SECONDS = 20
REFRESH_LEAD_SECONDS = 600
# How often the running app checks whether the session needs renewing (the Mac's 5-minute timer).
REFRESH_CHECK_SECONDS = 300

UNREACHABLE = "couldn't reach openclicky right now, try again in a minute."
BAD_CODE = "that code didn't work. check it, or ask for a new one."
ACCOUNTS_UNAVAILABLE = "openclicky's accounts are having trouble right now, try again in a few minutes."
ACCOUNTS_FULL = "openclicky's free accounts are full right now, you can use your own key instead."
DEVICE_LIMIT = "this computer already has two openclicky accounts, sign in with one of them."
SESSION_EXPIRED = "your sign-in expired, sign in again with your email."


def device_hash(machine_id_paths: tuple[str, ...] = ("/etc/machine-id", "/var/lib/dbus/machine-id")) -> str | None:
    for path in machine_id_paths:
        try:
            machine_id = Path(path).read_text().strip()
        except OSError:
            continue
        if machine_id:
            return hashlib.sha256(f"openclicky-device-v1:{machine_id}".encode()).hexdigest()
    return None


def slow_down_message(retry_after_seconds: int | None) -> str:
    if not retry_after_seconds or retry_after_seconds <= 0:
        return "too many tries, wait a few minutes and try again."
    minutes = max(1, -(-retry_after_seconds // 60))
    return f"too many tries, wait about {minutes} minute{'' if minutes == 1 else 's'} and try again."


def failure_message(status: int, error: str | None, retry_after_seconds: int | None = None) -> str:
    """The sentence for a refused /auth/start, /auth/code or /auth/confirm (the Mac's)."""
    return {
        "accounts_full": ACCOUNTS_FULL,
        "device_limit": DEVICE_LIMIT,
        "bad_email": "that doesn't look like an email address.",
        "bad_code": BAD_CODE,
        "slow_down": slow_down_message(retry_after_seconds),
        "accounts_unavailable": ACCOUNTS_UNAVAILABLE,
    }.get(error or "", slow_down_message(retry_after_seconds) if status == 429 else UNREACHABLE)


@dataclass(frozen=True)
class Outcome:
    kind: str  # "signed_in" | "code_sent" | "already_confirmed" | "failed"
    message: str | None = None  # the sentence to show when it failed
    confirmed: bool = False


class _Reply:
    def __init__(self, status: int, body: dict, retry_after: int | None) -> None:
        self.status = status
        self.body = body
        self.retry_after = retry_after


def _post(url: str, body: dict | None, token: str | None = None, headers: dict[str, str] | None = None) -> _Reply:
    request = urllib.request.Request(url, data=json.dumps(body or {}).encode(), method="POST",
                                     headers={"Content-Type": "application/json", **(headers or {})})
    if token:
        request.add_header("Authorization", f"Bearer {token}")
    try:
        with urllib.request.urlopen(request, timeout=REQUEST_TIMEOUT_SECONDS) as response:
            return _Reply(response.status, _json(response.read()), None)
    except urllib.error.HTTPError as refusal:
        retry_after = refusal.headers.get("Retry-After") if refusal.headers else None
        return _Reply(refusal.code, _json(refusal.read()), int(retry_after) if retry_after and retry_after.strip().isdigit() else None)


def _json(data: bytes) -> dict:
    try:
        value = json.loads(data.decode("utf-8") or "{}")
    except ValueError:
        return {}
    return value if isinstance(value, dict) else {}


def _store(session: dict, email: str, shell_json_path: Path | None = None) -> None:
    save_account(token=session["access_token"], refresh_token=session["refresh_token"],
                 expires_at=time.time() + float(session.get("expires_in") or 3600), email=email, shell_json_path=shell_json_path)


def outcome_of(reply: _Reply, email: str, shell_json_path: Path | None = None) -> Outcome:
    """The backend's answer to /auth/start, /auth/code or /auth/guest as one thing the form shows; a
    session in it is stored."""
    if 200 <= reply.status < 300:
        if reply.body.get("status") == "code_sent":
            return Outcome("code_sent")
        session = reply.body.get("session")
        if isinstance(session, dict) and session.get("access_token") and session.get("refresh_token"):
            _store(session, email, shell_json_path)
            confirmed = bool(reply.body.get("confirmed"))
            applog.append(f"sign-in: signed in ({'confirmed' if confirmed else 'a guest'})")
            return Outcome("signed_in", confirmed=confirmed)
        return Outcome("failed", UNREACHABLE)
    return Outcome("failed", failure_message(reply.status, reply.body.get("error"), reply.retry_after))


def start(email: str, backend_url: str | None = None) -> Outcome:
    """Setup's one question: the email."""
    email = email.strip().lower()
    device = device_hash()
    if not device:
        return Outcome("failed", UNREACHABLE)
    try:
        reply = _post(f"{backend_url or load_settings().backend_url}/auth/start", {"email": email, "device": device})
    except (urllib.error.URLError, OSError, TimeoutError):
        return Outcome("failed", UNREACHABLE)
    return outcome_of(reply, email)


def start_guest(backend_url: str | None = None) -> Outcome:
    """No email at all: a guest made from this computer's device hash, on OpenClicky's free credits
    (pooled per computer, so reinstalling never refills them). The email can come later through
    `start`, whose guest replaces this one. Fails with "email_needed" when the backend wants one."""
    device = device_hash()
    if not device:
        return Outcome("failed", UNREACHABLE)
    try:
        reply = _post(f"{backend_url or load_settings().backend_url}/auth/guest", {"device": device})
    except (urllib.error.URLError, OSError, TimeoutError):
        return Outcome("failed", UNREACHABLE)
    if reply.status == 402:
        return Outcome("failed", "email_needed")
    return outcome_of(reply, "")


def submit_code(email: str, code: str, backend_url: str | None = None) -> Outcome:
    email = email.strip().lower()
    body = {"email": email, "code": "".join(character for character in code if character.isdigit())}
    device = device_hash()
    if device:
        body["device"] = device
    try:
        reply = _post(f"{backend_url or load_settings().backend_url}/auth/code", body)
    except (urllib.error.URLError, OSError, TimeoutError):
        return Outcome("failed", UNREACHABLE)
    return outcome_of(reply, email)


def confirm(code: str) -> Outcome:
    """A guest confirms its email with the 6-digit code from the confirmation mail."""
    refresh_if_needed()
    settings = load_settings()
    if not settings.account_token:
        return Outcome("failed", UNREACHABLE)
    try:
        reply = _post(f"{settings.backend_url}/auth/confirm", {"code": "".join(character for character in code if character.isdigit())},
                      token=settings.account_token)
    except (urllib.error.URLError, OSError, TimeoutError):
        return Outcome("failed", UNREACHABLE)
    if 200 <= reply.status < 300 and reply.body.get("status") == "already_confirmed":
        return Outcome("already_confirmed", confirmed=True)
    return outcome_of(reply, settings.account_email or "")


def _auth_config(backend_url: str) -> dict:
    with urllib.request.urlopen(f"{backend_url}/auth/config", timeout=REQUEST_TIMEOUT_SECONDS) as response:
        config = _json(response.read())
    if not config.get("supabaseUrl") or not config.get("publishableKey"):
        raise OSError("this backend has no sign-in configured")
    return config


# One refresh at a time: a refresh token works once, and two refreshes racing with the same one
# would have the second refused, which signs out.
_refresh_lock = threading.Lock()


def refresh_if_needed(force: bool = False, now: float | None = None) -> str:
    """"not needed", "refreshed", "signed out" (the sign-in server refused the refresh token: it
    will never work again) or "failed" (offline or the server is down: the session is kept)."""
    with _refresh_lock:
        return _refresh(force, now)


def _refresh(force: bool, now: float | None) -> str:
    settings = load_settings()
    if not settings.refresh_token:
        return "not needed"
    now = time.time() if now is None else now
    if not force and now <= (settings.token_expires_at or 0) - REFRESH_LEAD_SECONDS:
        return "not needed"
    try:
        config = _auth_config(settings.backend_url)
        reply = _post(f"{config['supabaseUrl']}/auth/v1/token?grant_type=refresh_token", {"refresh_token": settings.refresh_token},
                      headers={"apikey": config["publishableKey"]})
    except (urllib.error.URLError, OSError, TimeoutError, ValueError) as failure:
        applog.append(f"sign-in: refresh failed, session kept ({type(failure).__name__})")
        return "failed"
    if reply.status == 200 and reply.body.get("access_token") and reply.body.get("refresh_token"):
        user = reply.body.get("user") if isinstance(reply.body.get("user"), dict) else {}
        _store(reply.body, (user or {}).get("email") or settings.account_email or "")
        applog.append("sign-in: session refreshed")
        return "refreshed"
    if 400 <= reply.status < 500 and reply.status != 429:
        applog.append(f"sign-in: refresh refused ({reply.status}); signing out")
        sign_out()
        return "signed out"
    applog.append(f"sign-in: refresh failed ({reply.status}), session kept")
    return "failed"


def sign_out(shell_json_path: Path | None = None) -> None:
    save_account(token=None, refresh_token=None, expires_at=None, email=None, shell_json_path=shell_json_path)
    applog.append("sign-in: signed out")


def billing(settings=None) -> dict | None:
    """`GET /billing/me`: the account's allowance, or None when it cannot be read."""
    settings = settings or load_settings()
    if not settings.account_token:
        return None
    request = urllib.request.Request(f"{settings.backend_url}/billing/me", headers={"Authorization": f"Bearer {settings.account_token}"})
    try:
        with urllib.request.urlopen(request, timeout=REQUEST_TIMEOUT_SECONDS) as response:
            return _json(response.read())
    except (urllib.error.URLError, OSError, TimeoutError):
        return None
