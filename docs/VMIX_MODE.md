# vMix replay mode

In this mode **vMix makes the replays** and Endora only does the automation: when the vMix replay starts playing,
Endora plays the vMix stinger into the replay and switches the ATEM Mini Extreme to vMix at the stinger's transition
point. When the replay stops, it plays the stinger back and cuts the ATEM to the camera that was on air before.
Endora records no cameras in this mode.

It is the default replay engine. The start-up window of Endora's own replay also has a *Replays from vMix* button
(key **V**), and Settings → *Replay engine* switches back and forth (restart needed).

## Wiring

```
Cameras ──HDMI──► ATEM Mini Extreme ◄──HDMI── vMix PC output (ATEM input 8 by default)
                        ▲
   Endora PC ──LAN──────┤ (ATEM control)
       └────── TCP 8099 ──► vMix (often the same PC: 127.0.0.1)
```

vMix needs the camera pictures for its replay (capture cards, or an ATEM HDMI out into vMix).

## vMix set-up

1. **Replay input** – the vMix Replay input. Leave the setting empty to use the first Replay input.
2. **Black input** – *Add Input → Colour*, black. vMix shows it whenever no replay is on air. Leave the setting empty to use the first Colour input.
3. **Stinger** – put the stinger on *Stinger 1* in vMix and set its **transition point** to a moment where the stinger
   covers the whole picture. Enter the same number in Endora as *ATEM cut after (ms)*, and the stinger's full length as
   *Stinger length*.
4. vMix's output goes to the ATEM input set in *ATEM input from vMix* (HDMI 8 by default).

## Modes

| Mode | What viewers see | Needs |
|---|---|---|
| **Keyed** (default) | The whole stinger over the live picture, both ways | Black input; stinger with a transparent background; ATEM DSK 1 free |
| **Cut at transition point** | The stinger from the moment it covers the picture | Nothing extra |
| **No vMix stinger** | ATEM Mix / Dip / Wipe / DVE or CUT | Nothing extra |

In **Keyed** mode the vMix output (black when idle) is luma-keyed on ATEM DSK 1 over Program. vMix plays the stinger
from black into the replay, so the stinger is seen over the live camera; at the transition point the ATEM cuts to vMix
underneath and the key goes off. Going back, the key comes on over vMix, the ATEM cuts to the camera underneath, and
the rest of the stinger plays over the live picture. The key is a hard luma key (black below 3 % is transparent), as in
Endora's own stinger. The scoreboard stays on DSK 2 or upstream key 1.

## What starts and ends a replay

- **vMix replay starts playing** → stinger in (setting *Start when the vMix replay starts playing*).
- **vMix replay stops** for longer than *Stopped for at least* → stinger back to live.
- **REPLAY / LIVE** in Endora, on the keyboard (Space / Enter), a MIDI controller, the phone remote, Companion or a vMix shortcut.
- **CUT TO LIVE** (Esc): straight ATEM cut back to the camera, no stinger.
- **HOLD** (Backspace): keep the replay on air when it stops (no automatic return).
- **OVERRIDE** (F12): Endora sends nothing to the ATEM or vMix until pressed again.
- If someone in vMix takes the replay to Program by hand, or the ATEM operator cuts away, Endora follows.

## Phone remote

Scan the QR code on the vMix screen (phone on the same Wi-Fi). **PLAY** from READY runs the stinger and the ATEM and
starts the vMix replay playing at the cut, so the start of the replay is not hidden under the stinger. On air, PLAY
and **STOP** play and pause vMix; with auto return on, Endora goes back to live after a stop. The phone also has
mark in / out / cancel, last 5-15 s (`ReplayMarkInOutLive`), camera 1-8 (`ReplayCameraN`), jog ±1 / ±10 frames,
speed 100/75/50/25 %, previous / next event, play last event, NOW, LIVE, CUT TO LIVE, HOLD, scoreboard and override.
Only these vMix Replay functions can be run from a remote.

## Companion and Stream Deck

Each action is one URL (shown and copyable in the vMix screen):

```
GET http://<endora-pc>:8686/api/cmd/Replay?code=<access code>
```

Actions: `Replay`, `Live`, `Stop`, `CancelAuto`, `Scoreboard`, `EmergencyOverride`, and the vMix replay controls
`vmix.Play`, `vmix.Stop`, `vmix.MarkIn`, `vmix.MarkOut`, `vmix.MarkCancel`, `vmix.Last` (`&value=8`), `vmix.Camera` (`&value=2`),
`vmix.Jog` (`&value=-10`), `vmix.Speed` (`&value=0.5`), `vmix.PlayLast`, `vmix.NextEvent`, `vmix.PreviousEvent`, `vmix.Now`. Use the *Generic HTTP* module in
Companion. The reply is JSON `{ ok, action, error }` (HTTP 409 when the action was refused).

For live button text, enter Companion's address (e.g. `http://127.0.0.1:8000`) in Settings and create these Companion
custom variables: `endora_state` (READY, STINGER IN, REPLAY ON AIR, STINGER OUT, OVERRIDE), `endora_onair` (1/0),
`endora_atem` and `endora_vmix` (OK/OFF). Endora writes them through Companion's HTTP API
(`POST /api/custom-variable/<name>/value`).

## vMix shortcut

The vMix screen copies a short VB.NET script that calls the URL above. In vMix: *Settings → Scripting → Add*, paste,
name it (e.g. `EndoraReplay`). Then *Settings → Shortcuts → Add*: your key, function **ScriptStart**, value
`EndoraReplay`. This is only needed if you want a vMix key for REPLAY/LIVE; normal use needs no shortcut because
Endora starts when the replay plays.

## Not yet tested on hardware

The vMix TCP client and the automation are covered by automated tests against a simulated vMix and a simulated ATEM.
Check on the real ATEM Mini Extreme and vMix before going on air: the cut timing (*ATEM cut after*) and that vMix
reports the replay as playing when you press play (the event log shows each step).
