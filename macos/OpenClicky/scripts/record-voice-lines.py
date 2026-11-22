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
    # Account, sign-in and key notices: said the same way on every Mac and PC, and before there is any
    # voice to say them in (no account, no key). The text must match the app's sentence word for word
    # (RecordedVoice.notices on the Mac, RecordedVoice.Notices on Windows), capitals aside; the tests
    # there fail when a sentence changes, as the reminder to record it again.
    "notice-needs-key-answer": "I can answer that once you add your email for a free account, in Settings, Account. A key from OpenAI, Anthropic, OpenRouter, Gemini and others works too.",
    "notice-needs-key-openai": "I can do that once you add your email for a free account, in Settings, Account. Your own OpenAI key works too.",
    "notice-sign-in-expired": "Your OpenClicky sign-in has expired. Sign in again in Settings, Account.",
    "notice-own-key-refused": "Your own API key was refused. Check it in Settings, Keys & Plans.",
    "notice-ask-again": "Please ask that again.",
    "notice-unreachable-retry": "Couldn't reach OpenClicky right now, try again in a minute.",
    "notice-backend-trouble": "Something went wrong talking to the OpenClicky backend. Check the backend and your token in the settings file.",
    "notice-month-allowance-used": "You've used this month's free allowance, it comes back on the 1st. Dictation still works.",
    "notice-day-allowance-used": "You've used today's free allowance, it comes back tomorrow. Dictation still works.",
    "notice-free-budget-used": "OpenClicky's free allowance is used up for now, you can keep going with your own key.",
    "notice-needs-own-key": "That needs your own key for now.",
    "notice-accounts-full": "OpenClicky's free accounts are full right now, you can use your own key instead.",
    "notice-account-paused": "This account is paused. Dictation still works.",
    "notice-confirm-email": "Confirm your email to keep going: type the code from the email in Settings, Account.",
    "notice-device-limit-mac": "This Mac already has two OpenClicky accounts, sign in with one of them.",
    "notice-device-limit-pc": "This PC already has two OpenClicky accounts, sign in with one of them.",
    "notice-guest-trial-ended": "Your free trial has ended. Add your email in Settings, Keys and Account, to keep going for free.",
    "notice-service-trouble": "OpenClicky's free service is having trouble, try again in a minute.",
    "notice-switched-off": "This login isn't switched on for OpenClicky yet. Write to hello at flowsxr dot com, or use your own key.",
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
