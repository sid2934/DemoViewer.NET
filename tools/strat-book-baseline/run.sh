#!/usr/bin/env bash
# Strat Book extension plan, item M0 and item 9 (docs/architecture/strat-book-plugin.md §12): resident
# memory and per-demo indexing time, on a COPY of a real config dir, never the live one. Runs the
# StratBookPackBaselineTests probes in DemoViewer.NET.App.Tests (each [Category("Environmental")], so the
# standard tier never runs them) and prints medians. --state selects which of M0's or item 9's probes run;
# item 9 is the same harness, a later head, and a config copy whose settings.json gates the pack.
#
# Usage:
#   tools/strat-book-baseline/run.sh --config <config-copy-dir> [--demos <demo-list-file>]
#       [-c Release|Debug] [--state on|off|toggle] [--trials N]
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
#            (the sample demo is incomplete and is never evidence) and anything not present on disk.
# --state    "on" (default): M0's three probes, pack on, against a config copy with no override (or an
#            explicit Features.Overrides.pack.stratbook = true). "off": item 9's pack-off counterparts;
#            the config copy's settings.json MUST carry Features.Overrides.pack.stratbook = false, or the
#            probe throws rather than silently measuring the wrong state. "toggle": item 9's in-session
#            on-then-off-then-on probe, on a config copy with the pack on at boot (no override).
# --trials   Number of process-per-trial repeats for the resident-set and toggle probes. Default 3; the
#            doc's §12 table wants 5 for "on" and "off", 3 for "toggle".
#
# Never writes to --config beyond what the app itself writes there (cache sidecars from the indexing-time
# probe's Evaluate/EvaluateForward calls); never touches the live config dir; never copies, moves or
# deletes a .dem file.
#
# Start every invocation from a FRESH copy. The indexing-time probe rewrites its demos' round index, round
# facts, suggested-tags and highlights sidecars on every run, so a copy reused across invocations is not
# the state the resident-memory probes above it were measured against, and a copy reused across several
# indexing-time runs is on its second or third genuine recompute, not its first.
set -euo pipefail

CONFIG=""
DEMOS=""
BUILD_CONFIG=Release
STATE=on
TRIALS=3
while [ $# -gt 0 ]; do
  case "$1" in
    --config) CONFIG="$2"; shift 2 ;;
    --demos) DEMOS="$2"; shift 2 ;;
    -c) BUILD_CONFIG="$2"; shift 2 ;;
    --state) STATE="$2"; shift 2 ;;
    --trials) TRIALS="$2"; shift 2 ;;
    *) echo "usage: $0 --config <dir> [--demos <file>] [-c Release|Debug] [--state on|off|toggle] [--trials N]" >&2; exit 2 ;;
  esac
done

if [ -z "$CONFIG" ] || [ ! -d "$CONFIG" ]; then
  echo "error: --config must name an existing config-copy directory" >&2
  exit 2
fi

case "$STATE" in
  on|off|toggle) ;;
  *) echo "error: --state must be on, off or toggle" >&2; exit 2 ;;
esac

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

# $1: tag (exact, no trailing space needed, matched with a trailing space so "@X" never also matches
# "@X_suffix" or vice versa), $2: json field, $3: output of one probe run.
field_of() {
  local tag="$1" field="$2" out="$3"
  echo "$out" | grep -F -- "$tag " | tail -n1 | grep -oE "\"$field\":-?[0-9.]+" | cut -d: -f2
}

resident_trials() {
  # $1: method name, $2: tag prefix (e.g. @M0_RESIDENT or @M0_RESIDENT_OFF), $3: trial count.
  local method="$1" prefix="$2" n="$3"
  local -a ws_e=() gc_e=() co_e=() ws_l=() gc_l=() co_l=()
  for ((i = 1; i <= n; i++)); do
    local out
    out="$(run_probe "$method")"
    echo "$out" | grep -F -- "${prefix}_early " >/dev/null || { echo "$out"; exit 1; }
    ws_e+=("$(field_of "${prefix}_early" workingSetMb "$out")")
    gc_e+=("$(field_of "${prefix}_early" gcMb "$out")")
    co_e+=("$(field_of "${prefix}_early" committedMb "$out")")
    ws_l+=("$(field_of "${prefix}_late" workingSetMb "$out")")
    gc_l+=("$(field_of "${prefix}_late" gcMb "$out")")
    co_l+=("$(field_of "${prefix}_late" committedMb "$out")")
  done
  echo "[strat-book-baseline] $prefix medians (n=$n): early workingSetMb=$(printf '%s\n' "${ws_e[@]}" | median) gcMb=$(printf '%s\n' "${gc_e[@]}" | median) committedMb=$(printf '%s\n' "${co_e[@]}" | median)"
  echo "[strat-book-baseline] $prefix medians (n=$n): late  workingSetMb=$(printf '%s\n' "${ws_l[@]}" | median) gcMb=$(printf '%s\n' "${gc_l[@]}" | median) committedMb=$(printf '%s\n' "${co_l[@]}" | median) (privateMb reads 0 on macOS)"
}

if [ "$STATE" = on ]; then
  echo "[strat-book-baseline] state=on: resident set after startup, one boot per process, $TRIALS trials"
  resident_trials ResidentSetAfterStartup "@M0_RESIDENT" "$TRIALS"

  echo "[strat-book-baseline] state=on: pack resident cost, direct construction, 3 trials"
  SIT=() GRN=() TM=() TOT=()
  for i in 1 2 3; do
    OUT="$(run_probe PackResidentCostDirect)"
    echo "$OUT" | grep -F -- '@M0_PACKCOST ' >/dev/null || { echo "$OUT"; exit 1; }
    SIT+=("$(field_of '@M0_PACKCOST' situationsMb "$OUT")")
    GRN+=("$(field_of '@M0_PACKCOST' grenadesMb "$OUT")")
    TM+=("$(field_of '@M0_PACKCOST' teamsMb "$OUT")")
    TOT+=("$(field_of '@M0_PACKCOST' totalMb "$OUT")")
  done
  echo "[strat-book-baseline] pack resident cost medians: situationsMb=$(printf '%s\n' "${SIT[@]}" | median) grenadesMb=$(printf '%s\n' "${GRN[@]}" | median) teamsMb=$(printf '%s\n' "${TM[@]}" | median) totalMb=$(printf '%s\n' "${TOT[@]}" | median)"

  echo "[strat-book-baseline] state=on: indexing time per demo, full evaluator list versus library+highlights only"
  OUT="$(run_probe IndexingTimePerDemo)"
  echo "$OUT" | grep '@M0_INDEXMS ' # per-demo lines; excludes the summary below by the trailing space
  echo "$OUT" | grep '@M0_INDEXMS_SUMMARY' || { echo "$OUT"; exit 1; }

elif [ "$STATE" = off ]; then
  echo "[strat-book-baseline] state=off: resident set after startup, pack gated off, one boot per process, $TRIALS trials"
  resident_trials ResidentSetAfterStartup_PackOff "@M0_RESIDENT_OFF" "$TRIALS"

  echo "[strat-book-baseline] state=off: indexing time per demo, library+highlights only (the pack's evaluators never want anything)"
  OUT="$(run_probe IndexingTimePerDemo_PackOff)"
  echo "$OUT" | grep '@M0_INDEXMS_OFF_RULESET'
  echo "$OUT" | grep '@M0_INDEXMS_OFF ' # per-demo lines; excludes the summary below by the trailing space
  echo "$OUT" | grep '@M0_INDEXMS_OFF_SUMMARY' || { echo "$OUT"; exit 1; }

else # toggle
  echo "[strat-book-baseline] state=toggle: on, then off (immediate and after 90s idle), then on again, in one process, $TRIALS trials"
  ON_B=() OFF_W=() OFF_L=() ON_A=()
  for ((i = 1; i <= TRIALS; i++)); do
    OUT="$(run_probe OnThenOffThenOn_InSession)"
    echo "$OUT" | grep -F -- '@TOGGLE_RESIDENT_on-before ' >/dev/null || { echo "$OUT"; exit 1; }
    ON_B+=("$(field_of '@TOGGLE_RESIDENT_on-before' gcMb "$OUT")")
    OFF_W+=("$(field_of '@TOGGLE_RESIDENT_off' gcMb "$OUT")")
    OFF_L+=("$(field_of '@TOGGLE_RESIDENT_off-late' gcMb "$OUT")")
    ON_A+=("$(field_of '@TOGGLE_RESIDENT_on-again' gcMb "$OUT")")
    echo "$OUT" | grep -F -- '@TOGGLE_RESIDENT_'
  done
  echo "[strat-book-baseline] toggle gcMb medians (n=$TRIALS): on-before=$(printf '%s\n' "${ON_B[@]}" | median) off=$(printf '%s\n' "${OFF_W[@]}" | median) off-late=$(printf '%s\n' "${OFF_L[@]}" | median) on-again=$(printf '%s\n' "${ON_A[@]}" | median)"
fi

echo "[strat-book-baseline] done. Copy the numbers above into §12 of docs/architecture/strat-book-plugin.md."
