<#
.SYNOPSIS
    Builds the OpenClicky for Windows release: a self-contained publish per architecture, packed by
    Velopack into Setup.exe plus full (and, with an earlier release at hand, delta) packages.

.DESCRIPTION
    For each runtime (win-x64, win-arm64 by default):
      1. dotnet publish -c Release -r <rid> --self-contained into windows\publish\<rid>
      2. vpk download github (only with -Publish or -Deltas: fetches the last release so a delta can be made)
      3. vpk pack into windows\Releases\<rid>, channel "win" (x64) or "win-arm64"
    -Publish uploads each channel with `vpk upload github` to the OpenClicky repository's releases
    (needs a token: -GitHubToken or $env:GITHUB_TOKEN). Velopack's UpdateManager in the app reads the
    same releases (Companion\VelopackAppUpdater.cs), each architecture from its own channel.

.EXAMPLE
    .\build-release.ps1                                   # both architectures, version from the csproj
    .\build-release.ps1 -Runtimes win-arm64 -Version 0.1.1
    .\build-release.ps1 -Version 0.2.0 -Publish           # build, pack with deltas, upload a draft release
#>
[CmdletBinding()]
param(
    [string[]] $Runtimes = @('win-x64', 'win-arm64'),
    [string] $Version,
    [switch] $Deltas,
    [switch] $Publish,
    [switch] $PublishAsFinal,
    [string] $GitHubToken = $env:GITHUB_TOKEN,
    [string] $RepoUrl = 'https://github.com/weijietan97/openclicky',
    # Velopack installs into %LOCALAPPDATA%\<packId> and deletes that folder on uninstall. The app keeps
    # its own data (logs, preferences, history, downloaded models) in %LOCALAPPDATA%\OpenClicky, so the
    # package must not be called "OpenClicky" or an uninstall would wipe it.
    [string] $PackId = 'OpenClicky.Windows'
)

$ErrorActionPreference = 'Stop'
$windowsDirectory = Split-Path -Parent $PSScriptRoot
$project = Join-Path $windowsDirectory 'src\OpenClicky.Windows\OpenClicky.Windows.csproj'
$icon = Join-Path $windowsDirectory 'src\OpenClicky.Windows\Assets\OpenClicky.ico'
$publishRoot = Join-Path $windowsDirectory 'publish'
$releasesRoot = Join-Path $windowsDirectory 'Releases'

# dotnet and the vpk tool on PATH.
$dotnetDirectory = 'C:\Program Files\dotnet'
if (Test-Path $dotnetDirectory) { $env:PATH = "$dotnetDirectory;$env:PATH" }
$toolsDirectory = Join-Path $env:USERPROFILE '.dotnet\tools'
$env:PATH = "$env:PATH;$toolsDirectory"
if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    Write-Host 'vpk (the Velopack CLI) is not installed; installing it as a global dotnet tool.'
    # The CLI matches the Velopack package the app references.
    dotnet tool install -g vpk --version 1.2.161
    if ($LASTEXITCODE -ne 0) { throw 'could not install vpk' }
}

if (-not $Version) {
    [xml] $csproj = Get-Content $project
    $Version = ($csproj.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ }) | Select-Object -First 1
    if (-not $Version) { throw 'no -Version given and none in the csproj' }
}
if ($Publish -and -not $GitHubToken) { throw '-Publish needs -GitHubToken or $env:GITHUB_TOKEN' }

Write-Host "OpenClicky $Version for $($Runtimes -join ', ')"
foreach ($runtime in $Runtimes) {
    $channel = if ($runtime -eq 'win-x64') { 'win' } else { $runtime }
    $publishDirectory = Join-Path $publishRoot $runtime
    $releaseDirectory = Join-Path $releasesRoot $runtime

    Write-Host "== ${runtime}: dotnet publish"
    if (Test-Path $publishDirectory) { Remove-Item -Recurse -Force $publishDirectory }
    dotnet publish $project -c Release -r $runtime --self-contained true -p:Version=$Version -o $publishDirectory
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $runtime" }

    New-Item -ItemType Directory -Force $releaseDirectory | Out-Null
    if ($Publish -or $Deltas) {
        Write-Host "== ${runtime}: fetching the last release for a delta"
        $downloadArguments = @('download', 'github', '--repoUrl', $RepoUrl, '--channel', $channel, '-o', $releaseDirectory)
        if ($GitHubToken) { $downloadArguments += @('--token', $GitHubToken) }
        vpk @downloadArguments
        if ($LASTEXITCODE -ne 0) { Write-Warning "no earlier release for $channel; packing a full package only" }
    }

    Write-Host "== ${runtime}: vpk pack"
    vpk pack --packId $PackId --packVersion $Version --packDir $publishDirectory --mainExe OpenClicky.exe `
        --packTitle OpenClicky --packAuthors OpenClicky --icon $icon --runtime $runtime --channel $channel `
        --shortcuts 'Desktop,StartMenuRoot' -o $releaseDirectory
    if ($LASTEXITCODE -ne 0) { throw "vpk pack failed for $runtime" }

    if ($Publish) {
        Write-Host "== ${runtime}: vpk upload github"
        $uploadArguments = @('upload', 'github', '--repoUrl', $RepoUrl, '--token', $GitHubToken, '--channel', $channel,
                             '-o', $releaseDirectory, '--merge', '--releaseName', "OpenClicky $Version", '--tag', "v$Version")
        if ($PublishAsFinal) { $uploadArguments += '--publish' }
        vpk @uploadArguments
        if ($LASTEXITCODE -ne 0) { throw "vpk upload failed for $runtime" }
    }
    Get-ChildItem $releaseDirectory | Select-Object Name, Length | Format-Table -AutoSize | Out-String | Write-Host
}
Write-Host "Done: $releasesRoot"
