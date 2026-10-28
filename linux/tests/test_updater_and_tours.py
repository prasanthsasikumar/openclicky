"""Updates from GitHub Releases, and the tours' steps and words."""

import hashlib
import json

import pytest

from openclicky_linux import updater
from openclicky_linux.updater import Update, expected_sha256, newest_update


def release(version, linux=True, prerelease=False):
    assets = [{"name": "OpenClicky.dmg", "browser_download_url": "https://x/dmg"}]
    if linux:
        assets += [{"name": f"openclicky_linux-{version}-py3-none-any.whl", "browser_download_url": f"https://x/{version}.whl"},
                   {"name": "SHA256SUMS-linux.txt", "browser_download_url": f"https://x/{version}.sums"}]
    return {"tag_name": f"v{version}", "assets": assets, "prerelease": prerelease, "body": "notes"}


def test_the_newest_linux_package_newer_than_this_one_is_found():
    releases = [release("0.9.10"), release("0.9.11", prerelease=True), release("0.9.12", linux=False), release("0.9.3")]
    found = newest_update(releases, current="0.9.2")
    assert found.version == "0.9.10" and found.wheel_url == "https://x/0.9.10.whl"
    assert newest_update(releases, current="0.9.10") is None


def test_a_package_is_installed_only_when_its_checksum_matches(monkeypatch, tmp_path):
    wheel = b"PK wheel bytes"
    sums = f"{hashlib.sha256(wheel).hexdigest()}  ./openclicky_linux-1.0.0-py3-none-any.whl\n".encode()
    assert expected_sha256(sums.decode(), "openclicky_linux-1.0.0-py3-none-any.whl") == hashlib.sha256(wheel).hexdigest()
    update = Update("1.0.0", "https://x/w", "openclicky_linux-1.0.0-py3-none-any.whl", "https://x/s")
    monkeypatch.setattr(updater, "is_installed_copy", lambda: True)
    installed = []
    fetched = {"https://x/s": sums, "https://x/w": wheel}
    updater.install(update, fetch=lambda url, timeout=20: fetched[url],
                    run=lambda command, **options: installed.append(command) or type("Done", (), {"returncode": 0, "stderr": ""})())
    assert installed[0][-1].endswith("openclicky_linux-1.0.0-py3-none-any.whl") and installed[0][1:4] == ["-m", "pip", "install"]
    fetched["https://x/w"] = b"tampered"
    with pytest.raises(OSError, match="did not match"):
        updater.install(update, fetch=lambda url, timeout=20: fetched[url])
    monkeypatch.setattr(updater, "is_installed_copy", lambda: False)
    with pytest.raises(OSError, match="git pull"):
        updater.install(update, fetch=lambda url, timeout=20: fetched[url])


def test_a_check_is_due_once_a_day(monkeypatch):
    stored = {}
    monkeypatch.setattr(updater.preferences, "get", lambda key, default=None: stored.get(key, default))
    monkeypatch.setattr(updater.preferences, "set_values", lambda **values: stored.update(values))
    assert updater.is_due(now=1000.0)
    updater.check(fetch=lambda url: json.dumps([release("0.0.1")]).encode())
    assert not updater.is_due(now=stored[updater.LAST_CHECK_KEY] + 3600)
    assert updater.is_due(now=stored[updater.LAST_CHECK_KEY] + 25 * 3600)


def test_the_tours_say_the_linux_keys():
    import gi
    gi.require_version("Gtk", "3.0")
    from openclicky_linux.tour import cards, proves_dictation, seconds_to_say, settings_tour_stops, step_order
    shown = cards("Left Ctrl", can_ask=True, on_free_account=True)
    assert shown["dictate"].title == "hold left ctrl and say something"
    assert "hold left ctrl and left alt" in shown["talk"].detail and shown["talk"].keys == ("Left Ctrl", "Left Alt")
    assert step_order(True) == ["meet", "dictate", "talk", "act", "account", "done"] and "account" not in step_order(False)
    assert proves_dictation("hello there") and not proves_dictation("no")
    stops = settings_tour_stops("Right Ctrl")
    assert stops[0].page == "dictation" and "Hold right ctrl and left alt" in stops[6].line and stops[-1].page == "general"
    assert seconds_to_say("one two three") == 3.0 and seconds_to_say(" ".join(["word"] * 26)) == 11.0
