# Performance baseline

**Status: only the CI busy-chat run below has been measured by this fork.** The development environment for the P0 milestone was a
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

## Busy chat on the CI runner (first numbers)

Not a substitute for the measurements above: a GitHub Windows Server 2025 runner (no GPU, no video, made-up 7TV
emotes of 28 px, a quarter animated), LiteView's own chat at 300 messages a second for two minutes, the second minute
scrolled back (2000 messages kept and each new one laid out). Run 43677013718 (commit `d29ef49`), every 10 s:

| | Start (10 s) | Bottom of chat (50 s) | Scrolled back (90 s) | End (130 s) |
|---|---|---|---|---|
| Private memory | 42 MB | 45 MB | 52 MB | 56 MB |
| Working set | 72 MB | 74 MB | 85 MB | 84 MB |
| Handles / GDI / USER | 626 / 61 / 76 | 604 / 61 / 75 | 607 / 61 / 74 | 613 / 61 / 74 |
| Longest UI wait in the 10 s | 4 ms | 3 ms | 1 ms | 1 ms |
| Emotes | 120 decoded | 120 | 120 | 120, none refused, 0 MB of the 128 MB budget |

Memory grows only with the number of messages kept (300 at the bottom, 2000 scrolled back) and then levels off;
handles and GDI/USER objects stay flat; the window's thread never waited more than 4 ms. That run's generator only
reached 239 messages a second (it fed a fixed number per 50 ms timer tick and Windows' timer runs in 15.6 ms steps);
it now feeds by elapsed time. RES-7 (re-laying out every message that uses an emote when the emote arrives) is
bounded by the 300/2000 message cap and does not show up here.

The next run (`fa4f767`, generator fixed) kept 299-300 messages a second: private memory 42 MB at the bottom of the
chat, 62 MB scrolled back, the UI wait at most 5 ms.

**Channel switching** (fixture scene `switching`, same runner): another channel every 5 s for two minutes, each with
video (FakeStreamlink and the real mpv), its chat connection, emotes and recent messages, plus 50 chat messages a
second. 26 switches: private memory 40-44 MB, threads 26-29, GDI 62-65, USER 79-82, UI wait at most 20 ms; handles
634 → 697 (about 2.5 per switch). Nothing in the switch path keeps a handle (Streamlink's process and job, the mpv
pipe and the chat socket are all closed), so these are objects waiting for the garbage collector: with a full
collection before each sample (as the scene does now, `d58c1a0`) handles went 666 → 673 over the last 20 switches,
threads 28 → 26, GDI and USER flat. No leak across channel switches; CI's budget (40 handles over about 20 switches)
would catch one.

**Seamless switching** (`p3-playback`, run 43709662523, commit `1a673be`): the same switching scene with the old stream
kept until the new one's first frame. All 26 switches handed over that way; mpv players counted at each sample: 1
(the hand-over itself lasts about 1.6 s, of which 1.5 s is FakeStreamlink imitating Streamlink opening a stream, so
mpv's own start to its first frame is about 0.1-0.3 s on the runner); private memory 40-42 MB, handles 641 → 659, GDI
64, USER 83-85, threads 23-26, UI wait at most 372 ms (one sample; the rest under 20 ms). This is also why a persistent
mpv was not chosen: on a real PC most of a switch's wait is Streamlink and Twitch, which only hand-over hides.
`liteview.log` has "stream N: first frame after X s" for every stream, so the owner's real numbers can be read there.

**Picture presets compared** (fixture scene `presets`, run 43710858661, commit `b612178`): a test picture (chat text in
six sizes, a score box, a Siemens star, fine lines, coloured shapes, texture) at 1024x576 is the original; the stream
is it at 512x288 after a JPEG round trip at quality 55 (blocks and ringing like a stream); LiteView shows it 2x in
fullscreen and mpv saves what it shows with each preset (`tools/preset-metrics.py`, brightness only):

| Preset | PSNR (dB) | SSIM | Overshoot | Detail |
|---|---|---|---|---|
| Off (mpv's fast scaling) | 24.04 | 0.866 | 0.93 | 0.64 |
| Enhance | 24.55 | 0.855 | 2.23 | 1.13 |
| High, sharpness Soft | 25.67 | 0.870 | 2.55 | 1.16 |
| High (Normal) | 24.89 | 0.854 | 3.18 | 1.27 |
| High, Strong | 24.26 | 0.841 | 3.69 | 1.35 |
| Ultra, sharpness Off | **26.68** | **0.893** | 1.16 | 0.86 |
| Ultra, Soft | 25.84 | 0.887 | 1.88 | **1.01** |
| **Ultra (Normal, new)** | 25.09 | 0.880 | 2.33 | 1.10 |
| Ultra as it was (two sharpeners) | 20.94 | 0.836 | 5.22 | 1.45 |

PSNR/SSIM: closeness to the original (higher is better). Overshoot: luma levels per pixel beyond the original's local
brightest/darkest, i.e. halos (lower is better). Detail: edge energy against the original (1.00 = as sharp; above =
sharper than the original). The old Ultra was the furthest from the original of all presets, with the most halos and
the most "detail" (oversharpening, as the owner saw). The new Ultra is closer to the original than High at every
sharpness with fewer halos, because NLMeans removes the compression noise before the sharpener could amplify it;
Ultra with sharpness Soft is as sharp as the original. GPU cost is not measured here (the runner renders in software);
the stats overlay (I) shows it on a real card.

CI now fails when a run goes over this budget (`tools/stress-check.ps1` in `.github/workflows/ci.yml`): private
memory 250 MB, growth over the last 30 s 30 MB / 100 handles / 20 GDI / 20 USER objects, UI wait 250 ms, the
generator at 80 % of its rate, at least 100 emotes decoded and none refused.

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
