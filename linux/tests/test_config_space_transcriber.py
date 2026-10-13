import base64
import json

import pytest

from openclicky_linux.config import HOSTED_BACKEND_URL, LinuxSettings, load_settings
from openclicky_linux.space import load_space
from openclicky_linux.transcriber import TranscriptionError, route_for


def test_settings_come_from_shell_json_with_the_environment_winning(tmp_path):
    shell_json = tmp_path / "shell.json"
    shell_json.write_text(json.dumps({"openaiApiKey": " sk-file ", "token": "tok", "backendUrl": "http://localhost:8787/",
                                      "language": "en-IN", "linuxDictationKey": "Alt_R"}))
    settings = load_settings(shell_json, environment={})
    assert settings == LinuxSettings(openai_api_key="sk-file", account_token="tok", backend_url="http://localhost:8787",
                                     language_code="en-IN", dictation_key="Alt_R")
    assert settings.bare_language_code == "en"
    overridden = load_settings(shell_json, environment={"OPENCLICKY_OPENAI_KEY": "sk-env", "BACKEND_URL": "http://other"})
    assert overridden.openai_api_key == "sk-env"
    assert overridden.backend_url == "http://other"


def test_missing_or_broken_shell_json_gives_the_defaults(tmp_path):
    assert load_settings(tmp_path / "absent.json", environment={}) == LinuxSettings()
    broken = tmp_path / "broken.json"
    broken.write_text("{not json")
    assert load_settings(broken, environment={}).backend_url == HOSTED_BACKEND_URL


def test_the_mac_apps_dictionary_and_shortcut_files_are_read(tmp_path):
    # The shape the Mac app's JSONEncoder writes (ids and dates included, which are ignored here).
    (tmp_path / "dictionary.json").write_text(json.dumps([
        {"id": "A1", "written": "Aaditya Kshatriya", "heardAs": ["aditya shatriya"], "createdAt": 781000000.0},
        {"id": "A2", "written": "  "},
    ]))
    (tmp_path / "shortcuts.json").write_text(json.dumps([{"id": "B1", "trigger": "my sign-off", "replacement": "Cheers", "createdAt": 1.0}]))
    space = load_space(tmp_path)
    assert [term.written for term in space.dictionary] == ["Aaditya Kshatriya"]
    assert space.dictionary[0].heard_as == ("aditya shatriya",)
    assert space.shortcuts[0].replacement == "Cheers"


def test_an_own_openai_key_goes_straight_to_openai_never_the_backend():
    route = route_for(b"RIFFwav", LinuxSettings(openai_api_key="sk-own", account_token="tok", language_code="hi-IN"))
    assert route.kind == "openai"
    assert route.request.full_url == "https://api.openai.com/v1/audio/transcriptions"
    assert route.request.get_header("Authorization") == "Bearer sk-own"
    body = route.request.data
    assert b'name="model"\r\n\r\ngpt-4o-mini-transcribe' in body
    assert b'name="language"\r\n\r\nhi' in body
    assert b'filename="audio.wav"\r\nContent-Type: audio/wav\r\n\r\nRIFFwav' in body


def test_an_account_goes_to_the_backend_as_base64_json():
    route = route_for(b"RIFFwav", LinuxSettings(account_token="tok", backend_url="http://localhost:8787"))
    assert route.kind == "backend"
    assert route.request.full_url == "http://localhost:8787/agent/transcribe"
    assert route.request.get_header("Authorization") == "Bearer tok"
    payload = json.loads(route.request.data)
    assert base64.b64decode(payload["audio"]) == b"RIFFwav"
    assert payload["mime"] == "audio/wav"
    assert "language" not in payload


def test_no_key_and_no_account_names_what_is_missing():
    with pytest.raises(TranscriptionError, match="openai key or an openclicky account"):
        route_for(b"", LinuxSettings())
