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
| `buffer` | `seconds` (120), `jpegQuality` (70/90/97), `dataFolder`, `minFreeDiskGb`, `autoDeleteOldClips` (true), `keepTaggedClips` (false) | When the drive runs low the oldest clips are deleted, except clips that are armed, playing or queued. Without auto-delete, clips are refused below the free-space limit. |
| `atem` | `backend` (`NetworkProtocol`, `BlackmagicSdk`, `Simulated`), `address`, `replayInput` (8) | `replayInput` is the ATEM input wired to Endora's HDMI output. |
| `automation` | `enabled` (**false**), `autoSwitchIn`, `startOnProgramEdge`, `returnToLive`, `transitionIn/Out`, styles, stinger files, `stingerKey` (whole stinger over live on DSK 1, hard luma key), `lockoutMs`, `waitForProgramTimeoutMs` | With `enabled: false` Endora only switches when the operator presses LIVE. |
| `output` | `kind` (`Display`, `Simulated`), `displayDevice` (`\\.\DISPLAY2`), `holding` (`Slate`, `LastFrame`, `Black`), `replayBadge` (true), `replayBadgeText`, `ndiOutput` (false), `ndiOutputName` | Replay is blocked if the display is missing, primary, or the wrong mode. |
| `midi` | `enabled`, `device` (empty = all), `bindings` (action + note/CC + channel), `jog` (CC), `jogEncoding` (`Auto`, `Offset64`, `TwosComplement`, `SignBit`, `Absolute`), `jogFramesPerStep` (1), `jogInvert`, `tBar` (CC or pitch bend), `tBarZero`, `tBarFull`, `tBarMinSpeed` (0), `tBarMaxSpeed` (100) | Set everything with Learn in Settings → MIDI controller. Right turn = forward, left = reverse. |
| `scoreboard` | `enabled`, `url`, `displayDevice` (a third HDMI output), `atemInput` (7), `zoomPercent` (100), `keyer` (`Dsk2` or `UpstreamKey1`), `hideDuringReplay` (true) | The page is shown full screen on black and keyed over Program with ATEM DSK 2 or upstream key 1 (hard luma key). SCOREBOARD button or F5 puts it on and off air. |
| `ui` | `cameraCount` (4), `askCameraCountOnStart` (true), `controlsMinimized` (false) | Endora asks for the camera count at start; cameras above it are turned off. |
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
| F5 | Scoreboard on/off | F6 | Split screen on/off |
| + / − | Zoom replay in / out | 0 | Zoom reset |
| Ctrl+arrows | Pan the zoomed replay | | |

## Replay tools

These are new and have not been tested on the real hardware yet.

- **Highlights package:** filter the clip library by a tag (or Ctrl/Shift-click clips), press HIGHLIGHTS, then
  REPLAY. The clips play oldest first as one replay with the stinger IN between each pair; the clip changes under
  the stinger's cover frame. Without a stinger they cut. The camera cannot change while a package plays.
- **Export MP4:** right-click a clip → Export MP4…. It is saved on the selected camera (or the angle it was marked
  on) at the current speed, without audio. Several selected clips export as one highlights video with the stinger.
  H.264 is used when this PC's video library can encode it, otherwise MPEG-4; the status line says which.
- **Split screen (F6 or SPLIT under the replay monitor):** replays show a second angle on the right, at the same
  moment. While it is on and a replay plays, the camera keys choose the right-hand picture. Not for highlights.
- **Zoom and pan:** while a replay plays, + / − or the mouse wheel over the replay monitor zooms (up to 4×), Ctrl+arrows
  or dragging pans, 0 or a double-click resets. Each new replay starts unzoomed. All of these can also be learnt on
  a MIDI controller. Split screen and zoom re-encode every replay frame on the CPU; check the replay stays smooth on
  the match PC.

## Typical cricket setup

1. Cameras 1–4 on USB capture cards, Program from the ATEM's HDMI out on a fifth card (optional).
2. GPU second HDMI → ATEM HDMI 8, Windows display set to Extend, 1920×1080, 50 Hz.
3. Settings → ATEM: address of the ATEM, replay input 8.
4. Rehearse with automation off, then switch it on, choose Endora stinger video, load the stinger file and set
   the cut frame.
