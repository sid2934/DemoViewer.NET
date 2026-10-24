#!/usr/bin/env bash
# Prints the project directory of every package this repository publishes, one per line.
#
# Derived from IsPackable rather than listed: Directory.Build.props defaults it to false and only the
# SDK libraries turn it back on, so the property already IS the list. It used to be restated in
# ci.yml, release-sdk.yml's pack step, release-sdk.yml's PublicAPI gate and ship-public-api.sh, which
# meant four places to find when the family gained Playback2D.Scene.
#
# Consumers must capture the output first (`dirs=$(bash scripts/sdk-packages.sh)`) and loop over the
# variable. `for d in $(...)` swallows a nonzero exit here and runs the body zero times, which turns
# the release's Unshipped gate into a no-op.
set -euo pipefail
cd "$(dirname "$0")/.."

DIRS=$(git ls-files '*.csproj' | xargs grep -l '<IsPackable>true</IsPackable>' | xargs -n1 dirname | sort)
if [ -z "$DIRS" ]; then
    echo "error: no project sets IsPackable=true, so there is nothing to publish" >&2
    exit 1
fi

# A derived list can reach a project that never acquired the analyzer's lists, and release-sdk.yml
# gates the release on `grep -qv '^#nullable'` over Unshipped. grep on a missing file exits 2, which
# the gate's `if` reads as "no entries", so the release would go out ungated.
for dir in $DIRS; do
    for list in PublicAPI.Shipped.txt PublicAPI.Unshipped.txt; do
        if [ ! -f "$dir/$list" ]; then
            echo "error: $dir sets IsPackable=true but has no $list" >&2
            exit 1
        fi
    done
done

echo "$DIRS"
