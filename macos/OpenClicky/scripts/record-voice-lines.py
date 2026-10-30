#!/usr/bin/env python3
"""Records the Settings tour's narration into OpenClicky/RecordedVoice as mp3.

The same voice as the other recorded lines (OpenAI speech, marin), so the app sounds the same
with no key and no network. Needs OPENAI_API_KEY in the environment. Run again after changing a
line; pass line names to record only those:

    OPENAI_API_KEY=... scripts/record-voice-lines.py
    OPENAI_API_KEY=... scripts/record-voice-lines.py settings-tour-voice
"""

import json
import os
import pathlib
import sys
import urllib.request

LINES = {
    "settings-tour-intro": "Here's a one-minute tour of settings. Everything lives in these six pages, and the search box finds any setting.",
    "settings-tour-dictation": "On Dictation, this is the key you hold, anywhere, to type with your voice.",
    "settings-tour-language": "Here you choose the language you speak, and a little further down, whether AI tidies up your words.",
    "settings-tour-voice": "On Clicky, you choose how I sound. There's a free voice, so you don't need a key.",
    "settings-tour-pointer": "Here you choose when I follow your mouse. If I'm hiding, shake the mouse and I'll come back.",
    "settings-tour-words": "Your words keeps everything you've dictated, so you can always find it again.",
    "settings-tour-shortcuts": "Hold control and option to talk to me. The rarer shortcuts are under More, at the bottom of each page.",
    "settings-tour-keys": "If you have your own AI key, or a Claude or ChatGPT plan, add it here. You don't need one to start.",
    "settings-tour-done": "That's it! You can watch this tour again from here, any time.",
}

INSTRUCTIONS = "Warm, friendly and unhurried, like showing a parent around a new app. Clear and calm."

OUTPUT_FOLDER = pathlib.Path(__file__).resolve().parent.parent / "OpenClicky" / "RecordedVoice"


def record(name: str, text: str, api_key: str) -> None:
    body = json.dumps({
        "model": "gpt-4o-mini-tts",
        "voice": "marin",
        "input": text,
        "instructions": INSTRUCTIONS,
        "response_format": "mp3",
    }).encode()
    request = urllib.request.Request(
        "https://api.openai.com/v1/audio/speech",
        data=body,
        headers={"Authorization": f"Bearer {api_key}", "Content-Type": "application/json"},
    )
    with urllib.request.urlopen(request, timeout=60) as response:
        audio = response.read()
    (OUTPUT_FOLDER / f"{name}.mp3").write_bytes(audio)
    print(f"{name}.mp3  {len(audio) // 1024} KB")


def main() -> None:
    api_key = os.environ.get("OPENAI_API_KEY", "").strip()
    if not api_key:
        sys.exit("OPENAI_API_KEY is not set")
    chosen = sys.argv[1:] or list(LINES)
    for name in chosen:
        if name not in LINES:
            sys.exit(f"no line called {name}")
        record(name, LINES[name], api_key)


if __name__ == "__main__":
    main()
