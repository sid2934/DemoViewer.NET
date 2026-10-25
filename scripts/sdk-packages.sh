#!/usr/bin/env bash
# Prints the project directory of every package this repository publishes, one per line.
#
# A text match for the IsPackable element over the csproj bodies, not a hand-kept list:
# Directory.Build.props defaults the property to false and only the published libraries turn it back
# on. It is a grep, not an evaluation, so three shapes fool it and fool all four consumers at once: a
# Condition on the element, a `$(...)` value, and the property set anywhere but the csproj. Spell it
# plainly in the csproj and nowhere else.
#
# Consumers must capture the output (`dirs=$(bash scripts/sdk-packages.sh)`) and loop over the
# variable. `for d in $(...)` swallows a nonzero exit here and runs the body zero times, which turns
# the release's Unshipped gate into a no-op.
set -euo pipefail
cd "$(dirname "$0")/.."

# `|| true`: grep exits 1 on no match, and under pipefail that would end the run here, before the
# diagnostic below can say what went wrong.
MATCHED=$(git ls-files '*.csproj' | xargs grep -lEi '<IsPackable>[[:space:]]*true[[:space:]]*</IsPackable>' || true)
DIRS=$(printf '%s' "$MATCHED" | sed '/^$/d; s|/[^/]*$||' | sort -u)
if [ -z "$DIRS" ]; then
    echo "error: no project sets IsPackable=true, so there is nothing to publish" >&2
    exit 1
fi

# release-sdk.yml gates on `grep -qv '^#nullable'` over Unshipped, and grep on a missing file exits
# 2, which the gate's `if` reads as "no entries". A derived list can reach a project that has no such
# file, so it has to be refused here or that release goes out ungated.
for dir in $DIRS; do
    for list in PublicAPI.Shipped.txt PublicAPI.Unshipped.txt; do
        if [ ! -f "$dir/$list" ]; then
            echo "error: $dir sets IsPackable=true but has no $list" >&2
            exit 1
        fi
    done
done

echo "$DIRS"
