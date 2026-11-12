# Following upstream LiteView

This repository is a fork of **[itsdeeezy/LiteView-for-Twitch](https://github.com/itsdeeezy/LiteView-for-Twitch)**.
We review upstream's new work regularly and take what improves the app, without letting it overwrite this fork's own
changes.

## Setup (already done in this clone)

| What | Value |
|---|---|
| `upstream` remote | `https://github.com/itsdeeezy/LiteView-for-Twitch.git`, fetch only (its push URL is set to `DISABLED-do-not-push-to-upstream`) |
| Upstream tags | fetched as `upstream/v1.0.0`, `upstream/v1.1.0`… so they never collide with this fork's own tags |
| Fork point | tag `upstream-baseline` = upstream `46e76df` (LiteView 1.2.0 source, 2026-11-09) |
| Last reviewed upstream commit | `.upstream-reviewed` (tracked, so every clone agrees) |
| `rerere` | on: a conflict resolved once is resolved the same way next time |
| `origin` | not set yet. Add your GitHub fork when you create it: `git remote add origin https://github.com/<you>/LiteView-Enhanced.git` |

A fresh clone of this fork needs the remote again:

```bash
git remote add upstream https://github.com/itsdeeezy/LiteView-for-Twitch.git
git remote set-url --push upstream DISABLED-do-not-push-to-upstream
git config remote.upstream.tagOpt --no-tags
git config --add remote.upstream.fetch '+refs/tags/*:refs/tags/upstream/*'
git config rerere.enabled true
```

## The review routine

Run from Git Bash (or `powershell -File tools\upstream.ps1 <command>` on Windows):

```bash
tools/upstream.sh status          # fetch; list unreviewed upstream commits, flag files we changed too
tools/upstream.sh show <sha>      # read one commit
```

Then decide, commit by commit, oldest first:

| Decision | Command | When |
|---|---|---|
| Take it whole | `tools/upstream.sh pick <sha>` | It applies cleanly, or the conflicts are small. Records `(cherry picked from commit …)`, adds a ledger row, then builds and runs the tests. |
| Take part of it | `tools/upstream.sh pick <sha> --no-commit` | Upstream ships big release commits. Stage it, drop the hunks we do not want (`git restore -p --staged .` then `git restore -p .`), build, test, commit, then `tools/upstream.sh port <sha> HEAD "kept X, dropped Y"`. |
| Re-implement it | write it ourselves, then `tools/upstream.sh port <sha> <our-commit> "<why>"` | The idea is good but the code no longer fits our structure (for example it edits code we moved into `src/core`). |
| Leave it | `tools/upstream.sh skip <sha> "<why>"` | It conflicts with a deliberate decision here (see the rules below) or we already fixed it differently. |

When every commit up to some point is decided: `tools/upstream.sh done <sha>`, then commit `.upstream-reviewed` and this file.

`tools/upstream.sh file src/chat.cs` shows upstream's and our changes to one file side by side, which helps with conflicts.

## Rules that protect the fork's work

1. **Never** `git merge -X theirs`, `git checkout upstream/main -- <file>`, or copy upstream files over ours. They silently
   undo our fixes. Use three-way picks; a conflict is the signal to look.
2. Resolve conflicts by keeping our behaviour and adding upstream's improvement on top. When in doubt, check the
   security notes in `docs/SECURITY_MODEL.md`: upstream changes that bring back something we removed on purpose
   (listed below) are skipped or re-implemented safely.
3. Every pick must build and pass `dotnet test` before it is pushed. `pick` runs both.
4. Keep the upstream files at their upstream paths (`src/*.cs`, `build.ps1`, `config/`). New code goes into new
   files (`src/core/`, `src/app/`), and edits to upstream files stay small, so upstream's patches keep applying.

Deliberate differences from upstream (an upstream change touching these needs a careful port, not a plain pick):

| Area | Fork behaviour | Why |
|---|---|---|
| Browser extensions | Never copied from Chrome or downloaded from the Chrome Web Store; WebView2 extensions off | Unverified code from outside the app (SEC-1) |
| Twitch login | Device code shown in the app, Twitch's page opens in the system browser | No embedded login page (SEC-6) |
| Links | Only `http`/`https` URLs are opened, through one validated launcher | Chat and web content could launch other URL schemes (SEC-2, SEC-3) |
| WebView2 | Navigation limited to Twitch, messages to the app checked for origin and format | SEC-4, SEC-5 |
| Downloads | Size caps and host allow-lists for API data, emotes and avatars | SEC-7, SEC-8 |
| GameFirst | Ships changing only LiteView's own player processes; Chrome etc. only if you add them | Unrelated apps are not touched by default (SEC-14) |
| Build | SDK-style projects (`LiteView.sln`) instead of `csc.exe` lines in `build.ps1` | Tests, pinned packages, CI |

## Ledger

One row per reviewed upstream commit. The script appends rows; edit the notes by hand when useful.

| Date | Upstream commit | Decision | Our commit | Notes |
|---|---|---|---|---|
| 2026-11-12 | `46e76dfea4` LiteView for Twitch 1.2.0: emote picker, smooth chat text | fork point | - | Baseline of this fork (tag `upstream-baseline`) |
