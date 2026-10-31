# Records the Windows versions of the tour lines that name keys, as <line>-windows*.mp3.
#
# The Mac recordings say "hold the fn key" and "control and option"; a Windows PC has neither. The app
# plays the Windows recording for the person's dictation key (RecordedVoice.WindowsFileName) and, with
# none, says the sentence (RecordedVoice.WindowsSentence) in this PC's voice, which sounds like someone
# else. So every key has its recordings, in the same voice as every other recorded line (OpenAI speech,
# marin; see macos/OpenClicky/scripts/record-voice-lines.py). Keep the texts in step with
# RecordedVoice.WindowsSentence.
#
#     $env:OPENAI_API_KEY = "..."; windows\scripts\record-windows-voice-lines.ps1
#     $env:OPENAI_API_KEY = "..."; windows\scripts\record-windows-voice-lines.ps1 tour-act-windows-control

param([string[]] $Names)

$ErrorActionPreference = "Stop"

$talkRight = "right control and the Windows key"
$talkLeft = "left control and the Windows key"
$talkAny = "control and the Windows key"

$lines = [ordered]@{
    "tour-dictate-windows"             = "Hold right control or right alt, say a sentence, then let go. Your words land wherever your cursor is."
    "tour-dictate-windows-left-control" = "Hold left control, say a sentence, then let go. Your words land wherever your cursor is."
    "tour-dictate-windows-right-alt"   = "Hold right alt, say a sentence, then let go. Your words land wherever your cursor is."
    "tour-dictate-windows-right-shift" = "Hold right shift, say a sentence, then let go. Your words land wherever your cursor is."
    "tour-dictate-windows-caps-lock"   = "Hold caps lock, say a sentence, then let go. Your words land wherever your cursor is."
    "tour-talk-windows"                = "Your turn. Hold $talkRight, and ask me about anything on your screen."
    "tour-talk-windows-left-control"   = "Your turn. Hold $talkLeft, and ask me about anything on your screen."
    "tour-talk-windows-control"        = "Your turn. Hold $talkAny, and ask me about anything on your screen."
    "tour-act-windows"                 = "Now, give me a task: hold $talkRight, and tell me what to do."
    "tour-act-windows-left-control"    = "Now, give me a task: hold $talkLeft, and tell me what to do."
    "tour-act-windows-control"         = "Now, give me a task: hold $talkAny, and tell me what to do."
    "settings-tour-shortcuts-windows"                = "Hold $talkRight to talk to me. The rarer shortcuts are under More, at the bottom of each page."
    "settings-tour-shortcuts-windows-left-control"   = "Hold $talkLeft to talk to me. The rarer shortcuts are under More, at the bottom of each page."
    "settings-tour-shortcuts-windows-control"        = "Hold $talkAny to talk to me. The rarer shortcuts are under More, at the bottom of each page."
}

$instructions = "Warm, friendly and unhurried, like showing a parent around a new app. Clear and calm."
$outputFolder = Join-Path $PSScriptRoot "..\src\OpenClicky.Windows\Assets\RecordedVoice"

$apiKey = "$env:OPENAI_API_KEY".Trim()
if (-not $apiKey) { throw "OPENAI_API_KEY is not set" }

$chosen = if ($Names) { $Names } else { @($lines.Keys) }
foreach ($name in $chosen) {
    if (-not $lines.Contains($name)) { throw "unknown line $name; one of $($lines.Keys -join ', ')" }
    $body = @{
        model           = "gpt-4o-mini-tts"
        voice           = "marin"
        input           = $lines[$name]
        instructions    = $instructions
        response_format = "mp3"
    } | ConvertTo-Json
    $path = Join-Path $outputFolder "$name.mp3"
    Invoke-WebRequest -Uri "https://api.openai.com/v1/audio/speech" -Method Post -UseBasicParsing `
        -Headers @{ Authorization = "Bearer $apiKey" } -ContentType "application/json" `
        -Body ([Text.Encoding]::UTF8.GetBytes($body)) -OutFile $path -TimeoutSec 60
    "{0}.mp3  {1} KB" -f $name, [int]((Get-Item $path).Length / 1024)
}
