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
- Video pictures on a channel's Videos page stopped loading for minutes after scrolling quickly through years of
  videos: the ones scrolled past were still downloaded first. Now only pictures still on screen are downloaded, and a
  failed one is tried again after 20 s (not 5 minutes).
- The followed channels column was gone after leaving fullscreen (until LiteView started again).
- The top bar's text (channel, status line, buttons, boxes) was drawn in grey instead of ClearType like the chat;
  all of the window's controls now use ClearType.
- An extra window's quality change no longer goes black: the old picture plays until the new one is ready, like the
  main window. The gear in an extra window's chat did nothing; it now has *Hide the chat* and *Open the chat on twitch.tv*.
- After typing in the chat box, clicking anywhere else (the video, the chat's messages, the followed channels, the top
  bar) now gives the keyboard back to the window, so Space, M, I, B and the other keys work again.
- Choosing a lighter picture preset after Ultra (or High) now gives back the memory the heavier one needed: the player
  restarts behind the picture a moment later (seamless switching; the picture does not stop). It kept about 1 GB with
  Ultra until a pause or the next channel.
- **RTX** made the picture softer when the video was shown at about its own size (a 1080p stream in the window on a
  1440p screen: it was enlarged only 1.05x). RTX now enlarges only when the video is enlarged at least 1.3x (a 720p
  or 936p stream, or 1080p in fullscreen on a 1440p screen; at 1.23x in the window it was still softer than Enhance), and then does the whole enlarging, with the preset's
  sharpening after it; otherwise it waits. The stats (I) say which, and that RTX's own GPU work is not in "GPU per
  frame" (mpv cannot measure it).

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
  broadcasts, highlights and uploads, or its most viewed clips of the last 30 days. They play in LiteView with a
  **seek bar** under the picture (click or drag; Left/Right jump 10 s), and Pause, F5 or a new quality continue where
  the video was.
- **Chat replay**: a past broadcast shows its chat as it was at that moment of the broadcast (emotes, badges and your
  chat settings as in live chat), and jumping in the video starts the chat again from there. It comes from the same
  Twitch service the website uses, asked anonymously (no login is sent). Chat gear > *Chat replay for videos* turns it
  off (then the channel's live chat shows, as for clips).
- **Pause keeps the picture**: the frame on screen stays (to read something on it) while the stream stops as before
  (nothing downloads or plays while paused); a small note at the bottom says how to continue.
- **Search a channel's videos** by title (the box on its Videos page): words in any order; while searching, older
  videos are looked through by themselves (up to the newest 4000).
- **Where you left off**: a red bar under a video's picture on its channel's Videos page shows how much you watched,
  and the video plays on from there (right-click: *Play from the start*, *Forget where I stopped*). Kept in
  `liteview.videos` for the last 500 videos.
- **More videos**: a channel's Videos page loads the next 40 as you scroll to the end, and its filter shows all
  videos, past broadcasts, highlights or uploads (years-old highlights and uploads are still there); Clips can be of the
  last 7 days, 30 days, year or all time.
- **Download videos and clips** (right-click > Download > quality, with the size it will be about): Streamlink writes
  them to `Videos\LiteView Downloads` in their original quality (`.ts` for videos, `.mp4` for clips), two at a time;
  menu > *Downloads...* shows their progress, Cancel and Show in folder. An unfinished download is never left looking
  finished (`.part` until done).
- **Emotes stay on disk** between starts (`cache\emotes` next to LiteView.exe, at most 200 MB, 60 days), so a chat
  shows its emotes at once instead of downloading them again.
- **Save clips on your PC**: the scissors button (or Alt+X) saves the last 60 s of what plays, live or a video, in its
  original quality to `Videos\LiteView Clips` (`.mkv`, plays in VLC, mpv or the Media Player). Menu > Playback > Clips:
  30 s, 60 s, 2 minutes, or Off; the player keeps that much of the stream in memory (about 1 MB a second).
- **Colour emoji** in the chat (drawn with DirectWrite like the browser does; where that is not available they stay
  as before).
- **More streams at once**: right-click a channel > *Watch in a new window* plays it in a separate window, up to 3
  next to the main one. Each has a bar with mute and its own volume (kept for the next window), the chat beside the
  video (button), on top of other windows or not (pin button; on by default, for picture in picture), and a menu with
  the quality (720p60 by default, or the same as the main window, Best, 1080p60, 480p, 360p), *Keep the video's
  shape* (resizing keeps it 16:9, no black bars; off by default) and *Watch in the main window*.
- Right-click a channel in the **followed column** while watching: watch it, watch it in a new window, its videos or
  clips, favourite, open on twitch.tv, copy link (a right-click no longer switches to it).
- LiteView has **its own icon** (a purple chat bubble with a play button) instead of mpv's.
- **Settings window** (menu > *Settings...*): every setting in one place, in pages on the left (Playback, Chat, Text and
  font, Appearance, Following page, Notifications, Start and tray, Extra windows, Performance, Account and help, Back up
  and reset), with switches, choices and a line of explanation under each. It uses the same settings as the menus,
  which stay. The menus' *Settings* submenu is now *Back up and reset*.
- **Themes** (menu > *Appearance*, or Settings > Appearance): Dark (as before), **Black** (black backgrounds, for OLED
  screens and dark rooms) and **Light** (Twitch's light colours). The video area stays black. A new theme applies when
  LiteView starts again (it offers to restart).
- Menu > Text size > **Smoothing**: ClearType (as before, follows Windows), Grey (no colour fringes) or Smooth (no
  font hinting: softer, rounder letters, closer to how some browsers draw) - to pick the one that looks best on your
  screen.
- Menu > **Extra windows**: how new extra windows start (on top, with the chat, keep the video's shape, quality).
- **Previews on hover**: rest the mouse on a live channel's card on the Following page and it plays there, muted, at
  the lowest quality (menu > Playback turns it off).
- Menu > **Start and tray**: start with Windows (no administrator rights), start minimised, minimise to the tray
  (all off unless you turn them on), and *Open the last channel at start* (moved here).
- The chat and the Following page use **Inter**, the font of Twitch's own site, drawn with ClearType when Windows
  uses it. Words with characters Inter does not have (Chinese, emoji, ...) are drawn in Segoe UI as before.
  Menu > Text size > Font switches back to Segoe UI. The top bar (channel, status line, buttons), the chat box and the
  extra windows use it too.

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
