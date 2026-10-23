#!/usr/bin/env bash
# Pack-on visit harness for the single-walk work. Runs SingleWalkVisitProbe (Strat Book tests,
# Environmental) once per arm and demo. The process runs every scenario and trial itself, each cell in its
# own fresh config directory and its own booted app, and this script prints the cells per arm.
#
# The gate is the decode counters (exact integers), asserted inside the probe, so the default is ONE trial.
# Wall time, memory and allocation are reported, not gated. Use --trials N --warmup only when a wall-time
# comparison is the point, and then interleave A/B in one session; the recorded run-to-run drift is 27-31%.
#
# Usage:
#   tools/single-walk-bench/run.sh --tmp <dir> [--a <repo>] [--b <repo>] [--demos <file>]
#       [--visits sweep,open] [--trials N] [--warmup] [-c Release|Debug] [--out <file.jsonl>] [--no-build]
#
# --tmp      Required. Scratch root for the per-trial config directories. Never the live config dir.
# --a, --b   Checkouts to compare. Default: this checkout for both, which is the A/A run that sets the
#            session's noise band. Each arm is built once, then the arms alternate A, B, A, B.
# --demos    File of demo paths, one per line. Default: every demos/benchmarks/*.dem of arm A. Demos are
#            read in place, never copied, moved or linked.
# --visits   Scenarios, comma separated. sweep: import with every background pass on, every stamp absent.
#            open: imported with the sweeps off, then opened through the shell; the whole open is
#            measured: read, parse, the shell's rules run and scoreboard write, then the open's visit.
#            open-stale: open with the round facts and facts stamps cleared first, so the open's visit
#            writes them.
#            recheck: import with the sweeps on, then re-enlist the demo for the library's score repair;
#            only the re-check visit is measured, with the library row present and every stamp current.
# --trials   Measured trials per demo, scenario and arm. Default 1.
# --warmup   Run one extra leading trial per scenario, kept in the output as trial 0 and left out of the
#            medians. Only for wall-time runs; the counters do not need it.
#
# One heavy job at a time on a 16 GB machine: run the whole script under the machine's heavy-job lock,
# not each step.
set -euo pipefail

HERE="$(cd "$(dirname "$0")/../.." && pwd)"
A="$HERE"
B=""
DEMOS=""
VISITS="sweep,open"
TRIALS=1
WARMUP=0
CONFIG=Release
OUT=""
TMP=""
BUILD=1
while [ $# -gt 0 ]; do
  case "$1" in
    --a) A="$(cd "$2" && pwd)"; shift 2 ;;
    --b) B="$(cd "$2" && pwd)"; shift 2 ;;
    --demos) DEMOS="$2"; shift 2 ;;
    --visits) VISITS="$2"; shift 2 ;;
    --trials) TRIALS="$2"; shift 2 ;;
    --warmup) WARMUP=1; shift ;;
    -c) CONFIG="$2"; shift 2 ;;
    --out) OUT="$2"; shift 2 ;;
    --tmp) TMP="$2"; shift 2 ;;
    --no-build) BUILD=0; shift ;;
    *) echo "usage: $0 --tmp <dir> [--a <repo>] [--b <repo>] [--demos <file>] [--visits sweep,open] [--trials N] [-c Release|Debug] [--out <file>] [--no-build]" >&2; exit 2 ;;
  esac
done

if [ -z "$TMP" ]; then
  echo "error: --tmp names the scratch root for config directories" >&2
  exit 2
fi
mkdir -p "$TMP"
B="${B:-$A}"
PROJ_REL="src/Extensions/StratBook/DemoViewer.NET.Extensions.StratBook.Tests"
OUT="${OUT:-$TMP/single-walk-$(date +%Y%m%d-%H%M%S).jsonl}"
: > "$OUT"

if [ -z "$DEMOS" ]; then
  DEMOS="$TMP/single-walk-demos.txt"
  ls "$A"/demos/benchmarks/*.dem > "$DEMOS"
fi

if [ "$BUILD" = 1 ]; then
  for repo in $(printf '%s\n' "$A" "$B" | sort -u); do
    echo "[single-walk] building $repo ($CONFIG)"
    dotnet build "$repo/$PROJ_REL" -c "$CONFIG" -v q --nologo
  done
fi

run_demo() {
  # $1 arm label, $2 repo, $3 demo. One process covers every scenario and trial.
  # --output Detailed is load-bearing: at Normal the runner prints a passing test's stdout nowhere, so
  # the @SW_VISIT rows are only visible when the probe FAILS its decode targets.
  local out status=0 line n=0
  out="$(SW_DEMO="$3" SW_VISIT="$VISITS" SW_TRIALS="$TRIALS" SW_WARMUP="$WARMUP" SW_CONFIG_ROOT="$TMP" \
    dotnet run --project "$2/$PROJ_REL" -c "$CONFIG" --no-build -- \
    --treenode-filter "/*/*/SingleWalkVisitProbe/Visit/*" --output Detailed 2>&1)" || status=$?
  while IFS= read -r line; do
    [ -n "$line" ] || continue
    n=$((n + 1))
    python3 -c 'import json,sys; d=json.loads(sys.argv[1]); d["arm"]=sys.argv[2]; print(json.dumps(d))' \
      "$line" "$1" >> "$OUT"
    python3 -c 'import json,sys; d=json.loads(sys.argv[1]); print("[single-walk] %s %s trial %s %s: analysis decodes %s, shell decodes %s %s, reads %s, wallMs %s" % (sys.argv[2], d["visit"], d["trial"], d["demo"], d.get("analysisDecodes"), d.get("shellDecodes"), d.get("shellSites") or "", d.get("reads"), d.get("wallMs")))' \
      "$line" "$1"
  done < <(echo "$out" | grep -F '@SW_VISIT ' | sed 's/^.*@SW_VISIT //')
  if [ "$n" = 0 ]; then
    echo "$out" | tail -n 40
    return 1
  fi
  # The probe prints every cell, then fails if any missed its decode target; keep the rows.
  if [ "$status" != 0 ]; then
    echo "[single-walk] warning: $1 $(basename "$3") missed its decode targets"
    MISSED=1
  fi
}

MISSED=0
while IFS= read -r demo; do
  [ -n "$demo" ] || continue
  run_demo A "$A" "$demo"
  run_demo B "$B" "$demo"
done < "$DEMOS"

echo "[single-walk] results: $OUT (trial 0, if any, is left out of the summary)"
python3 - "$OUT" <<'PY'
import json, statistics, sys
rows = [json.loads(l) for l in open(sys.argv[1]) if l.strip()]
rows = [r for r in rows if r["trial"] > 0 or not any(x["trial"] > 0 for x in rows)]
metrics = ["wallMs", "peakWsMb", "peakWsLessFileMb", "peakHeapMb", "allocatedMb", "gc2", "decodes", "analysisDecodes", "shellDecodes", "reads"]
keys = sorted({(r["visit"], r["demo"]) for r in rows})
sources = sorted({r.get("counterSource", "hooks") for r in rows})
if len(sources) > 1:
    print("warning: decodes come from different counters across rows (" + ", ".join(sources) + "); do not compare them")
counters = ("decodes", "analysisDecodes", "shellDecodes", "hookDecodes", "reads")
unstable = []
for arm, visit, demo in sorted({(r["arm"], r["visit"], r["demo"]) for r in rows}):
    sel = [r for r in rows if (r["arm"], r["visit"], r["demo"]) == (arm, visit, demo)]
    for c in counters:
        if len({r.get(c) for r in sel}) > 1:
            unstable.append(f"{arm} {visit} {demo} {c}")
if unstable:
    print("warning: counters that should be exact differed between trials: " + "; ".join(unstable))
def stat(vals):
    return statistics.median(vals), min(vals), max(vals)
print("visit  demo  arm  " + "  ".join(f"{m}(med [min..max])" for m in metrics))
for visit, demo in keys:
    meds = {}
    for arm in ("A", "B"):
        sel = [r for r in rows if r["visit"] == visit and r["demo"] == demo and r["arm"] == arm]
        if not sel:
            continue
        cells = []
        for m in metrics:
            vals = [r[m] for r in sel if m in r]
            if not vals:
                cells.append("-")
                continue
            med, lo, hi = stat(vals)
            meds[(arm, m)] = med
            cells.append(f"{med:g} [{lo:g}..{hi:g}]")
        print(f"{visit}  {demo}  {arm}  " + "  ".join(cells))
    deltas = []
    for m in ("wallMs", "peakWsMb", "peakHeapMb"):
        a, b = meds.get(("A", m)), meds.get(("B", m))
        if a and b:
            deltas.append(f"{m} {100.0 * (b - a) / a:+.1f}%")
    print(f"{visit}  {demo}  B-vs-A  " + ", ".join(deltas))
PY
exit "$MISSED"
