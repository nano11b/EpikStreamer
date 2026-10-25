# Release: build -> (optional) Authenticode signing -> SHA-256 + update.json -> zips -> (optional) GitHub release.
#   powershell -ExecutionPolicy Bypass -File installer\release.ps1 [-Version 2.1.0] [-Sign] [-Publish] [-Draft] [-NotesFile CHANGELOG-excerpt.md]
#
# Signing (optional, -Sign). Nothing secret lives in this repo; the certificate comes from environment variables:
#   SIGN_CERT_THUMBPRINT   thumbprint of a code-signing cert in the CurrentUser\My store (preferred; e.g. a hardware token), or
#   SIGN_CERT_PATH         path to a .pfx file  +  SIGN_CERT_PASSWORD  its password
#   SIGN_TIMESTAMP_URL     RFC 3161 timestamp server (default http://timestamp.digicert.com)
#   SIGNTOOL               path to signtool.exe (default: newest one in the Windows 10/11 SDK)
# The app exe is signed before it is packaged, then the installer is signed; both signatures are verified.
#
# Publishing (optional, -Publish) uses the GitHub CLI against nano11b/EpikStreamer:
#   gh auth login   (once)    then:  release.ps1 -Publish
# It creates tag v<Version> and uploads the installer, update.json, the .sha256 file and the zips. The app's
# updater reads https://api.github.com/repos/nano11b/EpikStreamer/releases/latest -> update.json (or the .sha256).
param(
  [string]$Version = "",
  [switch]$Sign,
  [switch]$Publish,
  [switch]$Draft,
  [string]$Repo = "nano11b/EpikStreamer",
  [string]$NotesFile = "",
  [string]$FfmpegDir = ""
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root 'EpikStreamer\EpikStreamer.csproj'
if (-not $Version) { $Version = ([xml](Get-Content $proj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1 }
$dist = Join-Path $root 'dist'

. (Join-Path $PSScriptRoot 'sign.ps1')

& (Join-Path $PSScriptRoot 'build-installer.ps1') -Version $Version -FfmpegDir $FfmpegDir -Sign:$Sign

$setupName = "EpikStreamer-$Version-setup.exe"
$setup = Join-Path $dist $setupName
if ($Sign) { Invoke-Sign $setup }

# checksums + manifest
$sha = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLower()
"$sha *$setupName" | Set-Content -Encoding ascii -NoNewline (Join-Path $dist "$setupName.sha256")
$notes = if ($NotesFile -and (Test-Path $NotesFile)) { Get-Content $NotesFile -Raw } else { "EpikStreamer $Version" }
[ordered]@{
  version = $Version
  url     = "https://github.com/$Repo/releases/download/v$Version/$setupName"
  sha256  = $sha
  notes   = $notes.Trim()
} | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $dist 'update.json')

# zips
$zips = @()
foreach ($kind in 'app', 'full') {
  $z = Join-Path $dist "EpikStreamer-v$Version-$kind-win-x64.zip"
  if (Test-Path $z) { Remove-Item $z }
  Compress-Archive -Path (Join-Path $dist "$kind\*") -DestinationPath $z -CompressionLevel Optimal
  $zips += $z
}
Write-Host "`nInstaller : $setup"
Write-Host "SHA-256   : $sha"
Write-Host "Manifest  : $(Join-Path $dist 'update.json')"
$zips | ForEach-Object { Write-Host "Zip       : $_" }

if ($Publish) {
  if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw "GitHub CLI (gh) not found: winget install GitHub.cli, then gh auth login" }
  $notesArg = if ($NotesFile -and (Test-Path $NotesFile)) { @('--notes-file', $NotesFile) } else { @('--notes', "EpikStreamer $Version") }
  $flags = @(); if ($Draft) { $flags += '--draft' }
  $assets = @($setup, (Join-Path $dist "$setupName.sha256"), (Join-Path $dist 'update.json')) + $zips
  gh release create "v$Version" @assets --repo $Repo --title "EpikStreamer $Version" @notesArg @flags
  if ($LASTEXITCODE -ne 0) { throw "gh release create failed" }
  Write-Host "Published https://github.com/$Repo/releases/tag/v$Version"
}
