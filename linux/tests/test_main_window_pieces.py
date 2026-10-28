"""The smaller pieces around the main window: styles saved the Mac's way, the orb's settings, guest
accounts, the automatic model download, the farewell, the agent's card."""

import json

from openclicky_linux import accounts, auto_download, offline
from openclicky_linux.config import load_settings, save_settings
from openclicky_linux.space import SEEDED_STYLES, load_space, save_styles


def test_styles_are_saved_as_the_mac_writes_them_keeping_its_taglines(tmp_path):
    (tmp_path / "styles.json").write_text(json.dumps([{"id": "email", "name": "email", "tagline": "polished", "rules": "old",
                                                        "sentenceCase": True, "removeFillers": True, "polishWithModel": True,
                                                        "appBundleIDs": ["com.apple.mail"]}]))
    styles = list(load_space(tmp_path).styles)
    styles[0] = styles[0].__class__(**{**styles[0].__dict__, "rules": "Warm and short.", "apps": ("com.apple.mail", "thunderbird")})
    save_styles(styles, tmp_path)
    saved = json.loads((tmp_path / "styles.json").read_text())[0]
    assert saved["tagline"] == "polished" and saved["rules"] == "Warm and short."
    assert saved["appBundleIDs"] == ["com.apple.mail", "thunderbird"]
    assert load_space(tmp_path).style_for("Thunderbird").id == "email"
    assert SEEDED_STYLES  # the seeded ones are what a fresh computer has


def test_the_orbs_settings_round_trip_and_defaults_write_nothing(tmp_path):
    shell = tmp_path / "shell.json"
    save_settings({"orb_shows": "always", "orb_position": "top", "orb_size": "normal", "orb_theme": "light", "reduce_motion": True,
                   "workspace": "~/Work"}, shell)
    assert json.loads(shell.read_text()) == {"linuxOrbShows": "always", "linuxOrbPosition": "top", "linuxOrbTheme": "light",
                                             "linuxReduceMotion": True, "workspace": "~/Work"}
    settings = load_settings(shell, environment={})
    assert (settings.orb_shows, settings.orb_position, settings.orb_size, settings.reduce_motion) == ("always", "top", "normal", True)


def test_a_guest_starts_from_the_device_alone(monkeypatch, tmp_path):
    sent = []
    monkeypatch.setattr(accounts, "device_hash", lambda: "a" * 64)
    monkeypatch.setattr(accounts, "_post", lambda url, body, token=None, headers=None: sent.append((url, body)) or accounts._Reply(
        200, {"status": "signed_in", "session": {"access_token": "t", "refresh_token": "r", "expires_in": 3600}, "confirmed": False}, None))
    stored = []
    monkeypatch.setattr(accounts, "_store", lambda session, email, shell_json_path=None: stored.append((session["access_token"], email)))
    outcome = accounts.start_guest("https://backend")
    assert outcome.kind == "signed_in" and sent == [("https://backend/auth/guest", {"device": "a" * 64})] and stored == [("t", "")]
    monkeypatch.setattr(accounts, "_post", lambda url, body, token=None, headers=None: accounts._Reply(402, {"error": "email_needed"}, None))
    assert accounts.start_guest("https://backend").message == "email_needed"


def test_models_download_on_their_own_only_when_wanted_unmetered_and_with_room(monkeypatch, tmp_path):
    monkeypatch.setenv("OPENCLICKY_HOME", str(tmp_path))
    monkeypatch.setattr(offline, "is_installed", lambda asset: False)
    monkeypatch.setattr(offline, "is_engine_available", lambda: True)
    monkeypatch.setattr(offline, "models_directory", lambda: tmp_path / "models")
    monkeypatch.setattr(auto_download, "network_is_metered", lambda: False)
    assert auto_download.models_to_fetch([offline.PARAKEET]) == [offline.PARAKEET]
    auto_download.set_declined(offline.PARAKEET, True)
    assert auto_download.models_to_fetch([offline.PARAKEET]) == []
    auto_download.set_declined(offline.PARAKEET, False)
    monkeypatch.setattr(auto_download, "network_is_metered", lambda: True)
    assert auto_download.models_to_fetch([offline.PARAKEET]) == []


def test_the_farewell_says_how_to_come_back_and_the_card_says_how_it_went():
    from openclicky_linux.agent import AgentRunResult
    from openclicky_linux.farewell import FAREWELLS, farewell
    assert farewell() in FAREWELLS and all("openclicky" in line for line in FAREWELLS)
    import gi
    gi.require_version("Gtk", "3.0")
    from openclicky_linux.result_card import card_title
    assert card_title(AgentRunResult(status="completed")) == "the agent finished"
    assert card_title(AgentRunResult(status="failed", error_message="codex is not signed in")) == "the agent stopped: codex is not signed in"
