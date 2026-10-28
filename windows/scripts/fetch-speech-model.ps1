<#
.SYNOPSIS
    Puts the built-in dictation model (Moonshine tiny, English, int8) into the app's Assets\Models
    folder, so a release build ships it and dictates offline from the first take.

.DESCRIPTION
    The model is 123 MB unpacked, too big for the repository, so release builds fetch it here: the
    sherpa-onnx asr-models tarball, checked against its pinned SHA-256, unpacked with Windows' own
    tar.exe, test wavs left out. Already there: nothing is downloaded. A plain `dotnet build` without
    it still works; that build just has no Moonshine (MoonshineFacts.IsInstalled is false) and keeps
    Windows speech as the default engine.

.EXAMPLE
    .\fetch-speech-model.ps1
#>
[CmdletBinding()]
param(
    [string] $Destination = (Join-Path (Split-Path -Parent $PSScriptRoot) 'src\OpenClicky.Windows\Assets\Models')
)

$ErrorActionPreference = 'Stop'
$name = 'sherpa-onnx-moonshine-tiny-en-int8'
# Our own mirror (the repository's "models" prerelease) of sherpa-onnx's asr-models release, so a
# build never depends on a third-party link; the upstream original is the same file, same hash:
# https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/$name.tar.bz2
$url = "https://github.com/prasanthsasikumar/openclicky/releases/download/models/$name.tar.bz2"
$sha256 = 'd5fe6ec4334fef36255b2a4010412cad4c007e33103fec62fb5d17cad88086f2'
$files = @('preprocess.onnx', 'encode.int8.onnx', 'uncached_decode.int8.onnx', 'cached_decode.int8.onnx', 'tokens.txt', 'LICENSE')

$modelFolder = Join-Path $Destination $name
if (@($files | Where-Object { -not (Test-Path (Join-Path $modelFolder $_)) }).Count -eq 0) {
    Write-Host "speech model: $name is already in place"
    return
}

$work = Join-Path ([System.IO.Path]::GetTempPath()) "openclicky-$name-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force $work | Out-Null
try {
    $archive = Join-Path $work "$name.tar.bz2"
    Write-Host "speech model: downloading $url"
    $previousProgress = $ProgressPreference
    $ProgressPreference = 'SilentlyContinue'
    Invoke-WebRequest -Uri $url -OutFile $archive -UseBasicParsing
    $ProgressPreference = $previousProgress
    $actual = (Get-FileHash -Algorithm SHA256 $archive).Hash.ToLowerInvariant()
    if ($actual -ne $sha256) { throw "speech model: SHA-256 mismatch for $name (expected $sha256, got $actual)" }

    & "$env:SystemRoot\System32\tar.exe" -xjf $archive -C $work
    if ($LASTEXITCODE -ne 0) { throw "speech model: could not unpack $name" }

    New-Item -ItemType Directory -Force $modelFolder | Out-Null
    foreach ($file in $files) { Copy-Item (Join-Path $work "$name\$file") (Join-Path $modelFolder $file) -Force }
    Write-Host "speech model: $name is in $modelFolder"
}
finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
