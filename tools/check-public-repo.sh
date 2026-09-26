#!/bin/sh
# Refuses a push that would put working notes or build output into the published repo.
#
# This repository will be linked from the mod's Paradox Mods listing, so its contents are part of what
# players and reviewers judge the mod by. Local working notes and build output are process
# leftovers - the same category as bin/ or a .pid file - and do not belong in a published
# artifact. A note asking for the check to be run by hand was not enough; this makes it automatic.
#
# Installed as .git/hooks/pre-push. Hooks are not cloned, so after a fresh clone run:
#   cp tools/check-public-repo.sh .git/hooks/pre-push && chmod +x .git/hooks/pre-push
#
# Run it by hand any time with:
#   sh tools/check-public-repo.sh

set -e

FORBIDDEN='(^|/)_dev/|(^|/)(AGENTS|DESIGN|CLAUDE|NOTES|TODO|SESSION|LESSONS)\.md$|(^|/)Library/|\.pid$|(^|/)(bin|obj)/|\.user$|(^|/)UI/dist/|(^|/)node_modules/|(^|/)__pycache__/|\.pyc$|\.zip$'

found=$(git ls-files | grep -Ei "$FORBIDDEN" || true)

if [ -n "$found" ]; then
	echo "" >&2
	echo "  PUSH BLOCKED - these are tracked and must not be in the public repo:" >&2
	echo "" >&2
	echo "$found" | sed 's/^/      /' >&2
	echo "" >&2
	echo "  Working notes belong in _dev/ (gitignored), build output in .gitignore." >&2
	echo "  Untrack with:  git rm --cached <file>" >&2
	echo "" >&2
	echo "  Override only if you are certain:  git push --no-verify" >&2
	echo "" >&2
	exit 1
fi

exit 0
