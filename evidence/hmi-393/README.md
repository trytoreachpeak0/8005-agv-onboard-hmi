# onboard-hmi#393 evidence

Test: `MultiDemandJourneyG2Tests.TheMarkedSettlementStillResendsAnUnacknowledgedCompletedResult`.

## Why it failed on CI (run 39550925050)

The wait at `HandoffOverUnrecordedLoad.cs:390` (before the fix) waited, in the usual 5 s, for the restore's resend of
A's `OperationResult` to give up. Giving up is the vehicle's own `DurableAck` wait running out
(`WireToGateSessionClient.SendDurableCoreAsync`, `response.Task.WaitAsync(_options.MessageTimeout)`; the test sets the
message timeout to 3 s and the double drops that ack). So the wait can only end at
`(readiness -> restore -> resend) + 3 s`, leaving 2 s for the first part. Timeline from the CI scene:

| time (UTC) | what |
| --- | --- |
| 05:42:22.444 | RECOVERY_REQUIRED received and published |
| 05:42:22.460 | wait starts |
| 05:42:23.746 | business service's `OnSessionStateChanged` runs (restore started) |
| 05:42:24.804 / 25.340 | SafetyStateChanged v3 sent / acked |
| ~05:42:25.42 | double reads the resend (listed after that SafetyStateChanged; its read loop idle from 25.421) |
| 05:42:27.476 | wait times out; the resend is still inside its 3 s ack wait (would give up ~28.4) |

The product resent and was going to give up; nothing was stuck.

## Red / green

`apply-injection.sh` drops `injection-DelayedRestoreJournal.cs.txt` into the G2 project and wraps the test's journal:
once armed (after the load's own give-up, before RECOVERY_REQUIRED) the restore's resend
(`ResendOperationResultAsync` under `TrySettleInterruptedOperationAsync`) is delayed 3 s before it goes out -- the
readiness-to-resend latency CI showed. Disarmed after the fixed waits. Never committed.

Runs on this machine, CPU affinity 0xF, through `Invoke-HeavyLocal.ps1`: see `runs.txt`. Pre-fix 4/5 red with the CI
shape (`red-scene-run1.txt`: stuck on the vehicle side, the result owed and not given up, the resend read 2.5-3 s into
the wait, thread pool idle); fixed 5/5 green. The one pre-fix pass: the injection's start relative to the wait's start
varies by a few hundred ms. Non-ASCII vehicle log lines in the scene file are mis-decoded by the console capture.
