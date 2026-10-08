#!/bin/bash
SP=/c/Users/szy/AppData/Local/Temp/claude/C--Users-szy-Desktop-8005-workspace-v2/9a1054ef-b782-407f-89d3-dbba9179d6f8/scratchpad
cd /c/Users/szy/Desktop/8005-workspace-v2/worktrees/h278
FILES="src/SQCD.Agv.Wpf/WireToGateBusinessService.RecoveryVectors.cs src/SQCD.Agv.Application/WireToGateSlotOperationExecutor.cs src/SQCD.Agv.Wpf/WireToGateBusinessService.cs src/SQCD.Agv.Wpf/WireToGateBusinessService.ConflictedRecovery.cs"
G2F="FullyQualifiedName~MultiDemandJourneyG2Tests.AnUnknownHandoffOverALoadWhoseAckCameLate|FullyQualifiedName~AForgottenCompensationOverAnUnsettledLoad|FullyQualifiedName~AnUnknownCompensationOverARecordedLoad|FullyQualifiedName~ARefusedHandoffEndedAfterTheManualCheck|FullyQualifiedName~AHandoffThatOpenedNothing|FullyQualifiedName~TheMarkedSettlementStillResends|FullyQualifiedName~WireToGateTakenOverMarkerTests"
UF="FullyQualifiedName~WireToGateSlotOperationExecutorResultUnderVectorTests|FullyQualifiedName~ACheckpointOfTheMarkedAttemptDropsTheMarker"
for m in "$@"; do
  echo "=== $m (base $(git rev-parse --short HEAD))"
  python $SP/mutate2.py $m || exit 1
  git diff --stat | tail -1
  dotnet build SQCD_8005AGV.sln --no-incremental -v q 2>&1 | grep -E "Build succeeded|rror\(s\)" | head -2
  timeout 1200 dotnet test tests/SQCD.Agv.WireToGateG2Tests --no-build --filter "$G2F" 2>&1 | grep -E "^\s+Failed SQCD|Failed!|Passed!"; echo "g2 exit=${PIPESTATUS[0]}"
  timeout 600 dotnet test tests/SQCD.Agv.UnitTests --no-build --filter "$UF" 2>&1 | grep -E "^\s+Failed SQCD|Failed!|Passed!"; echo "unit exit=${PIPESTATUS[0]}"
  for f in $FILES; do git show HEAD:$f > $f; done
  echo "restored, dirty: $(git status --short | grep -v '^??' | wc -l)"
done
dotnet build SQCD_8005AGV.sln --no-incremental -v q 2>&1 | grep -E "Build succeeded|rror\(s\)" | head -2
