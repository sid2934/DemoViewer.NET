#!/usr/bin/env bash
# Prints the project directory of every package this repository publishes, one per line.
#
# Derived from IsPackable, not listed: Directory.Build.props defaults it to false and only the SDK
# libraries turn it back on, so the property IS the list.
#
# Consumers must capture the output (`dirs=$(bash scripts/sdk-packages.sh)`) and loop over the
# variable. `for d in $(...)` swallows a nonzero exit here and runs the body zero times, which turns
# the release's Unshipped gate into a no-op.
set -euo pipefail
cd "$(dirname "$0")/.."

DIRS=$(git ls-files '*.csproj' | xargs grep -l '<IsPackable>true</IsPackable>' | xargs -n1 dirname | sort)
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
