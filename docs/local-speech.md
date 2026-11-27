# Local speech: your own speech server

OpenClicky can hear your questions and speak its answers with models you run yourself, on this PC or another
one on your network. Nothing goes to a cloud service, nothing is counted, and it works with no account or key.

Windows only for now. The Mac app already hears and speaks offline with Parakeet and Kokoro on the Mac itself.

## What you get without installing anything

The Windows app already has three models that run on this PC's own processor:

| Job | Model | Languages |
| --- | --- | --- |
| Hearing (dictation, push-to-talk) | Parakeet v3 (a one-time download) | English and 24 European languages |
| Hearing | Moonshine tiny (built in) | English |
| Voice | Kokoro (a one-time download) | English, Spanish, French, Hindi, Italian, Japanese, Portuguese, Chinese |

A speech server adds bigger models running on a graphics card, more languages (Whisper hears about 99, Urdu
among them), and one place for a whole classroom's PCs to share.

## Turning it on

1. Start a speech server (below) and note its address, such as `http://localhost:11434` (Ollama),
   `http://localhost:8000` (Speaches), or another PC's, such as `http://192.168.0.8:8000`.
2. In OpenClicky: **Settings → Clicky → Your models → Speech server**. Type the address and press **Turn on**.
   OpenClicky lists the models the server has.
3. Pick a model under **Hears your questions** and one under **Speaks answers**. Each is optional: you can
   hear with the server and speak with Kokoro, or the other way round. A voice model also takes a voice name
   (`af_heart` for Kokoro; empty uses the server's default).
4. Press **Try**. It speaks a line through the server and hears it back, and shows how long each took.

The speech server hears and speaks when **Fast voice** is off (Settings → Clicky → Hands-free). With Fast
voice on, OpenAI Realtime or Gemini Live hears and speaks instead. Dictation can use the server too: pick
**Your speech server** under **Dictation → Who hears you**.

## Which server, which models

| Your hardware | Hearing | Voice |
| --- | --- | --- |
| Any PC, no graphics card | Parakeet or Moonshine (already in the app) | Kokoro (already in the app) |
| A PC with an NVIDIA card (6 GB or more), Speaches | Whisper large-v3-turbo | Kokoro |
| Ollama already installed | Gemma 4 E2B | Phonon (Mac now; Windows and Linux when Ollama ships it) |

- **Whisper large-v3-turbo** (MIT licence): hears about 99 languages, Urdu among them, in a fraction of a
  second on a graphics card (0.3 s for a 5-second question on an RTX 3090). The best all-round choice.
- **Gemma 4 E2B** through Ollama: hears well, but takes a few seconds a question, since it is a language model
  that also listens. Good if Ollama is all you want to run.
- **Kokoro** (Apache 2.0): a clear, quick voice in 8 languages (0.1 s for a sentence on an RTX 3090).
- **Piper** (MIT; each voice has its own licence): small voices in many languages, quick even without a graphics
  card, less natural than Kokoro.
- **Chatterbox Multilingual** (MIT, 23 languages including Hindi) and **Qwen3-TTS** (Apache 2.0, 10 languages)
  sound more natural and can copy a voice from a short recording; run them through LocalAI or their own servers.
- **Urdu voice**: no strong open model yet. Speaches lists a community Piper voice,
  `Keshavji/piper-ur_PK-fasih-medium` (voice `fasih`). It is understandable but mispronounces some words, and
  it states no licence, so try it at home but don't hand it out. Indic Parler-TTS lists Urdu, but Speaches
  doesn't run it, and its licence needs checking. Until then Urdu answers are best spoken by a Gemini key.

## Option 1: Ollama

Ollama 0.40 and later answer OpenAI's audio requests on the same address as their chat models.

```bash
ollama pull gemma4:e2b
```

In OpenClicky use `http://localhost:11434` and pick `gemma4:e2b` to hear. On a Mac, `ollama pull phonon` adds a
voice (English; voices Zoey, Easton, Freya).

To reach Ollama on another PC, start it there with `OLLAMA_HOST=0.0.0.0` and allow port 11434 through its firewall.

## Option 2: Speaches (recommended with a graphics card)

[Speaches](https://speaches.ai) is a free server for Whisper (hearing) and Kokoro or Piper (voice). It runs in Docker.

With an NVIDIA card:

```bash
docker run --detach --restart unless-stopped --publish 8000:8000 --name speaches --volume hf-hub-cache:/home/ubuntu/.cache/huggingface/hub --gpus=all ghcr.io/speaches-ai/speaches:latest-cuda
```

`--restart unless-stopped` starts it again after the PC restarts. Without a graphics card, use the image
`ghcr.io/speaches-ai/speaches:latest-cpu` and leave out `--gpus=all`. The GPU image is about 6 GB.

Then download the models:

```bash
curl -X POST http://localhost:8000/v1/models/deepdml/faster-whisper-large-v3-turbo-ct2
curl -X POST http://localhost:8000/v1/models/speaches-ai/Kokoro-82M-v1.0-ONNX
```

`curl "http://localhost:8000/v1/registry?task=text-to-speech"` lists the voices it can download
(`task=automatic-speech-recognition` the hearing models); the `speaches-cli` tool (`uvx speaches-cli`) does the same.
Pick any large-v3-turbo model the registry lists. In OpenClicky use `http://localhost:8000`, pick the Whisper
model to hear and `speaches-ai/Kokoro-82M-v1.0-ONNX` to speak, with the voice `af_heart`.

Speaches unloads a model after 5 idle minutes, so the first question after a pause takes a second or two longer.
It sends a Piper voice's answer as one WAV per sentence; OpenClicky joins them, so every sentence is spoken.

## Option 3: anything else that speaks OpenAI's audio API

OpenClicky sends two requests, so any server that answers them works:

- `POST /v1/audio/transcriptions`: a WAV as `file`, with `model`, and `language` and `prompt` when known.
  The reply is `{ "text": "…" }`.
- `POST /v1/audio/speech`: JSON with `model`, `input`, `voice` (when one is set) and `response_format: "wav"`.
  The reply is the WAV.

`GET /v1/models` lists what to pick from. [LocalAI](https://localai.io) (Whisper, Piper, Chatterbox, Qwen3-TTS)
and [Kokoro-FastAPI](https://github.com/remsky/Kokoro-FastAPI) both work. A server that needs a key takes it in
the key box; it is sent as `Authorization: Bearer …`.

## When something is wrong

- **"Nothing answered at …"**: the server isn't running, the address is wrong, or a firewall blocks it. From
  another PC, Ollama needs `OLLAMA_HOST=0.0.0.0`.
- **The first question is slow**: the server loads its model on first use. OpenClicky waits up to 45 seconds
  for the words, then the next questions are quick.
- **It still answers in the old voice**: check that **Fast voice** is off, and that **Talking → Voice** says
  "Your speech server".
- **"The speech server said 400 …"**: the model doesn't do that job (a chat model picked to hear, say), or the
  voice name isn't one the model has. The message after the number is the server's own.
- **Slow on a graphics card**: check that `nvidia-smi` lists the card on the server. If it says it can't talk to
  the driver, the models run on the processor; on Ubuntu, `sudo apt install nvidia-open` and a restart put the
  driver back (an `apt autoremove` can take it away when nothing else installed by hand needs it).
- **Slow from another place**: through a VPN each request also waits for the network. On the same network the
  server answers in well under a second; through a slow VPN it can take two or three.

Everything you say goes only to the address you typed. The app's log (`%LOCALAPPDATA%\OpenClicky\Logs\app.log`)
notes each failed request, but never what was said.
