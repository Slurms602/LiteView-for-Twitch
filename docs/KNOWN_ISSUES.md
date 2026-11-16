# Known issues

Verified or clearly visible in the code. Finding ids are from `BASELINE_AUDIT.md`.

## Release blockers

| Issue | Detail |
|---|---|
| **No manual pass on Windows yet** | CI starts and closes the packaged app on a Windows runner (`tools/smoke.ps1`), but login, playback, chat and the rest of the manual pass in `TESTING.md` have not been done. |
| **Upstream's Twitch client id** (SEC-16) | The fork logs in as upstream's registered application. A public release needs its own registration on dev.twitch.tv (client type *Public*, device code flow) and the id in `src/helix.cs`. |

## Open

| Issue | Detail | Plan |
|---|---|---|
| DPI scaling (UI-1) | Fixed pixel layout; best at 100 % display scaling, blurry when Windows scales it | P2 |
| Web chat has no 7TV emotes | Extensions are no longer loaded (security). The native chat shows 7TV, BTTV and FFZ emotes | By design |
| The Twitch website Following page (now only a fallback) depends on Twitch's markup (EXT-1) | A Twitch redesign can break clicking channels there | LiteView's own page is the default |
| Logging in to twitch.tv on the Following page fails through a VPN | Twitch's login answers "This username does not exist" or "Your browser is not currently supported" when it distrusts the network: the owner hit it through a VPN, and a GitHub Windows runner (datacenter address) gets the same in plain Microsoft Edge, upstream 1.1.0 and this fork alike. Log in with the VPN off; the login is kept afterwards. Email login is refused by Twitch either way (its older form says to use the username) | Native Following page, P2 (uses the browser login instead) |
| Emoji draw in one colour on PCs without colour-font support | The chat draws emoji in colour with DirectWrite (checked when LiteView starts: `emoji: colour` in `liteview.log`); where that fails they draw in one colour as before. Chinese text draws correctly on the owner's Windows 11 PC; boxes on the CI runner come from Windows Server's fonts | - |
| P2-branch features are only CI-checked | The native Following page, notifications, Gaming mode, CPU modes, chat preferences, settings import/export and the update check were screenshot or unit-tested, not used on a real PC | `TESTING.md` items 23-40 |
| The stats overlay shows no GPU load and no delay behind live | GPU load needs vendor tools; the delay needs Twitch's own player. It shows the GPU time per frame that mpv measures, which is what a picture preset costs | By design for now |
| RTX's GPU work is not in the stats' "GPU per frame" | RTX runs in the graphics card's video processor, which mpv cannot time; the stats say when RTX is enlarging. Task Manager's GPU "Video Processing" engine shows it | Owner's PC |
| A lighter preset's memory comes back by restarting the player | mpv keeps a heavier preset's compiled shaders until it closes; LiteView restarts it behind the picture (seamless switching), a video continues about where it was (a second may repeat). With seamless switching off, the memory comes back at the next pause or channel | By design |
| Chat replay uses Twitch's website API | Twitch has no official chat replay API; LiteView asks the one twitch.tv uses, anonymously. Twitch can change or close it at any time (its cursor paging already refuses apps); then the chat says replay is not available and the video plays on. Clips show the live chat | Watch for breakage |
| A saved clip can be shorter than asked | The player keeps about 1 MB a second (Twitch's highest bitrate): right after a stream starts, after a jump in a video, or at more than 8 Mb/s there is less | By design |
| A downloaded video is MPEG-TS (.ts) | Streamlink writes Twitch's segments as they come (no re-encoding); VLC, mpv and Windows' Media Player play it; some editors want MP4 (remux with ffmpeg: `ffmpeg -i in.ts -c copy out.mp4`) | By design |
| Twitch keeps past broadcasts for a while only | Past broadcasts disappear after 7-60 days (Twitch's rule for the channel); highlights and uploads stay. Older ones are not hidden by LiteView: the filter pill shows highlights and uploads | Twitch |
| No rewinding a live stream | It needs minutes of video kept in memory; the owner agreed it is not worth it (2026-11-16) | Dropped |
| A new theme applies at the next start | Each window reads its colours when it is made; Appearance offers to restart | By design |
| The Light theme is CI-checked only by screenshots | Parts drawn by Windows (scroll bars of text boxes, some menus) follow Windows' own light/dark setting | Owner's PC |
| Seamless switching downloads two streams for a few seconds | The old stream plays until the new one shows its first picture (at most 12 s); not while Gaming mode is on or paused. Menu > Playback > Seamless channel switching turns it off | By design |
| Extra windows have no pause or picture presets | They are meant for keeping an eye on another channel (with its chat, volume, quality and "on top"); open it in the main window for everything | By design for now |
| Gaming mode recognises fullscreen windows only | A game in a normal window does not switch it on; use Gaming mode > On | By design (no process scanning) |
| Upgrading from upstream with 7TV already installed in `webview\` | The extension files stay on disk in `extensions\` and the profile but are no longer loaded; the `extensions\` folder can be deleted by hand | Documented |
