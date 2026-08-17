#!/bin/zsh
# Runs the full App test suite as N sequential PROCESS batches instead of one process.
#
# Why: a single process accumulating the whole suite (multi-GB ParsedDemo cache entries,
# entity replays, render surfaces) peaks ~4-5 GB — on a memory-pressured 16 GB dev machine
# the OS kills it mid-suite (observed as silent SIGKILL/SIGABRT with no crash report).
# Sequential batches cap the per-process peak and return memory between batches. Batch
# membership is computed from the discovered class list, so new test classes are always
# covered — a partition bug shows up as a count mismatch, not silent loss.
#
# Usage: scripts/test-app-suite.sh [-c Release|Debug] [-n BATCHES]
set -u
CONFIG=Release
BATCHES=3
while getopts "c:n:" opt; do
  case $opt in
    c) CONFIG=$OPTARG ;;
    n) BATCHES=$OPTARG ;;
    *) echo "usage: $0 [-c CONFIG] [-n BATCHES]" >&2; exit 2 ;;
  esac
done

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJ="$ROOT/src/App/DemoViewer.NET.App.Tests"
cd "$ROOT" || exit 2

echo "[batch-runner] building $CONFIG..."
dotnet build "$PROJ" -c "$CONFIG" -v q --nologo || exit 2

# Discover test classes from SOURCE (files bearing [Test]); '--list-tests' prints bare
# method names, so it can't provide class names. Helper classes caught by the grep are
# harmless (they match zero tests in the filter). The audit below catches discovery GAPS:
# the sum of batch totals must equal the assembly's own discovered test count.
CLASSES=($(grep -rlE '\[Test\]' "$PROJ" --include="*.cs" \
  | xargs grep -hE "^public (sealed |partial )*class" \
  | sed -E 's/^public (sealed |partial )*class ([A-Za-z0-9_]+).*/\2/' | sort -u))
EXPECTED=$(dotnet run --project "$PROJ" -c "$CONFIG" --no-build -- --list-tests 2>/dev/null \
  | grep -cE '^  [A-Za-z0-9_]+$')
if [ ${#CLASSES[@]} -lt 5 ] || [ "$EXPECTED" -lt 10 ]; then
  echo "[batch-runner] discovery failed (classes=${#CLASSES[@]} expected-tests=$EXPECTED) — refusing to run a partial suite" >&2
  exit 2
fi
echo "[batch-runner] ${#CLASSES[@]} test classes, $EXPECTED tests, $BATCHES batches"

TOTAL_FAILED=0
TOTAL_RUN=0
TOTAL_LINE=""
for ((b = 1; b <= BATCHES; b++)); do
  # Round-robin partition: class i goes to batch (i mod BATCHES). Keeps batches balanced
  # even though heavy classes cluster alphabetically (Playback*, Z*).
  MEMBERS=()
  for ((i = 1; i <= ${#CLASSES[@]}; i++)); do
    if (( (i - 1) % BATCHES == b - 1 )); then MEMBERS+=("${CLASSES[$i]}"); fi
  done
  FILTER="/*/*/($(IFS='|'; echo "${MEMBERS[*]}"))/*"
  echo "[batch-runner] batch $b/$BATCHES: ${#MEMBERS[@]} classes"
  OUT=$(dotnet run --project "$PROJ" -c "$CONFIG" --no-build -- --treenode-filter "$FILTER" 2>&1)
  EX=$?
  SUMMARY=$(echo "$OUT" | grep -E "total:|failed:|succeeded:|skipped:" | tr -d ' ' | paste -sd' ' -)
  BATCH_TOTAL=$(echo "$SUMMARY" | grep -oE 'total:[0-9]+' | grep -oE '[0-9]+')
  TOTAL_RUN=$((TOTAL_RUN + ${BATCH_TOTAL:-0}))
  echo "[batch-runner] batch $b exit=$EX  $SUMMARY"
  if [ $EX -ne 0 ]; then
    TOTAL_FAILED=$((TOTAL_FAILED + 1))
    echo "$OUT" | grep -B2 -A8 -iE "failed|error" | head -60
  fi
  TOTAL_LINE="$TOTAL_LINE [b$b exit=$EX $SUMMARY]"
done

echo "[batch-runner] DONE:$TOTAL_LINE"
# Floor, not equality: '--list-tests' prints parametrized tests once, but they expand to
# multiple runtime tests (e.g. 200 listed -> 208 run), so the run count may legitimately
# exceed the listed count. Running FEWER than listed means a class escaped the source
# discovery grep — that is the silent-loss case this audit exists to catch.
if [ "$TOTAL_RUN" -lt "$EXPECTED" ]; then
  echo "[batch-runner] PARTITION AUDIT FAILED: batches ran $TOTAL_RUN tests, assembly discovers $EXPECTED — a class escaped discovery" >&2
  exit 1
fi
echo "[batch-runner] partition audit OK: ran $TOTAL_RUN >= $EXPECTED listed"
[ $TOTAL_FAILED -eq 0 ] || exit 1
