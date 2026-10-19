# Hardware validation checklist

Run this on the real replay PC with the real ATEM Mini Extreme before the first match. Record the result,
date, Endora version and hardware model on each line. Until a line passes, treat that path as untested.

Setup: Endora installed with `EndoraReplay-win-Setup.exe`, ATEM firmware and ATEM Software Control version
noted, project format 1080p50 unless stated.

## 1. PC and display output

- [ ] GPU second HDMI connected to ATEM HDMI 8. Windows: Extend, 1920×1080, 50 Hz. ATEM shows the picture on input 8.
- [ ] Settings → Output lists the display, shows "Display matches the project format".
- [ ] Set the display to 60 Hz: Endora shows the mismatch and blocks REPLAY with an alert. Set back to 50 Hz: clears.
- [ ] Unplug the HDMI cable: Endora reports the output missing within a few seconds; reconnect recovers.
- [ ] Holding slate appears on ATEM input 8 when idle; Black and Last frame options work.
- [ ] Play a 10 s clip at 100 %: no visible stutter on the ATEM multiview. Note any dropped/repeated frames.

## 2. USB capture

For each card model used:

- [ ] Card appears in Settings → Capture inputs with its Windows name. Test reports 1920×1080 @ 50.
- [ ] Camera tile shows live picture, measured fps 49.5–50.5 for 10 minutes, dropped = 0.
- [ ] Unplug the card: tile shows NO SIGNAL / reconnecting; replug recovers without restarting Endora.
- [ ] Feed a 1080p59.94 signal: Endora reports mode mismatch, does not silently accept it.
- [ ] All configured cameras together (up to 8) for 30 minutes: CPU, disk MB/s and dropped frames noted.
- [ ] Two cameras pointed at the same clapper: frames line up within one frame across angles.
- [ ] Program capture (ATEM HDMI out → card) shows in the PROGRAM monitor.

## 3. ATEM control (LibAtem network protocol)

- [ ] Settings → ATEM shows Connected, model "ATEM Mini Extreme", input names match the ATEM.
- [ ] Changing Program/Preview on the ATEM panel updates Endora's PGM/PVW pills within 100 ms.
- [ ] Pull the network cable: Endora shows disconnected, raises an alert, does not switch anything; reconnect recovers.
- [ ] Repeat with the Blackmagic SDK backend if it was compiled in.

## 4. Automation

With automation **off**:

- [ ] REPLAY plays the clip on input 8 but does not switch the ATEM. LIVE switches back to the live camera only when pressed.

With automation **on**, transition ATEM AUTO (Mix):

- [ ] REPLAY: ATEM previews input 8, transitions, clip starts after the transition, returns to live at the end.
- [ ] Cutting to input 8 from the ATEM panel while a clip is armed starts the clip.
- [ ] STOP during playback: clip stops, holding frame shown; ATEM stays where it is.
- [ ] CANCEL AUTO during WAITING FOR PROGRAM: no further switching happens.
- [ ] EMERGENCY MANUAL OVERRIDE: Endora sends no more ATEM commands until cleared; ATEM panel stays in control.
- [ ] Wait-for-program timeout: block the ATEM transition (e.g. put it in a transition) and confirm Endora alerts after 5 s.
- [ ] Rapid double press of REPLAY within the lockout does not double-switch.

With **Endora stinger video**:

- [ ] Stinger loads (Settings shows frame count). The ATEM cut lands on the cover frame, no flash of the old picture.
- [ ] Stinger back to live at the end of the clip.
- [ ] With the DSK key on: the whole stinger shows over live on the way in and out, the background is keyed out, DSK 1 goes off when the stinger ends, and the fade hides the last frame.
- [ ] Settings shows each stinger's coverage near 100 %; a lower figure warns that the cut may show.

## 5. Playback and clips

- [ ] 100 %, 50 %, 25 % run at the right length (stopwatch on a 4 s clip: 4 s / 8 s / 16 s).
- [ ] Jog wheel and frame step move one frame per step in Jog mode.
- [ ] IN/OUT, save, tag, queue; clips survive an Endora restart.
- [ ] Change angle while a clip is armed: the same moment from the other camera is armed.
- [ ] Press a camera key (or numpad 1–8) while a replay plays: playout continues from the same moment on that camera.
- [ ] The REPLAY badge spins in the bottom-right corner during the clip and hides during stingers and the holding frame.
- [ ] Right-click a clip: the Tag submenu sets its tag.
- [ ] Fill the drive near the limit: the oldest clips are deleted, an armed or queued clip is kept.

## 5a. MIDI controller

- [ ] The controller appears in Settings → MIDI controller and "Last message" changes when a control moves.
- [ ] Program all: each action learns the pressed button; Skip leaves an action unbound.
- [ ] Jog wheel learn (turn right): right turns go forward and left turns go back by the frames-per-step value.
- [ ] T-bar: Set 0 % and Set 100 % at each end; speed follows the T-bar smoothly during playback.
- [ ] Keyboard Program all keys: each key is learned in turn; Esc stops, Backspace skips.

## 5c. Scoreboard (DSK 2)

- [ ] The scoreboard link shows full screen on the chosen display, with transparent areas black.
- [ ] SCOREBOARD / F5 puts DSK 2 on air over Program and takes it off; the ATEM panel shows the same state.
- [ ] Only the black background is keyed out; dark parts of the scoreboard stay solid.
- [ ] A stinger (DSK 1) plays correctly while the scoreboard is on air.
- [ ] "Hide during replays": the scoreboard goes off air when REPLAY starts and comes back after the return to live.
- [ ] With "Upstream key 1": the scoreboard goes on and off air on M/E 1 key 1, stays on air through REPLAY/LIVE cuts, and the stinger plays over it.

## 5b. NDI

- [ ] With the NDI Runtime installed, NDI sources appear in Settings → Capture inputs and can be used as a camera or as Program.
- [ ] NDI output on: the source "PC NAME (Endora Replay)" shows the replay playout and holding frame in NDI Studio Monitor, vMix or OBS.
- [ ] Without the runtime, Settings says why and the HDMI output still works.
- [ ] Disk below the free-space limit: saving is refused with a message; buffer recording continues.

## 6. Phone remote

- [ ] iPhone (Safari) and Android (Chrome) on the same Wi-Fi scan the QR and connect. Windows Firewall prompt allowed for private networks.
- [ ] Cue, mark, tag, speed and REPLAY/LIVE from the phone. With "Allow REPLAY / LIVE / STOP" off, the phone cannot take to air.
- [ ] New code disconnects every phone; old QR is refused.

## 7. Install and update

- [ ] Installer runs without admin, starts Endora, shortcuts created.
- [ ] Publish a newer tag: installed PC downloads it, shows "Update ready", Restart & install is disabled while a replay is on air, installs and keeps settings and clips.

## Sign-off

| Area | Pass/Fail | Tester | Date | Version | Notes |
|---|---|---|---|---|---|
| Display output | | | | | |
| USB capture | | | | | |
| ATEM control | | | | | |
| Automation | | | | | |
| Playback | | | | | |
| Phone remote | | | | | |
| Install/update | | | | | |
