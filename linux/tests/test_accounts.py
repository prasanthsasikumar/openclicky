"""The free account: the backend's answers as the form shows them, the session kept in shell.json the
Mac's way, the refresh ahead of expiry (and the sign-out when the refresh token is dead), and a
request the backend refused asked once more after renewing; with a stand-in backend."""

from __future__ import annotations

import json

import pytest

from openclicky_linux import accounts
from openclicky_linux.__main__ import on_the_account
from openclicky_linux.ask import AskError
from openclicky_linux.config import load_settings


@pytest.fixture
def home(tmp_path, monkeypatch):
    monkeypatch.setenv("OPENCLICKY_HOME", str(tmp_path))
    return tmp_path


def shell_json(home) -> dict:
    return json.loads((home / "shell.json").read_text())


def test_a_new_email_is_signed_in_as_a_guest_and_kept_under_the_macs_keys(home):
    reply = accounts._Reply(200, {"status": "signed_in", "confirmed": False,
                                  "session": {"access_token": "access", "refresh_token": "refresh", "expires_in": 3600}}, None)
    outcome = accounts.outcome_of(reply, "sam@example.com")
    assert (outcome.kind, outcome.confirmed) == ("signed_in", False)
    stored = shell_json(home)
    assert (stored["token"], stored["refreshToken"], stored["accountEmail"]) == ("access", "refresh", "sam@example.com")
    assert stored["tokenExpiresAt"] > 0


@pytest.mark.parametrize("status, body, retry_after, sentence", [
    (402, {"error": "accounts_full"}, None, accounts.ACCOUNTS_FULL),
    (401, {"error": "bad_code"}, None, accounts.BAD_CODE),
    (429, {"error": "slow_down"}, 150, "too many tries, wait about 3 minutes and try again."),
    (502, {}, None, accounts.UNREACHABLE),
])
def test_a_refusal_is_the_macs_sentence(home, status, body, retry_after, sentence):
    assert accounts.outcome_of(accounts._Reply(status, body, retry_after), "sam@example.com") == accounts.Outcome("failed", sentence)


def test_an_email_with_an_account_gets_a_code(home):
    assert accounts.outcome_of(accounts._Reply(200, {"status": "code_sent"}, None), "sam@example.com").kind == "code_sent"
    assert not (home / "shell.json").exists()


def test_the_computer_is_known_by_a_salted_hash_of_its_machine_id(tmp_path):
    machine_id = tmp_path / "machine-id"
    machine_id.write_text("0123456789abcdef\n")
    hashed = accounts.device_hash((str(machine_id),))
    assert len(hashed) == 64 and "0123456789abcdef" not in hashed
    assert accounts.device_hash((str(tmp_path / "none"),)) is None


def signed_in(home, expires_at: float) -> None:
    (home / "shell.json").write_text(json.dumps({"token": "old access", "refreshToken": "old refresh", "tokenExpiresAt": expires_at,
                                                 "accountEmail": "sam@example.com"}))


def stand_in_sign_in_server(monkeypatch, status: int, body: dict) -> list:
    sent = []
    monkeypatch.setattr(accounts, "_auth_config", lambda backend_url: {"supabaseUrl": "https://auth.example", "publishableKey": "publishable"})
    monkeypatch.setattr(accounts, "_post", lambda url, body_sent, token=None, headers=None: sent.append((url, body_sent)) or accounts._Reply(status, body, None))
    return sent


def test_the_session_is_renewed_only_in_the_last_ten_minutes(home, monkeypatch):
    signed_in(home, expires_at=10_000)
    sent = stand_in_sign_in_server(monkeypatch, 200, {"access_token": "new access", "refresh_token": "new refresh", "expires_in": 3600})
    assert accounts.refresh_if_needed(now=10_000 - 601) == "not needed" and sent == []
    assert accounts.refresh_if_needed(now=10_000 - 599) == "refreshed"
    assert sent == [("https://auth.example/auth/v1/token?grant_type=refresh_token", {"refresh_token": "old refresh"})]
    stored = shell_json(home)
    assert (stored["token"], stored["refreshToken"], stored["accountEmail"]) == ("new access", "new refresh", "sam@example.com")


def test_a_dead_refresh_token_signs_out_and_a_server_that_is_down_keeps_the_session(home, monkeypatch):
    signed_in(home, expires_at=0)
    stand_in_sign_in_server(monkeypatch, 503, {})
    assert accounts.refresh_if_needed() == "failed" and load_settings().account_token == "old access"
    stand_in_sign_in_server(monkeypatch, 400, {"error": "invalid_grant"})
    assert accounts.refresh_if_needed() == "signed out"
    assert load_settings().account_token is None and not shell_json(home)


def test_a_question_the_account_was_refused_for_is_asked_once_more_after_renewing(home, monkeypatch):
    renewals = []
    monkeypatch.setattr(accounts, "refresh_if_needed", lambda force=False: renewals.append(force) or ("refreshed" if force else "not needed"))
    attempts = []

    def ask():
        attempts.append(1)
        if len(attempts) == 1:
            raise AskError("the openclicky backend refused the key or token. check it in settings.", account_refused=True)
        return "an answer"
    assert on_the_account(ask) == "an answer"
    assert renewals == [False, True] and len(attempts) == 2


def test_an_own_keys_refusal_is_not_retried(home, monkeypatch):
    monkeypatch.setattr(accounts, "refresh_if_needed", lambda force=False: "refreshed")

    def ask():
        raise AskError("anthropic refused the key or token. check it in settings.")
    with pytest.raises(AskError, match="anthropic refused"):
        on_the_account(ask)
