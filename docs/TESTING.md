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
package check and the full `build.ps1` packaging on Windows.

The race tests (log out during a refresh, stale validation) and the RECONNECT/backoff tests were checked to fail
when the fix they guard is removed.

**Not automated yet** (P1): fake Streamlink/mpv processes (process start, job object and pipe code are Win32 and
need a Windows runner), UI tests, the chat stress test, long-run leak tests.

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
| 19 | Display scaling 100 % and 150 % | Same as upstream (DPI support is P2, not part of this milestone) |
| 20 | Gear → *Sign out of twitch.tv in LiteView*, then ◀ Following | The Following page shows Twitch logged out; chat login unchanged |
| 21 | Sleep the PC for a few minutes while watching, wake it | Chat and the stream come back by themselves (at the live edge) |
| 22 | `LiteView.exe <busy channel> --chat-stress=200` for 15 minutes | `stress:` lines in `liteview.log`; memory and GDI objects level off (send the log) |

Report results (pass/fail, with `liteview.log` and the diagnostics file for failures) in `KNOWN_ISSUES.md` or as issues.

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
