#!/usr/bin/env bash
# hmi#404 mutation run. Each mutation edits exactly one file on a clean, committed tree,
# rebuilds with --no-incremental, runs the wall-clock-step tests, and restores that file only.
set -u -o pipefail
WT=C:/work/8005-wt/hmi404-8005-agv-onboard-hmi
OUT=C:/work/8005-scratch/hmi404-mutations
FILTER='FullyQualifiedName~SafetyRulesTests|FullyQualifiedName~WallClock|FullyQualifiedName~ModbusTcpIoModuleClientIntegrationTests'
cd "$WT" || exit 2
[ -z "$(git status --porcelain)" ] || { echo "tree not clean"; exit 2; }
echo "HEAD $(git rev-parse HEAD)" > "$OUT/summary.txt"

mutate() { # id file perl-expr
  local id=$1 file=$2 expr=$3 log="$OUT/$1.txt"
  perl -0pi -e "$expr" "$file"
  local changed; changed=$(git diff --name-only)
  if [ "$changed" != "$file" ]; then
    echo "$id: mutation did not apply to exactly $file (got: '$changed')" | tee -a "$OUT/summary.txt"
    git checkout -- "$file"; return
  fi
  { echo "== $id: $file"; git diff; echo; } > "$log"
  if dotnet build tests/SQCD.Agv.UnitTests --no-incremental -nologo -clp:ErrorsOnly >> "$log" 2>&1; then
    pwsh -File C:/work/8005-workspace-v2/Invoke-HeavyLocal.ps1 -Ticket hmi#404 \
      dotnet test tests/SQCD.Agv.UnitTests --no-build --filter "$FILTER" >> "$log" 2>&1
    echo "test exit=$?" >> "$log"
  else
    echo "BUILD FAILED" >> "$log"
  fi
  git checkout -- "$file"
  echo "$id: $(grep -E '已通过|失败!|Passed!|Failed!|BUILD FAILED' "$log" | tail -1)" | tee -a "$OUT/summary.txt"
  grep -E '^\s*(失败|Failed) ' "$log" | sed 's/^/    /' >> "$OUT/summary.txt"
}

# M1 freshness ignores the monotonic timestamp (back to the wall clock)
mutate M1-fresh-wall src/SQCD.Agv.Core/SafetyRules.cs \
  's/if \(snapshot\.ObservedTimestamp is \{ \} observed\)/if (Environment.ProcessorCount < 0 && snapshot.ObservedTimestamp is { } observed)/'
# M2 the IClock overload reads the real Stopwatch instead of the clock it was given
mutate M2-fresh-own-stopwatch src/SQCD.Agv.Core/SafetyRules.cs \
  's/clock\.Now, clock\.GetTimestamp\(\), snapshotMaxAge/clock.Now, Stopwatch.GetTimestamp(), snapshotMaxAge/'
# M3 WaitForLockerAsync measures the stable window and same-reading check on the wall clock
mutate M3-window-wall src/SQCD.Agv.Infrastructure/ModbusTcpIoModuleClient.cs \
  's/long observed = locker\.ObservedTimestamp\s*\?\? throw new InvalidOperationException\("IO reading without a monotonic timestamp\."\);/long observed = locker.ObservedTimestamp is null ? throw new InvalidOperationException("x") : locker.ObservedAt.UtcTicks * Stopwatch.Frequency \/ TimeSpan.TicksPerSecond;/'
# M4 only the same-reading check on the wall clock
mutate M4-same-reading-wall src/SQCD.Agv.Infrastructure/ModbusTcpIoModuleClient.cs \
  's/if \(lastEvaluated == observed\)/if (lastEvaluated == locker.ObservedAt.UtcTicks)/; s/lastEvaluated = observed;/lastEvaluated = locker.ObservedAt.UtcTicks;/'
# M5 "reading taken after that one" compares ObservedAt
mutate M5-no-earlier-wall src/SQCD.Agv.Core/DomainModels.cs \
  's/\? observed >= otherObserved/? ObservedAt >= other.ObservedAt/'
# M6 the published IoSnapshot carries no monotonic stamp (freshness falls back to the wall clock)
mutate M6-snapshot-unstamped src/SQCD.Agv.Infrastructure/ModbusTcpIoModuleClient.cs \
  's/new\(true, lockers, now\) \{ ObservedTimestamp = timestamp \}/new(true, lockers, now) { ObservedTimestamp = Environment.ProcessorCount < 0 ? timestamp : null }/'
# M7 the "published after the caller read its clock" clause dropped (expected equivalent)
mutate M7-future-clause src/SQCD.Agv.Core/SafetyRules.cs \
  's/return nowTimestamp <= observed \|\| Stopwatch/return Stopwatch/'

echo "final status: '$(git status --porcelain)'" | tee -a "$OUT/summary.txt"
