# Security model

What LiteView Enhanced trusts, what it does not, and how each boundary is protected. Finding numbers
(SEC-n, REL-n) refer to `BASELINE_AUDIT.md`. "Verified" here means covered by the unit tests in
`tests/`; nothing in this file has been checked on a running Windows build yet (`TESTING.md`).

## Assets

1. The Twitch access and refresh tokens (they let anyone chat as the user and read their follows and emotes).
2. The Twitch website session inside the WebView2 profile (`webview\` next to the exe).
3. The user's PC: no program, file or protocol handler may be started by outside content.
4. The user's privacy: which channels they watch, their chat.

## Trust boundaries

| # | Boundary | Trusted? | Protection |
|---|---|---|---|
| B1 | Twitch API answers (`api.twitch.tv`, `id.twitch.tv`) | Authentic (TLS) but parsed as untrusted data | HTTPS only; 16 MB cap on JSON; numeric ids and logins validated before use in URLs or commands; validate answer must match our client id |
| B2 | Twitch IRC (`wss://irc-ws.chat.twitch.tv`) | Server authentic; **message content is user-written** | 1 MB cap per WebSocket message; 16 KB per line; text cut to 2000 characters for layout; `room-id` must be digits; only the real `RECONNECT` command reconnects (REL-1) |
| B3 | Third-party emote services (7TV, BTTV, FFZ) | Not trusted | API hosts allow-listed (HTTPS); image hosts allow-listed (`*.7tv.app`, `*.betterttv.net`, `*.frankerfacez.com`, `*.jtvnw.net`); 4 MB per image; PNG/GIF/JPEG only, size from the header checked before decoding (≤ 2048 px a side, ≤ 2 MP); frames ≤ 400 and ≤ 8 MB decoded per image; drawn width ≤ 5× height; 4 downloads at once; 128 MB of decoded frames in total (SEC-7, SEC-8) |
| B4 | Web content in WebView2 (twitch.tv and whatever it loads) | Not trusted | Main-frame navigation only to `https://*.twitch.tv`; other web links go to the system browser when the user clicked them; other schemes blocked; permissions denied; downloads cancelled; dev tools, autofill, password saving and host objects off; no extensions (SEC-1, SEC-4) |
| B5 | Page → app messages | Not trusted | Only from `https://*.twitch.tv`, only `watch:<per-run secret>:<login>`, login validated (SEC-5) |
| B6 | Links (chat, web pages, login) | Not trusted | One launcher (`src/app/Launcher.cs`) opens absolute `http(s)` URLs with a host and no user-info; everything else is refused and logged by scheme only (SEC-2, SEC-3) |
| B7 | Child processes (Streamlink, mpv) | Trusted binaries (pinned, SHA-256) | Command line built from validated parts and quoted by CommandLineToArgvW rules; started suspended in a kill-on-close job; Below Normal priority |
| B8 | mpv IPC pipe | Local, same user | Random name per stream; we are the client; replies parsed defensively (length cap, numbers clamped). mpv 0.42.0 restricts the pipe to the current user at the same or higher integrity level and rejects remote clients (`input/ipc-win.c`, read 2026-11-12) |
| B9 | Local files next to the exe (`liteview.ini`, `liteview.token`, `liteview.emotes`, logs) | The user's own | ini parse is tolerant and size-capped; atomic writes; token DPAPI-encrypted for the Windows user |
| B10 | Downloaded dependencies at build time | Must be authentic | Streamlink and mpv: pinned URLs + SHA-256; NuGet: nuget.org only, signed packages required, versions pinned, hashes in `packages.lock.json`, restore in locked mode; WebView2 DLLs: Microsoft Authenticode check |
| B11 | GameFirst and other processes | Other apps are not ours | Default list is LiteView's own player processes only; other apps only when the user lists them (SEC-14) |

## Authentication

- **Flow**: OAuth Device Code Grant with a public client (no client secret exists or ships). The activation
  page opens in the **system browser**; the app shows the code and the scopes and never sees a password.
- **Scopes**: `user:read:follows user:read:emotes chat:read chat:edit`, each needed by a shipped feature
  (followed column, emote picker, reading and sending chat). The login window lists them.
- **Storage**: `liteview.token` (access + refresh token) encrypted with DPAPI for the current Windows user,
  written atomically. Any process running as the same user can decrypt it; that is accepted (the same holds for
  Windows Credential Manager).
- **Lifecycle**: validated at start and hourly (Twitch's rule); refreshed on 401 with one refresh at a time
  (refresh tokens are single use); dropped when refresh fails; log out revokes the token at Twitch, deletes the
  file and clears the in-memory scopes.
- **Anonymous chat** (read-only) works without logging in.
- **Never logged**: every log line passes through `Redact.Secretsless` (`oauth:…`, `Bearer …`,
  `access_token`, `refresh_token`, `device_code`, `user_code`, `client_secret`, `Authorization`/`Client-Id`
  headers). Diagnostics exports also hide channel names and redact private settings keys.
- **Website session**: the Following page and web chat keep Twitch's own website login in the `webview` folder.
  *Log out* ends the chat/API login only; gear menu → *Sign out of twitch.tv in LiteView* clears the website session.
- **Open**: the client id is upstream's (SEC-16).

## Not done / residual risks

| Risk | Why it remains | Plan |
|---|---|---|
| The Following page runs Twitch's full website in WebView2, with its third-party scripts | Upstream's browsing UI; navigation, messages and permissions are restricted | Native browsing (P2) removes it from normal use |
| Web chat (optional) runs Twitch's popout chat | Kept as a fallback | Same restrictions as above |
| Upstream's Twitch client id | Needs the owner's dev.twitch.tv registration | Before a public release |
| GDI+ still decodes PNG/GIF/JPEG from the network | No managed decoder in .NET Framework | Inputs are size-checked first; consider WIC with limits later |
| No code signing of LiteView.exe | No certificate | Releases are not described as signed; checksums are published (`FILES.sha256`, `<zip>.sha256`) |
| GameFirst does not undo its changes when it exits | Upstream design | P4: per-app policies with rollback |

## Reporting

Security problems in this fork: open a private security advisory on the fork's GitHub repository (once it
exists), not a public issue.
