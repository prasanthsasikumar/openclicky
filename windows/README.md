# OpenClicky for Windows

The Windows port of the macOS app (`../macos/OpenClicky`): dictation into any app, the pointer buddy that
answers questions about your screen and points at things, Realtime voice, the agent lane through the
`openclicky` CLI. C# / .NET 8 / WPF. How the port is organised and what maps to what: [PORTING.md](PORTING.md).

## Build and run

Needs the .NET 8 SDK (`C:\Program Files\dotnet`) on Windows 10 2004 or later (x64 or ARM64).

```powershell
cd windows
dotnet build OpenClicky.Windows.sln
dotnet test tests/OpenClicky.Core.Tests
dotnet run --project src/OpenClicky.Windows          # or bin\Debug\...\OpenClicky.exe
```

OpenClicky has no main window: it lives in the tray and the island at the top of the screen. Launch it a
second time (or pick "Open OpenClicky" in the tray menu) to open its window.

| Shortcut | What |
|---|---|
| hold **Right Ctrl** (Settings → Shortcuts) | dictate into the app in front |
| hold **Ctrl + Win** | ask about your screen (spoken answer, pointing) |
| tap **Ctrl** twice | type a question in the island |
| tap the dictation key + **Ctrl** twice | hands-free (Realtime, always listening) |

## Configuration and files

- `%USERPROFILE%\.openclicky\shell.json` — keys, account token, backend URL, CLI command, workspace
  (the same file the Mac app and the CLI use; watched while the app runs).
- `%LOCALAPPDATA%\OpenClicky\` — `preferences.json`, `dictation.sqlite`, downloaded models (Kokoro, Parakeet),
  `Logs\app.log` (truncated past 2 MB at launch; read it first when something misbehaves).

## Debug flags

| Flag | What |
|---|---|
| `--openclicky-capturable-windows` (or `OPENCLICKY_CAPTURABLE_WINDOWS=1`) | let screenshots see the island, overlay and orb (normally excluded from every capture) |
| `--openclicky-ui-demo [--shots <folder>]` | walks the floating UI through its states, saving screenshots |
| `--openclicky-window-demo [--shots <folder>] [--page <page>] [--tour]` | the window over a seeded throwaway data folder |
| `--openclicky-platform-smoke` | exercises the OS layer on this PC and prints the results |
| `--openclicky-smoke-run "<text>"` | the agent gate + a Codex run through the `openclicky` CLI |
| `--openclicky-smoke-transcribe <wav> [offline\|parakeet\|sarvam\|openclicky\|assemblyai]` | one dictation engine on a recording |
| `--openclicky-voice-smoke <voice>` | renders two sentences in one voice (`openai`, `kokoro`, `mac`…) |
| `--openclicky-check-source <source>` | Settings' Test button for one answer source (`anthropic`, `openai`…) |
| `--openclicky-answer-smoke` | one real question (a red square) through the answer route |

The smoke flags write to the console they were started from and exit with 0 on success. The
`--openclicky-kokoro-helper` / `--openclicky-parakeet-helper` flags are the app's own helper processes.

## Release

```powershell
cd windows\installer
.\build-release.ps1                                    # win-x64 + win-arm64, version from the csproj
.\build-release.ps1 -Runtimes win-arm64 -Version 0.1.1 # one architecture
.\build-release.ps1 -Version 0.2.0 -Publish            # with deltas, uploaded as a draft GitHub release
```

The script publishes self-contained builds, packs them with Velopack (`vpk`, installed as a global dotnet tool
when missing) into `windows\Releases\<rid>\` (`OpenClicky.Windows-<rid>-Setup.exe`, the full package and a
portable zip; a delta too when an earlier release was fetched), and with `-Publish` uploads them to the
releases of https://github.com/weijietan97/openclicky (`-GitHubToken` or `$env:GITHUB_TOKEN`; add
`-PublishAsFinal` to publish instead of leaving a draft). x64 uses Velopack's `win` channel, ARM64 `win-arm64`.
The installed app checks those releases at launch and daily (Settings → General → Updates), downloads in
the background and applies the update when it quits; "Check now" applies it at once.
