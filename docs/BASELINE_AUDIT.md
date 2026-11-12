# Baseline audit of upstream LiteView

| | |
|---|---|
| Upstream | https://github.com/itsdeeezy/LiteView-for-Twitch |
| Audited revision | `46e76dfea431d3ad02a61bc1116fc694190e8aa5` (2026-11-09, "LiteView for Twitch 1.2.0: emote picker, smooth chat text"), tagged `upstream-baseline` |
| Licence | MIT, (c) 2026 Deezy. Bundled components keep their own licences (see `THIRD_PARTY_NOTICES.md`) |
| Audit date | 2026-11-12 |
| Method | Every source, build, config and doc file read in full (about 180 KB of C#, `build.ps1`, `config/`). The code was compiled (SDK build, .NET Framework 4.8 reference assemblies, Linux). **It was not run**: no Windows machine was available, so nothing below comes from watching the program run. |

How to read this file: **Confirmed** means visible in the code, with the line given. **Hypothesis** means likely from the code but it needs a Windows run to prove. Line numbers refer to the baseline revision. The *Status* column is the state in this fork after the P0 milestone.

## 1. What upstream does

| Area | What it does | Where |
|---|---|---|
| Browse | Twitch's own *Following → Live* page in WebView2. An injected script turns plain clicks on channel links into "watch this channel" messages to the app | `liteview.cs` 252-281 |
| Watch | Streamlink (`streamlinkw.exe`, pinned 8.6.3) resolves the stream and starts mpv (pinned 0.42.0) embedded in the window (`--wid`), with low latency, HEVC/AV1 allowed | `liteview.cs` 368-405 |
| Process control | Streamlink starts suspended, at Below Normal priority, pinned off the V-Cache die on X3D CPUs, inside a kill-on-close job object | `liteview.cs` 386-399, `common.cs` |
| mpv control | JSON IPC over a named pipe: volume, mute, shaders, `vf`, observed properties, double-click/F/Esc from `input.conf` | `liteview.cs` 609-673 |
| Picture presets | Off / Enhance (FSR 1 + CAS + deband) / High (FSRCNNX 8 + Adaptive Sharpen) / Ultra (KrigBilateral, NLMeans, FSRCNNX 16, SSimDownscaler, strong sharpen, `display-resample` + interpolation), RTX VSR via `d3d11vpp` | `liteview.cs` 555-597 |
| Native chat | Twitch IRC over WebSocket, IRCv3 tags, Twitch/7TV/BTTV/FFZ emotes (global + channel), animated GIF frames, zero-width 7TV, badges, colours, replies, mentions, sub/raid notices, deletions/timeouts, sending, Tab completion, ↑ history, paused scroll | `chat.cs` |
| Emote picker | Search, sections per source, recently used (file `liteview.emotes`) | `picker.cs`, `chat.cs` 764-853 |
| Followed column | Helix `streams/followed`, avatars, collapse, one-click switch, refresh each minute | `sidebar.cs` |
| Login | Device Code Grant Flow (public client, no secret) shown in an embedded WebView2; DPAPI-encrypted token; hourly validation; refresh with one-time refresh tokens; revoke on log out | `helix.cs`, `liteview.cs` 472-511 |
| Web chat fallback | Twitch popout chat in WebView2, with 7TV copied from Chrome or downloaded from the Chrome Web Store | `liteview.cs` 283-353, 409-455 |
| GameFirst.exe | Separate background helper: every 4 s pins listed processes (default includes Chrome) off the V-Cache die, lowers CPU priority, lowers GPU priority of Chrome's GPU process and mpv | `guard.cs`, `config/gamefirst.ini` |
| Build | `build.ps1` downloads pinned Streamlink/mpv (SHA-256 checked) and the WebView2 NuGet package (Authenticode checked), compiles with the C# 5 `csc.exe` of .NET Framework, zips | `build.ps1` |

### Well implemented (kept)

- **Process ownership**: suspended start + job object with `KILL_ON_JOB_CLOSE` means Streamlink and mpv cannot outlive LiteView, even on a crash. The job struct layout was checked against `winnt.h` and is right for x64.
- **Pinned, hashed dependencies** in `build.ps1`, and Microsoft-signature checks on the WebView2 DLLs.
- **No browser while watching with native chat**: the WebView2 environment is disposed (`DropWeb`), a large real saving.
- **Chat rendering design**: messages are drawn once into cached bitmaps, only on-screen animated emotes animate, off-screen caches are dropped, message history is capped (300, or 2000 while scrolled), the text-width cache is capped (20 000).
- **Token handling basics**: device flow with a public client (no secret in the binary), DPAPI user-scope encryption, hourly validation as Twitch asks, careful single-flight refresh for one-time refresh tokens, revoke on log out.
- **Twitch emote positions** are counted in code points (surrogate pairs handled).
- **mpv buffer cap** (`demuxer-max-bytes=32MiB`).
- **CPU topology detection** by L3 size (does not guess from CPU names).

## 2. Findings

Severity: **High**: exploitable or likely to hurt users. **Medium**: real defect with limited reach. **Low**: defence in depth, rare, or small. **Info**: noted, no defect.

### 2.1 Security

| ID | Sev. | Finding | Evidence (baseline) | Fix in this fork | Tests | Status |
|---|---|---|---|---|---|---|
| SEC-1 | High | **Browser extension code from outside the app is loaded automatically.** 7TV is copied from the user's Chrome profile, or downloaded from the Chrome Web Store when Chrome lacks it. The CRX header (which holds the signature) is skipped without verification, then the zip is unpacked and loaded into the WebView2 that holds the user's Twitch session. | `liteview.cs` 283-346 (`SyncExtensions`, `FromWebStore`: `start = ... header length`, no signature check) | Removed. `AreBrowserExtensionsEnabled = false`. The native chat already shows 7TV/BTTV/FFZ emotes. | n/a (code removed) | Fixed |
| SEC-2 | High | **Web chat opens any URI through the shell.** `NewWindowRequested → Process.Start(e.Uri)` runs whatever the page asks for: `file:` paths, programs, any registered protocol handler (`ms-settings:`, `search-ms:`, ...). | `liteview.cs` 426 | All links go through `Launcher` → `SafeUrl.ForBrowser`: absolute `http`/`https`, host required, no user-info. | `SafeUrlTests.BrowserLaunchOnlyForWebUrls` | Fixed |
| SEC-3 | Medium | **Chat words reach `ShellExecute` unvalidated.** A word matching the URL regex that starts with `http` but not `http://` (e.g. `http.com`, `https.co`) is passed as is: the shell looks it up as a *file* (`.com` is an executable extension). Others get `https://` prepended. | `chat.cs` 521, regex 574 | `SafeUrl.FromChatWord` builds a real `https://` URL or nothing; the tooltip now shows the destination host. | `SafeUrlTests.ChatLinksBecomeWebUrls`, `ChatWordsThatMustNotOpen` | Fixed |
| SEC-4 | Medium | **No navigation policy** in the Following-page WebView2: `NewWindowRequested` navigates the app's browser to any URI (including `file:`); no `NavigationStarting` check. | `liteview.cs` 264 | `Harden()`: main-frame navigation allowed only to `https://*.twitch.tv` and `about:blank`; other web links open in the system browser when the user clicked them; everything else is blocked. | `SafeUrlTests.WebViewNavigationPolicy` | Fixed |
| SEC-5 | Medium | **Unauthenticated web-to-app messages.** Any script on any page loaded in the WebView2 can `postMessage("somechannel")` and the app starts that stream; the origin is not checked and the format is a bare string. | `liteview.cs` 265 | `WebMessage.Watch`: only `https://*.twitch.tv`, only `watch:<per-run secret>:<login>`, the login validated. The secret lives in the injected script's closure, so other page scripts cannot forge it. | `WebMessageTests` (14 cases) | Fixed |
| SEC-6 | Medium | **Login inside an app-hosted browser.** Twitch's real activation page, but in an embedded WebView2 (credentials typed into a window the app controls). The spec asks for the system browser. | `liteview.cs` 474-511 | Activation page (validated to be `https://*.twitch.tv`) opens in the default browser; a native dialog shows the code and the scopes. | `SafeUrlTests.ActivationPageMustBeTwitch` | Fixed (the Following page itself still uses Twitch's web login until P2) |
| SEC-7 | Medium | **Unbounded network reads.** `HttpClient` keeps the 2 GB default buffer; `JavaScriptSerializer.MaxJsonLength = int.MaxValue`; `GetByteArrayAsync` for images and avatars without limits. | `helix.cs` 22-23, 72; `chat.cs` 102; `sidebar.cs` 182 | 16 MB cap on JSON, 4 MB on images, 1 MB on avatars; `Content-Length` checked first, then a bounded stream read. | `BoundedTests` | Fixed |
| SEC-8 | Medium | **Image decoding without limits.** No limit on source dimensions; `Fit` caps height only, so a 10 000×32 emote gets 10 000-px frames, up to 400 of them (~512 MB). GDI+ parses every format it knows (EMF, WMF, TIFF, ICO, BMP...) from network data. | `chat.cs` 88-134 | `ImageSniff`: PNG/GIF/JPEG only, size read from the header and checked before GDI+ sees the bytes; `EmoteLimits`: width capped at 5× height, frames capped by count (400) and decoded bytes (8 MB/emote) with even thinning that keeps the animation's speed. | `ImageSniffTests`, `EmoteLimitTests` | Fixed |
| SEC-9 | Low | Emote/badge/avatar URLs from third-party APIs are fetched from any host (address leak, requests to LAN addresses). | `chat.cs` 720, 737, 757 | Allow-list: `*.jtvnw.net`, `*.7tv.app`, `*.betterttv.net`, `*.frankerfacez.com`, https only. API hosts allow-listed too. | `SafeUrlTests.ImagesOnlyFromKnownCdns`, `ApisOnlyFromKnownHosts` | Fixed |
| SEC-10 | Low | IRC `room-id` tag inserted into Helix and emote-API URLs unchecked (`1/../../x` would reach other endpoints). | `chat.cs` 880, 677-687 | Must be digits. | `ChannelNameTests.Ids` | Fixed |
| SEC-11 | Low | Outgoing chat text not cleaned of CR/LF/control characters (an injected second IRC command). The single-line TextBox makes it hard to type, so defence in depth. | `chat.cs` 1021-1023 | `IrcProtocol.CleanOutgoing`; `Irc.Send` also refuses raw lines containing CR/LF. | `IrcProtocolTests.OutgoingTextCannotInjectCommands` | Fixed |
| SEC-12 | Low | One WebSocket message is buffered without limit (`MemoryStream`). | `chat.cs` 193-201 | `FrameBuffer`, 1 MB, then reconnect. | `IrcProtocolTests.FrameBuffer*` | Fixed |
| SEC-13 | Low | mpv's IPC pipe name is predictable (`liteview-mpv-<pid>`): another local process could create it first. mpv's IPC accepts `run` (start a program). Same-user processes can already run code, so the gain is small. | `liteview.cs` 42 | A random name per stream (`StreamCommand.NewPipeName`). | `WinArgsTests.PipeNamesAreUniqueAndSafe` | Fixed. mpv 0.42.0's pipe (`input/ipc-win.c`) is owned by the user, grants read/write to that user only, blocks lower integrity levels and remote clients, so only same-user processes could ever squat it |
| SEC-14 | Low | **GameFirst changes unrelated apps by default**: the shipped `apps=` includes Chrome (affinity, priority, GPU priority, and reading Chrome processes' command lines). | `config/gamefirst.ini`, `guard.cs` 36 | Default is LiteView's own processes; other apps only when listed (documented in the ini). | none (config) | Fixed |
| SEC-15 | Info | DPAPI user-scope token without extra entropy: any process of the same user can decrypt it. Inherent to per-user storage (Credential Manager has the same property). The write was not atomic. | `helix.cs` 44-52 | Atomic write; logout deletes it (and any `.bak`). Entropy not added (it would live in the exe). | `StorageTests.AtomicWriteAndDelete` | Accepted risk |
| SEC-16 | Info | The fork uses **upstream's Twitch client id**, which belongs to upstream's dev.twitch.tv application. | `helix.cs` 20 | Comment added; needs its own registration before a public release. | n/a | **Open (owner decision)** |
| SEC-17 | Low | Logging out of chat does not clear the WebView2 profile, so the Twitch *website* stays signed in inside LiteView. | `helix.cs` 54-59 | - | - | Open (P1) |
| SEC-18 | Low | After log out, `Tw.Granted` keeps the old scopes (`Has()` stays true). | `helix.cs` 57 | Cleared on log out and on failed validation. | none (static state) | Fixed |
| SEC-19 | Info | Command line built with `string.Format` and hand quotes; safe only because the channel is reduced to `[a-z0-9_]` and Windows paths cannot contain `"`. | `liteview.cs` 383-391 | `WinArgs.Quote` (CommandLineToArgvW rules) + channel/pipe validation; the builder refuses bad input. | `WinArgsTests` (round trip through an argv parser) | Fixed |
| SEC-20 | Info | `/oauth2/validate` answer not checked against the client id; login/user id not validated. | `helix.cs` 102 | Checked. | none | Fixed |
| SEC-21 | Low | WebView2 dev tools on, general autofill on, permission requests left to the default prompt, downloads allowed. | `liteview.cs` 261-263, 423-426 | Dev tools off (`LITEVIEW_DEVTOOLS=1` turns them on), autofill and password saving off, every permission denied, downloads cancelled, host objects off, web messages only where used. | none (needs Windows) | Fixed, **unverified on Windows** |

### 2.2 Reliability

| ID | Sev. | Finding | Evidence | Fix | Tests | Status |
|---|---|---|---|---|---|---|
| REL-1 | Medium | **Any chatter can force every LiteView viewer to reconnect**: a line *ending* in ` RECONNECT` is treated as Twitch's command, so `PRIVMSG #c :type RECONNECT` drops and rejoins chat. | `chat.cs` 205 | Only the parsed command `RECONNECT`. | `IrcProtocolTests.ReconnectOnlyForTheCommand` | Fixed |
| REL-2 | Medium | "Playing" means "an `mpv.exe` exists anywhere on the PC". With the user's own mpv open, the status is wrong. | `liteview.cs` 525 | Playing = our mpv connected on this stream's pipe. | none (needs Windows) | Fixed, unverified |
| REL-3 | Low | Exit detection by PID (`GetProcessById(slPid)` every 500 ms): a reused PID looks alive. Also allocates a `Process` per tick. | `liteview.cs` 524 | A `Process` with its handle opened while the child is still suspended. | none | Fixed, unverified |
| REL-4 | Medium | No playback recovery: a network drop, a Twitch error or the stream ending leaves a message and nothing else. | `liteview.cs` 518-535 | `StreamOutcome.Classify` (offline / failed / ended / fatal), then retry: offline every 60 s, errors with backoff 2 s→60 s ±50 % jitter, at most 8 quick retries, then "click to try again". | `StreamOutcomeTests`, `BackoffTests` | Fixed, unverified end to end |
| REL-5 | Low | Chat reconnect delay resets as soon as a socket connects, so a server that accepts and drops gives a reconnect every ~1 s forever. | `chat.cs` 192 | `Backoff`: reset only after 60 s connected. | `BackoffTests.ResetsOnlyAfterAHealthyConnection` | Fixed |
| REL-6 | Low | Channel names from URLs: `twitch.tv/xqc/videos` → `xqcvideos`, `?referrer=raid` → `xqcreferrerraid`. | `liteview.cs` 370 | `ChannelName.Normalize` (first path segment, popout/embed forms). | `ChannelNameTests` (20 cases) | Fixed |
| REL-7 | Low | **Settings written mid-load**: setting `picture.SelectedIndex` in `LoadSettings` fires `SaveSettings` before the window bounds and chat mode are loaded, so the file briefly holds defaults; a crash then loses them. Writes were not atomic. | `liteview.cs` 680-716 (`picture.SelectedIndexChanged → SaveSettings`) | No saves while loading; atomic write with `liteview.ini.bak` as last-known-good; a torn or empty file falls back to it. | `StorageTests` | Fixed |
| REL-8 | Low | One pipe name per LiteView process. mpv 0.42.0 creates its pipe with `FILE_FLAG_FIRST_PIPE_INSTANCE` (confirmed in `input/ipc-win.c`), so if the previous mpv's pipe still exists when the next mpv starts, the new one gets no IPC: no volume, fullscreen or shader control. Whether quick switching hits that timing is **unverified**. | `liteview.cs` 42, 626 | Unique pipe name per stream. | - | Fixed |
| REL-9 | Low | Pipe callbacks from the previous stream can still change the current one (`videoH`, mute...). | `liteview.cs` 635-663 | Generation id on every callback. | - | Fixed |
| REL-10 | Low | GameFirst crashes on a typo in `interval` (`int.Parse`) or `affinity` (`Convert.ToUInt64`). | `guard.cs` 31, 34 | `TryParse` with defaults, logged. | `StorageTests.ParseAndGetters` | Fixed |
| REL-11 | Low | `TryGetWebMessageAsString` throws for non-string messages inside an event handler. | `liteview.cs` 265 | Caught. | - | Fixed |
| REL-12 | Low | Missing `twitch\streamlink` makes `Directory.GetFiles` throw in the constructor; a missing `streamlinkw.exe` gives `CreateProcessW(null, "\"\" ...")`. | `liteview.cs` 79, 392 | Caught; a clear message names the missing program. | - | Fixed |
| REL-13 | Low | No handling of unhandled exceptions: crashes leave no trace. | `liteview.cs` 731-742 | Logged; a UI exception shows a short message (at most every 30 s) and the app continues. | - | Fixed |
| REL-14 | Low | Helix 429 (rate limit) is treated as a failure. | `helix.cs` 128-147 | Waits for `Ratelimit-Reset` (≤10 s) once. | - | Fixed |
| REL-15 | Low | CPU topology ignores processor groups: on CPUs with more than 64 threads a group-1 mask would be applied to group 0. | `common.cs` 200-222 | No pinning when any L3 is outside group 0, or the result has fewer than 2 threads. | `TopologyTests` | Fixed |

### 2.3 Resources (CPU, memory, wake-ups)

| ID | Sev. | Finding | Evidence | Fix | Tests | Status |
|---|---|---|---|---|---|---|
| RES-1 | Medium | **Decoded emote frames are never freed**: every emote of every channel visited stays in `Images.byUrl` with all frames for the life of the process. Long sessions with channel switching grow without bound. | `chat.cs` 68-135 | 128 MB budget of decoded frames, least recently drawn freed first, nothing drawn in the last 10 s freed; freed emotes reload when needed. The `Emote` records themselves (~100 bytes each) are still kept. | `ByteBudgetTests` | Fixed (frames); metadata Open (Low) |
| RES-2 | Low | The mpv pipe thread tries to connect every second for the whole run, throwing a timeout exception each time, even on the Following page. | `liteview.cs` 620-673 | Waits on an event while no stream runs. | - | Fixed |
| RES-3 | Low | Followed channels refresh every minute even while the window is minimized. | `sidebar.cs` 132 | Skipped while minimized. | - | Fixed |
| RES-4 | Low | `chatters` and learned `twitchEmotes` grow for the whole time in one channel. | `chat.cs` 558-559 | Cleared past 20 000 entries. | - | Fixed |
| RES-5 | Info | Followed-channel avatars are never evicted (bounded by the number of follows). | `sidebar.cs` 119 | - | - | Open (Info) |
| RES-6 | Info | LiteView's own UI thread runs at Below Normal priority; under full CPU load from a game the UI may lag. A design choice ("game first"). | `liteview.cs` 738 | - | - | Open: make it a setting (P4) |
| RES-7 | Hypothesis | Image decode and avatar rounding happen on worker threads (good), but `Images.Loaded` invalidates and re-lays out *every* message using the emote; with thousands of messages scrolled back this is O(n) per image. | `chat.cs` 323-330 | - | - | Open: measure with the chat stress test (P1) |

### 2.4 Fragile external behaviour

| ID | Finding | Where | Status |
|---|---|---|---|
| EXT-1 | The Following page depends on Twitch's website markup and URL patterns (the click script intercepts `<a href>` with one path segment). A Twitch redesign can break browsing silently. | `liteview.cs` 267-274 | Open, replaced by native browsing in P2 |
| EXT-2 | Web chat uses Twitch's popout URL `popout/<ch>/chat?popout=&darkpopout` (not a documented API). | `liteview.cs` 416 | Open |
| EXT-3 | BetterTTV zero-width emotes are a hard-coded name list. | `chat.cs` 727 | Open |
| EXT-4 | 7TV API v3 (`7tv.io/v3`). Hypothesis: 7TV may retire v3 in favour of its v4 API. | `chat.cs` 662, 679 | Open, watch |
| EXT-5 | Stream status comes from parsing Streamlink's log text (`error:` lines). Wording changes in Streamlink would change the messages; the classifier's tests pin today's strings. | `liteview.cs` 529; now `StreamOutcome` | Open (inherent) |
| EXT-6 | "NVIDIA present" = `nvapi64.dll` exists. Also true for non-RTX NVIDIA cards, where the RTX box does nothing. | `liteview.cs` 58 | Open |
| EXT-7 | Anonymous chat uses Twitch's documented `justinfan` convention; IRC itself is supported by Twitch for chat clients. | `chat.cs` 189-190 | OK |

### 2.5 UI, DPI, layout

| ID | Sev. | Finding | Status |
|---|---|---|---|
| UI-1 | Medium | Not DPI aware: fixed pixel sizes everywhere (`BarH = 40`, `ChatW = 340`, row heights, fonts in points but bounds in pixels). At 125-200 % Windows stretches the window (blurry). The README itself says "best at 100 %". Declaring DPI awareness without scaling the layout would make it tiny, so it is **not** declared yet. | Open (P2) |
| UI-2 | Low | Fullscreen uses the screen the window is on at that moment; moving monitors or changing resolution while fullscreen is not handled. | Open (P3) |
| UI-3 | Low | Chat width is fixed (340 px) except when maximized. | Open (P4) |
| UI-4 | Medium | The custom-drawn controls (followed channels, chat, emote picker) expose nothing to screen readers and have little keyboard navigation. | Open (P5) |
| UI-5 | Low | The status line is the only error surface; long Streamlink errors are cut off by the bar. | Open (P1) |

### 2.6 Duplication and dependencies

| ID | Finding | Status |
|---|---|---|
| DUP-1 | Two `CreateProcessW` P/Invokes (`LiteView`, `Native`), three `SendMessage` P/Invokes, two dark combo set-ups. | Open (cleanup when touched) |
| DUP-2 | Colours and fonts repeated in every control class (`Bg`, `Fg`, `Dim`, `Accent`...): no theme module. | Open (P4 theming) |
| DEP-1 | `System.Web.Extensions` (`JavaScriptSerializer`) for JSON: a .NET Framework-only, unmaintained API. Bounded by the new size caps; fine while the app targets .NET Framework. | Open (Info) |
| DEP-2 | Build compiled with the C# 5 compiler in Windows (`csc.exe`); no tests, no CI, no lock files. | Fixed: SDK projects, tests, CI, lock files |

### 2.7 Tests, diagnostics, feedback

| ID | Finding | Status |
|---|---|---|
| DIAG-1 | No automated tests at all. | 175 unit tests for the extracted logic (`tests/`). UI and integration tests: Open (P1) |
| DIAG-2 | No application log; only `streamlink.log`. | `liteview.log`, rotating, redacted |
| DIAG-3 | No diagnostics export. | Chat gear menu → *Export diagnostics...* |
| DIAG-4 | Many `catch { }` blocks hide failures (token load, settings save, emote loads, chat). | The important ones now log; the rest are deliberate (logging must not break drawing) |

## 3. Baseline build

| Step | Result |
|---|---|
| Upstream `build.ps1` (csc.exe) | Not run (needs Windows) |
| Same sources, SDK build (`net48`, reference assemblies) on Linux | **Compiles**, 0 warnings, before any change (commit `86c1923`) |
| Running the app | **Not done.** Needs a Windows 10/11 PC; see `docs/TESTING.md` for the manual pass that has to happen there |

## 4. Not audited

- The shader files' internals (taken as the published shaders they credit) and whether the chosen chains are optimal: P3.
- mpv's and Streamlink's own code, and mpv's IPC pipe permissions.
- Real-world performance (no Windows hardware here): see `docs/PERFORMANCE_BASELINE.md`.
