#!/usr/bin/env bash
# Applies the onboard-hmi#396 red injection to the four waits the review found in HandoffOverUnrecordedLoad.cs (M1), as
# uncommitted changes; undo with
#   git checkout -- tests/SQCD.Agv.WireToGateG2Tests
#   rm tests/SQCD.Agv.WireToGateG2Tests/Hmi396Injection.cs
# Usage: apply-injection-m1.sh [checkout] [delay-ms]   (defaults: the checkout this script sits in, 3500)
# Same wrapper as apply-injection.sh: armed just before the handoff or compensation is requested, it holds that vector's
# result before it is saved and sent; disarmed after the wait under test with Assert.True(delayed.Fired). Every mark
# carries the test's label. Works on the test file before the fix (bc0447d) and after it.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
W=${1:-$(git -C "$here" rev-parse --show-toplevel)}
D=${2:-3500}
G2=$W/tests/SQCD.Agv.WireToGateG2Tests
cp "$here/injection-DelayedOutgoingJournal.cs.txt" "$G2/Hmi396Injection.cs"
t=$G2/MultiDemandJourneyG2Tests.HandoffOverUnrecordedLoad.cs
D=$D perl -0pi -e '
sub inject {
  my ($m, $type, $label, $after) = @_;
  $m =~ s/(        FakeIoModuleClient io = new\(\) \{ OperatorNeverActs = true \};\n)/$1        Hmi396DelayedOutgoingJournal? delayed = null;\n/ or die "$label decl";
  $m =~ s/(\n            (?:token|messageTimeout: TimeSpan\.FromSeconds\(3\)))\);/$1,\n            wrapJournal: inner => delayed = new Hmi396DelayedOutgoingJournal(inner, "$type", TimeSpan.FromMilliseconds($ENV{D}), $label));/ or die "$label wrap";
  $m =~ s/(        Assert\.True\(await harness\.Business\.Request(?:FaultCargoHandoff|LoadCompensation)Async\()/        delayed!.Arm();\n$1/ or die "$label arm";
  $m =~ s/(\Q$after\E)/$1        delayed.Disarm();\n        Assert.True(delayed.Fired, "the injection did not fire");\n/ or die "$label disarm";
  return $m;
}
my @sites = (
  ["AnUnknownHandoffOverALoadWhoseAckCameLateKeepsItsDoorAcrossAReconnect(bool ackAfterHandoffEnded)", "FaultCargoRecoveryResult",
   q{$"handoff-late-ack-{ackAfterHandoffEnded} "}, qq{Assert.Equal("UNKNOWN", handoffResult.GetProperty("overallOutcome").GetString());\n}],
  ["AnUnknownCompensationOverARecordedLoadKeepsItsAttemptAndDoorAcrossAReconnect()", "LoadCompensationResult",
   q{"compensation-recorded "}, qq{Assert.Equal("UNKNOWN", compensation.GetProperty("overallOutcome").GetString());\n}],
  ["ARefusedHandoffEndedAfterTheManualCheckKeepsTheLoadsAttemptAndDoorAcrossAReconnect()", "FaultCargoRecoveryResult",
   q{"refused-handoff "}, qq{"the refused handoff to wait for a maintainer\x27s manual check",\n            token);\n}],
  ["TheMarkedSettlementStillResendsAnUnacknowledgedCompletedResult()", "FaultCargoRecoveryResult",
   q{"marked-settlement "}, qq{"the UNKNOWN handoff to be forgotten once acknowledged",\n            token);\n}],
);
for my $s (@sites) {
  my ($sig, $type, $label, $after) = @$s;
  s{(public async Task \Q$sig\E.*?\n    \}\n)}{inject($1, $type, $label, $after)}se or die "no $sig";
}
' "$t"
test "$(grep -c 'delayed' "$t")" -eq 20 || { echo "injection did not apply cleanly to $t"; exit 1; }
git -C "$W" status --short
