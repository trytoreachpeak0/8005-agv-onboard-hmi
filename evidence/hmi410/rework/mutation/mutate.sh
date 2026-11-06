#!/usr/bin/env bash
# Each mutation: committed tree, change exactly one file, run the targeted tests, restore that file with git checkout.
set -o pipefail
cd C:/work/8005-wt/hmi410-8005-agv-onboard-hmi || exit 1
OUT=C:/work/8005-scratch/hmi410-rework/mutation
[ -z "$(git status --porcelain)" ] || { echo "tree not clean"; exit 1; }
run() { # name file filter
  local name=$1 file=$2 filter=$3
  local changed; changed=$(git status --porcelain | wc -l)
  [ "$changed" = 1 ] || { echo "$name: expected exactly one changed file, got $changed"; git checkout -- "$file"; return; }
  git diff > "$OUT/$name.diff"
  dotnet test "$4" -nologo --filter "$filter" > "$OUT/$name.txt" 2>&1
  git checkout -- "$file"
  echo "$name: $(grep -E '已通过!|失败!' "$OUT/$name.txt")"
}
B=src/SQCD.Agv.Wpf/WireToGateBusinessService.cs
G2=tests/SQCD.Agv.WireToGateG2Tests

# M1: the text class back at the end of the partial.
P=src/SQCD.Agv.Wpf/WireToGateBusinessService.ResumeAbandon.cs
printf '\n/// <summary>Mutant.</summary>\npublic static class WireToGateResumeAbandonMutantText\n{\n    public const string Caption = "x";\n}\n' >> $P
run M1-class-back-in-partial $P "FullyQualifiedName~RecoveryMarkerPersistenceArchitectureTests" tests/SQCD.Agv.UnitTests

# S2a: drop #410's rule (「不装了」 when the resume can be given up).
sed -i 's/        string? giveUpCaption = CanAbandonResume$/        string? giveUpCaption = false/' $B
run S2a-drop-abandon-caption $B "FullyQualifiedName~AResumedLoadOverAnEmptySlotIsGivenUpByPressAndThenCompensated" $G2

# S2b: drop #392's rule (cancellation named only while its entry is lit over this load).
sed -i 's/            : cancellationEntryOverThisLoad$/            : true/' $B
run S2b-drop-entry-lit-rule $B "FullyQualifiedName~MultiDemandJourneyG2Tests|FullyQualifiedName~LoadCancellationEmptyScopeG2Tests|FullyQualifiedName~StationDeadlineExpiredG2Tests" $G2

# S1: back to the old wording of the grant.
N=src/SQCD.Agv.Wpf/WireToGateNoOnboardExitText.cs
sed -i 's/请联系系统维护人员为这辆车开放「补偿清空」，开放后再按「补偿清空」/请联系系统维护人员开放这辆车的恢复，开放后按「补偿清空」/' $N
run S1-old-grant-wording $N "FullyQualifiedName~AResumedLoadOverAnEmptySlotIsGivenUpByPressAndThenCompensated|FullyQualifiedName~AnUnknownLoadHandedToTheServerNamesCompensationAndPressingItAfterTheGrantSettlesTheLoad|FullyQualifiedName~ACancellationConfirmedAfterTheLoadWasHandedOverNamesCompensationWhenItIsOffered|FullyQualifiedName~TheLoadCancellationEntryClosesOnceTheLoadsNonCompletedResultIsOnItsWay" $G2

git status --porcelain
