#!/bin/sh
# Regenerates the Playback2D v2 golden corpus under tests/fixtures/playback2d/goldens/cpu/.
#
# Goldens are a gate, not a cache: a missing golden FAILS the suite unless PB2D_GOLDEN_UPDATE=1 is
# set, which is what this script sets. Run it only for a DELIBERATE visual change, and look at the
# images (and `git diff --stat`) before committing them. A golden that rewrites itself silently is a
# test that no longer tests.
#
# Usage: scripts/update-playback2d-goldens.sh [-c Release|Debug] [--scene-only]
set -eu

CONFIG=Release
SCENE_ONLY=0
while [ $# -gt 0 ]; do
  case "$1" in
    -c) CONFIG="$2"; shift 2 ;;
    --scene-only) SCENE_ONLY=1; shift ;;
    *) echo "usage: $0 [-c CONFIG] [--scene-only]" >&2; exit 2 ;;
  esac
done

# The prev2-* PNGs are FROZEN ON PURPOSE and nothing here regenerates them. They are the pre-v2
# control's own output, and that control is deleted, so the only thing a writer could put in those
# files is the v2 render they are supposed to be compared AGAINST. GoldenParityTests reads all three
# and would then be comparing v2 to itself. When a deliberate v2 change finally moves one past
# GoldenDistribution.PreV2Capture, retire that parity case with the reason, do not re-baseline it.
#
# Their scenes are not frozen, and step 3 renders v2's own goldens from two of them.

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

export PB2D_GOLDEN_UPDATE=1

# 1. The synthetic family plus the asset-backed entries SceneGoldenTests judges: CPU-provider renders
#    of the committed fixtures over the committed assets/ bundles. No demo, no Avalonia, so this half
#    always runs. It pins the PRODUCTION layer stack through HeadlessSceneRenderer, the same path
#    `dv2d golden verify` uses, over the same PNGs, so the two readers of those files cannot disagree.
#    --scene-only stops the script after this step. It does not narrow the step.
echo "[goldens] scene corpus (direct execution)"
dotnet run --project src/Playback2D/DemoViewer.NET.Playback2D.Tests -c "$CONFIG" \
  -- --treenode-filter "/*/*/SceneGoldenTests/*"

if [ "$SCENE_ONLY" = "1" ]; then
  exit 0
fi

# 1b. The hand-authored budget fixture. It is generated from code (SyntheticScenes) rather than
#     captured, but it is still a corpus entry that dv2d loads by name, so the committed JSON has to be
#     regenerated whenever the generator changes; a test asserts the two agree.
echo "[goldens] budget fixture (generated)"
dotnet run --project src/Playback2D/DemoViewer.NET.Playback2D.Tests -c "$CONFIG"   -- --treenode-filter "/*/*/BudgetFixtureCorpusTests/*"

# 2. The level family: nuke-multilevel-upper and nuke-multilevel-noradar are rendered from the
#    COMMITTED nuke-multilevel scene (one floor at full height, and the same scene with no radar
#    bound). Direct execution: no demo needed, only the committed scene and the de_nuke bundle.
echo "[goldens] levels (direct execution)"
dotnet run --project src/Playback2D/DemoViewer.NET.Playback2D.Tests -c "$CONFIG" \
  -- --treenode-filter "/*/*/LevelGoldenTests/*"

# 3. The captured family: v2's own render of the two real-demo captures, over the baked de_mirage and
#    de_dust2 floors and radar. Written through `dv2d golden update` rather than a test, so the
#    writer and `dv2d golden verify` are the same code by construction. The pre-v2 PNGs of these same
#    two scenes are the frozen prev2-* pair above; these are the regenerable v2 gate.
echo "[goldens] captured scenes (dv2d)"
"$ROOT/scripts/dv2d.sh" golden update --name mirage-roundstart
"$ROOT/scripts/dv2d.sh" golden update --name dust2-roundstart

echo "[goldens] done. Review the diff before committing:"
echo "          git status --short tests/fixtures/playback2d"
