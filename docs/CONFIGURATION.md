# Configuration

Everything is editable in the app (**Settings**, or Ctrl+,). The file lives at
`%LOCALAPPDATA%\EndoraReplay\settings.json`. A complete commented example is in
[`config/settings.example.json`](../config/settings.example.json); the test suite checks that it loads and
validates.

## Sections

| Section | Key settings | Notes |
|---|---|---|
| `videoFormat` | `1080p50` | All inputs and the output must match. Restart to change. |
| `sources[]` | `sourceId` 1–8 cameras, `0` Program; `enabled`, `name`, `backend`, `deviceId` | A missing device is shown as NOT FOUND, never replaced by another device. `Simulated` backend = test pattern. |
| `buffer` | `seconds` (120), `jpegQuality` (70/90/97), `dataFolder`, `minFreeDiskGb` | Clips are refused below the free-space limit. |
| `atem` | `backend` (`NetworkProtocol`, `BlackmagicSdk`, `Simulated`), `address`, `replayInput` (8) | `replayInput` is the ATEM input wired to Endora's HDMI output. |
| `automation` | `enabled` (**false**), `autoSwitchIn`, `startOnProgramEdge`, `returnToLive`, `transitionIn/Out`, styles, stinger files, `stingerKey` (DSK luma key over live), `stingerKeyer`, `stingerKeyClip`, `stingerKeyGain`, `stingerFadeMs`, `lockoutMs`, `waitForProgramTimeoutMs` | With `enabled: false` Endora only switches when the operator presses LIVE. |
| `output` | `kind` (`Display`, `Simulated`), `displayDevice` (`\\.\DISPLAY2`), `holding` (`Slate`, `LastFrame`, `Black`) | Replay is blocked if the display is missing, primary, or the wrong mode. |
| `remote` | `enabled`, `port` (8686), `allowTransport`, `accessCode` | The access code is in the QR link; a new code disconnects every phone. |
| `updates` | `checkAutomatically`, `repositoryUrl`, `includePrereleases` | Updates install only on Restart & install. |
| `keyBindings` | action → key, e.g. `"Replay": "Space"` | Conflicts are refused in Settings. |

## Default keys

| Key | Action | Key | Action |
|---|---|---|---|
| Space | REPLAY | I / O | Mark IN / OUT |
| Enter | LIVE | S | Save clip |
| Esc | STOP | T | Tag clip |
| Backspace | CANCEL AUTO | Q | Add to queue |
| F12 | EMERGENCY MANUAL OVERRIDE | L / J | Live / Jog mode |
| F9 | Clear error | ← / → | Frame back / forward |
| 1–8 | Select camera | Shift+← / → | Shuttle 1 s |
| Tab / Shift+Tab | Next / previous camera | F1 / F2 / F3 | Speed 100 / 50 / 25 % |

## Typical cricket setup

1. Cameras 1–4 on USB capture cards, Program from the ATEM's HDMI out on a fifth card (optional).
2. GPU second HDMI → ATEM HDMI 8, Windows display set to Extend, 1920×1080, 50 Hz.
3. Settings → ATEM: address of the ATEM, replay input 8.
4. Rehearse with automation off, then switch it on, choose Endora stinger video, load the stinger file and set
   the cut frame.
