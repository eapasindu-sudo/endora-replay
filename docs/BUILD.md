# Building Endora Replay

## Requirements

- Windows 10 22H2 or Windows 11, x64
- .NET 10 SDK (10.0.100 or later; `global.json` rolls forward to the latest feature band)
- Optional: ATEM Software Control, for the official Blackmagic SDK backend

The solution also compiles on Linux and macOS (`EnableWindowsTargeting`), which is useful for CI checks, but
the app only runs on Windows.

## Build and test

```powershell
dotnet restore EndoraReplay.sln
dotnet build EndoraReplay.sln -c Release
dotnet test tests/EndoraReplay.Core.Tests -c Release
```

Run from source (no auto-update when not installed):

```powershell
dotnet run --project src/EndoraReplay.App
```

## Projects

| Project | Target | Contents |
|---|---|---|
| `EndoraReplay.Core` | net10.0 | Buffers, clip files, SQLite storage, playback, ATEM automation state machine, settings, diagnostics, simulators |
| `EndoraReplay.Hardware` | net10.0-windows | USB capture (OpenCV MSMF/DirectShow), ATEM via LibAtem or the Blackmagic SDK, JPEG decode, stinger loading |
| `EndoraReplay.Remote` | net10.0 | Phone remote web server (Kestrel + WebSocket) and the embedded phone page |
| `EndoraReplay.App` | net10.0-windows (WPF) | Operator UI, replay output window, settings, updates |
| `EndoraReplay.Core.Tests` | net10.0 | xUnit tests |

## Optional: Blackmagic ATEM SDK backend

1. Install ATEM Software Control on the build PC.
2. From a Developer PowerShell: `tools\Generate-AtemInterop.ps1`
3. Rebuild. `lib\Interop.BMDSwitcherAPI.dll` is detected and the `BMD_SDK` backend is compiled in.

Without it the "Blackmagic ATEM SDK" choice in Settings says it is unavailable, and the network protocol
backend (LibAtem) is used.

## Installer and auto-update (Velopack)

```powershell
dotnet publish src/EndoraReplay.App -c Release -r win-x64 --self-contained -p:Version=1.2.0 -o publish
dotnet tool install -g vpk --version 1.2.161
vpk pack --packId EndoraReplay --packVersion 1.2.0 --packDir publish --mainExe EndoraReplay.exe `
  --packTitle "Endora Replay" --icon src/EndoraReplay.App/Assets/endora.ico --outputDir Releases
```

`Releases\EndoraReplay-win-Setup.exe` is the installer. It installs per user without admin rights and adds
Start menu and desktop shortcuts.

## Releasing an update

GitHub Actions (`.github/workflows/build.yml`) builds every push and pull request on `windows-latest`, runs
the tests, and uploads the installer as a build artifact.

To ship an update to installed PCs, use either way:

- GitHub → **Releases** → **Draft a new release**, create a tag such as `v1.2.0`, and publish. The workflow
  builds that version and adds the installer and update files to the release.
- GitHub → **Actions** → **Build Windows installer** → **Run workflow** with a version such as `1.2.0`.

If the repository is private, installed PCs cannot read its releases without a token. Keep releases in a
public repository, or publish them to a public releases-only repository and point Release source at it.
