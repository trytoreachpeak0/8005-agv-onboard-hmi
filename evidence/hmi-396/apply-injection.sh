#!/usr/bin/env bash
# Applies the onboard-hmi#396 red injection to an onboard-hmi checkout, as uncommitted changes; undo with
#   git checkout -- tests/SQCD.Agv.WireToGateG2Tests
#   rm tests/SQCD.Agv.WireToGateG2Tests/Hmi396Injection.cs
# Usage: apply-injection.sh [checkout] [delay-ms]   (defaults: the checkout this script sits in, 3500)
# Works on the tests before the fix and after it. Set W2G_HMI396_MARKS to a file path when running a test to get the
# ARMED / FIRED / DISARMED times.
#
# Two tests get it, each holding its first LoadCompensationResult before it is saved and sent:
# - MultiDemandJourneyG2Tests.AForgottenCompensationOverAnUnsettledLoadLeavesItsAttemptMarked: the result lands after
#   the executor's OperationTimeout (2 s) plus the delay;
# - RecoveryVectorG2Tests.AnUnknownResultWhoseAckArrivesLateIsForgottenWithoutAReconnect (WaitForAckPendingAsync right
#   after the command, the issue's closest shape): the ack-pending line lands after the delay plus the message
#   timeout (2 s).
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
W=${1:-$(git -C "$here" rev-parse --show-toplevel)}
D=${2:-3500}
G2=$W/tests/SQCD.Agv.WireToGateG2Tests
cp "$here/injection-DelayedOutgoingJournal.cs.txt" "$G2/Hmi396Injection.cs"

t=$G2/MultiDemandJourneyG2Tests.HandoffOverUnrecordedLoad.cs
D=$D perl -0pi -e '
s{(public async Task AForgottenCompensationOverAnUnsettledLoadLeavesItsAttemptMarked\(\).*?\n    \}\n)}{
  my $m = $1;
  $m =~ s/(FakeIoModuleClient io = new\(\) \{ OperatorNeverActs = true \};\n)/$1        Hmi396DelayedOutgoingJournal? delayed = null;\n/ or die "m1";
  $m =~ s/(server => server\.SendLoadCompensationCommandOnRequest = true,\n            token)\);/$1,\n            wrapJournal: inner => delayed = new Hmi396DelayedOutgoingJournal(inner, "LoadCompensationResult", TimeSpan.FromMilliseconds($ENV{D})));/ or die "m2";
  $m =~ s/(        int unlocksBefore = io\.UnlockCount;\n)/        delayed!.Arm();\n$1/ or die "m3";
  $m =~ s/(        Assert\.Equal\("UNKNOWN", compensation\.GetProperty\("overallOutcome"\)\.GetString\(\)\);\n)/$1        delayed.Disarm();\n        Assert.True(delayed.Fired, "the injection did not fire");\n/ or die "m4";
  $m }se or die "m0";
' "$t"
test "$(grep -c 'delayed' "$t")" -eq 5 || { echo "injection did not apply cleanly to $t"; exit 1; }

r=$G2/RecoveryVectorG2Tests.AckOutsideTheSend.cs
D=$D perl -0pi -e '
s{(public async Task AnUnknownResultWhoseAckArrivesLateIsForgottenWithoutAReconnect\(\).*?\n    \}\n)}{
  my $m = $1;
  $m =~ s/(FakeControlServer\.DurableAckHold\? hold = null;\n)/$1        Hmi396DelayedOutgoingJournal? delayed = null;\n/ or die "r1";
  $m =~ s/(            lockerWaitTimesOut: true)\);/$1,\n            wrapJournal: inner => delayed = new Hmi396DelayedOutgoingJournal(inner, "LoadCompensationResult", TimeSpan.FromMilliseconds($ENV{D})));/ or die "r2";
  $m =~ s/(        await harness\.Server\.SendCommandAsync\(\n            "LoadCompensationCommand")/        delayed!.Arm();\n$1/ or die "r3";
  $m =~ s/(        await WaitForAckPendingAsync\(harness, token\);\n)/$1        delayed.Disarm();\n        Assert.True(delayed.Fired, "the injection did not fire");\n/ or die "r4";
  $m }se or die "r0";
' "$r"
test "$(grep -c 'delayed' "$r")" -eq 5 || { echo "injection did not apply cleanly to $r"; exit 1; }
git -C "$W" status --short
