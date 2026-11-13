# Testing

## Automated

```powershell
dotnet test LiteView.sln -c Release
```

228 unit tests (xUnit, `tests/LiteView.Core.Tests`) cover the pure logic in `src/core`. They run on any OS and need
no network, credentials or Twitch account.

| Area | Test class | What it checks |
|---|---|---|
| IRC parsing | `IrcMsgTests` | Tags, escapes, prefix, command, channel, text; malformed and over-long lines rejected |
| IRC protocol | `IrcProtocolTests` | PING/PONG; only the real RECONNECT command; outgoing text cannot inject lines; 500-char cap without splitting surrogates; display cap; bounded frame assembly and UTF-8 across fragments |
| Channel names | `ChannelNameTests` | Typed names, `@name`, URLs with paths/queries/popout; rejects reserved paths, bad characters, too long; numeric ids |
| URL policy | `SafeUrlTests` | Chat words → web URLs only; browser launch only for http(s); image and API host allow-lists; WebView2 navigation policy; activation page must be Twitch |
| Page messages | `WebMessageTests` | Origin, secret and login checks; random secrets |
| Image headers | `ImageSniffTests` | PNG/GIF/JPEG sizes; oversized, unknown and truncated inputs refused |
| Emote limits | `EmoteLimitTests` | Drawn size rule, width cap, source limits, frame budget, frame thinning keeps duration |
| LRU budget | `ByteBudgetTests` | Eviction order, in-use entries kept, size updates |
| Storage | `StorageTests` | Atomic writes, `.bak` last-known-good, torn-file fallback, values cannot add lines, delete removes `.bak` |
| Logging | `RedactTests`, `LogTests`, `DiagnosticsTests` | Credentials and channels removed; rotation and retention; tail |
| Process command | `WinArgsTests` | Quoting round-trips through a CommandLineToArgvW-rule parser; full Streamlink command; bad channels/pipe names refused |
| Stream outcome, quality, backoff | `StreamOutcomeTests`, `QualityTests`, `BackoffTests` | Offline/failed/ended/fatal from Streamlink errors; selectors; growth, cap, jitter range, healthy reset |
| CPU topology | `TopologyTests` | X3D dual-die, equal dies (9900X: no pinning), multi-group, buffer parsing |
| JSON | `JsonTests` | Value shapes the app reads, numbers, escapes and surrogates, malformed input, depth limit |
| Twitch login and API (fake Twitch) | `TwitchClientTests` | Refresh and save; rejected vs unreachable refresh; log out during a refresh; a stale validation after a new login; two expired requests refresh once (one-time refresh tokens); token of another app; offline validation keeps the session; 429 wait; oversized answers; unknown hosts never contacted; stalled download ends at the deadline; device flow; log out revokes |
| Chat connection (fake server) | `IrcClientTests` | Anonymous and logged-in handshake; PING/PONG; RECONNECT uses the current token; chat text ending in RECONNECT is text; oversized message reconnects; backoff grows and resets after a healthy connection; Stop; one-line Send; bad channel |

CI (`.github/workflows/ci.yml`) runs the build with warnings as errors, the tests, a secret scan, a vulnerable
package check and the full `build.ps1` packaging on Windows. It then starts the packaged app on the Windows runner
(`tools/smoke.ps1`, on a copy of the folder): the main window must appear and stay up for 30 s, `liteview.log` must
have no errors, and the window must close normally with nothing left running. The run's `smoke-<commit>` artifact
has a picture of the window, the whole screen, the logs and Windows' crash events. The runner is not logged in to
Twitch and has no GPU, so this covers starting and closing, not playback. `tools\smoke.ps1 -Dir <built folder>`
does the same on your own PC without touching that folder.

The race tests (log out during a refresh, stale validation) and the RECONNECT/backoff tests were checked to fail
when the fix they guard is removed.

CI also starts the packaged app on the Windows runner in several scenes with made-up data (see *UI fixture mode*),
including the whole watch path with `tests/FakeStreamlink` standing in for Streamlink (the real mpv plays a generated
video; offline and failing streams too), and reads the Following page, followed column and chat through UI
Automation and MSAA (`tools/a11y-dump.ps1`): what Narrator and NVDA would read.

**Not automated yet**: real Twitch playback, the chat stress test over long runs, long-run leak tests, display
scaling.

## Manual pass on Windows (required for the P0 milestone)

Build: `powershell -ExecutionPolicy Bypass -File build.ps1`, then run `dist\LiteView-Enhanced-0.1.0\LiteView.exe`.
Keep a copy of your current `liteview.ini` / `liteview.token` if you run it in an existing LiteView folder.

| # | Check | Expected |
|---|---|---|
| 1 | Start; Following page loads; log in to the Twitch website there | Works as upstream; `liteview.log` appears |
| 2 | Click a live channel on the page | Stream + chat open (the click goes through the new `watch:` message) |
| 3 | On the page, click a link to a non-Twitch site (e.g. a panel link) | Opens in your default browser, not inside LiteView |
| 4 | *Log in with Twitch* (followed column or chat) | Your browser opens Twitch's activation page; the LiteView dialog shows the same code and the scopes; after Activate + Authorize the dialog closes and the followed column fills |
| 5 | Chat: post a link, e.g. `example.com`; hover, then click it | Tooltip shows the host; it opens in the browser |
| 6 | Chat: from another account, send `test RECONNECT` | Chat does **not** reconnect (upstream did) |
| 7 | Send messages, Tab completion, ↑ history, emote picker | As upstream |
| 8 | Busy 7TV channel for 30 minutes, switching channels a few times | No missing emotes that stay missing; *Export diagnostics* → "Emote image cache" stays ≤ 128 MB decoded |
| 9 | Switch channels quickly 10 times | Each stream plays; volume/mute/fullscreen work on the last one; no stray `mpv.exe`/`streamlinkw.exe` in Task Manager afterwards |
| 10 | Unplug the network for 20 s while watching, then reconnect | Status shows an error and "trying again in N s"; playback returns without clicking; you stay logged in |
| 11 | Open an offline channel | Within a few seconds: "Offline - it starts by itself when the channel goes live"; it does start when the channel goes live. Send `streamlink.log` from this test: it confirms the log format the status relies on |
| 12 | Rename `twitch\mpv\mpv.exe`, open a stream | "mpv is missing..." message; rename it back |
| 13 | Quality: each choice including *Audio only* | Plays; audio only shows a black video area |
| 14 | Picture presets Off/Enhance/High/Ultra, RTX on/off | As upstream |
| 15 | Web chat (gear → Twitch web chat) | Loads Twitch's chat **without** 7TV; links open in the browser |
| 16 | Gear → *Export diagnostics...* | Explorer opens with the file selected; the file has no token, no `oauth:`, no channel names |
| 17 | Close LiteView while watching | No `mpv.exe` / `streamlinkw.exe` left; `liteview.ini` and `liteview.ini.bak` present; reopening restores window position |
| 18 | GameFirst.exe with the shipped ini | `gamefirst.log` lists only mpv, streamlink, streamlinkw; Chrome is untouched |
| 19 | Display scaling 100 % and 150 % | Same as upstream (DPI support is still open, UI-1) |
| 20 | Gear → *Sign out of twitch.tv in LiteView*, then ◀ Following | The Following page shows Twitch logged out; chat login unchanged |
| 21 | Sleep the PC for a few minutes while watching, wake it | Chat and the stream come back by themselves (at the live edge) |
| 22 | `LiteView.exe <busy channel> --chat-stress=200` for 15 minutes | `stress:` lines in `liteview.log`; memory and GDI objects level off (send the log) |

### P2 branch (native Following page and the rest)

| # | Check | Expected |
|---|---|---|
| 23 | Start while logged in (*Log in with Twitch*) | LiteView's own Following page: live channels as cards with previews, avatars, uptime and viewers |
| 24 | Click a card; ◀ Following | It plays; back on the page the scroll position is kept and *Recently watched* lists it |
| 25 | Sort menu; right-click a channel > Add to favourites | Order changes; the favourite moves first with a ★ |
| 26 | *All followed* tab | Every followed channel, live ones first, offline ones "Following since ..." |
| 27 | Type in *Go to channel...* (e.g. part of a name) | Results after a short pause, live ones first; Enter still opens what you typed; click the empty box later: recent searches |
| 28 | Arrow keys / Enter / F5 / Ctrl+F on the page | Selection moves, Enter watches, F5 refreshes, Ctrl+F focuses the search box |
| 29 | Menu > Following page > Twitch website, then back | The website page appears; switching back closes the browser (no `msedgewebview2.exe` while browsing natively) |
| 30 | Minimise for 5 minutes on the Following page | No requests while minimised (no `following page` errors); on restore it refreshes |
| 31 | Open an offline channel, wait for the error, click the ⚠ status | Details with the Streamlink lines; *Try again* restarts; *Open log folder* works |
| 32 | Fullscreen, then Win+Shift+Right (two monitors); Esc | It fills the other monitor; leaving fullscreen restores the window |
| 33 | RTX box | Shows on an RTX card (your 5080); diagnostics say "NVIDIA RTX card: true" |
| 34 | Keyboard shortcuts (menu > Keyboard shortcuts) while watching | Each one works; typing in chat is not affected |
| 35 | Favourite a channel, menu > Notify me when a favourite goes live; wait for it to go live (or favourite one that is about to) | A Windows notification; clicking it opens the channel; none during quiet hours |
| 36 | Settings > Export, change something, Import the file | LiteView restarts with the exported settings; the login is kept |
| 37 | Chat gear: text sizes, timestamps, a highlight word, an ignored user; drag the chat's left edge | Each applies at once; the width is kept after a restart |
| 38 | Performance > Gaming mode > Automatic; start a fullscreen game with a stream on the other monitor | After about 10 s the status says Gaming mode and the picture is lighter; 15 s after leaving the game it is back |
| 39 | Performance > CPU use > Windows decides (restart) | `liteview.log` "window ready; CPU Windows decides"; Task Manager: LiteView, Streamlink and mpv at Normal priority |
| 40 | Start GameFirst.exe, then `GameFirst.exe --stop` | `gamefirst.log`: "guard stopped (--stop): settings of N process(es) put back" |
| 41 | Quality box after a stream starts | Lists that stream's qualities (e.g. 1080p60, 720p60, 480p, 360p, 160p, Audio only) |
| 42 | Narrator (Win+Ctrl+Enter) on the Following page; arrow keys | Each channel is read: name, live, viewers, game, title |

Report results (pass/fail, with `liteview.log` and the diagnostics file for failures) in `KNOWN_ISSUES.md` or as issues.

## UI fixture mode (screenshots without a Twitch account)

`LITEVIEW_UI_FIXTURE=1` makes LiteView answer its own Twitch requests with made-up channels and generated pictures
(`src/app/Fixture.cs`): no network for the Twitch API, no real login (the token file is not read or written), no
Streamlink, anonymous chat, and the title says *UI fixture*. `LITEVIEW_UI_FIXTURE_SCENE` picks the screen: `live`,
`followed`, `recent`, `history`, `search:<text>`, `problem`, `chat`, and with `LITEVIEW_UI_FIXTURE_STREAMS=1`
plus `smoke.ps1 -FakeStreamlink` also `watch`, `offline`, `fail`. CI runs every scene with `tools/smoke.ps1` and
uploads the screenshots (`smoke-<commit>` artifact), so UI changes can be looked at without a Windows PC:

```powershell
$env:LITEVIEW_UI_FIXTURE = '1'; $env:LITEVIEW_UI_FIXTURE_SCENE = 'followed'
tools\smoke.ps1 -Dir dist\LiteView-Enhanced-0.1.0 -Out smoke\followed
```

## Chat stress mode (Windows)

```powershell
dist\LiteView-Enhanced-0.1.0\LiteView.exe <a channel with many 7TV emotes> --chat-stress=200
```

While you watch with the native chat, LiteView adds 200 synthetic messages per second (the channel's own emotes,
long messages, Unicode, links, replies, deletions, timeouts, sub notices) on top of the real chat. The title bar says
*chat stress*. Every 10 seconds `liteview.log` gets a `stress:` line: messages per second, working set, private
memory, threads, handles, GDI/USER objects, CPU seconds and the emote cache. Run it for 10-30 minutes, scroll the
chat up and down now and then, and keep the log. Healthy: memory and GDI objects level off; the chat stays smooth;
the emote cache stays within its budget. The generator is `src/core/ChatStress.cs` (tested in `ChatStressTests`).

## Test rules

- Never use real credentials in automated tests.
- A feature is not "done" because it compiles; it needs its tests and the Windows check above.
- Do not report a test as passing that was not run. Record what was run where (this file, CI logs).
