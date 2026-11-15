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
- The quality box lists what each stream really offers (1440p60 too, where a channel has it).
- Screen readers (Narrator, NVDA, JAWS) can read the Following page, the followed channels column and the chat.

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
- The top right of the Following page showed a broken white box instead of the menu button until a stream was opened.
  The Following page now shows only the menu there; quality, Chat and the player buttons appear while watching.
- Names and titles on the Following page and sent chat messages no longer end in half an emoji; a `/me` message
  longer than Twitch allows keeps its action form.
- A broken emote size from an emote service can no longer give an emote a negative width.
- **Ultra was oversharpened**: it sharpened twice (NLMeans' sharpening variant and the strong Adaptive Sharpen).
  It now cleans up the stream's compression noise without sharpening and sharpens once, like High, so it is cleaner
  than High without looking harsher.
- The "Replying to" line above replies in the chat was empty at the larger text sizes. A reply also no longer
  repeats "@name" at its start (like Twitch).

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
- **Pause** (button left of the volume, Space, or the keyboard's play/pause key): stops the video and its download,
  so it uses nothing; the chat keeps going. Play continues at live.
- The chat shows the channel's **recent messages** when you join, like on Twitch (from recent-messages.robotty.de, the
  service Chatterino uses; chat gear > *Recent messages when joining* turns it off).
- Menu > **Text size** for the Following page and the chat. Both are a step larger by default than before.
- **Browse** tab on the Following page: the most watched categories on Twitch; click one for its live channels (or
  *All live channels* for the most watched of all). *Language* filters the channels. Backspace or Esc goes back.
- Right-click a chat message: **Reply** (sent as a Twitch reply; Esc cancels), Copy message, Copy name.
- While a stream plays, the status line shows its **viewers, uptime, category and title** (the whole title on hover).
- Chat gear menu: **Chat on the left** (between the followed channels and the video; drag its right edge for the width).
- Emote picker by keyboard: **Ctrl+E** in the chat box opens it; Down goes from its search into the grid, arrow keys
  choose, Enter inserts, typing searches again, Esc closes. Screen readers read each emote and its set.
- **Seamless channel switching**: the playing stream keeps playing until the next channel shows its first picture,
  then it switches at once (no black wait). Also for a quality change. Menu > Playback turns it off.
- **Stats on the video** (I, or menu > Playback): the stream's quality, codec, hardware or software decoding, dropped
  frames, buffer, how long it took to open, the GPU time the picture preset costs per frame (measured by mpv), and
  CPU and memory of LiteView and the player.
- Menu > Playback > **Sharpness** for High and Ultra (Off, Soft, Normal, Strong), and **B** to see the original
  picture and back, to compare.
- **Videos and clips**: right-click a channel on the Following page > *Videos and clips*: its newest past
  broadcasts, highlights and uploads, or its most viewed clips of the last 30 days. They play in LiteView with the
  channel's live chat; videos can be seeked (arrow keys or mpv's bar at the bottom), and Pause, F5 or a new quality
  continue where the video was.
- **Previews on hover**: rest the mouse on a live channel's card on the Following page and it plays there, muted, at
  the lowest quality (menu > Playback turns it off).
- Menu > **Start and tray**: start with Windows (no administrator rights), start minimised, minimise to the tray
  (all off unless you turn them on), and *Open the last channel at start* (moved here).
- The chat and the Following page use **Inter**, the font of Twitch's own site, drawn with ClearType when Windows
  uses it. Words with characters Inter does not have (Chinese, emoji, ...) are drawn in Segoe UI as before.
  Menu > Text size > Font switches back to Segoe UI.

### For developers

- `LiteView.exe <channel> --chat-stress=200` floods the chat with synthetic messages and logs memory and handle
  counts every 10 s (docs/TESTING.md), and now also the longest wait of the window's thread (UI lag).
- CI runs that stress for two minutes at 300 messages a second with made-up 7TV emotes and checks memory, handles,
  GDI/USER objects and UI lag against a budget (`tools/stress-check.ps1`).
- Random-input tests (`FuzzTests`) for every parser of outside data: IRC, JSON, the chat history, image headers,
  links, channel names, Helix answers, Streamlink's log, settings files.
- CI's channel-switching run checks that each switch keeps the old stream until the new one plays and that no
  player is left behind (`players` in the `stress:` lines, `-MaxPlayers`), and prints how long streams took to
  their first frame. Fixture scene `stats` screenshots the stats overlay.
- CI plays a known test picture at half size with every picture preset in fullscreen and saves what mpv shows;
  `tools/preset-metrics.py` compares each with the original (PSNR, SSIM, overshoot = halos, detail = sharpness).

- Twitch login/API and the chat connection moved into the tested library and are tested against a simulated
  Twitch and chat server; JSON is read by a small built-in reader instead of `JavaScriptSerializer`.
- `LITEVIEW_UI_FIXTURE=1` (testing only): made-up Twitch data, so CI screenshots every page; a `vX.Y.Z` tag builds a
  draft release with the zip and its SHA-256.
- SDK-style projects (`LiteView.sln`), 309 unit tests, GitHub Actions CI, pinned packages with lock files,
  `tools/upstream.sh` for taking upstream changes. See `docs/`.
- CI starts the packaged app on Windows and checks that its window appears and closes cleanly (`tools/smoke.ps1`).
- If LiteView cannot start, it says so in a message box (upstream closed without a word).
