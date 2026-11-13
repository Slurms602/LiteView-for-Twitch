# Changelog

## LiteView Enhanced 0.1.0 (unreleased)

Based on LiteView for Twitch 1.2.0 (upstream `46e76df`). **Not yet tested on Windows** - see `docs/TESTING.md`.

### Changed

- **Log in with Twitch** now opens Twitch's activation page in your normal browser (where you are usually already
  logged in). LiteView shows the code to check and what it asks permission for. You never type a password into LiteView.
- **Links** from chat and web pages open in your browser only when they are ordinary web links. Hovering a chat link
  shows where it goes.
- **Web chat** no longer loads the 7TV browser extension (LiteView used to copy it from Chrome or download it). The
  native chat still shows 7TV, BetterTTV and FFZ emotes.
- **GameFirst** changes only LiteView's own player processes unless you add other apps (Chrome, Discord...) to `gamefirst.ini`.
- Quality menu: new **Audio only** choice.

- **Following page** is now LiteView's own (it uses *Log in with Twitch*, so there is no website login inside
  LiteView): live followed channels as preview cards with uptime and viewers, sorted by viewers, name, newest or
  longest, favourites first; *All followed* and *Recently watched* lists; search from the box at the top with recent
  searches; right-click a channel to favourite it, open it on twitch.tv or copy its link; arrow keys and Enter work.
  Twitch's website page is still there: menu (top right) > Following page > Twitch website.
- Errors show as "⚠ ..." in the status line; click for the whole message with *Try again*, *Open log folder* and
  *Copy details*.
- The RTX option only shows with an NVIDIA RTX card (it showed for any NVIDIA card).

### Fixed

- A chat message ending in "RECONNECT" no longer disconnects your chat.
- Typing or pasting a channel URL such as `twitch.tv/name/videos` opens `name`, not `namevideos`.
- When a stream drops, LiteView tries again by itself (quickly at first, then more slowly). An offline channel shows
  "Offline" and starts by itself when it goes live. After repeated failures it stops; click the status text to try again.
- A short network or Twitch outage no longer logs you out; chat keeps working after the hourly token refresh.
- A frozen video player can no longer freeze the whole window.
- The "playing" check no longer gets confused by another mpv window on your PC.
- Settings are saved safely (a crash while saving cannot wipe them; the previous file is kept as `liteview.ini.bak`),
  and no longer briefly reset to defaults while LiteView starts.
- Long sessions: animated emotes no longer use more and more memory; very wide or oversized emote images are limited.
- GameFirst no longer crashes on a typo in `gamefirst.ini`.
- Clear messages when Streamlink or mpv is missing.
- Fullscreen follows the monitor: moving it with Win+Shift+Arrow, a resolution change or unplugging a monitor while
  fullscreen keeps it filling a screen, and leaving fullscreen never puts the window on a monitor that is gone.
- GameFirst can undo what it changed: `GameFirst.exe --stop`.

### Added

- Gear menu: **Sign out of twitch.tv in LiteView** (the website login used by the Following page and web chat;
  *Log out* still ends only the chat login).
- `liteview.log` (rotated at 1 MB, 3 kept; no tokens, no chat), and crash logging.
- Chat gear menu: **Export diagnostics...** (a report without credentials or channel names) and **Open log folder**.

- Chat no longer stops silently after sleep or a network change: LiteView notices a dead connection and reconnects,
  and after resume from sleep it restarts the stream at the live edge.

- A menu at the top right: log in/out, Following page, open the last channel at start, live notifications for
  favourites (opt-in, with quiet hours), remember watched channels and searches, Performance (CPU use, Gaming mode),
  Settings (export, import, reset), Check for updates (only opens the release page; daily check opt-in), keyboard
  shortcuts.
- Chat gear menu: text size, timestamps, highlight words, ignored users; drag the chat's left edge to change its width.
- Keyboard: Ctrl+L go to channel, F5, Alt+Left / Backspace back to Following, M mute, Ctrl+Up/Down volume, C chat.
- Performance > CPU use: Automatic (as before), Windows decides, or custom cores. Gaming mode (opt-in): while a
  fullscreen game is in front, a lighter picture preset, no RTX and slower emote animation; it all comes back after.

### For developers

- `LiteView.exe <channel> --chat-stress=200` floods the chat with synthetic messages and logs memory and handle
  counts every 10 s (docs/TESTING.md).

- Twitch login/API and the chat connection moved into the tested library and are tested against a simulated
  Twitch and chat server; JSON is read by a small built-in reader instead of `JavaScriptSerializer`.
- `LITEVIEW_UI_FIXTURE=1` (testing only): made-up Twitch data, so CI screenshots every page; a `vX.Y.Z` tag builds a
  draft release with the zip and its SHA-256.
- SDK-style projects (`LiteView.sln`), 309 unit tests, GitHub Actions CI, pinned packages with lock files,
  `tools/upstream.sh` for taking upstream changes. See `docs/`.
- CI starts the packaged app on Windows and checks that its window appears and closes cleanly (`tools/smoke.ps1`).
- If LiteView cannot start, it says so in a message box (upstream closed without a word).
