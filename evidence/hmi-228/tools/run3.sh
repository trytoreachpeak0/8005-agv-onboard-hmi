#!/usr/bin/env bash
# usage: run3.sh <out-dir> <label> <filter> [runs]
# Builds once, runs the filtered tests N times, keeps each run's full output.
set -u
WT=/c/Users/szy/Desktop/8005-workspace-v2/worktrees/hmi228-8005-agv-onboard-hmi
OUT="$(cd "$(dirname "$0")" && pwd)/$1"; LABEL="$2"; FILTER="$3"; RUNS="${4:-3}"
[ -n "$OUT" ] || { echo "empty out dir"; exit 2; }
mkdir -p "$OUT"
cd "$WT" || exit 2
dotnet build tests/SQCD.Agv.WireToGateG2Tests -c Release -v q > "$OUT/$LABEL.build.log" 2>&1
if ! grep -q "Build succeeded" "$OUT/$LABEL.build.log"; then echo "BUILD FAILED"; grep -E "error" "$OUT/$LABEL.build.log" | head -20; exit 1; fi
for i in $(seq 1 "$RUNS"); do
  dotnet test tests/SQCD.Agv.WireToGateG2Tests -c Release --no-build --filter "$FILTER" > "$OUT/$LABEL.run$i.log" 2>&1
  echo "run$i exit=$? $(grep -E '^(Passed|Failed)!' "$OUT/$LABEL.run$i.log")"
done
grep -h -A8 "\[FAIL\]" "$OUT/$LABEL.run1.log" | head -40
