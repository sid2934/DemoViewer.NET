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
set -u

CONFIG="${1:-Release}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT" || exit 2

if [ -z "${DEMO_CORPUS:-}" ] && [ -z "${DEMO_SOURCES:-}" ]; then
  exit 0
fi

# project path|treenode filter|how many that filter must actually RUN
GUARDS=(
  "src/App/DemoViewer.NET.App.Tests|/*/*/MatchMetaRealDemoTests/*|5"
)

STATUS=0
for guard in "${GUARDS[@]}"; do
  G_PATH="${guard%%|*}"; G_REST="${guard#*|}"
  G_FILTER="${G_REST%|*}"; G_MIN="${G_REST##*|}"

  dotnet run --project "$G_PATH" -c "$CONFIG" --no-build -- \
    --treenode-filter "$G_FILTER" --disable-logo --progress off \
    --minimum-expected-tests "$G_MIN" > /dev/null 2>&1
  G_EXIT=$?
  if [ $G_EXIT -eq 0 ]; then
    echo "[realdemo] ok: >=$G_MIN ran for $G_FILTER"
  else
    echo "[realdemo] !!! GUARD FAILED (exit=$G_EXIT). A corpus or source is configured, so" >&2
    echo "[realdemo] !!! '$G_FILTER' must RUN at least $G_MIN tests and it did not. They were" >&2
    echo "[realdemo] !!! selected and skipped: demo resolution is BROKEN, not absent." >&2
    STATUS=1
  fi
done

exit $STATUS
