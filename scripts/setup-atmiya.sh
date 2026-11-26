#!/usr/bin/env bash
# ATMIYA AI local development setup (free, no paid services).
# Nothing here purchases anything or enables a paid service. Sensitive system changes
# (macOS privacy permissions, launching, installing Xcode/ffmpeg, pulling Ollama models)
# are printed for you to approve and perform; this script does not silently do them.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

echo "== ATMIYA AI setup =="
echo "Node $(node --version) (need >=22), Swift $(swift --version 2>/dev/null | head -1)"

echo "== npm install =="
npm install

echo "== build backend + agent =="
npm run build

echo "== local backend secrets (no paid keys) =="
if [ ! -f backend/.dev.vars ]; then
  cp backend/.dev.vars.example backend/.dev.vars
  # local-only session secret; generated, never committed (.dev.vars is git-ignored)
  SECRET="$(openssl rand -hex 32 2>/dev/null || head -c32 /dev/urandom | od -An -tx1 | tr -d ' \n')"
  {
    echo ""
    echo "SESSION_TOKEN_SECRET=$SECRET"
    echo "SUPABASE_URL="
    echo "SUPABASE_JWT_SECRET="
    echo "# Free Google Gemini key (no paid tier needed): https://aistudio.google.com/apikey"
    echo "GEMINI_API_KEY="
    echo "# Optional local Ollama: OPENAI_BASE_URL=http://localhost:11434/v1 OPENAI_MODEL=llama3.2 OPENAI_API_KEY="
  } >> backend/.dev.vars
  chmod 600 backend/.dev.vars
  echo "wrote backend/.dev.vars (mode 600). Add a free GEMINI_API_KEY if you want hosted-backend model calls."
fi

echo "== run tests =="
npm test

cat <<'NEXT'

Next steps (you approve anything system-level):
  * Start the local backend:   npm run dev -w backend        # http://localhost:8787
  * Build the native app:      cd macos/OpenClicky && open OpenClicky.xcodeproj
     - ATMIYA AI needs Xcode (not just Command Line Tools) to build the SwiftUI app.
     - No Apple Developer paid certificate is required: sign ad-hoc / "Sign to Run Locally".
     - Grant Microphone, Speech Recognition, Screen Recording, Accessibility when asked.
  * Model provider (pick one, all free):
     - Free Google Gemini API key -> Settings -> Account, or shell keychain via app,
       or `GEMINI_API_KEY` for the backend / OpenCode.
     - Optional local Ollama: install Ollama, `ollama pull llama3.2-vision` (a download you approve),
       enable "Ollama (local)" in Settings. No key, no paid service.
  * Agent lane uses OpenCode (installed `opencode`); it runs free `opencode/*` models or your
     Gemini/Ollama provider. No OpenAI/Anthropic key required.
  * Voice in the app uses Apple on-device recognition; spoken replies fall back to this Mac's voice.

Data is stored locally (~/.openclicky, app data). API keys go to the macOS Keychain.
NEXT
