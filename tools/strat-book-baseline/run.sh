#!/usr/bin/env bash
# Strat Book extension plan, item M0 and item 9 (docs/architecture/strat-book-plugin.md §12): resident
# memory and per-demo indexing time, on a COPY of a real config dir, never the live one. Runs the three
# StratBookPackBaselineTests probes in DemoViewer.NET.App.Tests (each [Category("Environmental")], so the
# standard tier never runs them) and prints medians. Item 9 reruns this unchanged against its own config
# copy and its own head, with the pack gated off, to fill the other two rows of §12.
#
# Usage:
#   tools/strat-book-baseline/run.sh --config <config-copy-dir> [--demos <demo-list-file>] [-c Release|Debug]
#
# --config   Required. A COPY of ~/Library/Application Support/DemoViewer.NET/ (or the platform
#            equivalent), never the live directory. At minimum: settings.json, library.json, teams.json,
#            review-queue.json, strats/, tags/, strat-mining.json and cache/ (round-index/, suggestions/,
#            team-index.json, demos/, the grenade sidecars and grenade-lineups.json.gz). Leave out
#            lineup-clips/ (rendered GIFs, nothing here reads them), logs/, crash.log and *.bak. In the
#            copy's settings.json, set Highlights.BackgroundScan, ProcessingQueue.BackgroundProcessingEnabled,
#            Situations.BackgroundIndex, Grenades.BackgroundIndex and Grenades.RenderLineupClips to false,
#            so the only work at boot is the fixed startup-load list App.axaml.cs runs today.
# --demos    Optional. A file of demo paths, one per line, the first line a discarded warm-up. Demos are
#            read in place, NEVER copied or moved. Default: picked from <config>/library.json below, the
#            8 demos nearest the library's size median (by file size), skipping anything under assets/tour
#            (the sample demo is incomplete, see the project memory note) and anything not present on disk.
#
# Never writes to --config beyond what the app itself writes there (cache sidecars from the indexing-time
# probe's Evaluate calls); never touches the live config dir; never copies, moves or deletes a .dem file.
#
# Start every invocation from a FRESH copy. The indexing-time probe rewrites its demos' round index, round
# facts, suggested-tags and highlights sidecars on every run, so a copy reused across invocations is not
# the state the resident-memory probes above it were measured against, and a copy reused across several
# indexing-time runs is on its second or third genuine recompute, not its first.
set -euo pipefail

CONFIG=""
DEMOS=""
BUILD_CONFIG=Release
while [ $# -gt 0 ]; do
  case "$1" in
    --config) CONFIG="$2"; shift 2 ;;
    --demos) DEMOS="$2"; shift 2 ;;
    -c) BUILD_CONFIG="$2"; shift 2 ;;
    *) echo "usage: $0 --config <dir> [--demos <file>] [-c Release|Debug]" >&2; exit 2 ;;
  esac
done

if [ -z "$CONFIG" ] || [ ! -d "$CONFIG" ]; then
  echo "error: --config must name an existing config-copy directory" >&2
  exit 2
fi

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
PROJ="$ROOT/src/App/DemoViewer.NET.App.Tests"
cd "$ROOT"

if [ -z "$DEMOS" ]; then
  DEMOS="$(mktemp -t strat-book-baseline-demos)"
  python3 - "$CONFIG/library.json" "$DEMOS" <<'PY'
import json, os, sys
library_path, out_path = sys.argv[1], sys.argv[2]
with open(library_path) as f:
    cache = json.load(f)["Cache"]
rows = [(e["Path"], e["Size"]) for e in cache
        if os.path.exists(e["Path"]) and "assets/tour" not in e["Path"].replace("\\", "/")]
rows.sort(key=lambda r: r[1])
n = len(rows)
mid = n // 2
lo = max(0, mid - 4)
picked = rows[max(0, lo - 1):lo + 8]
if len(picked) < 2:
    sys.exit("not enough demos on disk to pick a warm-up plus a measured set")
with open(out_path, "w") as f:
    f.write("\n".join(p for p, _ in picked) + "\n")
PY
  echo "[strat-book-baseline] picked $(wc -l < "$DEMOS" | tr -d ' ') demos (1 warm-up + median-sized) from $CONFIG/library.json"
  trap 'rm -f "$DEMOS"' EXIT
fi

echo "[strat-book-baseline] building $BUILD_CONFIG..."
dotnet build "$PROJ" -c "$BUILD_CONFIG" -v q --nologo

run_probe() {
  local method="$1"
  DV_M0_CONFIG="$CONFIG" DV_M0_DEMOS="$DEMOS" \
    dotnet run --project "$PROJ" -c "$BUILD_CONFIG" --no-build -- \
    --treenode-filter "/*/*/StratBookPackBaselineTests/$method/*"
}

median() {
  # Prints the median of its newline-separated numeric args (stdin), sorted numerically.
  sort -n | awk '{a[NR]=$1} END {if (NR==0) {print "NaN"} else if (NR%2==1) {print a[(NR+1)/2]} else {print (a[NR/2]+a[NR/2+1])/2}}'
}

echo "[strat-book-baseline] resident set after startup, one boot per process, 3 trials"
WS=() GCM=() COM=()
for i in 1 2 3; do
  OUT="$(run_probe ResidentSetAfterStartup)"
  echo "$OUT" | grep '@M0_RESIDENT' || { echo "$OUT"; exit 1; }
  LINE="$(echo "$OUT" | grep '@M0_RESIDENT')"
  WS+=("$(echo "$LINE" | grep -oE '"workingSetMb":[0-9.]+' | cut -d: -f2)")
  GCM+=("$(echo "$LINE" | grep -oE '"gcMb":[0-9.]+' | cut -d: -f2)")
  COM+=("$(echo "$LINE" | grep -oE '"committedMb":[0-9.]+' | cut -d: -f2)")
done
echo "[strat-book-baseline] resident set medians: workingSetMb=$(printf '%s\n' "${WS[@]}" | median) gcMb=$(printf '%s\n' "${GCM[@]}" | median) committedMb=$(printf '%s\n' "${COM[@]}" | median) (privateMb reads 0 on macOS)"

echo "[strat-book-baseline] pack resident cost, direct construction, 3 trials"
SIT=() GRN=() TM=() TOT=()
for i in 1 2 3; do
  OUT="$(run_probe PackResidentCostDirect)"
  echo "$OUT" | grep '@M0_PACKCOST' || { echo "$OUT"; exit 1; }
  LINE="$(echo "$OUT" | grep '@M0_PACKCOST')"
  SIT+=("$(echo "$LINE" | grep -oE '"situationsMb":[0-9.]+' | cut -d: -f2)")
  GRN+=("$(echo "$LINE" | grep -oE '"grenadesMb":[0-9.]+' | cut -d: -f2)")
  TM+=("$(echo "$LINE" | grep -oE '"teamsMb":[0-9.]+' | cut -d: -f2)")
  TOT+=("$(echo "$LINE" | grep -oE '"totalMb":[0-9.]+' | cut -d: -f2)")
done
echo "[strat-book-baseline] pack resident cost medians: situationsMb=$(printf '%s\n' "${SIT[@]}" | median) grenadesMb=$(printf '%s\n' "${GRN[@]}" | median) teamsMb=$(printf '%s\n' "${TM[@]}" | median) totalMb=$(printf '%s\n' "${TOT[@]}" | median)"

echo "[strat-book-baseline] indexing time per demo, full evaluator list versus library+highlights only"
OUT="$(run_probe IndexingTimePerDemo)"
echo "$OUT" | grep '@M0_INDEXMS ' # per-demo lines; excludes the summary below by the trailing space
echo "$OUT" | grep '@M0_INDEXMS_SUMMARY' || { echo "$OUT"; exit 1; }

echo "[strat-book-baseline] done. Copy the numbers above into §12 of docs/architecture/strat-book-plugin.md."
