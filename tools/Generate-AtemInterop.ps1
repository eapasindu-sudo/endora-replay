# Generates lib\Interop.BMDSwitcherAPI.dll from the Blackmagic ATEM Switchers SDK type library.
# Run on a Windows PC with ATEM Software Control (or the ATEM SDK) installed, from a Developer PowerShell
# so tlbimp.exe is on PATH. Then rebuild: the Blackmagic SDK backend is compiled in automatically.
$ErrorActionPreference = "Stop"
$candidates = @(
  "${env:ProgramFiles(x86)}\Blackmagic Design\Blackmagic ATEM Switchers\ATEM Software Control\BMDSwitcherAPI64.dll",
  "${env:ProgramFiles}\Blackmagic Design\Blackmagic ATEM Switchers\ATEM Software Control\BMDSwitcherAPI64.dll",
  "${env:ProgramFiles(x86)}\Blackmagic Design\Blackmagic ATEM Switchers\BMDSwitcherAPI64.dll"
)
$tlb = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $tlb) { throw "BMDSwitcherAPI64.dll not found. Install ATEM Software Control first." }
$out = Join-Path $PSScriptRoot "..\lib\Interop.BMDSwitcherAPI.dll"
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null
tlbimp.exe $tlb /out:$out /namespace:BMDSwitcherAPI /machine:X64
Write-Host "Wrote $out"
