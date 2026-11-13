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
| Following page depends on Twitch's website markup (EXT-1) | A Twitch redesign can break clicking channels | Native browsing, P2 |
| Logging in to twitch.tv on the Following page fails through a VPN | Twitch's login answers "This username does not exist" or "Your browser is not currently supported" when it distrusts the network: the owner hit it through a VPN, and a GitHub Windows runner (datacenter address) gets the same in plain Microsoft Edge, upstream 1.1.0 and this fork alike. Log in with the VPN off; the login is kept afterwards. Email login is refused by Twitch either way (its older form says to use the username) | Native Following page, P2 (uses the browser login instead) |
| RTX box shows on any NVIDIA GPU (EXT-6) | Detection is "NVIDIA driver installed" | P3 |
| Screen readers cannot read chat or the followed column (UI-4) | Custom-drawn controls | P5 |
| GameFirst does not undo its changes when it exits | Priorities/affinities stay until the target app restarts | P4 |
| Status line is the only place for errors (UI-5) | Long Streamlink errors are cut | P1 |
| Upgrading from upstream with 7TV already installed in `webview\` | The extension files stay on disk in `extensions\` and the profile but are no longer loaded; the `extensions\` folder can be deleted by hand | Documented |
