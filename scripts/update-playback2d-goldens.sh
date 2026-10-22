#!/bin/sh
# Regenerates the Playback2D v2 golden corpus under tests/fixtures/playback2d/goldens/cpu/.
#
# Goldens are a gate, not a cache: a missing golden FAILS the suite unless PB2D_GOLDEN_UPDATE=1 is
# set, which is what this script sets. Run it only for a DELIBERATE visual change, and look at the
# images (and `git diff --stat`) before committing them. A golden that rewrites itself silently is a
# test that no longer tests.
#
# Usage: scripts/update-playback2d-goldens.sh [-c Release|Debug] [--synthetic-only]
set -eu

CONFIG=Release
SYNTHETIC_ONLY=0
while [ $# -gt 0 ]; do
  case "$1" in
    -c) CONFIG="$2"; shift 2 ;;
    --synthetic-only) SYNTHETIC_ONLY=1; shift ;;
    *) echo "usage: $0 [-c CONFIG] [--synthetic-only]" >&2; exit 2 ;;
  esac
done

# The prev2-* goldens and their paired scene fixtures are FROZEN: the control that captured them is
# gone, so nothing here regenerates them. A deliberate change to those three images is a re-baseline
# against the v2 compositor, committed on its own.

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

export PB2D_GOLDEN_UPDATE=1

# 1. The synthetic family: CPU-provider renders of the hand-authored fixtures. No demo, no Avalonia,
#    so this half always runs. It pins the PRODUCTION layer stack through HeadlessSceneRenderer, the
#    same path `dv2d golden verify` uses, over the same three PNGs, so the two readers of those files
#    cannot disagree.
echo "[goldens] synthetic (direct execution)"
dotnet run --project src/Playback2D/DemoViewer.NET.Playback2D.Tests -c "$CONFIG" \
  -- --treenode-filter "/*/*/SceneGoldenTests/*"

if [ "$SYNTHETIC_ONLY" = "1" ]; then
  exit 0
fi

# 1b. The hand-authored budget fixture. It is generated from code (SyntheticScenes) rather than
#     captured, but it is still a corpus entry that dv2d loads by name, so the committed JSON has to be
#     regenerated whenever the generator changes; a test asserts the two agree.
echo "[goldens] budget fixture (generated)"
dotnet run --project src/Playback2D/DemoViewer.NET.Playback2D.Tests -c "$CONFIG"   -- --treenode-filter "/*/*/BudgetFixtureCorpusTests/*"

# 2. The level family (B3): nuke-multilevel-upper and nuke-multilevel-noradar are rendered from the
#    COMMITTED nuke-multilevel scene (one floor at full height, and the same scene with no radar
#    bound). Direct execution: no demo needed, only the committed scene and the de_nuke bundle.
echo "[goldens] levels (direct execution)"
dotnet run --project src/Playback2D/DemoViewer.NET.Playback2D.Tests -c "$CONFIG" \
  -- --treenode-filter "/*/*/LevelGoldenTests/*"

echo "[goldens] done. Review the diff before committing:"
echo "          git status --short tests/fixtures/playback2d"
