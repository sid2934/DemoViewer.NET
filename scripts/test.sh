#!/usr/bin/env bash
# The one entry point for running this repository's tests, at a chosen TIER.
#
#   scripts/test.sh [-t fast|standard|full] [-p PROJECT|all] [-c Release|Debug] [-n] [-l]
#
#   -t  tier (default: standard — the in-flight default; see docs/playback2d-v2/plans/P3-test-tiers.md)
#   -p  project key or `all` (default: all).  Keys: playback2d cli app livesync trimmer visualization
#   -c  configuration (default: Release, matching CI)
#   -n  no build — assume the binaries are current
#   -l  list only — discover and count, run nothing
#
# ── Why this script exists ──────────────────────────────────────────────────────────────────────────
#
# Three runner behaviours make hand-typed invocations unsafe, and all three are encapsulated here.
#
#  1. `dotnet test --filter "Category=X"` is SILENTLY IGNORED by this runner. TUnit runs on
#     Microsoft.Testing.Platform and registers no `--filter` option; in VSTest mode the argument is
#     swallowed and the ENTIRE suite runs, reporting success. Everything must go through
#     `-- --treenode-filter`, and this script is the only place that string is written.
#
#  2. The tree-node filter parser CRASHES on the obvious spelling of a boolean. Inside `[...]`,
#     `&` and `|` bind TIGHTER than `=`, so `[Category!=A&Category!=B]` throws
#     `System.InvalidOperationException` out of `TreeNodeFilter.ProcessStackOperator` before a
#     single test runs. Every operand must be individually parenthesised —
#     `[(Category!=A)&(Category!=B)]` — which is what TIER_FILTER below emits. Worse spellings fail
#     SILENTLY rather than loudly: top-level `|` between two whole paths matches EVERYTHING, top-level
#     `&` drops the second clause, and unary `!` is not a token at all on this platform version, so
#     `[!(Category=A)]` also matches everything. Do not invent a shorthand.
#
#  3. `dotnet test` collapses every platform exit code to MSBuild's `1`, which loses the distinction
#     between "tests failed" (2), "the filter matched nothing" (8) and "bad arguments" (5) — exactly
#     the distinctions a tier runner has to report. `dotnet run` preserves them, so `dotnet run` is
#     what this script uses.
#
# The filter strings below are asserted against their canonical definition in
# tests/shared/TestTiers.cs by TestTierContractTests, so editing one here without editing the other
# turns every suite red rather than quietly changing what a tier means.
set -u

TIER=standard
PROJECT=all
CONFIG=Release
BUILD=1
LIST_ONLY=0

while getopts "t:p:c:nlh" opt; do
  case $opt in
    t) TIER=$OPTARG ;;
    p) PROJECT=$OPTARG ;;
    c) CONFIG=$OPTARG ;;
    n) BUILD=0 ;;
    l) LIST_ONLY=1 ;;
    h) sed -n '2,12p' "$0"; exit 0 ;;
    *) echo "usage: $0 [-t fast|standard|full] [-p PROJECT|all] [-c CONFIG] [-n] [-l]" >&2; exit 2 ;;
  esac
done

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT" || exit 2

# ── Tier definitions ────────────────────────────────────────────────────────────────────────────────
# Mirrors tests/shared/TestTiers.cs exactly (TestTierContractTests asserts it, character for
# character). Exclusion, never inclusion: an untagged test is in every tier, so a new unit test is
# covered the moment it is written.
case "$TIER" in
  fast)
    TIER_FILTER='/*/*/*/*[(Category!=Budget)&(Category!=Environmental)&(Category!=Gpu)&(Category!=Integration)&(Category!=RealDemo)&(Category!=Render)]'
    TIER_BLURB='pure unit + contract — no demo, no pixels, no process, no benchmark' ;;
  standard)
    TIER_FILTER='/*/*/*/*[(Category!=Budget)&(Category!=Environmental)&(Category!=Integration)&(Category!=RealDemo)]'
    TIER_BLURB='the in-flight default — fast plus the render and golden gates' ;;
  full)
    TIER_FILTER='/*/*/*/*'
    TIER_BLURB='everything — what CI and a pre-push review run' ;;
  *)
    echo "unknown tier '$TIER' (expected fast, standard or full)" >&2; exit 2 ;;
esac

# ── Project registry ────────────────────────────────────────────────────────────────────────────────
# key|path. Ordered cheapest-first so a broken build or an obvious regression surfaces in seconds
# rather than after the slowest suite has finished.
PROJECTS=(
  "visualization|src/Visualization/DemoViewer.NET.Visualization.Tests"
  "trimmer|tools/DemoViewer.NET.DemoTrimmer.Tests"
  "livesync|src/Testing/DemoViewer.NET.LiveSync.Tests"
  "playback2d|src/Playback2D/DemoViewer.NET.Playback2D.Tests"
  "cli|tools/DemoViewer.NET.Playback2D.Cli.Tests"
  "app|src/App/DemoViewer.NET.App.Tests"
)

SELECTED=()
if [ "$PROJECT" = "all" ]; then
  SELECTED=("${PROJECTS[@]}")
else
  for entry in "${PROJECTS[@]}"; do
    [ "${entry%%|*}" = "$PROJECT" ] && SELECTED+=("$entry")
  done
  if [ ${#SELECTED[@]} -eq 0 ]; then
    echo "unknown project '$PROJECT'. Known: all $(for e in "${PROJECTS[@]}"; do printf '%s ' "${e%%|*}"; done)" >&2
    exit 2
  fi
fi

echo "[test] tier=$TIER ($TIER_BLURB)"
echo "[test] filter=$TIER_FILTER"
echo "[test] config=$CONFIG projects=${#SELECTED[@]}"

# ── Build once, up front ────────────────────────────────────────────────────────────────────────────
# Per-project `dotnet run` without --no-build would re-evaluate the shared graph once per suite; one
# build of the selected projects is both faster and the only way the timings printed below mean
# anything.
if [ "$BUILD" -eq 1 ]; then
  BUILD_START=$(date +%s)
  for entry in "${SELECTED[@]}"; do
    # Quiet on success, everything on failure: a green build's output is noise that pushes the
    # numbers this script exists to print off the top of the terminal.
    if ! BUILD_OUT=$(dotnet build "${entry#*|}" -c "$CONFIG" -v q --nologo 2>&1); then
      echo "$BUILD_OUT" >&2
      echo "[test] BUILD FAILED: ${entry#*|}" >&2
      exit 2
    fi
  done
  echo "[test] build: $(( $(date +%s) - BUILD_START ))s"
fi

# ── Run ─────────────────────────────────────────────────────────────────────────────────────────────
TOTAL=0; FAILED=0; SKIPPED=0; BROKEN=0
SUITE_START=$(date +%s)

for entry in "${SELECTED[@]}"; do
  KEY="${entry%%|*}"
  PATH_="${entry#*|}"

  ARGS=(--treenode-filter "$TIER_FILTER" --disable-logo --no-progress)
  [ "$LIST_ONLY" -eq 1 ] && ARGS+=(--list-tests)

  START=$(date +%s%N)
  OUT=$(dotnet run --project "$PATH_" -c "$CONFIG" --no-build -- "${ARGS[@]}" 2>&1)
  EXIT=$?
  MS=$(( ( $(date +%s%N) - START ) / 1000000 ))

  if [ "$LIST_ONLY" -eq 1 ]; then
    N=$(echo "$OUT" | grep -oE 'found [0-9]+ test' | grep -oE '[0-9]+' | head -1)
    printf '  %-14s %6sms  discovered=%s\n' "$KEY" "$MS" "${N:-?}"
    TOTAL=$(( TOTAL + ${N:-0} ))
    continue
  fi

  T=$(echo "$OUT" | grep -oE '^  total: [0-9]+'     | grep -oE '[0-9]+' | head -1)
  F=$(echo "$OUT" | grep -oE '^  failed: [0-9]+'    | grep -oE '[0-9]+' | head -1)
  S=$(echo "$OUT" | grep -oE '^  skipped: [0-9]+'   | grep -oE '[0-9]+' | head -1)
  TOTAL=$(( TOTAL + ${T:-0} )); FAILED=$(( FAILED + ${F:-0} )); SKIPPED=$(( SKIPPED + ${S:-0} ))

  # Exit 8 is "the filter matched no tests". For a tier run that is a defect in the tier, not a pass:
  # it means every test in the suite carries a tag this tier drops, which is never intended.
  STATUS=ok
  if [ $EXIT -eq 8 ]; then STATUS="NO-TESTS-MATCHED"; BROKEN=$(( BROKEN + 1 ));
  elif [ $EXIT -ne 0 ]; then STATUS="FAILED(exit=$EXIT)"; BROKEN=$(( BROKEN + 1 )); fi

  printf '  %-14s %7sms  total=%-5s failed=%-4s skipped=%-4s %s\n' \
    "$KEY" "$MS" "${T:-?}" "${F:-0}" "${S:-0}" "$STATUS"

  if [ $EXIT -ne 0 ] && [ $EXIT -ne 8 ]; then
    echo "$OUT" | grep -E '^failed |AssertionException|Unhandled exception' | head -20
  fi
done

echo "[test] ─────────────────────────────────────────────────────────────"
if [ "$LIST_ONLY" -eq 1 ]; then
  echo "[test] tier=$TIER discovered=$TOTAL across ${#SELECTED[@]} project(s) in $(( $(date +%s) - SUITE_START ))s"
  exit 0
fi
echo "[test] tier=$TIER  total=$TOTAL  failed=$FAILED  skipped=$SKIPPED  wall=$(( $(date +%s) - SUITE_START ))s"
[ $BROKEN -eq 0 ] || exit 1
exit 0
