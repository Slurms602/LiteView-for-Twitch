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
| The emote picker is not readable by screen readers (UI-4) | The Following page, followed column and chat are (UI Automation and MSAA); the picker is still only drawn | P5 |
| Emoji and some scripts may draw as boxes in the native chat | Seen on the CI runner (Windows Server): the chat draws text with GDI+, which does not fall back to other fonts the way browsers do. Not yet checked on Windows 11 | Check on the owner's PC; DirectWrite or GDI text if confirmed |
| P2-branch features are only CI-checked | The native Following page, notifications, Gaming mode, CPU modes, chat preferences, settings import/export and the update check were screenshot or unit-tested, not used on a real PC | `TESTING.md` items 23-40 |
| Gaming mode recognises fullscreen windows only | A game in a normal window does not switch it on; use Gaming mode > On | By design (no process scanning) |
| Upgrading from upstream with 7TV already installed in `webview\` | The extension files stay on disk in `extensions\` and the profile but are no longer loaded; the `extensions\` folder can be deleted by hand | Documented |
