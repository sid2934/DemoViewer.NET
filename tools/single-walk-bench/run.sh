#!/usr/bin/env bash
# Pack-on visit harness for the single-walk work. Runs SingleWalkVisitProbe (Strat Book tests,
# Environmental) once per arm, demo, scenario and trial, each in its own process and its own fresh config
# directory, and prints medians with min and max per arm.
#
# Usage:
#   tools/single-walk-bench/run.sh --tmp <dir> [--a <repo>] [--b <repo>] [--demos <file>]
#       [--visits sweep,open] [--trials N] [-c Release|Debug] [--out <file.jsonl>] [--no-build]
#
# --tmp      Required. Scratch root for the per-trial config directories. Never the live config dir.
# --a, --b   Checkouts to compare. Default: this checkout for both, which is the A/A run that sets the
#            session's noise band. Each arm is built once, then the arms alternate A, B, A, B.
# --demos    File of demo paths, one per line. Default: every demos/benchmarks/*.dem of arm A. Demos are
#            read in place, never copied, moved or linked.
# --visits   Scenarios, comma separated. sweep: import with every background pass on, every stamp absent.
#            open: imported with the sweeps off, then opened; the open's visit is measured.
# --trials   Measured trials per demo, scenario and arm. Default 3, after one discarded trial.
#
# One heavy job at a time on a 16 GB machine: run the whole script under the machine's heavy-job lock,
# not each step.
set -euo pipefail

HERE="$(cd "$(dirname "$0")/../.." && pwd)"
A="$HERE"
B=""
DEMOS=""
VISITS="sweep,open"
TRIALS=3
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

run_one() {
  # $1 arm label, $2 repo, $3 demo, $4 scenario, $5 trial
  local out line
  out="$(SW_DEMO="$3" SW_VISIT="$4" SW_CONFIG_ROOT="$TMP" \
    dotnet run --project "$2/$PROJ_REL" -c "$CONFIG" --no-build -- \
    --treenode-filter "/*/*/SingleWalkVisitProbe/Visit/*" 2>&1)" || { echo "$out" | tail -n 40; return 1; }
  line="$(echo "$out" | grep -F '@SW_VISIT ' | tail -n1 | sed 's/^.*@SW_VISIT //')"
  if [ -z "$line" ]; then
    echo "$out" | tail -n 40
    return 1
  fi
  python3 -c 'import json,sys; d=json.loads(sys.argv[1]); d["arm"]=sys.argv[2]; d["trial"]=int(sys.argv[3]); print(json.dumps(d))' \
    "$line" "$1" "$5" >> "$OUT"
  echo "[single-walk] $1 trial $5 $4 $(basename "$3"): $line"
}

IFS=',' read -r -a SCENARIOS <<< "$VISITS"
for ((t = 0; t <= TRIALS; t++)); do
  while IFS= read -r demo; do
    [ -n "$demo" ] || continue
    for scenario in "${SCENARIOS[@]}"; do
      run_one A "$A" "$demo" "$scenario" "$t"
      run_one B "$B" "$demo" "$scenario" "$t"
    done
  done < "$DEMOS"
done

echo "[single-walk] results: $OUT (trial 0 discarded)"
python3 - "$OUT" <<'PY'
import json, statistics, sys
rows = [json.loads(l) for l in open(sys.argv[1]) if l.strip()]
rows = [r for r in rows if r["trial"] > 0]
metrics = ["wallMs", "peakWsMb", "peakWsLessFileMb", "peakHeapMb", "decodes"]
keys = sorted({(r["visit"], r["demo"]) for r in rows})
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
            med, lo, hi = stat([r[m] for r in sel])
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
