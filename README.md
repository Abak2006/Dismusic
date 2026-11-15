# Dismusic

A Windows 10/11 tray app for audio routing:

- **Per-app output**: send each app to a chosen output device (Spotify → headphones, game → speakers).
- **Multi-output**: play the same audio on several devices at once, kept in sync, with per-device delay and volume.
- **Screen follow**: an app's audio follows the screen its window is on (e.g. move a video to the TV and its sound goes to the TV's HDMI audio).

No virtual audio driver and no admin rights are needed.

## Requirements

- Windows 10 version 2004 (build 19041) or newer. Windows 11 is recommended.
- .NET 10 SDK, to build.

## Build, run, test

```bash
dotnet build Dismusic.slnx
dotnet run --project src/Dismusic.App            # the tray app
dotnet test Dismusic.slnx                        # unit tests (no audio hardware needed)
dotnet run --project src/Dismusic.Diag -- all    # hardware/OS self-checks, see below
```

Self-contained single-file build:

```bash
dotnet publish src/Dismusic.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

## Using it

| Tab | What it does |
|---|---|
| Apps | Lists apps that have an audio session. Pick an output, or tick **Follow screen**. "Playing on now" shows the current routing and why. |
| Multi-output | Pick a source and the devices that should get a copy, then **Start**. Delay (0–1000 ms) and volume can be changed while it runs. |
| Screens | Map each monitor to an audio device. **Auto-detect** matches HDMI/DP audio by monitor name. |
| Settings | Start with Windows (HKCU `Run` key, starts with `--tray`), start minimized, reset all app outputs, open the log folder. |

Closing the window keeps the app in the notification area. Use **Exit** from the tray menu to quit.

Multi-output sources:
- **Everything on \<device\>**: endpoint loopback of that device, i.e. the classic "also play my speakers' audio on the headphones". This is the recommended source.
- **All apps except Dismusic**: process loopback in exclude mode. Avoid it with audio enhancers such as FxSound, because it also captures the enhancer's re-rendered output, so you'd hear everything twice.
- **Only \<app\>**: process loopback of that app's process tree (browsers included, since their audio runs in a child process).

## How it works

```
src/Dismusic.Core
  Routing/AudioPolicyConfig.cs     undocumented Windows per-app endpoint API (raw vtable calls)
  Routing/RoutingPlanner.cs        pure decision: screen-follow > fixed rule > leave alone
  Routing/RoutingCoordinator.cs    polls devices/sessions/windows/monitors and applies decisions
  Sessions/                        MMDevice + audio-session enumeration (NAudio), process snapshot
  Screens/                         monitor names (DisplayConfig), window Z-order, WinEvent hooks
  Mirror/ProcessLoopbackCapture.cs process loopback via ActivateAudioInterfaceAsync (raw COM)
  Mirror/CaptureSources.cs         endpoint loopback (NAudio)
  Mirror/MirrorChannel.cs          per-output ring buffer + drift-correcting linear resampler
  Mirror/MirrorEngine.cs           one capture → N WASAPI shared-mode outputs
  Settings/                        JSON settings with atomic writes and corrupt-file recovery
src/Dismusic.App                   WPF UI (Fluent theme), tray icon, single instance
src/Dismusic.Diag                  console diagnostics
tests/Dismusic.Tests               xUnit tests
```

- **Per-app routing** uses `Windows.Media.Internal.AudioPolicyConfig`, the same mechanism as Settings › Sound › Volume mixer. Windows stores the choice per app, so it survives restarts even without Dismusic. Dismusic re-applies its rules every 2 s and after window moves, only when the current value differs.
- **Screen follow** listens for window move, focus and restore events (debounced 350 ms). It takes the app's topmost visible window, finds its monitor, looks up the mapped device and routes the app there.
- **Multi-output drift correction**: every device has its own clock, so copies slowly drift apart. Each output keeps its buffer at 60 ms plus the user's delay by adjusting its resampling ratio by at most ±0.5%. Clock drift is typically under 0.02%, and the adjustment is inaudible. Underruns output silence and refill before resuming, and a large backlog (over 250 ms) is skipped.

## Diagnostics (`Dismusic.Diag`)

| Command | Checks |
|---|---|
| `devices`, `sessions`, `monitors` | What Windows reports, including each session's pinned device. |
| `policytest` | Pins this process to a device, reads it back and clears it. **Run this first after a Windows feature update.** If it fails, the undocumented interface changed. |
| `capture [s]` | Process-loopback capture (expect 0 frames while nothing plays). |
| `mirrortest [s]` | Full pipeline with an inaudible tone (≈ -60 dB) captured and rendered at volume 0. Checks that the buffer holds near target. |
| `mirror <device> [s]` | Audible: mirrors the default device to `<device>` and prints buffer/drift stats. |
| `follow <exe>` | Which windows of `<exe>` screen-follow sees and which monitor each is on. |
| `tonewindow [s]` | Opens a window that plays an inaudible tone, so you can test screen-follow on it. |

## Known limitations

- **Per-app, not per-window.** Windows routes audio per process. Two windows of one browser on two screens can't go to different devices; the topmost window decides.
- **Undocumented API.** Per-app routing could break in a future Windows build (it changed once, in build 21390). If it does, the app shows a banner and the other features keep working. `Diag policytest` detects this.
- **Apps that choose their own device** (some games, DAWs, Discord's own output setting) ignore per-app routing. Some apps switch only when playback restarts.
- **Minimized windows don't trigger screen-follow.** The app keeps its last routing.
- **Mirror latency.** Copies lag the original by about 60–80 ms; use per-device delay to align. Bluetooth targets add their own latency (often 100–250 ms).
- **Protected audio** (some DRM video) may be silent in captured copies; this is enforced by Windows.
- UWP/Store apps whose windows belong to `ApplicationFrameHost.exe` aren't matched for screen-follow.
- Rendering is forced to software mode, because WPF's GPU path produced a blank window on the development laptop. The UI is static, so the cost is negligible.

## Files

- Settings: `%APPDATA%\Dismusic\settings.json` (a corrupt file is renamed `settings.json.corrupt-<time>` and defaults are used).
- Log: `%LOCALAPPDATA%\Dismusic\dismusic.log` (rotated at 1 MB).

Dismusic makes no network connections and collects no data.
