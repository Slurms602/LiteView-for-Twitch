# Known issues

Verified or clearly visible in the code. Finding ids are from `BASELINE_AUDIT.md`.

## Release blockers

| Issue | Detail |
|---|---|
| **Not yet run on Windows** | All P0 changes compile and the logic is unit-tested, but the app itself has not been started since the changes. The manual pass in `TESTING.md` is required. |
| **Upstream's Twitch client id** (SEC-16) | The fork logs in as upstream's registered application. A public release needs its own registration on dev.twitch.tv (client type *Public*, device code flow) and the id in `src/helix.cs`. |

## Open

| Issue | Detail | Plan |
|---|---|---|
| DPI scaling (UI-1) | Fixed pixel layout; best at 100 % display scaling, blurry when Windows scales it | P2 |
| Web chat has no 7TV emotes | Extensions are no longer loaded (security). The native chat shows 7TV, BTTV and FFZ emotes | By design |
| Following page depends on Twitch's website markup (EXT-1) | A Twitch redesign can break clicking channels | Native browsing, P2 |
| RTX box shows on any NVIDIA GPU (EXT-6) | Detection is "NVIDIA driver installed" | P3 |
| Screen readers cannot read chat or the followed column (UI-4) | Custom-drawn controls | P5 |
| GameFirst does not undo its changes when it exits | Priorities/affinities stay until the target app restarts | P4 |
| Status line is the only place for errors (UI-5) | Long Streamlink errors are cut | P1 |
| Upgrading from upstream with 7TV already installed in `webview\` | The extension files stay on disk in `extensions\` and the profile but are no longer loaded; the `extensions\` folder can be deleted by hand | Documented |
