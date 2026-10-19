# Endora Replay

Multi-camera instant replay for cricket and live sport, built for a Windows PC working with a
Blackmagic **ATEM Mini Extreme**.

- Up to **8 USB capture cameras** recorded into rolling disk buffers on one shared clock, plus the
  ATEM **Program** (final out) feed in its own monitor.
- **Live** and **Jog** cue modes, jog wheel, frame step and shuttle, **IN / OUT** marks, clip library with
  cricket tags, queue, playback at **100 / 50 / 25 %**.
- Replay output is a full-screen picture on the PC's second HDMI port, wired to an ATEM input
  (default **HDMI 8**, selectable in Settings → ATEM & automation).
- ATEM control over the network: **REPLAY** puts the replay on air (ATEM AUTO, CUT or an **Endora stinger**
  video), the clip plays, and Endora returns to live when it ends. **LIVE**, **STOP**, **CANCEL AUTO** and
  **EMERGENCY MANUAL OVERRIDE** are always available.
- **Phone remote** for iPhone and Android: scan the QR code on the same network, no app needed.
- Configurable keyboard shortcuts, event log, source health, support bundle.
- Installer with **automatic updates** from GitHub Releases.

Automation is **off by default**. Until you switch it on, Endora never changes the ATEM except when the
operator presses LIVE.

## Wiring

```
USB capture cards (Cam 1–8) ─┐
ATEM Program out (optional) ─┤──► Replay PC ──(GPU HDMI 2, 1080p50, extended display)──► ATEM HDMI 8
                             │        │
                             │        └──(Ethernet, ATEM control + phone remote)──► same LAN as ATEM
```

Video always travels over HDMI. The network carries only control and the phone remote.

## Documents

| | |
|---|---|
| [docs/BUILD.md](docs/BUILD.md) | Building, testing, packaging the installer, releasing updates |
| [docs/CONFIGURATION.md](docs/CONFIGURATION.md) | Every setting, with the example in `config/settings.example.json` |
| [docs/COMPATIBILITY.md](docs/COMPATIBILITY.md) | Dependency, licensing and hardware compatibility report |
| [docs/HARDWARE_VALIDATION.md](docs/HARDWARE_VALIDATION.md) | Checklist to run on the real PC and ATEM before going on air |

## What is tested and what is not

The replay engine (buffers, clip files, playback timing, the automation state machine and a full
REPLAY → PLAY → LIVE cycle against a simulated ATEM) is covered by 44 automated tests. The phone remote was
exercised in a mobile browser. **No part has yet been tested on a real ATEM Mini Extreme, real USB capture
cards or a real GPU HDMI output.** Those paths are marked untested in [COMPATIBILITY.md](docs/COMPATIBILITY.md)
until the [hardware checklist](docs/HARDWARE_VALIDATION.md) has been completed. Simulated cameras, switcher and
output are labelled **SIMULATED** everywhere they appear.
