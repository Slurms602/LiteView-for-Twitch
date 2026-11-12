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
| Basic tests | Done: 175 unit tests, CI on Linux and Windows (CI not yet run: no GitHub fork yet) |
| Diagnostics | Implemented: log, crash log, *Export diagnostics...* |
| Architecture docs | Done |

**Exit criterion** (secure, buildable, testable, no known critical defect): met in code; confirm with the
Windows pass and one green CI run.

## P1 - Core architecture and reliability (next)

1. **Windows validation of P0** (`TESTING.md` manual list) and fixes from it.
2. Register the fork's own Twitch application; switch the client id (SEC-16).
3. Log out also clears the WebView2 Twitch session, optionally (SEC-17).
4. Integration tests with fakes: a fake Helix server (`HttpMessageHandler`), a fake IRC server (local WebSocket),
   fake Streamlink/mpv executables that exit with chosen codes and messages. Requires moving `Tw` and `Irc`
   behind small interfaces (only where a test needs it).
5. Chat stress tool: synthetic IRC feed (high rate, long messages, Unicode, many animated emotes, frequent
   CLEARMSG/CLEARCHAT) to measure frame time, memory and the emote budget (RES-7).
6. Status/errors: a clearer error area than the status line (UI-5), with retry and "open log".
7. Sleep/resume and network change handling for playback (reconnect on resume).
8. Audit mpv's IPC pipe permissions.

## P2 - Native Twitch interface

Native Following page from Helix (`streams/followed`, `users`, `games`, thumbnails), search (`search/channels`),
favourites, recently watched, offline followed channels, list virtualisation, cached responses with rate-limit
awareness, stale-request protection. Keep the WebView2 page as a fallback during the migration.
**Per-monitor DPI** (UI-1): scale every layout constant and custom-drawn control, then declare PerMonitorV2.

## P3 - Playback and video excellence

Quality list from the stream's real variants; persistent mpv vs. restart benchmark for channel switching;
fullscreen across monitors (UI-2); preset review with objective comparisons (720p/1080p sources to 1080p/1440p/4K,
measured GPU cost and image metrics); before/after comparison tool; RTX VSR integration review.

## P4 - Performance and customisation

Measured CPU scheduling modes (Windows default / recommended / manual / gaming); opt-in Gaming Mode (foreground
game detection with non-invasive APIs, lower preset, restore); GameFirst per-app policies with rollback;
chat customisation (fonts, sizes, density, timestamps, highlights, ignore list, chat side and width); shortcuts;
a settings window; theming (DUP-2).

## P5 - Product polish

Accessibility for the custom-drawn controls (UI-4); notifications for favourite channels with quiet hours;
settings export/import/reset by category; safe update checking (no silent replacement, verified downloads);
release workflow with checksums; documentation and final regression pass.

## Not planned (for now)

Electron or a browser-based UI; telemetry; mandatory accounts; administrator rights.
