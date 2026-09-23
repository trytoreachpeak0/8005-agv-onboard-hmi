#!/usr/bin/env bash
# Reverse verification for onboard-hmi#206. Each state: one exact replacement in the product file (exits unless it
# matches exactly once), --no-incremental rebuild that must print "0 Error(s)", the two new cases run N times, then
# the product file is restored from the backup and its SHA-256 checked.
set -u
cd /c/Users/szy/Desktop/8005-workspace-v2/worktrees/hmi206-8005-agv-onboard-hmi || exit 1
F=src/SQCD.Agv.Infrastructure/WireToGateSessionClient.cs
OUT=${1:?output file}
N=${2:-10}
BACKUP="$TEMP/wtgsc-fix.bak"
FIXED_SHA=$(sha256sum "$F" | cut -d' ' -f1)
cp -p "$F" "$BACKUP"
FILTER="FullyQualifiedName~AHandshakeSnapshotAfterAResentSafetyChangeTakesAHigherRevisionInTheSameGeneration"

run_state() {
  local name=$1 from=$2 to=$3
  echo "=== $name" >> "$OUT"
  if [ -n "$from" ]; then
    local count
    count=$(FROM="$from" python -c "import os,io;print(io.open(r'$F',encoding='utf-8',newline='').read().count(os.environ['FROM']))")
    if [ "$count" != 1 ]; then echo "replacement matched $count times, abort" >> "$OUT"; exit 3; fi
    FROM="$from" TO="$to" python -c "import os,io;p=r'$F';s=io.open(p,encoding='utf-8',newline='').read();s=s.replace(os.environ['FROM'],os.environ['TO'],1);io.open(p,'w',encoding='utf-8',newline='').write(s)"
  fi
  git diff --stat -- "$F" >> "$OUT"
  git diff -U0 -- "$F" | grep -E '^[+-][^+-]' >> "$OUT"
  local build
  build=$(timeout 900 dotnet build tests/SQCD.Agv.WireToGateG2Tests --no-incremental -v q -nologo 2>&1 | grep -E 'Error\(s\)')
  echo "build: $build" >> "$OUT"
  case "$build" in *" 0 Error(s)"*) ;; *) echo "build failed, abort" >> "$OUT"; exit 4;; esac
  for i in $(seq 1 "$N"); do
    local res
    res=$(timeout 300 dotnet test tests/SQCD.Agv.WireToGateG2Tests --no-build --filter "$FILTER" 2>&1)
    echo "run $i: $(echo "$res" | grep -E 'Failed!|Passed!' | sed -E 's/, Duration.*//')" >> "$OUT"
    echo "$res" | grep -E '^\s+Failed SQCD' -A2 | grep -vE '^\s+Error Message:|^--' | sed -E 's/ \[[0-9]+ m?s\]//' >> "$OUT"
  done
  cp -p "$BACKUP" "$F"
  touch "$F"
  local sha
  sha=$(sha256sum "$F" | cut -d' ' -f1)
  echo "restored: $sha $( [ "$sha" = "$FIXED_SHA" ] && echo OK || echo MISMATCH )" >> "$OUT"
  [ "$sha" = "$FIXED_SHA" ] || exit 5
}

: > "$OUT"
echo "head: $(git rev-parse HEAD); product file with fix: $FIXED_SHA; N=$N" >> "$OUT"
run_state "FIXED" "" ""
run_state "M1 fix removed: the handshake snapshot takes the accepted revision again" \
  "            long safetyStateVersion = resentSafetyStateChange
                ? checked(acceptedSafetyStateVersion + 1)
                : acceptedSafetyStateVersion;" \
  "            long safetyStateVersion = acceptedSafetyStateVersion;"
run_state "M2 the accepted revision is not advanced to the snapshot's" \
  "            AdvanceSafetyStateVersion(safetyStateVersion);
" \
  ""
run_state "M3 the resend is never noticed (the flag is never set)" \
  "                resentSafetyStateChange |= string.Equals(" \
  "                _ = string.Equals("
echo "done" >> "$OUT"
