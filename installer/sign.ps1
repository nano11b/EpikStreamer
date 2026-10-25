# Optional Authenticode signing helpers (dot-sourced by build-installer.ps1 / release.ps1).
# Certificate comes ONLY from env vars: SIGN_CERT_THUMBPRINT, or SIGN_CERT_PATH + SIGN_CERT_PASSWORD; SIGN_TIMESTAMP_URL; SIGNTOOL.
function Find-SignTool {
  if ($env:SIGNTOOL) { if (Test-Path $env:SIGNTOOL) { return $env:SIGNTOOL } else { throw "SIGNTOOL=$env:SIGNTOOL not found" } }
  $kits = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
  $st = Get-ChildItem $kits -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue | Where-Object { $_.FullName -match '\\x64\\' } |
    Sort-Object FullName -Descending | Select-Object -First 1
  if (-not $st) { throw "signtool.exe not found (install the Windows SDK or set SIGNTOOL)" }
  return $st.FullName
}

function Invoke-Sign([string]$File) {
  if (-not (Test-Path $File)) { throw "nothing to sign at $File" }
  $signtool = Find-SignTool
  $ts = if ($env:SIGN_TIMESTAMP_URL) { $env:SIGN_TIMESTAMP_URL } else { 'http://timestamp.digicert.com' }
  $stArgs = @('sign', '/fd', 'SHA256', '/tr', $ts, '/td', 'SHA256', '/d', 'EpikStreamer')
  if ($env:SIGN_CERT_THUMBPRINT) { $stArgs += @('/sha1', $env:SIGN_CERT_THUMBPRINT, '/s', 'My') }
  elseif ($env:SIGN_CERT_PATH) {
    if (-not (Test-Path $env:SIGN_CERT_PATH)) { throw "no certificate at SIGN_CERT_PATH" }
    if (-not $env:SIGN_CERT_PASSWORD) { throw "SIGN_CERT_PASSWORD is not set" }
    $stArgs += @('/f', $env:SIGN_CERT_PATH, '/p', $env:SIGN_CERT_PASSWORD)
  }
  else { throw "-Sign needs SIGN_CERT_THUMBPRINT, or SIGN_CERT_PATH + SIGN_CERT_PASSWORD" }
  & $signtool @args $File | Out-Host            # the password is never echoed by signtool
  if ($LASTEXITCODE -ne 0) { throw "signing failed: $File" }
  & $signtool verify /pa /q $File | Out-Host
  if ($LASTEXITCODE -ne 0) { throw "the new signature did not verify: $File" }
  Write-Host "Signed + verified: $File"
}

