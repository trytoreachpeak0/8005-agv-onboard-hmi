#!/usr/bin/env bash
# Applies the onboard-hmi#393 red injection to an onboard-hmi checkout, as uncommitted changes; undo with
#   git checkout -- tests/SQCD.Agv.WireToGateG2Tests/MultiDemandJourneyG2Tests.HandoffOverUnrecordedLoad.cs
#   rm tests/SQCD.Agv.WireToGateG2Tests/MultiDemandJourneyG2Tests.Hmi393Injection.cs
# Usage: apply-injection.sh [checkout]   (default: the checkout this script sits in)
# Works on the test before the fix and after it. Set W2G_HMI393_MARKS to a file path when running the test to get the
# ARMED / FIRED / DISARMED times.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
W=${1:-$(git -C "$here" rev-parse --show-toplevel)}
G2=$W/tests/SQCD.Agv.WireToGateG2Tests
cp "$here/injection-DelayedRestoreJournal.cs.txt" "$G2/MultiDemandJourneyG2Tests.Hmi393Injection.cs"
t=$G2/MultiDemandJourneyG2Tests.HandoffOverUnrecordedLoad.cs
# The resend comes from the restore the load's own finally starts once its ack wait gives up (WireToGateBusinessService,
# after ReleaseInFlightAttempt), so the injection is armed as soon as the load is done, before that give-up.
perl -0pi -e '
s/(public async Task TheMarkedSettlementStillResendsAnUnacknowledgedCompletedResult\(\)\n    \{\n        CancellationToken token = TestContext\.Current\.CancellationToken;\n        FakeIoModuleClient io = new\(\) \{ OperatorNeverActs = true \};\n)/$1        DelayedRestoreJournal? delayed = null;\n/;
s/(server => server\.OperationResultAcksToDrop = 2,\n            token,\n            messageTimeout: TimeSpan\.FromSeconds\(3\))\);/$1,\n            wrapJournal: inner => delayed = new DelayedRestoreJournal(inner, TimeSpan.FromMilliseconds(3000)));/;
s/(        await LoadBothSlotsOfAAsync\(harness, io, token\);\n)(        await harness\.Wait\w+\(\n            \(\) => harness\.Logger\.Entries\.Any\(entry => entry\.Message\.StartsWith\(\n                "OperationResult暂未收到DurableAck")/$1        delayed!.Arm();\n$2/;
s/(            "the restore\x27s resend of A\x27s result to give up, and the handoff entry over A",\n            token\);\n)/$1        delayed!.Disarm();\n        Assert.True(delayed.Fired, "the injection did not fire");\n/;
' "$t"
test "$(grep -c 'delayed' "$t")" -eq 5 || { echo "injection did not apply cleanly"; exit 1; }
git -C "$W" status --short
