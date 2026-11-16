# Testing

## Automated

```powershell
dotnet test LiteView.sln -c Release
```

421 unit tests (xUnit, `tests/LiteView.Core.Tests`) cover the pure logic in `src/core`. They run on any OS and need
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
| Random input | `FuzzTests` | Seeded random and mutated input for every parser of outside data (IRC lines, JSON incl. 200 000 levels deep, the chat history answer, image headers, links, channel names, Helix answers, Streamlink's log, settings files, chat preferences): nothing throws, and the results keep their promises (no line breaks in IRC text, only allowed hosts and schemes, valid logins, bounded sizes, no half characters) |
| Browse tab | `CategoryTests` | Categories need a numeric id and a name, box art only from allowed hosts; only a numeric category and a known language code reach the request |
| Emoji | `EmojiTests` | Emoji sequences (skin tones, joined families, flags, keycaps, tag flags, FE0F) found as one; text symbols (©, ☺) without FE0F stay text; random text is always covered exactly; long joined runs capped |
| Fonts | `FontCoverageTests` | Inter's character map read from the font file (Latin, Greek, Cyrillic yes; Chinese, Arabic, emoji no); damaged font files give null, never an exception |
| Videos and clips | `VideoTests` | Videos need a numeric id and a valid channel, clips a slug of letters, digits, - and _ (the only values that reach the command line); durations, lengths, ages, the status line; the channel id is asked once; unreachable vs none |
| Stats overlay, presets | `PlaybackStatsTests`, `GpuTimeTests`, `PicturePresetTests` | mpv's answers parsed field by field and clamped; nothing in them can break out of the overlay's text or JSON; GPU time from vo-passes; every preset sharpens once at most, Ultra like High; every shader file exists |
| Chat history | `RecentMessagesTests` | Only chat lines of the channel asked for, no CR/LF/NUL, at most 50; the one-word form without ":" |
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
video; offline and failing streams too), and reads the Following page, the Browse tab, the followed column, the chat
and the emote picker through UI Automation and MSAA (`tools/a11y-dump.ps1`): what Narrator and NVDA would read.

A last CI step runs the chat at 300 messages a second for two minutes (fixture scene `stress`, with made-up 7TV
emotes, the second minute scrolled back so 2000 messages are kept) and checks the `stress:` lines with
`tools/stress-check.ps1`: private memory under its budget, no growth of memory, handles or GDI/USER objects over the
last 30 s (four samples), the generator keeping its rate, emotes decoded and none refused, and the window's thread never waiting
longer than its budget (UI lag). The table goes to `smoke/fixture-stress/stress.txt` in the artifact. Another step
switches channel every 5 s for two minutes (scene `switching`, with video through FakeStreamlink and 50 chat messages a
second) and checks that handles, GDI/USER objects, threads and memory do not grow from the 3rd sample to the last,
that every switch kept the old stream until the new one played (seamless switching; FakeStreamlink waits 1.5 s like
a real Streamlink opening a stream) and that never more than two players ran at once. It prints how long the
streams took to their first frame. A last step plays a known test picture (text, a Siemens star, fine lines,
coloured edges, texture; 1024x576, streamed at 512x288 after a JPEG round trip) in fullscreen with each picture preset
and saves what mpv shows (`smoke/fixture-presets/preset-*.png` with `preset-truth.png`);
`python3 tools/preset-metrics.py smoke/fixture-presets` compares them (numbers in `PERFORMANCE_BASELINE.md`).

**Not automated yet**: real Twitch playback, chat stress beyond two minutes, hour-long leak tests, display scaling.

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
| 43 | Start LiteView (Following page) | Top right shows only the menu button (⋯), no white line or quality box; quality, Chat and the player buttons appear when a stream plays |
| 44 | Menu > Text size > Following page / Chat | Normal is a bit larger than before; each size applies at once and is kept after a restart |
| 45 | Pause button (left of the speaker), Space, or the keyboard's play/pause key while watching | The video stops ("Paused"); Task Manager: no Streamlink or mpv; the chat keeps going; Play or Space starts again at live |
| 46 | Open a busy channel | The last ~50 messages are in the chat above "Welcome to the chat room!" (no duplicates); chat gear > *Recent messages when joining* off: none |
| 47 | Following page > *Browse*; click a category; *Language*: one you watch; Backspace | The most watched categories with their box art; the category's live channels, most watched first; only that language; back to the categories at the same place |
| 48 | In the chat box Ctrl+E; type part of a name; Down; arrow keys; Enter; Esc | The picker opens with the search box; Down moves into the grid (purple frame); Enter inserts the emote and the picker stays; Esc closes it and the chat box has the keyboard. Space, M and C do not pause, mute or hide the chat while in the grid |
| 49 | Narrator in the emote picker grid (item 48) | Each emote is read with its set, e.g. "Pog, 7TV channel" |
| 50 | Chat gear > *Chat on the left*; drag the chat's right edge; restart | Chat between the followed column and the video, the video still fills its area; the width follows the drag; both kept after a restart; unticking puts it back on the right |
| 51 | Watch a live channel for a few minutes | Next to the channel name: viewers, uptime, category and title; the numbers update about once a minute; hovering shows the whole title |
| 52 | Right-click someone's chat message > *Reply to ...*; type; Enter | "Replying to @name: ..." above the chat box; the message shows as a reply on twitch.tv and in LiteView; Esc or ✕ cancels; switching channel, or the message being deleted, ends it; sub and raid notices have no Reply |
| 53 | Watch a channel, then click another in the followed column | The first one keeps playing (with sound) until the second shows its picture, then it switches at once; the second then has the sound. Task Manager: one mpv after the switch. `liteview.log`: "stream N: first frame after X s; the last stream played until then" |
| 54 | Press I while watching (or i with the video clicked); change Picture; press I again | The stats box at the top left of the video: quality, codec, "graphics card (d3d11va)", dropped frames, buffer, GPU per frame (higher for High and Ultra), CPU and memory; it updates every second and goes away |
| 55 | Picture: Ultra; press B; B again | B shows the original picture ("Original picture" on the video), B again your preset. Ultra should look cleaner than High but not sharper (menu > Playback > Sharpness: Soft if it is still too sharp) |
| 56 | Menu > Start and tray > Start with Windows, Start minimised, Minimise to the tray; sign out and in | LiteView starts minimised in the tray; clicking its icon opens it; minimising hides it in the tray again. Untick Start with Windows: it no longer starts |
| 57 | Chat and Following page text | Inter like on twitch.tv; names bold; Chinese or emoji words still draw. Menu > Text size > Font > Segoe UI switches back |
| 58 | A reply in a busy chat (someone answering someone) | "↳ Replying to @name: ..." above it in smaller grey text, at every chat text size; the reply's text does not start with "@name" |
| 59 | Right-click a channel > *Videos and clips*; click a past broadcast; Right arrow on the video a few times; Pause, then Play | The channel's videos with length, views and age; *Clips* lists its clips; the video plays (status: "Past broadcast · 2:31:00 · ..."); arrows seek; Play continues where it was; Esc or ‹ Back returns to where you were |
| 60 | Switch channel twice quickly (before the second one shows) | The first one keeps playing until the third shows; never two sounds at once; Task Manager: one mpv at the end. Mute while a switch is opening: it mutes what you hear |
| 61 | Following page: rest the mouse on a live card for a second; move to another; scroll | The card plays muted at low quality after about a second; moving or scrolling stops it at once; clicking the card opens the channel normally; Task Manager: no Streamlink/mpv left once the mouse is away. Menu > Playback > Previews on hover turns it off |
| 62 | Right-click a channel > *Watch in a new window* (twice, different channels); M and the wheel on one; close LiteView | Each plays in its own window titled with the channel; sound per window; the main stream is unaffected; closing LiteView closes them and leaves no Streamlink/mpv in Task Manager. An offline channel's window says so |
| 63 | A chat with emoji (😂 🔥 👍🏽 flags) | In colour, the size of the text, the line no taller than others; `liteview.log`: "emoji: colour (Segoe UI Emoji)"; copying a message keeps "gg🔥" together |
| 64 | Click in the chat box ("Send a message"), then click the video; press Space. Again with a click on the chat's messages or the followed column | Space pauses each time (the keyboard left the box). A click on the emote button keeps it in the box |
| 65 | Picture: Ultra for a minute; Picture: Enhance; watch Memory in the stats (I) | About 3 s after choosing Enhance the player restarts behind the picture (no black, no gap in the sound); the player's memory drops to Enhance's level. `liteview.log`: "picture: Enhance after Ultra: the player restarts behind the picture" |
| 66 | RTX on, a 1080p stream in the window (not fullscreen); then fullscreen; then a 720p stream | Stats (I), Picture row: "RTX waits (the video is shown at 1.05x ...)" when the video is shown below 1.3x, the picture as sharp as without RTX; fullscreen: "RTX enlarges 1.33x"; 720p: about 2x |
| 67 | While watching, right-click a channel in the followed column | A menu: Watch, Watch in a new window, Videos (past broadcasts), Clips, favourites, Open on twitch.tv, Copy link; the right-click itself does not switch |
| 68 | In an extra window (item 62): the volume bar; the chat button; the pin button; ⋯ > Quality > 480p; ⋯ > Keep the video's shape, then resize | The volume changes only that window (and the wheel on its video moves the bar); the chat opens beside the video (the window widens); unpinned it can go behind other windows, pinned it stays on top; it restarts at 480p; resizing keeps the video without black bars. A new extra window starts with these choices |
| 69 | Taskbar, title bar, tray | LiteView's own purple icon (a chat bubble with a play button); an mpv you start yourself keeps mpv's icon |
| 70 | Top bar while watching | The channel name, the status line and the buttons in the same font as the chat (Inter); `liteview.log`: "font: the window's controls use Inter too" |
| 71 | Play a past broadcast (item 59); click in the bar under the picture; drag its knob; Left / Right | The bar shows where the video is and its length; a click or drag jumps there; Left/Right jump 10 s; hovering shows the time under the mouse |
| 72 | A past broadcast of a busy channel with the chat on; jump to the middle | The chat header says CHAT REPLAY, there is no chat box, and messages appear as the video plays (emotes and badges as live); after the jump "Chat replay from 1:23:45" and that moment's messages. `liteview.log`: "chat replay: N comments from ... s". Chat gear > *Chat replay for videos* off: the live chat |
| 73 | While watching, the scissors button (or Alt+X); then menu > Playback > Clips > Open the clips folder | "Clip saved: <channel> <date> <time>.mkv"; the file in Videos\LiteView Clips plays the last ~60 s in the stream's quality, sound included. Clips > Off: the button says clips are off (from the next stream on, less memory) |
| 74 | Menu > Settings...; click through the pages; switch a few settings; reopen the menu | Every page lists its settings with a line of explanation; a switch, choice or button does what the same menu item does (the menu shows the change too); settings are kept after a restart |
| 75 | Menu > Appearance > Light (restart), then Black (restart), then Dark | Light: white and light grey like twitch.tv's light mode, readable chat names, light title bar; Black: black backgrounds; the video black in all; Dark as before |
| 76 | Menu > Text size > Smoothing: ClearType, Grey, Smooth (compare the chat and the followed channels) | The text changes at once with each; the choice is kept after a restart. Tell us which looks best on your screen |
| 77 | Watch, go fullscreen, leave fullscreen | The followed channels column is back (it was gone until a restart) |
| 78 | Look at the top bar next to the chat (zoom in on a screenshot) | Its text has ClearType's colour edges like the chat's; `liteview.log` still "font: the window's controls use Inter too" |
| 79 | Stats (I) on; Picture Ultra, then Enhance (item 65) | The stats come back on the restarted player at once |
| 80 | Extra window: ⋯ > Quality > 480p; the chat's gear | The picture keeps playing until 480p is ready, then switches (no black); the gear opens Hide the chat / Open the chat on twitch.tv |
| 81 | Play a past broadcast for a few minutes; go back to its channel's Videos; play it again; right-click > Play from the start | A red bar under its picture shows how far you got; playing it again continues there; Play from the start starts at 0:00 |
| 82 | A channel's Videos (e.g. GamesDoneQuick): scroll to the end; the filter pill > Highlights; Clips > the pill > All time | Scrolling to the end loads 40 more (again and again); Highlights shows only highlights (old marathons are there); Clips of all time |
| 83 | Right-click a video > Download > 480p; menu > Downloads... | The Downloads window shows its progress in % and size; when done "Done" and Show in folder selects the .ts file, which plays in VLC/mpv. Cancel removes the unfinished file. Closing LiteView while downloading asks first |
| 84 | Open a channel with many 7TV emotes, close LiteView, open it again on the same channel | The second time the emotes show at once; `liteview.log` stress lines (if used) count "N from disk"; `cache\emotes` next to LiteView.exe has files |
| 85 | While watching, Pause (or Space) | The picture stays as it was (readable), with "Paused · click or Space: continue at live" at the bottom; Task Manager: no Streamlink or mpv; Play continues at live (a video where it was). `liteview.log`: "paused: stream stopped (its last picture stays)" |
| 86 | GamesDoneQuick's Videos: type "Awesome" (or "SGDQ 2019") in the Search titles box | Only matching videos; older ones appear as they are found ("looking further back..."); Esc clears the search |
| 87 | A channel's Videos: scroll quickly through a few hundred, stop | The pictures on screen load within seconds (not minutes) |
| 88 | RTX on, Enhance, a 1080p stream in the window, then fullscreen | Window: stats say "RTX waits (... needs 1.30x)", the picture as without RTX; fullscreen (1.33x on a 1440p screen): RTX enlarges - compare with B whether it looks better there |
| 89 | Run LiteView-Enhanced-Setup-….exe; on "Your settings" choose your current LiteView folder; finish | No administrator prompt; Start menu and desktop shortcuts "LiteView Enhanced" with the purple icon; LiteView starts with your settings, login, favourites and watched videos; if Start with Windows was on, it now starts the installed one. Run the installer again: it updates without asking for the folder. Settings > Apps > LiteView Enhanced > Uninstall: asks whether to remove the settings too |
| 90 | RTX on, a 1080p stream in the window (about 1.23x): High, then Ultra, then Enhance | High and Ultra: stats say "RTX enlarges 1.23x" (and it should look sharp: their sharpener runs after RTX); Enhance: "RTX waits (... needs 1.30x; High and Ultra use it from 1.15x)" |

Report results (pass/fail, with `liteview.log` and the diagnostics file for failures) in `KNOWN_ISSUES.md` or as issues.

## UI fixture mode (screenshots without a Twitch account)

`LITEVIEW_UI_FIXTURE=1` makes LiteView answer its own Twitch requests with made-up channels and generated pictures
(`src/app/Fixture.cs`): no network for the Twitch API, no real login (the token file is not read or written), no
Streamlink, anonymous chat, and the title says *UI fixture*. `LITEVIEW_UI_FIXTURE_SCENE` picks the screen: `live`,
`followed`, `recent`, `history`, `search:<text>`, `browse`, `category` (the Browse tab and one category), `problem`,
`chat`, `chatleft` (the chat on the left), `chatlog` (the chat's recent messages), `reply` (replying to one of them), `picker` (the emote picker, keyboard on an emote), `stress` (the
two-minute busy chat), and with
`videos`, `clips` (a channel's made-up videos and clips), and with `LITEVIEW_UI_FIXTURE_STREAMS=1` plus `smoke.ps1 -FakeStreamlink` also `watch`, `stats` (the stats overlay), `vod` (a video plays with made-up chat replay comments, then a jump to 10:00 is simulated: the replay must start again there), `preview` (a hover preview), `window` (a second channel in an extra window with its chat; its volume set through mpv's pipe), `focus` (typing in the chat box, then real clicks on the chat's header and on the video: the keyboard must leave the box each time), `sidemenu` (a real right-click in the followed column), `release` (Ultra, then Enhance: the player restarts behind the picture), `clip` (a clip of the test video saved through mpv's dump-cache), `vidsearch` (a title search in a channel's videos), `download` (a video downloaded through FakeStreamlink's imitation of Streamlink's --output and debug log), `settings` (the Settings window), any scene as `scene@light` or `scene@black` (CI: live and chatlog in Light, settings in Light, watch in Black), `presets` (the preset comparison), `offline`, `fail`, `paused`. `smoke.ps1
-Expect <regex>` also requires lines in `liteview.log` (CI: the chatlog scene must log 8 recent messages, the paused
scene must play and then pause), and `-A11yExpect <regex>` lines in what screen readers see (CI: the picker and Browse
lists with their items). The chat scenes have made-up 7TV channel emotes (generated, a quarter of them animated). CI runs every scene with `tools/smoke.ps1` and
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
memory, threads, handles, GDI/USER objects, CPU seconds, the longest wait of the window's thread (UI lag) and the
emote cache. `tools\stress-check.ps1 -Log liteview.log -Rate 200` turns them into a table with CPU per interval and
checks them against the same budget as CI. Run it for 10-30 minutes, scroll the
chat up and down now and then, and keep the log. Healthy: memory and GDI objects level off; the chat stays smooth;
the emote cache stays within its budget. The generator is `src/core/ChatStress.cs` (tested in `ChatStressTests`).

## Test rules

- Never use real credentials in automated tests.
- A feature is not "done" because it compiles; it needs its tests and the Windows check above.
- Do not report a test as passing that was not run. Record what was run where (this file, CI logs).
