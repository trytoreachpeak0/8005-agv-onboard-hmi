#!/usr/bin/env bash
# hmi#404 mutation run. Each mutation edits exactly one file on a clean, committed tree,
# rebuilds with --no-incremental, runs the wall-clock-step tests, and restores that file only.
set -u -o pipefail
WT=C:/work/8005-wt/hmi404-8005-agv-onboard-hmi
OUT=C:/work/8005-scratch/hmi404-mutations
FILTER='FullyQualifiedName~SafetyRulesTests|FullyQualifiedName~WallClock|FullyQualifiedName~ModbusTcpIoModuleClientIntegrationTests|FullyQualifiedName~OnboardAutomationHostTests'
cd "$WT" || exit 2
[ -z "$(git status --porcelain)" ] || { echo "tree not clean"; exit 2; }
echo "HEAD $(git rev-parse HEAD)" > "$OUT/rework-summary.txt"

mutate() { # id file perl-expr
  local id=$1 file=$2 expr=$3 log="$OUT/$1.txt"
  perl -0pi -e "$expr" "$file"
  local changed; changed=$(git diff --name-only)
  if [ "$changed" != "$file" ]; then
    echo "$id: mutation did not apply to exactly $file (got: '$changed')" | tee -a "$OUT/rework-summary.txt"
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
  echo "$id: $(grep -E '已通过|失败!|Passed!|Failed!|BUILD FAILED' "$log" | tail -1)" | tee -a "$OUT/rework-summary.txt"
  grep -E '^\s*(失败|Failed) ' "$log" | sed 's/^/    /' >> "$OUT/rework-summary.txt"
}


# R1 both [JsonIgnore] removed (review L1)
mutate R1-jsonignore-both src/SQCD.Agv.Core/DomainModels.cs 's/    \[JsonIgnore\]\n//g'
# R2 only LockerSnapshot's [JsonIgnore] removed (the first one in the file)
mutate R2-jsonignore-locker src/SQCD.Agv.Core/DomainModels.cs 's/    \[JsonIgnore\]\n//'
# R3 only IoSnapshot's [JsonIgnore] removed (the second one)
mutate R3-jsonignore-io src/SQCD.Agv.Core/DomainModels.cs 's/(\[JsonIgnore\].*?)    \[JsonIgnore\]\n/$1/s'
# R4 mixed stamps: fallback always "no earlier" (review S2)
mutate R4-mixed-always-true src/SQCD.Agv.Core/DomainModels.cs 's/: ObservedAt >= other\.ObservedAt;/: Environment.ProcessorCount > 0;/'
# R5 mixed stamps: fallback inverted
mutate R5-mixed-inverted src/SQCD.Agv.Core/DomainModels.cs 's/: ObservedAt >= other\.ObservedAt;/: ObservedAt <= other.ObservedAt;/'
# R6 the executor's opposite predicate loses its occupancy condition (review S1: the race test now uses it)
mutate R6-opposite-predicate src/SQCD.Agv.Application/WireToGateSlotOperationExecutor.cs 's/locker => locker\.IsKnown && locker\.IsLocked && locker\.HasCargo != expectedOccupied;/locker => locker.IsKnown && locker.IsLocked;/'
# R7 M3 again under the widened 100 ms tolerance (review L2): window and same-reading check on the wall clock
mutate R7-window-wall-tolerance100 src/SQCD.Agv.Infrastructure/ModbusTcpIoModuleClient.cs \
  's/long observed = locker\.ObservedTimestamp\s*\?\? throw new InvalidOperationException\("IO reading without a monotonic timestamp\."\);/long observed = locker.ObservedTimestamp is null ? throw new InvalidOperationException("x") : locker.ObservedAt.UtcTicks * Stopwatch.Frequency \/ TimeSpan.TicksPerSecond;/'

echo "final status: '$(git status --porcelain)'" | tee -a "$OUT/rework-summary.txt"
