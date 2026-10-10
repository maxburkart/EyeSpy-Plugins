# ContinuousLowRes — Agent DVR plugin

Records each camera's **low-resolution stream 24/7** while Agent keeps recording **high-resolution clips on motion** — on the same camera entry. No more adding every camera twice.

```
                 ┌── Live URL (sub, e.g. 640x360) ──► Agent: live view + motion detection
 camera ─────────┤                                     └─► ContinuousLowRes: 24/7 segments ─► Agent recordings (tag "continuous")
                 └── Record URL (main, e.g. 4K) ────► Agent: Raw Record on motion/alert   ─► Agent recordings (high-res events)
```

## Set up a camera

1. **Video Source** → *Live URL* = the sub stream, *Record URL* = the main stream.
2. **Recording** → mode *Raw Record*, record on *Detect* (or *Alert*). Agent now records high-res events only.
   Optional: *HD On Demand* (Video Source) so Agent only opens the main stream while it is recording.
3. **Plugins** tab → pick **ContinuousLowRes** → click **…**:
   - **Mode – Auto** works with no other settings: it re-encodes the frames Agent already decodes from the Live URL.
   - For the lowest CPU, paste the sub-stream URL (with credentials) into **Sub-stream URL**. Auto then switches to *Copy*: the stream is saved as-is, no decoding or encoding.

Each finished segment (15 min by default, aligned to :00/:15/:30/:45) is moved into the camera's recordings through Agent's `addrecording` API, tagged `continuous`, plus `event` if motion/an alert/a high-res recording happened during it. They show up in the timeline and Agent's storage management deletes them like any other recording. Turn off *Add to Agent's recordings* to keep them in the plugin's own folder with its own retention (days / GB).

## Modes

| | Copy | Encode |
|---|---|---|
| Source | camera's sub stream (URL) | frames Agent already decodes |
| CPU | ~0 (stream copy) | one small H.264 encode (default 480p @ 10 fps) |
| Camera connections | +1 to the camera's sub stream | none extra |
| Audio | none / AAC / copy | none |
| Works with | RTSP / HTTP / RTMP cameras | anything Agent can show, USB included |

## Requirements

- Agent DVR with plugin support (built for .NET 9, like the official plugins).
- **ffmpeg** executable. Looked for in *FFmpeg path*, then Agent's folder, then `PATH`.
  - Windows: `winget install Gyan.FFmpeg`, or set *FFmpeg path* to `ffmpeg.exe`.
  - Linux / Raspberry Pi: `sudo apt install ffmpeg`.
  - Docker: if the image has no `ffmpeg` binary, mount a static build into the container and point *FFmpeg path* at it.

## Build & install

Needs the .NET 9 SDK.

```
./build.sh          # or .\build.ps1 on Windows
```

Then copy `out/ContinuousLowRes/` to `[Agent install folder]/Plugins/ContinuousLowRes/` and restart Agent.
(Pushing this folder to GitHub also works: the included workflow builds the zip for you under *Actions*.)

## Commands

Buttons in the config dialog, or the HTTP API:

```
http://agent:8090/command.cgi?cmd=plugincommand&ot=2&oid=<camera id>&command=<cmd>
```

| command | |
|---|---|
| `status` | JSON: running, mode, source, segments, registration results, last error |
| `start` / `stop` | enable / disable continuous recording |
| `restart` | restart ffmpeg |
| `detect` | try to read the Live URL from Agent's camera settings |
| `cleanup` | apply retention now |

## Events

Under the camera's **Actions** you can react to `ContinuousLowRes: Recorder Stopped` (fires when recording fails twice in a row — bad URL, camera unreachable, ffmpeg missing) and `ContinuousLowRes: Recorder Resumed`.

## Behaviour notes

- Recording runs while Agent is receiving video from the camera; it pauses when the camera is switched off or offline and resumes automatically.
- ffmpeg is watched: if it exits or delivers no video for 45 s it is restarted with back-off (5 s → 2 min).
- *MP4, crash-safe* (default) writes fragmented MP4, so the segment in progress stays playable if Agent or the machine goes down. Standard MP4 loses it.
- An ffmpeg left running by an Agent crash is stopped on the next start (tracked by a `.ffmpeg.pid` file in the camera's folder).
- Segments that Agent refuses are retried 5 times, then left in the plugin folder and removed by retention.
- In copy mode, H.265 cameras play in most browsers; for Safari add `-tag:v hvc1` to *Extra output arguments*.
