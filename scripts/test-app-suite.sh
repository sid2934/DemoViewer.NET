#!/usr/bin/env bash
# Runs the full App test suite as N sequential PROCESS batches instead of one process.
#
# Why: a single process accumulating the whole suite (multi-GB ParsedDemo cache entries,
# entity replays, render surfaces) peaks ~4-5 GB — on a memory-pressured 16 GB dev machine
# the OS kills it mid-suite (observed as silent SIGKILL/SIGABRT with no crash report).
# Sequential batches cap the per-process peak and return memory between batches. Batch
# membership is computed from the discovered class list, so new test classes are always
# covered — a partition bug shows up as a count mismatch, not silent loss.
#
# Shell portability: this script used to be #!/bin/zsh and indexed CLASSES from 1, which is
# a zsh array convention. Under bash — Git Bash on Windows, and most Linux CI images — the
# same loop silently skipped the FIRST class and then aborted on the last iteration with
# "CLASSES[$i]: unbound variable" under set -u, so the partition audit never got to run.
# It now iterates the array itself with a 0-based counter, which means the same thing in
# both shells; there is no remaining zsh-ism, so bash is the shebang.
#
# Tiers: this script is the MEMORY-SAFE way to run the App suite, and is orthogonal to
# scripts/test.sh, which is the tier entry point for every suite. Passing -t composes the tier's
# category filter into each batch's class filter, so `-t full` (the default here, since batching
# exists precisely for the runs too heavy to do in one process) and `-t standard` both work. For a
# single-process run of one tier, prefer `scripts/test.sh -t TIER -p app`; come back here when the
# suite is holding real demos and the process is being OS-killed.
#
# Usage: scripts/test-app-suite.sh [-c Release|Debug] [-n BATCHES] [-t fast|standard|full]
set -u
CONFIG=Release
BATCHES=3
TIER=full
while getopts "c:n:t:" opt; do
  case $opt in
    c) CONFIG=$OPTARG ;;
    n) BATCHES=$OPTARG ;;
    t) TIER=$OPTARG ;;
    *) echo "usage: $0 [-c CONFIG] [-n BATCHES] [-t TIER]" >&2; exit 2 ;;
  esac
done

# Kept character-for-character in step with scripts/test.sh and tests/shared/TestTiers.cs — the
# contract test asserts the script text, so a drifting copy turns every suite red.
case "$TIER" in
  fast)     TIER_FILTER='[(Category!=Budget)&(Category!=Environmental)&(Category!=Gpu)&(Category!=Integration)&(Category!=RealDemo)&(Category!=Render)]' ;;
  standard) TIER_FILTER='[(Category!=Budget)&(Category!=Environmental)&(Category!=Integration)&(Category!=RealDemo)]' ;;
  full)     TIER_FILTER='' ;;
  *) echo "unknown tier '$TIER' (expected fast, standard or full)" >&2; exit 2 ;;
esac

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJ="$ROOT/src/App/DemoViewer.NET.App.Tests"
cd "$ROOT" || exit 2

echo "[batch-runner] building $CONFIG..."
dotnet build "$PROJ" -c "$CONFIG" -v q --nologo || exit 2

# The Playback2D direct-execution suite runs FIRST and UNBATCHED: it loads no Avalonia platform and
# holds no ParsedDemo, so it is neither slow nor a memory-pressure risk — and it is the fastest signal
# that the scene core is broken, which is worth having before the heavy batches start.
PB2D_PROJ="$ROOT/src/Playback2D/DemoViewer.NET.Playback2D.Tests"
echo "[batch-runner] playback2d core+pipeline (direct execution, tier=$TIER)"
dotnet run --project "$PB2D_PROJ" -c "$CONFIG" -- --treenode-filter "/*/*/*/*$TIER_FILTER" || exit 1

# Discover test classes from SOURCE (files bearing [Test]); '--list-tests' prints bare
# method names, so it can't provide class names. Helper classes caught by the grep are
# harmless (they match zero tests in the filter).
#
# TWO ROOTS, not one. tests/shared is compiled into this assembly as LINKED source, so its classes are
# every bit as much part of the partition as the ones under $PROJ — and walking only $PROJ left
# TestTierContractTests (6 tests: the vocabulary guard, the script-vs-TestTiers.cs text check, the
# tier-nesting proof) in no batch at all. Found while wiring this script into CI, where the lane would
# have selected on tier filters while never running the test that keeps them honest.
SRC_ROOTS=("$PROJ" "$ROOT/tests/shared")
CLASSES=($(grep -rlE '\[Test\]' "${SRC_ROOTS[@]}" --include="*.cs" \
  | xargs grep -hE "^public (sealed |partial )*class" \
  | sed -E 's/^public (sealed |partial )*class ([A-Za-z0-9_]+).*/\2/' | sort -u))
# Discovery is done UNDER THE TIER FILTER, so the partition audit below compares like with like: a
# tiered run legitimately executes fewer tests than the assembly holds, and an untiered floor would
# make every `-t fast` run look like silent loss.
EXPECTED=$(dotnet run --project "$PROJ" -c "$CONFIG" --no-build -- --list-tests \
  --treenode-filter "/*/*/*/*$TIER_FILTER" 2>/dev/null | grep -cE '^  [A-Za-z0-9_]+$')
if [ ${#CLASSES[@]} -lt 5 ] || [ "$EXPECTED" -lt 10 ]; then
  echo "[batch-runner] discovery failed (classes=${#CLASSES[@]} expected-tests=$EXPECTED) — refusing to run a partial suite" >&2
  exit 2
fi

# The audit the batch TOTALS cannot do. '--list-tests' counts a parametrized test once while the run
# expands it, so the floor comparison at the bottom ("ran >= listed") is satisfied with room to spare
# even when a whole class is missing — which is exactly how the tests/shared gap above stayed invisible
# for the life of this script. Listing under the CLASS filter and comparing it to the same listing
# unfiltered is EXACT: both sides count the same way, so any difference is a class the grep missed.
COVERED=$(dotnet run --project "$PROJ" -c "$CONFIG" --no-build -- --list-tests \
  --treenode-filter "/*/*/($(IFS='|'; echo "${CLASSES[*]}"))/*$TIER_FILTER" 2>/dev/null \
  | grep -cE '^  [A-Za-z0-9_]+$')
if [ "$COVERED" -ne "$EXPECTED" ]; then
  echo "[batch-runner] DISCOVERY AUDIT FAILED: the class list covers $COVERED of the $EXPECTED tests" \
       "this assembly lists — a class escaped the source grep, and every batch below would silently" \
       "skip it" >&2
  exit 2
fi
echo "[batch-runner] ${#CLASSES[@]} test classes, $EXPECTED tests (all covered), $BATCHES batches, tier=$TIER"

TOTAL_FAILED=0
TOTAL_RUN=0
TOTAL_LINE=""
for ((b = 1; b <= BATCHES; b++)); do
  # Round-robin partition: class i goes to batch (i mod BATCHES). Keeps batches balanced
  # even though heavy classes cluster alphabetically (Playback*, Z*).
  MEMBERS=()
  i=0
  for CLS in "${CLASSES[@]}"; do
    if (( i % BATCHES == b - 1 )); then MEMBERS+=("$CLS"); fi
    i=$((i + 1))
  done
  # Path alternation for the class segment, then the tier's category filter on the method segment.
  # Both halves in one expression is supported; what is NOT is a boolean between two whole paths, or
  # an unparenthesised operand inside the brackets — see scripts/test.sh for why.
  FILTER="/*/*/($(IFS='|'; echo "${MEMBERS[*]}"))/*$TIER_FILTER"
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
