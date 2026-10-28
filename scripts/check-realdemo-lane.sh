#!/bin/bash
# Fails when a RealDemo lane SELECTS tests and RUNS none.
#
# A skip is not a failure, so a lane whose demos stopped resolving exits 0 on its own. This turns
# that into a red: a configured corpus means these cases must RUN, not report skipped.
#
# --minimum-expected-tests counts tests that RAN, not tests that were selected. Measured, all four:
#   corpus present, 5 run,  no minimum -> 0      corpus present, minimum 5 -> 0
#   corpus empty,   5 skip, no minimum -> 0      corpus empty,   minimum 5 -> 9
#
# ARMED ONLY when DEMO_CORPUS or DEMO_SOURCES is set. A machine with neither is MEANT to skip these:
# that is the no-corpus lane, not a regression, and arming it there would red every such run.
#
# Called by scripts/test.sh (full tier) and scripts/test-app-suite.sh, which is the path CI's app
# lane actually takes. Those two reach the App suite by different routes, so the guard lives here
# once rather than in both.
#
# A LANE argument is required in practice: each guard names the lane that builds its project, and
# running the App guard from the ext lane would invoke a project that lane never built. With no lane
# every guard runs, which is only correct for `-p all`.
set -u

CONFIG="${1:-Release}"
LANE="${2:-}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT" || exit 2

if [ -z "${DEMO_CORPUS:-}" ] && [ -z "${DEMO_SOURCES:-}" ]; then
  exit 0
fi

# lane|project path|treenode filter|how many that filter must actually RUN
#
# One canary per lane, not per class: every RealDemo class resolves through the same DemoTestHelper
# and Corpus layer, so the systemic break this guard is named for shows up in any of them. The
# minimum is the floor the canary can reach, not its case count. RoundIndexRealDemoTests has 5 cases
# and two carry their own legitimate skips, for a demo with no rounds and for the nuke-only adjacency
# oracle, so 3 is the floor and 5 would red on a corpus that is merely not de_nuke.
GUARDS=(
  "app|src/App/DemoViewer.NET.App.Tests|/*/*/MatchMetaRealDemoTests/*|5"
  "ext|src/Extensions/StratBook/DemoViewer.NET.Extensions.StratBook.Tests|/*/*/RoundIndexRealDemoTests/*|3"
)

STATUS=0
for guard in "${GUARDS[@]}"; do
  IFS='|' read -r G_LANE G_PATH G_FILTER G_MIN <<< "$guard"
  if [ -n "$LANE" ] && [ "$G_LANE" != "$LANE" ]; then
    continue
  fi

  dotnet run --project "$G_PATH" -c "$CONFIG" --no-build -- \
    --treenode-filter "$G_FILTER" --disable-logo --progress off \
    --minimum-expected-tests "$G_MIN" > /dev/null 2>&1
  G_EXIT=$?
  if [ $G_EXIT -eq 0 ]; then
    echo "[realdemo] ok: >=$G_MIN ran for $G_FILTER ($G_LANE)"
  else
    echo "[realdemo] !!! GUARD FAILED (exit=$G_EXIT). A corpus or source is configured, so" >&2
    echo "[realdemo] !!! '$G_FILTER' must RUN at least $G_MIN tests and it did not. They were" >&2
    echo "[realdemo] !!! selected and skipped: demo resolution is BROKEN, not absent." >&2
    STATUS=1
  fi
done

exit $STATUS
