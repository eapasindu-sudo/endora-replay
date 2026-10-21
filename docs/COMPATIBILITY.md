# Dependency and compatibility report

Status key: **Tested** = exercised by automated tests or a real run in this repository. **Built** = compiles
and is wired up, not yet run against the hardware. **Unavailable** = not implemented in this version.

## Platform

| Item | Requirement | Status |
|---|---|---|
| OS | Windows 10 22H2 or Windows 11, x64 | Built (solution compiles for `net10.0-windows10.0.19041.0`) |
| Runtime | .NET 10, bundled in the installer (self-contained) | Built |
| CPU | 8 cameras at 1080p50 JPEG encode needs a recent 8-core CPU (estimate, not measured) | Not measured |
| Disk | SSD. About 20 MB/s per 1080p50 camera at quality 90 (estimate from 400 KB/frame) | Not measured |
| GPU | A second HDMI output, set to Extend at 1920×1080 @ the project rate | Built |

## Hardware paths

| Path | How | Status |
|---|---|---|
| USB capture cards (UVC) | OpenCV Media Foundation (MSMF), DirectShow fallback, MJPG requested | **Built, untested on real cards** |
| NDI inputs (cameras or Program) | NDI SDK runtime `Processing.NDI.Lib.x64.dll`, found through `NDI_RUNTIME_DIR_V6/V5/V4`. The runtime is **not** shipped inside Endora. Settings has an "Install NDI Runtime" button that downloads NDI's own installer from `ndi.link/NDIRedistV6` (V5 as fallback), checks it is signed by NDI, and runs it; the operator accepts NDI's licence there. Shipping the DLLs inside Endora is allowed by NDI only under EULA terms that cover them, so that is left for a later decision. | **Built, untested with real NDI sources** |
| Scoreboard link over Program | Microsoft Edge WebView2 (runtime included in Windows 10/11) on a third HDMI output into an ATEM input, ATEM DSK 2 luma key | **Built, untested on the real ATEM** |
| MIDI keyboard / controller | Windows multimedia MIDI (winmm), no extra driver. Buttons, jog wheel (auto-detected encoding) and T-bar. | **Built, untested with a real controller** |
| NDI output of the replay playout | Same runtime, sends BGRA at the project rate. The REPLAY badge is not included. | **Built, untested** |
| Stinger over live (DSK luma key) | ATEM DSK fill/cut = replay input, luma key clip/gain from settings. Stinger needs a black background. | **Built, tested in simulation only** |

NDI® is a registered trademark of Vizrt NDI AB. The ATEM Mini Extreme has no NDI output of its own; to bring a camera in as NDI use an HDMI splitter or an HDMI-to-NDI encoder with loop-out before the ATEM.
| Blackmagic DeckLink / UltraStudio capture | DeckLink SDK | **Unavailable**: reported as such in Settings, never substituted |
| Replay output to ATEM | Borderless full-screen window on a GPU HDMI output; resolution and refresh validated before use | **Built, untested on the real ATEM input** |
| DeckLink playout | DeckLink SDK | **Unavailable** |
| ATEM Mini Extreme control | LibAtem network protocol (UDP 9910) | **Built, untested on a real switcher** |
| ATEM official SDK | Blackmagic ATEM Switchers SDK via COM interop, compiled only when `lib/Interop.BMDSwitcherAPI.dll` exists | **Built, untested** |
| Simulated cameras / switcher / output | Test patterns and an in-memory switcher, labelled SIMULATED | Tested |
| Phone remote | Any phone browser on the same LAN (iOS Safari, Android Chrome) | Tested in a mobile browser emulator |

Frame-accurate timing through a GPU display output depends on Windows composition and the driver. A desktop
display output is not genlocked to the ATEM; the ATEM's HDMI inputs have frame synchronisers, so the picture
is accepted, but expect up to about one frame of output jitter. A DeckLink output would remove this and is the
recommended future upgrade.

## ATEM Mini Extreme notes

- Video inputs are HDMI only (8 × HDMI). Video cannot be sent to it over the network.
- Its media pool holds **stills only, no clips**, so the ATEM itself cannot run a video stinger transition.
  Endora's **Endora stinger video** mode plays the stinger on the replay output and sends CUT to the ATEM at the
  stinger's cover frame. When an ATEM stinger style is selected and the switcher reports no stinger
  transition, Settings warns.
- Sources: ATEM Mini Extreme spec sheet (Full Compass,
  https://www.fullcompass.com/common/files/95088-ATEMExtremeMiniSpecSheet.pdf) and the LibAtem project
  (https://github.com/LibAtem).

## Packages and licences

Versions are the ones pinned in the project files. Licences were read from each package's metadata.

| Package | Version | Licence | Used for | Redistribution |
|---|---|---|---|---|
| LibAtem | 1.0.0 | LGPL-3.0 (per its repository LICENSE) | ATEM network protocol | Shipped as a separate, replaceable DLL in the install folder, which satisfies LGPL relinking. Include the LGPL text and a link to the source with releases. |
| log4net | 3.5.0 | Apache-2.0 | LibAtem logging dependency (raised from 2.0.13 for a security advisory) | OK |
| OpenCvSharp4.Windows / runtime.win | 4.13.0.20260627 | Apache-2.0 | USB capture, JPEG, stinger decode | OK. The runtime also ships `opencv_videoio_ffmpeg4130_64.dll` (FFmpeg, LGPL build); keep it a separate DLL and include its notice. |
| Microsoft.Data.Sqlite | 10.0.0 | MIT (SQLite itself is public domain) | Clip database | OK |
| QRCoder | 1.8.1 | MIT | Phone remote QR code | OK |
| Velopack | 1.2.161 | MIT | Installer and auto-update | OK |
| System.Management | 10.0.0 | MIT | Capture device names (WMI) | OK |
| ASP.NET Core (shared framework) | 10.0 | MIT | Phone remote server | OK |
| xunit 2.9.3, Microsoft.NET.Test.Sdk 17.14.1 | | Apache-2.0 / MIT | Tests only | Not shipped |
| Blackmagic ATEM Switchers SDK | from ATEM Software Control | Blackmagic SDK licence | Optional ATEM backend | **Not verified.** The interop DLL is generated locally and is not committed or packaged by CI. Check the Blackmagic SDK licence before distributing any Blackmagic file. |

## Known limitations of this version

- No DeckLink capture or playout.
- Program feed capture is for monitoring and clipping only; Endora does not mix.
- Audio is not recorded or played.
- 1080p is the tested-in-code format; 2160p would need more disk and CPU than estimated above.
