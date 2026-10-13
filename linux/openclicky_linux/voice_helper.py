"""The voice's helper process: `python -m openclicky_linux.voice_helper <Kokoro model folder>`.

Loads Kokoro once with the Mac app's settings (`KokoroEngine.swift`: the model, its voices, tokens,
espeak-ng data, dictionary and the English and Mandarin lexicons), then answers one JSON line per
piece on stdin (`{"text": …, "sid": 3, "lang": "en-us"}`) with one on stdout (`{"wav": base64}` or
`{"error": …}`). Ends when stdin closes, which gives all of the model's memory back.
"""

from __future__ import annotations

import array
import base64
import io
import json
import os
import sys
import wave
from pathlib import Path


def make_voice(folder: Path, language_hint: str = "en-us"):
    import sherpa_onnx

    return sherpa_onnx.OfflineTts(sherpa_onnx.OfflineTtsConfig(model=sherpa_onnx.OfflineTtsModelConfig(
        kokoro=sherpa_onnx.OfflineTtsKokoroModelConfig(
            model=str(folder / "model.onnx"), voices=str(folder / "voices.bin"), tokens=str(folder / "tokens.txt"),
            data_dir=str(folder / "espeak-ng-data"), dict_dir=str(folder / "dict"),
            lexicon=f"{folder / 'lexicon-us-en.txt'},{folder / 'lexicon-zh.txt'}", lang=language_hint),
        num_threads=min(4, max(1, os.cpu_count() or 2)))))


def wav_bytes(samples, sample_rate: int) -> bytes:
    buffer = io.BytesIO()
    with wave.open(buffer, "wb") as wav_file:
        wav_file.setnchannels(1)
        wav_file.setsampwidth(2)
        wav_file.setframerate(sample_rate)
        wav_file.writeframes(array.array("h", (max(-32768, min(32767, int(sample * 32767))) for sample in samples)).tobytes())
    return buffer.getvalue()


def main() -> int:
    folder = Path(sys.argv[1])
    voices: dict[str, object] = {}
    for line in sys.stdin:
        try:
            request = json.loads(line)
            hint = request.get("lang", "en-us")
            # One loaded voice per language hint: switching languages mid-session reloads only once.
            if hint not in voices:
                voices[hint] = make_voice(folder, hint)
            audio = voices[hint].generate(request["text"], sid=int(request.get("sid", 3)), speed=1.0)
            answer = {"wav": base64.b64encode(wav_bytes(audio.samples, audio.sample_rate)).decode("ascii")}
        except Exception as failure:  # noqa: BLE001 - reported to the app, which logs it
            answer = {"error": f"{type(failure).__name__}: {failure}"}
        sys.stdout.write(json.dumps(answer) + "\n")
        sys.stdout.flush()
    return 0


if __name__ == "__main__":
    sys.exit(main())
