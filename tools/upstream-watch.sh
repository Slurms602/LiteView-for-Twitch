#!/usr/bin/env bash
# Used by .github/workflows/upstream-watch.yml: lists upstream commits newer than .upstream-reviewed and
# opens or updates the "Upstream changes to review" issue. With DRY_RUN=1 it only prints the issue text.
set -euo pipefail
cd "$(git rev-parse --show-toplevel)"
upstream=${UPSTREAM:-https://github.com/itsdeeezy/LiteView-for-Twitch.git}
title="Upstream changes to review"

git fetch --quiet --no-tags "$upstream" "+refs/heads/main:refs/remotes/upstream-watch/main"
head=refs/remotes/upstream-watch/main
base=$(tr -d ' \r\n' < .upstream-reviewed)

if ! git cat-file -e "$base^{commit}" 2>/dev/null || ! git merge-base --is-ancestor "$base" "$head"; then
    body="The last reviewed upstream commit \`${base:0:10}\` is no longer in upstream's main branch (history rewritten?). Check by hand: \`tools/upstream.sh status\`."
else
    n=$(git rev-list --count "$base..$head")
    if [ "$n" = 0 ]; then echo "Upstream has nothing new since ${base:0:10}."; exit 0; fi
    body=$(
        echo "Upstream ([itsdeeezy/LiteView-for-Twitch](${upstream%.git})) has **$n** commit(s) since the last review (\`${base:0:10}\`), oldest first:"
        echo
        git log --reverse --format='- `%h` %ad **%s**' --date=short "$base..$head"
        echo
        echo "Files touched:"
        echo
        git diff --stat=100 "$base" "$head" | sed 's/^/    /'
        echo
        echo "Review locally: \`tools/upstream.sh status\`, then pick / port / skip each commit (docs/UPSTREAM.md)."
        echo "This issue is updated weekly by the upstream-watch workflow; it never merges anything."
    )
fi

if [ "${DRY_RUN:-0}" = 1 ]; then printf '%s\n\n%s\n' "# $title" "$body"; exit 0; fi
num=$(gh issue list --state open --search "\"$title\" in:title" --json number,title --jq ".[] | select(.title == \"$title\") | .number" | head -1)
if [ -n "$num" ]; then gh issue edit "$num" --body "$body"; echo "updated issue #$num"
else gh issue create --title "$title" --body "$body"; fi
