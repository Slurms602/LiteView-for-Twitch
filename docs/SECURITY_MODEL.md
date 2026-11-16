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
| B1 | Twitch API answers (`api.twitch.tv`, `id.twitch.tv`) | Authentic (TLS) but parsed as untrusted data | HTTPS only; 16 MB cap on JSON; numeric ids and logins validated before use in URLs or commands (the Browse tab's category id too; its language is one of a fixed list of codes; a video id must be digits and a clip id letters, digits, - and _ before it goes into Streamlink's command line); validate answer must match our client id |
| B2 | Twitch IRC (`wss://irc-ws.chat.twitch.tv`) | Server authentic; **message content is user-written** | 1 MB cap per WebSocket message; 16 KB per line; text cut to 2000 characters for layout; `room-id` must be digits; only the real `RECONNECT` command reconnects (REL-1); a message id from a received line goes into a reply tag only when it is a UUID (`IrcProtocol.ReplyTag`) |
| B3 | Third-party emote services (7TV, BTTV, FFZ) | Not trusted | API hosts allow-listed (HTTPS); image hosts allow-listed (`*.7tv.app`, `*.betterttv.net`, `*.frankerfacez.com`, `*.jtvnw.net`); 4 MB per image; PNG/GIF/JPEG only, size from the header checked before decoding (≤ 2048 px a side, ≤ 2 MP); frames ≤ 400 and ≤ 8 MB decoded per image; drawn width ≤ 5× height; 4 downloads at once; 128 MB of decoded frames in total (SEC-7, SEC-8) |
| B4 | Web content in WebView2 (twitch.tv and whatever it loads) | Not trusted | Main-frame navigation only to `https://*.twitch.tv`; other web links go to the system browser when the user clicked them; other schemes blocked; permissions denied; downloads cancelled; dev tools, autofill, password saving and host objects off; no extensions (SEC-1, SEC-4) |
| B5 | Page → app messages | Not trusted | Only from `https://*.twitch.tv`, only `watch:<per-run secret>:<login>`, login validated (SEC-5) |
| B6 | Links (chat, web pages, login) | Not trusted | One launcher (`src/app/Launcher.cs`) opens absolute `http(s)` URLs with a host and no user-info; everything else is refused and logged by scheme only (SEC-2, SEC-3) |
| B7 | Child processes (Streamlink, mpv) | Trusted binaries (pinned, SHA-256) | Command line built from validated parts and quoted by CommandLineToArgvW rules; started suspended in a kill-on-close job; Below Normal priority. For videos and clips Streamlink passes the playlist URL it got from Twitch to mpv (`--player-passthrough hls,http`), so mpv downloads them itself (same user, same Twitch hosts) |
| B8 | mpv IPC pipe | Local, same user | Random name per stream; we are the client; replies parsed defensively (length cap, numbers clamped; the stats overlay's answers field by field, printable ASCII only, and escaped before they go into the overlay's ASS text and JSON command, so nothing in them can add OSD tags or commands). mpv 0.42.0 restricts the pipe to the current user at the same or higher integrity level and rejects remote clients (`input/ipc-win.c`, read 2026-11-12) |
| B9 | Local files next to the exe (`liteview.ini`, `liteview.token`, `liteview.emotes`, logs) | The user's own | ini parse is tolerant and size-capped; atomic writes; token DPAPI-encrypted for the Windows user |
| B10 | Downloaded dependencies at build time | Must be authentic | Streamlink and mpv: pinned URLs + SHA-256; NuGet: nuget.org only, signed packages required, versions pinned, hashes in `packages.lock.json`, restore in locked mode; WebView2 DLLs: Microsoft Authenticode check |
| B11 | GameFirst and other processes | Other apps are not ours | Default list is LiteView's own player processes only; other apps only when the user lists them (SEC-14); `--stop` restores what it changed, checking each process's start time so a reused process id is never touched |
| B12 | Update check (`api.github.com`) | Not trusted | Only when asked (menu) or opted in (daily); 1 MB cap; only a newer, non-pre-release version is offered; only a `https://github.com/...` release page is opened in the browser; nothing is downloaded or run |
| B13 | Imported settings files | Not trusted | Only known keys with one-line values ≤ 20 000 characters; files over 1 MB refused; lists re-validated when loaded (channel names) |
| B14 | `LITEVIEW_UI_FIXTURE=1` (testing only) | - | Made-up answers from an in-process handler: no Twitch API traffic, the token file untouched, Streamlink not started, chat anonymous, the title says so |
| B15 | Gaming mode detection | - | Reads only the foreground window (owner process id, class name, rectangle) and Windows' notification state; no process lists, no files |
| B24 | Emote cache (`cache\emotes`) | Written by LiteView only, but a file on disk is treated as untrusted | Files named by the picture URL's SHA-256 (no part of a URL in a path); written whole then moved; only pictures that passed the download checks; read back only through the same checks (size, image type, then the decoder limits); at most 200 MB and 60 days, oldest removed first |
| B23 | Downloads (right-click > Download) | Same as B7 | Streamlink's command from validated parts (numeric video id or clip slug, a fixed quality list, an output path of LiteView's own: the user's Videos folder, a file name of letters/digits/harmless punctuation without Streamlink's `{placeholders}`, ending in `.part` until done); in its own kill-on-close job at below-normal priority; two at a time; nothing else is downloaded or run |
| B22 | Clips saved on this PC (scissors button, Alt+X) | The user's own folder | mpv writes the last seconds it already holds (no new download, no re-encoding) to `Videos\LiteView Clips\<channel> <date> <time>.mkv`: the folder is the user's Videos known folder, the name is LiteView's own (a validated channel name and digits), and the path reaches mpv as a JSON string through the stream's private pipe; nothing is uploaded |
| B21 | Chat replay (`gql.twitch.tv`, past broadcasts, on by default, chat gear > *Chat replay for videos*) | Not trusted; Twitch's undocumented website API | A fixed URL; the twitch.tv website's public client id and **no token** (anonymous; the user's Twitch login is never sent there); the video id must be numeric; at most one request at a time, one a second, 4 MB per answer, one deadline; each comment is validated (channel-name logins, #RRGGBB colours, letters/digits emote ids and badges, no control characters, 1000 characters) and becomes a chat line drawn by the normal chat code; a refusal or three failures stop it with a note in the chat |
| B20 | Extra windows | Same as B7 | A valid channel name only; each in its own kill-on-close job (a process that cannot be put in the job is ended before it runs); at most 3; closed when the main window closes (they are not owned windows, so "on top" can be turned off). Each talks to its own mpv through the stream's random pipe name (volume and mute only; lines over 4 KB ignored, parsed by the bounded JSON reader); the quality comes from a fixed list (or the main window's box, itself validated) and the volume is clamped to 0-100 before it reaches mpv's command line; its chat is an ordinary chat connection like the main one |
| B19 | Hover previews (Following page) | Same as B7 | Only for a live card with a valid channel name, after the mouse rested on it; one at a time, in its own kill-on-close job at idle priority, the lowest quality, muted, no retries; mpv's window is disabled (no input); ends when the mouse leaves, the page scrolls or hides, or LiteView closes; menu > Playback turns it off |
| B17 | Windows start-up entry (menu > Start and tray > Start with Windows) | The user's own | Only when the user ticks it; written to the user's `HKCU\...\Run` (no administrator rights) as this `LiteView.exe` in quotes plus `--autostart`; read back from the registry (an imported settings file cannot switch it on); unticking or *Reset all settings* removes it |
| B18 | Bundled font (`fonts\Inter-*.ttf`) | Pinned like the shaders (release zip SHA-256 in THIRD_PARTY_NOTICES) | Loaded only from LiteView's own folder into a private font collection (not installed for Windows); also added for GDI with `AddFontResourceEx(FR_PRIVATE)`, which is private to LiteView's process and ends with it (nothing installed, no registry); its character map is read by a bounds-checked parser that gives up on damaged files (tested with damaged copies) |
| B16 | Chat history (`recent-messages.robotty.de`, on by default, chat gear > *Recent messages when joining*) | Not trusted; **learns which channels you open** (as the emote services already learn the channel id) | HTTPS, host allow-listed, JSON cap as B1; channel name validated before it goes in the URL; only `PRIVMSG`/`USERNOTICE` lines of the channel asked for, no CR/LF/NUL, at most 50; each line then goes through the same parsing and display limits as live chat (B2); no login or token is sent |

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
| The Twitch website Following page (now an opt-in fallback) runs Twitch's full website in WebView2 | Kept for anyone who prefers it; navigation, messages and permissions are restricted | LiteView's own page is the default and needs no browser |
| Web chat (optional) runs Twitch's popout chat | Kept as a fallback | Same restrictions as above |
| Upstream's Twitch client id | Needs the owner's dev.twitch.tv registration | Before a public release |
| GDI+ still decodes PNG/GIF/JPEG from the network | No managed decoder in .NET Framework | Inputs are size-checked first; consider WIC with limits later |
| No code signing of LiteView.exe | No certificate | Releases are not described as signed; checksums are published (`FILES.sha256`, `<zip>.sha256`) |

## Reporting

Security problems in this fork: open a private security advisory on the fork's GitHub repository (once it
exists), not a public issue.
