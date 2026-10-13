"""The offline engine's helper process: `python -m openclicky_linux.offline_helper <model folder>`.

Loads Parakeet once with the Mac app's settings (`ParakeetEngine.swift`: 80 mel features at 16 kHz,
a NeMo transducer, at most four threads), then answers one JSON line per take on stdin
(`{"pcm": base64 mono 16-bit, "rate": 16000}`; sherpa-onnx resamples any other rate) with one on stdout (`{"text": …}` or `{"error": …}`).
Ends when stdin closes. Separate from the app so ending it gives all of the model's memory back.
"""

from __future__ import annotations

import array
import base64
import json
import os
import sys
from pathlib import Path


def make_recognizer(model_folder: Path):
    import sherpa_onnx

    return sherpa_onnx.OfflineRecognizer.from_transducer(
        encoder=str(model_folder / "encoder.int8.onnx"),
        decoder=str(model_folder / "decoder.int8.onnx"),
        joiner=str(model_folder / "joiner.int8.onnx"),
        tokens=str(model_folder / "tokens.txt"),
        model_type="nemo_transducer",
        num_threads=min(4, max(1, (os.cpu_count() or 2) - 1)),
        sample_rate=16000,
        feature_dim=80,
        decoding_method="greedy_search",
    )


def transcribe(recognizer, pcm: bytes, sample_rate: int = 16000) -> str:
    samples = array.array("h", pcm[: len(pcm) - len(pcm) % 2])
    stream = recognizer.create_stream()
    stream.accept_waveform(sample_rate, [sample / 32768.0 for sample in samples])
    recognizer.decode_stream(stream)
    return stream.result.text.strip()


def main() -> int:
    recognizer = make_recognizer(Path(sys.argv[1]))
    for line in sys.stdin:
        try:
            request = json.loads(line)
            answer = {"text": transcribe(recognizer, base64.b64decode(request["pcm"]), int(request.get("rate", 16000)))}
        except Exception as failure:  # noqa: BLE001 - reported to the app, which shows it
            answer = {"error": f"{type(failure).__name__}: {failure}"}
        sys.stdout.write(json.dumps(answer) + "\n")
        sys.stdout.flush()
    return 0


if __name__ == "__main__":
    sys.exit(main())
