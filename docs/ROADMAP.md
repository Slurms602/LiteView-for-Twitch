# Roadmap

Priorities from the project brief (P0 → P5). Each item is **done** only after its tests pass and it has been
checked on Windows. "Implemented" means written, compiled and unit-tested where possible, but not yet run.

## P0 - Safety and foundation

| Item | State |
|---|---|
| Clone and audit upstream at a fixed commit | Done: `docs/BASELINE_AUDIT.md`, tag `upstream-baseline` |
| Verify licence | Done: MIT kept, notices kept |
| Baseline build | Done: SDK build compiles the unmodified sources (`86c1923`) |
| Upstream tracking workflow | Done: `docs/UPSTREAM.md`, `tools/upstream.sh` (tested against a simulated upstream) |
| Security hardening (SEC-1 … SEC-21) | Implemented, unit-tested where the logic allows; **needs the Windows pass** in `TESTING.md` |
| Confirmed reliability fixes (REL-1 … REL-15) | Implemented; same |
| Basic tests | Done: 228 unit tests, CI on Linux and Windows (CI not yet run: needs the push, and Actions enabled on the fork) |
| Diagnostics | Implemented: log, crash log, *Export diagnostics...* |
| Architecture docs | Done |

**Exit criterion** (secure, buildable, testable, no known critical defect): met in code; confirm with the
Windows pass and one green CI run.

## P1 - Core architecture and reliability (next)

1. **Windows validation of P0** (`TESTING.md` manual list) and fixes from it.
2. Register the fork's own Twitch application; switch the client id (SEC-16).
3. Website sign-out (SEC-17): **implemented** (gear menu); needs the Windows check.
4. Integration tests with fakes. **Done for Twitch login/API and the chat connection** (`TwitchClient`, `Irc` moved
   into `src/core`). Still to do: fake Streamlink/mpv executables (exit codes, messages, a hung pipe), which need
   the Win32 process code to run on a Windows CI runner.
5. Chat stress tool: **done** (`--chat-stress=N`, logs memory/handles/emote cache every 10 s). Still to do: run it on
   Windows and set the emote budget and any chat limits from the numbers (RES-7).
6. Status/errors (UI-5): **implemented** (⚠ in the status line, details with Try again / Open log folder / Copy).
7. Sleep/resume and network changes: **implemented** (chat liveness PING, restart on resume, reconnect when the
   network returns); needs the Windows check.

## P2 - Native Twitch interface

**Implemented** (branch `p2-native`, screenshots from CI with made-up data, not yet used on the owner's PC):
native Following page from Helix (`streams/followed`, `channels/followed`, `users`, `search/channels`,
`streams?user_login`): live cards, all followed, recently watched, search with history, favourites, sorting,
virtualised drawing, image budget, cached search with stale-answer protection, no polling while hidden or minimised.
The WebView2 page stays as a fallback (menu > Following page).
Not done: browse by game/category, language filter, hover previews (optional in the brief).
**Per-monitor DPI** (UI-1): still open. It needs every layout constant and custom-drawn control scaled (about 80
places in upstream files) and a way to test at 150 % on CI (per-monitor DPI can be changed without signing out;
system DPI cannot). Planned as its own step.

## P3 - Playback and video excellence

Done: fullscreen follows the monitor (UI-2), RTX option only with an RTX card (EXT-6).

Quality list from the stream's real variants; persistent mpv vs. restart benchmark for channel switching;
fullscreen across monitors (UI-2); preset review with objective comparisons (720p/1080p sources to 1080p/1440p/4K,
measured GPU cost and image metrics); before/after comparison tool; RTX VSR integration review.

## P4 - Performance and customisation

Done (P2 branch, not yet checked on Windows): CPU use modes (Automatic / Windows decides / custom cores), opt-in
Gaming mode, `GameFirst.exe --stop` with rollback, chat text size / timestamps / highlight words / ignored users /
width (UI-3), keyboard shortcuts. Not done: a settings window (the top-right menu holds the settings for now),
theming, chat on the left.

Measured CPU scheduling modes (Windows default / recommended / manual / gaming); opt-in Gaming Mode (foreground
game detection with non-invasive APIs, lower preset, restore); GameFirst per-app policies with rollback;
chat customisation (fonts, sizes, density, timestamps, highlights, ignore list, chat side and width); shortcuts;
a settings window; theming (DUP-2).

## P5 - Product polish

Done (P2 branch): live notifications for favourites with quiet hours (tray), settings export / import / reset,
update check (opens the release page only) and a release workflow (draft release with the zip's SHA-256).
Not done: accessibility (UI-4).

Accessibility for the custom-drawn controls (UI-4); notifications for favourite channels with quiet hours;
settings export/import/reset by category; safe update checking (no silent replacement, verified downloads);
release workflow with checksums; documentation and final regression pass.

## Not planned (for now)

Electron or a browser-based UI; telemetry; mandatory accounts; administrator rights.
