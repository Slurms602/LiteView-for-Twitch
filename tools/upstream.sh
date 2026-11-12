#!/usr/bin/env bash
# Review and selectively take changes from the original LiteView repository (the "upstream" remote)
# without overwriting this fork's own work. Runs in Git Bash on Windows, and on Linux/macOS.
#
#   tools/upstream.sh status              fetch upstream; list commits not yet reviewed, and which
#                                         of their files this fork has also changed (needs care)
#   tools/upstream.sh show <sha>          the commit, with a note per file on whether we changed it too
#   tools/upstream.sh pick <sha> [--no-commit] [--no-test]
#                                         cherry-pick it (records "cherry picked from" in the message);
#                                         --no-commit stages it so you can keep only some hunks
#   tools/upstream.sh skip <sha> <reason> record that it was reviewed and deliberately not taken
#   tools/upstream.sh port <sha> <ours> <note>
#                                         record that it was re-implemented by our commit <ours>
#   tools/upstream.sh done <sha>          mark everything up to <sha> as reviewed
#   tools/upstream.sh file <path>         3-way view of one file: baseline, upstream now, ours now
#
# Decisions go into docs/UPSTREAM.md (the ledger); the last reviewed commit is in .upstream-reviewed.
# Nothing here pushes, force-resets, or checks out upstream files over ours.
set -euo pipefail

root=$(git rev-parse --show-toplevel)
cd "$root"
remote=upstream
branch=main
marker=.upstream-reviewed
ledger=docs/UPSTREAM.md
baseline_tag=upstream-baseline

die() { echo "upstream.sh: $*" >&2; exit 1; }
need_remote() {
    git remote get-url "$remote" >/dev/null 2>&1 || die "no '$remote' remote (git remote add $remote https://github.com/itsdeeezy/LiteView-for-Twitch.git)"
}
reviewed() {
    if [ -f "$marker" ]; then tr -d ' \r\n' < "$marker"; else git rev-parse "$baseline_tag^{commit}"; fi
}
full() { git rev-parse --verify --quiet "$1^{commit}" || die "unknown commit: $1"; }
is_upstream() { git merge-base --is-ancestor "$1" "$remote/$branch"; }
# files this fork changed since the upstream commit we last reviewed (a three-way merge base)
ours_changed() { git diff --name-only "$(reviewed)" HEAD --; }
today() { date +%Y-%m-%d; }
subject() { git log -1 --format=%s "$1"; }

ledger_row() {   # sha decision ours note
    local sha=$1 decision=$2 ours=$3 note=$4
    [ -f "$ledger" ] || die "$ledger is missing"
    grep -q '^| Date | Upstream commit' "$ledger" || die "$ledger has no ledger table"
    local s; s=$(subject "$sha" | sed 's/|/\\|/g')
    note=$(printf '%s' "$note" | sed 's/|/\\|/g')
    printf '| %s | `%s` %s | %s | %s | %s |\n' "$(today)" "$(git rev-parse --short=10 "$sha")" "$s" "$decision" "$ours" "$note" >> "$ledger"
    echo "ledger: $decision $(git rev-parse --short "$sha")"
}

cmd=${1:-status}; shift || true
case "$cmd" in
status)
    need_remote
    git fetch --quiet "$remote"
    base=$(reviewed)
    echo "last reviewed upstream commit: $(git log -1 --format='%h %ad %s' --date=short "$base")"
    echo "upstream $remote/$branch:        $(git log -1 --format='%h %ad %s' --date=short "$remote/$branch")"
    count=$(git rev-list --count "$base..$remote/$branch")
    if [ "$count" = 0 ]; then echo; echo "Nothing new upstream."; exit 0; fi
    echo; echo "$count upstream commit(s) to review (oldest first):"
    mapfile -t mine < <(ours_changed)
    for c in $(git rev-list --reverse "$base..$remote/$branch"); do
        echo
        done_note=""
        [ -n "$(git log --format=%H --fixed-strings --grep="cherry picked from commit $c" HEAD)" ] && done_note="   [already picked]"
        grep -q "\`$(git rev-parse --short=10 "$c")\`" "$ledger" 2>/dev/null && done_note="   [in the ledger]"
        git log -1 --format="  %h  %ad  %an$done_note%n      %s" --date=short "$c"
        while IFS=$'\t' read -r add del path; do
            flag=""
            for m in "${mine[@]}"; do [ "$m" = "$path" ] && flag="  <- we changed this file too (review by hand)"; done
            printf '      %5s %5s  %s%s\n' "+$add" "-$del" "$path" "$flag"
        done < <(git diff --numstat "$c^" "$c")
    done
    echo
    echo "Next: tools/upstream.sh show <sha>, then pick / skip / port it; tools/upstream.sh done <sha> when finished."
    ;;
show)
    [ $# -ge 1 ] || die "usage: show <sha>"
    c=$(full "$1"); mapfile -t mine < <(ours_changed)
    git --no-pager log -1 --stat "$c"
    while IFS= read -r p; do
        for m in "${mine[@]}"; do [ "$m" = "$p" ] && echo "NOTE: this fork also changed $p since the last review - a pick may conflict there."; done
    done < <(git diff --name-only "$c^" "$c")
    echo; git --no-pager show --format= "$c"
    ;;
pick)
    [ $# -ge 1 ] || die "usage: pick <sha> [--no-commit] [--no-test]"
    c=$(full "$1"); shift
    nocommit=0; notest=0
    for a in "$@"; do case "$a" in --no-commit) nocommit=1;; --no-test) notest=1;; *) die "unknown option $a";; esac; done
    is_upstream "$c" || die "$c is not on $remote/$branch"
    [ -z "$(git status --porcelain --untracked-files=no)" ] || die "commit or stash your changes first"
    if [ $nocommit = 1 ]; then
        git cherry-pick --no-commit "$c" || { echo "Resolve the conflicts (git status), keep only what you want, then commit with:"; echo "  git commit -m \"upstream: <what> (cherry picked from commit $c)\""; exit 1; }
        echo "Staged. Drop hunks you do not want (git restore -p --staged . ; git restore -p .), build and test,"
        echo "then: git commit -m \"upstream: <what>\" -m \"(cherry picked from commit $c)\"  and  tools/upstream.sh port $c HEAD \"partial\""
        exit 0
    fi
    if ! git cherry-pick -x "$c"; then
        echo; echo "Conflicts. Fix them keeping our enhancements (git status shows the files), then:"
        echo "  git add <files> && git cherry-pick --continue      or give up with: git cherry-pick --abort"
        echo "rerere is on, so the same resolution is reused if this comes up again."
        exit 1
    fi
    ledger_row "$c" picked "cherry-pick" "find it with: git log --grep=$(git rev-parse --short=10 "$c")"
    git add "$ledger" && git commit --quiet --amend --no-edit
    if [ $notest = 0 ] && command -v dotnet >/dev/null; then
        dotnet build LiteView.sln -c Release --nologo -v q && dotnet test LiteView.sln -c Release --nologo -v q \
            || { echo "Build or tests FAIL after the pick. Fix forward, or undo with: git reset --hard HEAD~1"; exit 1; }
    fi
    ;;
skip)
    [ $# -ge 2 ] || die "usage: skip <sha> <reason>"
    c=$(full "$1"); shift; is_upstream "$c" || die "$c is not on $remote/$branch"
    ledger_row "$c" skipped "-" "$*"
    ;;
port)
    [ $# -ge 3 ] || die "usage: port <sha> <our-commit> <note>"
    c=$(full "$1"); o=$(git rev-parse --short "$(full "$2")"); shift 2; is_upstream "$c" || die "$c is not on $remote/$branch"
    ledger_row "$c" ported "$o" "$*"
    ;;
done)
    [ $# -ge 1 ] || die "usage: done <sha>"
    c=$(full "$1"); is_upstream "$c" || die "$c is not on $remote/$branch"
    git merge-base --is-ancestor "$(reviewed)" "$c" || die "$c is older than the last reviewed commit"
    printf '%s\n' "$c" > "$marker"
    echo "reviewed up to $(git log -1 --format='%h %s' "$c"). Commit $marker and $ledger:"
    echo "  git add $marker $ledger && git commit -m \"upstream: reviewed up to $(git rev-parse --short "$c")\""
    ;;
file)
    [ $# -ge 1 ] || die "usage: file <path>"
    p=$1; base=$(reviewed); need_remote
    echo "=== upstream changes to $p since the last review ($(git rev-parse --short "$base")..$remote/$branch)"
    git --no-pager diff "$base" "$remote/$branch" -- "$p"
    echo; echo "=== this fork's changes to $p since the same point"
    git --no-pager diff "$base" HEAD -- "$p"
    ;;
*) sed -n '2,20p' "$0"; exit 1;;
esac
