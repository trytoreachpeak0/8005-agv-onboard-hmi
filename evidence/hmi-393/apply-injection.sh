#!/usr/bin/env bash
# Applies the onboard-hmi#393 red injection to the worktree (temporary; restore with git checkout).
set -euo pipefail
W=/c/work/8005-wt/hmi393-8005-agv-onboard-hmi
cp /c/work/8005-scratch/hmi393/Hmi393Injection.cs.txt $W/tests/SQCD.Agv.WireToGateG2Tests/MultiDemandJourneyG2Tests.Hmi393Injection.cs
t=$W/tests/SQCD.Agv.WireToGateG2Tests/MultiDemandJourneyG2Tests.HandoffOverUnrecordedLoad.cs
perl -0pi -e 's/(public async Task TheMarkedSettlementStillResendsAnUnacknowledgedCompletedResult\(\)\n    \{\n        CancellationToken token = TestContext\.Current\.CancellationToken;\n        FakeIoModuleClient io = new\(\) \{ OperatorNeverActs = true \};\n)/$1        DelayedRestoreJournal? delayed = null;\n/; s/(server => server\.OperationResultAcksToDrop = 2,\n            token,\n            messageTimeout: TimeSpan\.FromSeconds\(3\))\);/$1,\n            wrapJournal: inner => delayed = new DelayedRestoreJournal(inner, TimeSpan.FromMilliseconds(3000)));/; s/(            "the load\x27s own wait for its acknowledgement to give up",\n            token\);\n)/$1        delayed!.Arm();\n/; s/(            "the restore\x27s resend of A\x27s result to give up, and the handoff entry over A",\n            token\);\n)/$1        delayed!.Disarm();\n/' $t
test "$(grep -c 'delayed' $t)" -eq 4 || { echo "injection did not apply cleanly"; exit 1; }
git -C $W status --short
