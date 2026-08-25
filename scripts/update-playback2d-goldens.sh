#!/bin/sh
# Regenerates the Playback2D v2 golden corpus under tests/fixtures/playback2d/goldens/cpu/.
#
# Goldens are a gate, not a cache: a missing golden FAILS the suite unless PB2D_GOLDEN_UPDATE=1 is
# set, which is what this script sets. Run it only for a DELIBERATE visual change, and look at the
# images (and `git diff --stat`) before committing them — a golden that silently rewrites itself is
# a test that no longer tests.
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

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

export PB2D_GOLDEN_UPDATE=1

# 1. The synthetic family: CPU-provider renders of the hand-authored fixtures. No demo, no Avalonia,
#    so this half always runs. It pins B0's own render loop (palette, transform, compositor, fixture
#    format) — NOT the pre-v2 control.
echo "[goldens] synthetic (direct execution)"
dotnet run --project src/Playback2D/DemoViewer.NET.Playback2D.Tests -c "$CONFIG" \
  -- --treenode-filter "/*/*/SceneGoldenTests/*"

if [ "$SYNTHETIC_ONLY" = "1" ]; then
  exit 0
fi

# 1b. The hand-authored budget fixture. It is generated from code (SyntheticScenes) rather than
#     captured, but it is still a corpus entry that dv2d loads by name, so the committed JSON has to be
#     regenerated whenever the generator changes — a test asserts the two agree.
echo "[goldens] budget fixture (generated)"
dotnet run --project src/Playback2D/DemoViewer.NET.Playback2D.Tests -c "$CONFIG"   -- --treenode-filter "/*/*/BudgetFixtureCorpusTests/*"

# 2. The demo-derived family: headless captures of the CURRENT Playback2DViewport, paired with the
#    SceneFixture built from the same push. This is the corpus B1 must match. Each case SKIPS when its
#    demo is absent, so a checkout with no demos staged is a no-op here rather than a failure.
echo "[goldens] demo-derived (headless Avalonia; skips without a demo)"
dotnet run --project src/App/DemoViewer.NET.App.Tests -c "$CONFIG" \
  -- --treenode-filter "/*/*/Playback2DGoldenCaptureTests/*"

echo "[goldens] done — review the diff before committing:"
echo "          git status --short tests/fixtures/playback2d"
