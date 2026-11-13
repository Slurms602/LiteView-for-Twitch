# Architecture

LiteView Enhanced keeps upstream LiteView's design: a small native Windows Forms window that drives
**Streamlink** (finds and downloads the stream) and **mpv** (decodes and draws it, inside our window),
draws **chat itself** from Twitch IRC, and uses **WebView2** only for the Twitch *Following* page and the
optional Twitch web chat. This file describes the code as it is now; planned changes are in `ROADMAP.md`.

## Processes

```
LiteView.exe  (.NET Framework 4.8, WinForms; Below Normal priority; pinned off the V-Cache die on X3D CPUs)
 ├─ job object (KILL_ON_JOB_CLOSE)          ← closing or crashing LiteView ends everything below
 │   └─ streamlinkw.exe  (bundled Python)   ← started suspended, pinned, Below Normal, then resumed
 │        └─ mpv.exe --wid=<video panel> --input-ipc-server=<random pipe>
 │                 ↕ JSON IPC on \\.\pipe\liteview-mpv-<guid>  (volume, mute, shaders, vf, events)
 ├─ msedgewebview2.exe ...                  ← only on the Following page / web chat; closed while watching with native chat
 └─ (HTTPS) Twitch Helix + id.twitch.tv, Twitch IRC (wss), 7TV / BTTV / FFZ APIs and CDNs

GameFirst.exe (optional, separate, started by the user)  ← adjusts listed processes every few seconds
```

## Source layout

| Path | Contents | Origin |
|---|---|---|
| `src/liteview.cs` | Main window: top bar, layout (16:9 video area), browse/watch modes, WebView2 set-up, Streamlink start, mpv IPC thread, picture presets, settings | upstream, small edits |
| `src/chat.cs` | Native chat: `Txt` (text drawing), `Emote`/`Images` (download, decode, frame budget), `ChatView` (layout + drawing), `ChatPanel` (emote sets, badges, sending, Tab completion) | upstream, edits |
| `src/helix.cs` | `Tw`: upstream's static Twitch API, now a facade over `src/core/TwitchClient.cs`; the DPAPI token file | upstream, rewritten as facade |
| `src/sidebar.cs` | Followed live channels column | upstream, small edits |
| `src/picker.cs` | Emote picker | upstream, one line |
| `src/common.cs` | `Native`: Win32 P/Invokes, CPU-die mask, ini helpers | upstream, delegates to core |
| `src/guard.cs` | GameFirst.exe | upstream, small edits |
| `src/app/LiteView.Fork.cs` | The fork's additions to the main window (same partial class): WebView2 hardening, stream start/exit/retry, diagnostics, logging, crash handling | fork |
| `src/app/Launcher.cs` | The only place links are opened | fork |
| `src/app/LoginDialog.cs` | Device-code login window (system browser) | fork |
| `src/core/` | `LiteView.Core.dll`: pure logic, no UI, no network, built for `net48` and `net10.0` | fork |
| `tests/LiteView.Core.Tests/` | xUnit tests of `src/core` (run on any OS) | fork |
| `config/` | mpv config and shaders, `gamefirst.ini` | upstream |
| `tools/upstream.sh` | Reviewing and taking upstream changes (`docs/UPSTREAM.md`) | fork |

**Why the upstream files did not move**: this fork follows upstream and takes its improvements
(`docs/UPSTREAM.md`). Git applies upstream patches cleanly only while the files keep their paths and the
fork's edits stay small. So new code goes into new files, and `LiteView` is a `partial class` whose
fork-only half is `src/app/LiteView.Fork.cs`.

## `src/core` modules

| File | Responsibility |
|---|---|
| `TwitchClient.cs` | Twitch login and API: token load/validate/refresh/log out, device-code flow, Helix GET with 401 refresh and 429 wait, bounded downloads. HTTP, token store, delay and clock are injected (tests use a fake Twitch) |
| `Json.cs` | Strict, depth-limited JSON reader with upstream's value shapes, and the `V/S/N/B/A/Props` accessors |
| `IrcClient.cs` | `Irc`: the chat connection loop (handshake, PING, RECONNECT, backoff, one-line sends) over `IIrcTransport` (`WebSocketTransport` in the app, a fake in tests) |
| `IrcMsg.cs` | Parse one IRCv3 line (tags, prefix, command, channel, text) |
| `IrcProtocol.cs` | PING/PONG, RECONNECT, outgoing text clean-up, display length cap, `FrameBuffer` (bounded WebSocket message assembly) |
| `ChannelName.cs` | Channel login validation and normalisation from typed text or URLs; numeric ids |
| `SafeUrl.cs` | URL policy: what may be opened in the browser, navigated in WebView2, downloaded as an image or fetched as API JSON |
| `WebMessage.cs` | The page→app message format and its checks (origin, per-run secret, login) |
| `StreamCommand.cs` | `WinArgs` (Windows quoting), `Quality` (choices → Streamlink selectors), `StreamCommand` (the Streamlink command line, pipe names), `StreamOutcome` (why a stream stopped) |
| `Backoff.cs` | Exponential backoff with jitter and a "healthy connection" reset |
| `EmoteLimits.cs` | Image size/frame/byte limits, drawn-size rule, frame thinning; `ByteBudget<T>` (LRU by bytes) |
| `ImageSniff.cs` | Format and size from the first bytes (PNG/GIF/JPEG only) |
| `Storage.cs` | `AtomicFile` (temp + replace, optional `.bak`), `IniStore` (parse, read with last-known-good fallback, write) |
| `Log.cs` | `Redact` (credential and channel removal), `Log` (levels, rotation, tail) |
| `Limits.cs` | `Bounded` (size-limited stream reads), `CpuTopology` (L3 parsing and the pinning decision), `Diagnostics` (report text) |

## Threads and ownership

| Thread | Owns / does | Talks to the UI by |
|---|---|---|
| UI thread | All controls, settings, the stream (`job`, `slProc`, `gen`), the emote frame budget, all drawing of frames | - |
| mpv pipe thread (`PipeLoop`, background, Below Normal) | Connects to and reads the pipe. Sleeps on `streamOn` while nothing plays | `Ui(gen, action)`: `BeginInvoke`, dropped if the generation changed |
| mpv writer thread (`MpvWriter`, background) | Writes queued commands to the current pipe (queue of 256, tagged with the stream generation) | the UI only enqueues (`SendMpv`), so it never waits on mpv |
| IRC task (`Irc.Loop`) | The WebSocket, PING/PONG, reconnect backoff | `ConcurrentQueue<Msg>` drained by a 100 ms UI timer |
| Image tasks (≤ 4 at once) | Download + decode one emote; set `Frames`, then `State = 2` | `Images.Loaded` → `BeginInvoke` |
| Emote-list / badge tasks | Build dictionaries, then swap the reference (`third`, `badges`) | volatile reference swap + `BeginInvoke` |

Rules that keep this safe:

- **Generations.** Each stream start increments `gen` and gets a new pipe name. Every callback from the pipe
  thread carries the generation it was created for and is ignored if it no longer matches, so a dying stream
  cannot change the new one.
- **Frames are freed only on the UI thread**, and only for emotes not drawn in the last 10 seconds
  (`Images.Trim`). Drawing code checks `State == 2` before touching `Frames`; a freed emote returns to
  `State = 0` and is loaded again on its next use.
- **No child process outlives the app**: the job object is closed by Windows when LiteView exits, even on a crash.

## Stream lifecycle

```
Watch(channel)  ──► StopStream (TerminateJobObject, streamOn.Reset)
                    gen++, new pipe name, StreamCommand.Build (validated, quoted)
                    CreateProcess suspended → affinity → job → own Process handle → resume → streamOn.Set
OnTick (500 ms) ──► alive + mpv connected for this gen → "playing"
                    alive, no player yet → every 2 s read the log: "Waiting for streams" → "Offline - it starts by itself"
                                           (Streamlink's --retry-streams 30 checks again), else the last [error] line
                    exited → StreamOutcome: Offline | Failed | Ended | Fatal → status text
                           → retry: Failed/Ended backoff 2 s…60 s (±jitter), 8 at most; Offline 60 s;
                             Fatal or too many → "click to try again"
```

Restarting the player for each channel is upstream's approach and is kept. Whether a persistent mpv
(`loadfile` over IPC) switches faster is a P3 measurement (`ROADMAP.md`).

## Trust boundaries

See `SECURITY_MODEL.md`. In short: everything from the network, web pages, chat, emote services, and local
files is checked in `src/core` before it is used; links leave the app only through `Launcher`; child processes
are started with fully quoted, validated command lines.

## Decisions

### Runtime: stay on .NET Framework 4.8 (for now), build with the modern SDK

The spec asks to evaluate moving to a current .NET LTS (.NET 10). Findings:

| | .NET Framework 4.8 (kept) | .NET 10, self-contained | .NET 10, framework-dependent |
|---|---|---|---|
| What users install | nothing (part of Windows 10 1903+ and 11) | nothing | the .NET 10 Desktop Runtime (a prerequisite: against the spec) |
| Added download size | 0 | roughly 60-100 MB for WinForms (trimming is not supported for WinForms; estimate, not measured) | 0 |
| Start-up / memory | shared, pre-compiled (NGEN) framework | not measured | not measured |
| Language | C# latest compiles for net48 (most features) | C# latest | C# latest |
| Security updates | through Windows Update | with each LiteView release | through the runtime |

So the app stays on .NET Framework 4.8 while the **build** moves to SDK-style projects: modern C#, NuGet
packages pinned with lock files, analyzers, `dotnet test`, CI on Linux and Windows. The pure-logic library is
built for both `net48` and `net10.0`, so a later move only has to port the UI. To revisit when WinForms
supports trimming / Native AOT, or if measurements show a real start-up or memory gain.

### JSON

Upstream used `JavaScriptSerializer`, which exists only in .NET Framework, so the login code could not be tested on
.NET 10. It was replaced by `src/core/Json.cs` (about 200 lines, no dependency) that returns the same shapes
(`Dictionary<string, object>`, `object[]`, `long`/`double`, `string`, `bool`, `null`). System.Text.Json was not used:
on net48 it brings about eight extra DLLs.

### Emote memory

Decoded frames are budgeted (128 MB) rather than counted, because one animated 7TV emote can cost 100× a static
one. The budget is a starting value; the chat stress test in P1 should set it from measurements.
