#!/usr/bin/env bash
# Reverse verification for onboard-hmi#208. Each state: one exact replacement in the business service (exits unless it
# matches exactly once), --no-incremental rebuild that must print "0 Error(s)", the new cases and onboard-hmi#204's six
# run N times, then the file is restored from the backup and its SHA-256 checked.
set -u
cd /c/Users/szy/Desktop/8005-workspace-v2/worktrees/hmi208-8005-agv-onboard-hmi || exit 1
F=src/SQCD.Agv.Wpf/WireToGateBusinessService.cs
OUT=${1:?output file}
N=${2:-3}
BACKUP="$TEMP/hmi208-wtgbs-fix.bak"
FIXED_SHA=$(sha256sum "$F" | cut -d' ' -f1)
cp -p "$F" "$BACKUP"
FILTER="FullyQualifiedName~ASafetyChangeTheHandshakeDidNotCarry|FullyQualifiedName~MultiDemandJourneyG2Tests.ASafetyReport"

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
    local res code
    res=$(timeout 300 dotnet test tests/SQCD.Agv.WireToGateG2Tests --no-build --filter "$FILTER" 2>&1)
    code=$?
    echo "run $i: exit=$code $(echo "$res" | grep -E 'Failed!|Passed!' | sed -E 's/, Duration.*//')" >> "$OUT"
    echo "$res" | grep -E '^\s+Failed SQCD' -A2 | grep -vE '^\s+Error Message:|^--' | sed -E 's/ \[[0-9]+ m?s\]//' >> "$OUT"
    echo "$res" | grep -E 'StaleSafetyResend.cs:line|HandshakeWindowIntrusion.cs:line' | sort | uniq -c >> "$OUT"
  done
  cp -p "$BACKUP" "$F"
  touch "$F"
  local sha
  sha=$(sha256sum "$F" | cut -d' ' -f1)
  echo "restored: $sha $( [ "$sha" = "$FIXED_SHA" ] && echo OK || echo MISMATCH )" >> "$OUT"
  [ "$sha" = "$FIXED_SHA" ] || exit 5
}

: > "$OUT"
echo "head: $(git rev-parse HEAD); business service with fix: $FIXED_SHA; N=$N" >> "$OUT"
run_state "FIXED" "" ""
run_state "M1 the give-up branch never taken: the kept change goes out as it is" \
  "                && carried.SessionGeneration != current.SessionGeneration" \
  "                && false && carried.SessionGeneration != current.SessionGeneration"
run_state "M2 the version is not skipped: the given-up number goes to the present reading" \
  "                _nextSafetyStateVersion = Math.Max(_nextSafetyStateVersion, checked(carried.Version + 1));
" \
  ""
run_state "M3 the outbox row is not settled: it stays unacknowledged for a later handshake" \
  "                await _session.AbandonSafetyStateChangedAsync(carried.Version, carried.ObservedAt, cancellationToken)
                    .ConfigureAwait(false);
" \
  ""
run_state "M4 no content comparison: every kept change is given up, same content or not" \
  "                && !string.Equals(carried.Signature, signature, StringComparison.Ordinal))" \
  ")"
echo "done" >> "$OUT"
