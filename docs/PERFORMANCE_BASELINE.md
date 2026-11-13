# Performance baseline

**Status: nothing has been measured by this fork yet.** The development environment for the P0 milestone was a
Linux sandbox without Windows, a GPU or Twitch playback. This file records upstream's own published numbers
(unverified by us), the method we will use, and what the P0 changes may affect, so the first real measurement on
Windows has something to compare against.

## Upstream's published numbers (unverified)

From upstream's README (LiteView 1.1, author's PC, KaiCenat at 37K viewers, 60 s per row, working set of all of
LiteView's processes):

| Scenario | CPU (one thread) | RAM |
|---|---|---|
| Chrome tab (3 tabs, extensions) | 118 % | 3.0 GB |
| LiteView 1.0: video + Twitch web chat with 7TV | 30 % | 1.4 GB |
| LiteView 1.1: video + native chat | 10 % | 0.35 GB |

GPU cost per picture preset (RTX 5080, 720p stream shown at 1080p): Off 0.6 %, Enhance 1.1 %, High 2.6 %,
Ultra 11 %, Ultra + RTX 25 %.

These came from one machine and short runs; they are a starting point, not a baseline.

## Method (to be run on the owner's PC)

Hardware for the first baseline: Ryzen 9 9900X, RTX 5080, 32 GB DDR5, Windows 11 64-bit. Record the GPU driver
version, Windows build, monitor resolutions and refresh rates, and the LiteView commit.

Builds compared: upstream `upstream-baseline` (release zip 1.2.0), this fork at the commit under test, and
Chrome or Edge with a documented configuration (version, extensions, hardware acceleration on).

For each scenario, 3 runs: 2 minutes warm-up, then 10 minutes measured (60 minutes for the long-run rows):

| Scenario | Notes |
|---|---|
| Idle on the Following page | |
| Watching 1080p60, native chat, quiet channel | |
| Watching 1080p60, native chat, very busy channel (>30K viewers) | animated 7TV emotes |
| Same, minimized | |
| Each picture preset: Off, Enhance, High, Ultra, Ultra + RTX | 720p → 1080p and 1080p → 1440p |
| Video on the second monitor while a game runs on the first | game FPS, 1 % lows, frame times |
| 60-minute session switching channels every 5 minutes | memory growth, handle/GDI counts |

Metrics: CPU of the whole process tree and per process (LiteView, msedgewebview2, streamlinkw, mpv); working set
and private bytes; threads, handles, GDI/USER objects (`Export diagnostics` shows LiteView's own); GPU per engine
(3D, Video Decode) from Task Manager or `typeperf "\GPU Engine(*)\Utilization Percentage"`; mpv dropped and delayed
frames (mpv stats overlay, `i` key, or the IPC property `frame-drop-count`); network throughput.

For chat specifically, `LiteView.exe <channel> --chat-stress=<messages per second>` (see `TESTING.md`) writes
memory, handle and emote-cache figures to `liteview.log` every 10 seconds, so busy-chat runs can be repeated and
compared without a busy real channel.

Suggested tools: Windows Performance Recorder/Analyzer or `typeperf` for CPU and GPU counters, PresentMon or
CapFrameX for game frame times. Store the raw CSVs under `docs/perf/<date>-<commit>/` with a short README.

## What P0 changed that could matter

| Change | Expected effect | To check |
|---|---|---|
| Pipe thread sleeps when no stream plays | Fewer wake-ups and exceptions while browsing (upstream: one connect attempt and timeout exception per second) | CPU when idle on the Following page |
| Emote frame budget (128 MB, LRU) | Bounded memory in long sessions; emotes freed and re-downloaded if they come back | Memory over the 60-minute run; network after channel switches |
| `Images.Used` on every paint | A dictionary update per drawn emote per frame (small) | CPU in a busy emote-heavy chat |
| Image header check, size caps, frame thinning | Negligible cost; very long animations keep fewer frames (same duration) | Visual check of long 7TV emotes |
| No browser extensions in WebView2 | Less work on the Following page and in web chat (7TV no longer injected) | Following page CPU/RAM; web chat no longer shows 7TV emotes (native chat does) |
| Stream retry | At most one Streamlink start per minute while a channel is offline | CPU while watching an offline channel |

No regression budget is set until the first measurement exists. Proposed rule after it: a change may not
raise steady-state CPU or memory by more than 5 % in any scenario above without a written reason.
