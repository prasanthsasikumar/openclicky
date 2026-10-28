"""Who speaks the answers: Automatic's order, each vendor's request, the answers' audio, the fallback."""

import base64
import datetime
import json
import urllib.request

from openclicky_linux import speech_voices
from openclicky_linux.config import LinuxSettings
from openclicky_linux.speech_voices import AWSCredentials, resolve, sign_aws_v4, speech_request


def test_automatic_takes_own_keys_first_then_kokoro_then_the_free_voice():
    assert resolve(LinuxSettings(openai_api_key="k", account_token="t"), True, "en") == speech_voices.OPENAI
    assert resolve(LinuxSettings(account_token="t"), True, "en") == speech_voices.KOKORO
    assert resolve(LinuxSettings(account_token="t"), False, "en") == speech_voices.ACCOUNT
    # Polly and Sarvam only in the languages they speak.
    polly = LinuxSettings(amazon_polly_credentials="AKID:secret")
    assert resolve(polly, False, "fr") == speech_voices.POLLY
    assert resolve(LinuxSettings(sarvam_api_key="k"), False, "ta-IN") == speech_voices.SARVAM
    # A picked voice without its key: this computer's voice.
    picked = resolve(LinuxSettings(speech_voice="elevenlabs"), True, "en")
    assert picked in (speech_voices.SYSTEM, None)


def test_each_vendor_is_asked_its_own_way():
    settings = LinuxSettings(openai_api_key="sk", elevenlabs_api_key="el", google_cloud_api_key="g", gemini_api_key="gm",
                             sarvam_api_key="sv", account_token="tok", backend_url="https://backend")
    openai = speech_request(speech_voices.OPENAI, "hi", settings, "en")
    assert json.loads(openai.data) == {"model": "gpt-4o-mini-tts", "voice": "marin", "input": "hi", "response_format": "mp3"}
    eleven = speech_request(speech_voices.ELEVENLABS, "hi", settings, "en")
    assert "EXAVITQu4vr4xnSDxMaL" in eleven.full_url and eleven.get_header("Xi-api-key") == "el"
    account = speech_request(speech_voices.ACCOUNT, "hi", settings, "en")
    assert account.full_url == "https://backend/tts" and json.loads(account.data) == json.loads(eleven.data)
    google = json.loads(speech_request(speech_voices.GOOGLE, "hi", settings, "hi-IN").data)
    assert google["voice"] == {"languageCode": "hi-IN", "name": "hi-IN-Neural2-A"}
    assert json.loads(speech_request(speech_voices.SARVAM, "vanakkam", settings, "ta").data)["target_language_code"] == "ta-IN"
    assert "gemini-3.8-flash-tts:generateContent" in speech_request(speech_voices.GEMINI, "hi", settings, "en").full_url


def test_polly_is_signed_as_aws_documents_it():
    credentials = AWSCredentials("AKIDEXAMPLE", "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY")
    now = datetime.datetime(2015, 8, 30, 12, 36, tzinfo=datetime.timezone.utc)
    for method, data, signature in (("GET", None, "5fa00fa31553b73ebf1942676e86291e8372ff2a2260956d9b8aae1d763fbf31"),
                                    ("POST", b"", "5da7c1a2acd57cee7505fc6676e4e544621c30862966e37dddb68e92efbe5d6b")):
        request = urllib.request.Request("https://example.amazonaws.com/", data=data, method=method)
        sign_aws_v4(request, credentials, "service", now)
        assert request.get_header("Authorization").endswith(f"Signature={signature}")  # AWS's get-vanilla and post-vanilla
    assert AWSCredentials.parse("id:secret") == AWSCredentials("id", "secret", "us-east-1")
    assert AWSCredentials.parse("id:se/c+ret:eu-west-1").region == "eu-west-1" and AWSCredentials.parse("nothing") is None
    polly = speech_request(speech_voices.POLLY, "hi", LinuxSettings(amazon_polly_credentials="id:secret"), "de")
    assert polly.full_url == "https://polly.us-east-1.amazonaws.com/v1/speech" and json.loads(polly.data)["VoiceId"] == "Vicki"


def test_audio_comes_out_as_wav_whatever_the_vendor_sends():
    pcm = b"\x01\x00" * 2400
    gemini = json.dumps({"candidates": [{"content": {"parts": [{"inlineData": {"mimeType": "audio/L16;codec=pcm;rate=24000",
                                                                               "data": base64.b64encode(pcm).decode()}}]}}]}).encode()
    wav = speech_voices.audio_for(speech_voices.GEMINI, "hi", LinuxSettings(gemini_api_key="k"), "en", send=lambda request: gemini)
    assert wav[:4] == b"RIFF" and wav.endswith(pcm)
    sarvam = json.dumps({"audios": [base64.b64encode(speech_voices.pcm_to_wav(pcm, 22050)).decode()]}).encode()
    assert speech_voices.audio_for(speech_voices.SARVAM, "hi", LinuxSettings(sarvam_api_key="k"), "hi", send=lambda request: sarvam)[:4] == b"RIFF"
    # An MP3 (the Mac's own recording) decoded by GStreamer.
    assert speech_voices.recorded_notice("notice-no-internet")[:4] == b"RIFF"


def test_failures_find_their_recorded_notice():
    assert speech_voices.notice_for("couldn't reach anthropic: timed out") == "notice-no-internet"
    assert speech_voices.notice_for("couldn't see the screen: refused") == "notice-no-screen"
    assert speech_voices.notice_for("claude answered with no text.") == "notice-empty-answer"
    assert speech_voices.notice_for("anthropic refused the key or token. check it in settings.") is None


def test_a_cloud_voice_that_fails_hands_the_rest_to_the_next_voice(monkeypatch):
    from openclicky_linux import voice as voice_module
    played = []
    voice = voice_module.Voice(player=("true",))
    monkeypatch.setattr(speech_voices, "audio_for", lambda *arguments, **options: (_ for _ in ()).throw(speech_voices.VoiceError("refused (401)")))
    monkeypatch.setattr(voice_module, "is_ready", lambda: False)
    monkeypatch.setattr(voice_module.shutil, "which", lambda name: "/usr/bin/" + name)
    monkeypatch.setattr(voice, "_run_player", lambda command, speech, piece: played.append(command))
    assert voice.begin("en", None, choice=speech_voices.OPENAI, settings=LinuxSettings(openai_api_key="k"))
    voice.say("hello there.")
    voice.end()
    import time
    deadline = time.time() + 3
    while not played and time.time() < deadline:
        time.sleep(0.05)
    assert played == [["spd-say", "--wait", "--language", "en", "--", "hello there."]]
