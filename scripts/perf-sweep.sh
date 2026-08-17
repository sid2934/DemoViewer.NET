#!/usr/bin/env bash
# Perf sweep over the full demo corpus. Keeping the whole recipe in one script means a
# re-run months later measures the same thing the same way.
# Usage:  scripts/perf-sweep.sh <run-id>   ("test" = dry run on one demo)
#         Writes docs/perf/parser-and-entity-decode/runs/<run-id>/{results.tsv, *.profile.txt}
#         (local output, not tracked). Summarize keepers into
#         docs/perf/parser-and-entity-decode/results.md (see "How to record a run" there).
# Wall-clock from NON-profile runs (median of 3 warm, 1 cold discarded); per-pass breakdown from a --profile run.
set -uo pipefail
cd "$(dirname "$0")/.."
DLL="artifacts/bin/AnalysisBench/release/AnalysisBench.dll"
RUNID="${1:-baseline}"
OUT="docs/perf/parser-and-entity-decode/runs/${RUNID}"; mkdir -p "$OUT"
RESULTS="$OUT/results.tsv"
printf "demo\tsource\ttotal_med_ms\tparse_ms\teval_ms\tpass1_ms\tpass2_ms\tpass3_ms\tprecompute_ms\tframes\tcompressed\teval_alloc_MiB\n" > "$RESULTS"

n1() { grep -oE '[0-9][0-9.,]*' | head -1 | tr -d ','; }   # first number, strip thousands commas

if [ "$RUNID" = "test" ]; then
  demos=( demos/benchmarks/003816248937665266002_0544286934.dem )
else
  demos=( demos/benchmarks/*.dem demos/pro-demos/*.dem )
fi

for demo in "${demos[@]}"; do
  name=$(basename "$demo" .dem); abs="$(pwd)/$demo"
  echo ">>> $name"
  dotnet "$DLL" "$abs" --no-golden >/dev/null 2>&1            # cold discard
  totals=()
  for i in 1 2 3; do                                          # 3 warm
    out=$(dotnet "$DLL" "$abs" --no-golden 2>&1)
    totals+=( "$(echo "$out" | grep 'Total (parse+build+eval)' | n1)" )
    parse=$(echo "$out" | grep -E '^  Parse:' | n1)
    eval=$(echo "$out"  | grep -E '^  Eval:'  | n1)
  done
  med=$(printf '%s\n' "${totals[@]}" | sort -n | sed -n '2p')
  prof=$(dotnet "$DLL" "$abs" --profile --no-golden 2>&1)     # breakdown
  echo "$prof" > "$OUT/$name.profile.txt"
  src=$(echo "$prof" | grep -oE 'Source: [A-Za-z]+' | head -1 | sed 's/Source: //')
  p1=$(echo "$prof" | grep 'Pass 1' | grep -oE '[0-9.]+ ms' | n1)
  p2=$(echo "$prof" | grep 'Pass 2' | grep -oE '[0-9.]+ ms' | n1)
  p3=$(echo "$prof" | grep 'Pass 3' | grep -oE '[0-9.]+ ms' | n1)
  pc=$(echo "$prof" | grep 'Parallel precompute' | grep -oE '[0-9.]+ ms' | n1)
  fr=$(echo "$prof" | grep 'Pass 1' | grep -oE '[0-9,]+ frames' | n1)
  cmp=$(echo "$prof" | grep 'Pass 1' | grep -oE '[0-9,]+ compressed' | n1)
  ea=$(echo "$prof" | grep 'Eval allocated' | grep -oE '[0-9.]+ MiB' | n1)
  printf "%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n" \
    "$name" "$src" "$med" "$parse" "$eval" "$p1" "$p2" "$p3" "$pc" "$fr" "$cmp" "$ea" >> "$RESULTS"
done
echo "=== PERF SWEEP DONE → $OUT ==="; column -t -s $'\t' "$RESULTS"
echo "Next: run the correctness gates (see candidates doc → Verification protocol), save to $OUT/correctness.txt,"
echo "      then add a row/column + insights to docs/perf/parser-and-entity-decode/results.md."
