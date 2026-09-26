#!/usr/bin/env bash
# Reverse verification for onboard-hmi#208 after review (second fix). Each state: one or more exact replacements in the
# business service, each of which must match exactly once (else exit), --no-incremental rebuild that must print
# "0 Error(s)", this ticket's cases and onboard-hmi#204's six run N times with the exit code of every run, then the file is
# restored from the backup and its SHA-256 checked.
set -u
cd /c/Users/szy/Desktop/8005-workspace-v2/worktrees/hmi208-8005-agv-onboard-hmi || exit 1
F=src/SQCD.Agv.Wpf/WireToGateBusinessService.cs
OUT=${1:?output file}
N=${2:-3}
BACKUP="$TEMP/hmi208-wtgbs-fix2.bak"
FIXED_SHA=$(sha256sum "$F" | cut -d' ' -f1)
cp -p "$F" "$BACKUP"
FILTER="FullyQualifiedName~ASafetyChangeTheHandshakeDidNotCarry|FullyQualifiedName~GivingUpAStaleSafetyChange|FullyQualifiedName~MultiDemandJourneyG2Tests.ASafetyReport"

replace_once() {
  local from=$1 to=$2 count
  count=$(FROM="$from" python -c "import os,io;print(io.open(r'$F',encoding='utf-8',newline='').read().count(os.environ['FROM']))")
  if [ "$count" != 1 ]; then echo "replacement matched $count times, abort: $from" >> "$OUT"; exit 3; fi
  FROM="$from" TO="$to" python -c "import os,io;p=r'$F';s=io.open(p,encoding='utf-8',newline='').read();s=s.replace(os.environ['FROM'],os.environ['TO'],1);io.open(p,'w',encoding='utf-8',newline='').write(s)"
}

run_state() {
  local name=$1
  shift
  echo "=== $name" >> "$OUT"
  while [ $# -ge 2 ]; do
    replace_once "$1" "$2"
    shift 2
  done
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
    echo "$res" | grep -E '^\s+Failed SQCD' -A2 | grep -vE '^\s+Error Message:|^--' | sed -E 's/ \[[0-9]+ m?s\]//' | cut -c1-400 >> "$OUT"
    echo "$res" | grep -oE '(StaleSafetyResend|HandshakeWindowIntrusion)\.cs:line [0-9]+' | sort | uniq -c >> "$OUT"
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
run_state "FIXED"
run_state "M1 the give-up branch never taken: the kept change goes out as it is" \
  "            if (_pendingSafetyChange is { } stale
                && !string.Equals" \
  "            if (_pendingSafetyChange is { } stale && false
                && !string.Equals"
run_state "M2 the version is not skipped: the given-up number goes to the present reading" \
  "                _nextSafetyStateVersion = Math.Max(_nextSafetyStateVersion, checked(stale.Version + 1));
" \
  ""
run_state "M3 the outbox row is not settled: it stays unacknowledged for a later handshake" \
  "                await _session.AbandonSafetyStateChangedAsync(stale.Version, stale.ObservedAt, cancellationToken)
                    .ConfigureAwait(false);
" \
  ""
run_state "M4 no content comparison: every kept change is given up, same content or not" \
  "            if (_pendingSafetyChange is { } stale
                && !string.Equals(stale.Signature, signature, StringComparison.Ordinal))" \
  "            if (_pendingSafetyChange is { } stale)"
run_state "M5 the first fix's condition back: given up only when the session generation number changed" \
  "        IReadOnlyList<int> AffectedSlots,
        string Signature);
" \
  "        IReadOnlyList<int> AffectedSlots,
        string Signature,
        long? SessionGeneration = null);
" \
  "                signature);
            SafetyChangeWork pending = _pendingSafetyChange;" \
  "                signature,
                current.SessionGeneration);
            SafetyChangeWork pending = _pendingSafetyChange;" \
  "            if (_pendingSafetyChange is { } stale
                && !string.Equals" \
  "            if (_pendingSafetyChange is { } stale
                && stale.SessionGeneration != current.SessionGeneration
                && !string.Equals"
run_state "M6 the signature is not cleared after giving up: the present reading may go unreported" \
  "                // repeated generation number it does not fire.
                _lastSafetySignature = null;
" \
  "                // repeated generation number it does not fire.
"
echo "done" >> "$OUT"
