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

### Added

- `liteview.log` (rotated at 1 MB, 3 kept; no tokens, no chat), and crash logging.
- Chat gear menu: **Export diagnostics...** (a report without credentials or channel names) and **Open log folder**.

- Chat no longer stops silently after sleep or a network change: LiteView notices a dead connection and reconnects,
  and after resume from sleep it restarts the stream at the live edge.

### For developers

- `LiteView.exe <channel> --chat-stress=200` floods the chat with synthetic messages and logs memory and handle
  counts every 10 s (docs/TESTING.md).

- Twitch login/API and the chat connection moved into the tested library and are tested against a simulated
  Twitch and chat server; JSON is read by a small built-in reader instead of `JavaScriptSerializer`.
- SDK-style projects (`LiteView.sln`), 228 unit tests, GitHub Actions CI, pinned packages with lock files,
  `tools/upstream.sh` for taking upstream changes. See `docs/`.
