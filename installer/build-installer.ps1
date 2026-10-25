# Builds the self-contained app (with ffmpeg) and the Inno Setup installer.
#   powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1 [-Version 2.1.0] [-FfmpegDir C:\ffmpeg\bin] [-Sign]
# Needs: .NET 8 SDK, Inno Setup 6 (winget install JRSoftware.InnoSetup). ffmpeg.exe/ffprobe.exe are taken from
# -FfmpegDir, or downloaded from github.com/BtbN/FFmpeg-Builds (GPL build) if not given.
param(
  [string]$Version = "",
  [string]$FfmpegDir = "",
  [string]$Iscc = "",
  [switch]$Sign
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root 'EpikStreamer\EpikStreamer.csproj'
if (-not $Version) { $Version = ([xml](Get-Content $proj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1 }
if (-not $Version) { throw "could not read <Version> from $proj" }
$dist = Join-Path $root 'dist'
$full = Join-Path $dist 'full'
$app = Join-Path $dist 'app'
foreach ($d in $full, $app) { if (Test-Path $d) { Remove-Item $d -Recurse -Force } }

Write-Host "== Publishing EpikStreamer $Version"
dotnet publish $proj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o $full
if ($LASTEXITCODE -ne 0) { throw "dotnet publish (self-contained) failed" }
dotnet publish $proj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o $app
if ($LASTEXITCODE -ne 0) { throw "dotnet publish (framework-dependent) failed" }

# ffmpeg
$ff = Join-Path $full 'ffmpeg'
New-Item -ItemType Directory -Force $ff | Out-Null
if (-not $FfmpegDir) {
  $zip = Join-Path $env:TEMP 'ffmpeg-win64-gpl.zip'
  if (-not (Test-Path $zip)) {
    Write-Host "== Downloading ffmpeg (BtbN win64 GPL)"
    Invoke-WebRequest 'https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip' -OutFile $zip
  }
  $x = Join-Path $env:TEMP 'ffmpeg-win64-gpl'
  if (Test-Path $x) { Remove-Item $x -Recurse -Force }
  Expand-Archive $zip $x
  $FfmpegDir = (Get-ChildItem $x -Recurse -Filter ffmpeg.exe | Select-Object -First 1).DirectoryName
  Copy-Item (Join-Path (Split-Path $FfmpegDir -Parent) 'LICENSE.txt') (Join-Path $ff 'LICENSE.txt') -ErrorAction SilentlyContinue
}
foreach ($e in 'ffmpeg.exe', 'ffprobe.exe') {
  $src = Join-Path $FfmpegDir $e
  if (-not (Test-Path $src)) { throw "missing $src" }
  Copy-Item $src $ff -Force
}
Get-ChildItem -Path "$full\*" -Include *.pdb,*.xml -File | Remove-Item
Get-ChildItem -Path "$app\*" -Include *.pdb,*.xml -File | Remove-Item

if ($Sign) {
  . (Join-Path $PSScriptRoot 'sign.ps1')
  Invoke-Sign (Join-Path $full 'EpikStreamer.exe')
  Invoke-Sign (Join-Path $app 'EpikStreamer.exe')
}

# Inno Setup
if (-not $Iscc) {
  $Iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $Iscc) { throw "ISCC.exe not found. Install Inno Setup 6 (winget install JRSoftware.InnoSetup) or pass -Iscc" }
Write-Host "== Building installer with $Iscc"
& $Iscc "/DAppVersion=$Version" "/DSourceDir=$full" "/DOutputDir=$dist" (Join-Path $PSScriptRoot 'EpikStreamer.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }
$setup = Join-Path $dist "EpikStreamer-$Version-setup.exe"
if (-not (Test-Path $setup)) { throw "installer not produced: $setup" }
Write-Host "Installer: $setup ($([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB)"
Write-Host ("sha256: " + (Get-FileHash $setup -Algorithm SHA256).Hash.ToLower())
