#!/bin/sh
# Bakes CS2's iconography out of a local Counter-Strike 2 install into assets/icons/.
#
# Wraps `dotnet run --project tools/DemoViewer.NET.AssetBaker` for one reason worth having a script
# for: that project's default RuntimeIdentifier is osx-arm64, so every invocation from any other
# machine needs an -r flag, and forgetting it fails in a way that does not mention RIDs. This picks
# the host RID.
#
# The output IS COMMITTED (assets/icons/), so this is run only when the curation in
# tools/DemoViewer.NET.AssetBaker/IconSet.cs changes or CS2 ships new artwork — not as part of a
# build. Nobody needs CS2 installed to build the app or to run CI.
#
# Usage:
#   scripts/bake-icons.sh                        bake the curated set
#   scripts/bake-icons.sh --list                 what CS2 ships, with what we already take marked
#   scripts/bake-icons.sh --list defus           ...filtered by substring
#   scripts/bake-icons.sh --list --free          ...only what is NOT yet taken
#   scripts/bake-icons.sh --cs2=<path>           point at a vpk or install root Steam discovery misses
#
# See docs/ui/game-icons.md for the whole add-an-icon loop.
set -eu

PROJECT=tools/DemoViewer.NET.AssetBaker

# Host RID. The baker only ever runs on a developer machine that has CS2 on it, so these three cover it.
case "$(uname -s)" in
    Darwin) RID=$([ "$(uname -m)" = "arm64" ] && echo osx-arm64 || echo osx-x64) ;;
    Linux)  RID=linux-x64 ;;
    *)      RID=win-x64 ;;   # MINGW/MSYS/Cygwin under git-bash
esac

MODE=--icons
FILTER=
PASSTHROUGH=

for arg in "$@"; do
    case "$arg" in
        --list)   MODE=--list-icons ;;
        --free|--taken) PASSTHROUGH="$PASSTHROUGH $arg" ;;
        --cs2=*)  PASSTHROUGH="$PASSTHROUGH $arg" ;;
        --*)      PASSTHROUGH="$PASSTHROUGH $arg" ;;
        *)        FILTER="$arg" ;;
    esac
done

if [ "$MODE" = "--list-icons" ] && [ -n "$FILTER" ]; then
    MODE="--list-icons=$FILTER"
fi

echo "baking icons: RID=$RID mode=$MODE"
# shellcheck disable=SC2086 -- PASSTHROUGH is a deliberately word-split flag list
dotnet run --project "$PROJECT" -r "$RID" -- "$MODE" $PASSTHROUGH
