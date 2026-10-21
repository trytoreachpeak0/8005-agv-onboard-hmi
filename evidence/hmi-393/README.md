# onboard-hmi#393 evidence

Test: `MultiDemandJourneyG2Tests.TheMarkedSettlementStillResendsAnUnacknowledgedCompletedResult`.

## Why it failed on CI (run 39550925050)

The wait at `HandoffOverUnrecordedLoad.cs:390` (before the fix) waited, in the usual 5 s, for the restore's resend of
A's `OperationResult` to give up. Giving up is the vehicle's own `DurableAck` wait running out
(`WireToGateSessionClient.SendDurableCoreAsync`, `response.Task.WaitAsync(_options.MessageTimeout)`; the test sets the
message timeout to 3 s and the double drops that ack). So the wait could only end at
`(restore -> resend) + 3 s`.

Which restore resends: the load's own send gives up first (`OperationResult暂未收到DurableAck`,
`WireToGateBusinessService.cs:3849`), and its `finally` starts a restore once the attempt's claim is released
(`WireToGateBusinessService.cs:3901`). That restore claims the attempt and resends; the one RECOVERY_REQUIRED starts
moments later (`OnSessionStateChanged`, `:1020`) finds the attempt in flight and leaves it (the hmi#278 skip). The
reviewer's marks showed the injected delay firing 8-31 ms after ARMED in 12 of 12 runs, before RECOVERY_REQUIRED went
out; the marks in `runs.txt` show the same (FIRED at the load's give-up, ~3.2 s after ARMED).

Timeline from the CI scene:

| time (UTC) | what |
| --- | --- |
| 05:42:19.405 | A's `OperationResult` sent; its ack is dropped |
| 05:42:22.428 | the load's ack wait gives up; its `finally` starts the restore that resends |
| 05:42:22.444 | RECOVERY_REQUIRED received and published |
| 05:42:22.460 | the wait starts |
| 05:42:23.746 | the business service's `OnSessionStateChanged` runs for RECOVERY_REQUIRED (its restore leaves the attempt) |
| 05:42:24.804 / 25.340 | SafetyStateChanged v3 stamped / acked |
| ~05:42:25.42 | double reads the resend (listed after that SafetyStateChanged; its read loop idle from 25.421) |
| 05:42:27.476 | the wait times out; the resend is still inside its 3 s ack wait (would give up ~28.4) |

So about 3 s from the restore starting to the resend reaching the double, under CI load, plus the 3 s timer: past the
5 s window. The product resent and was going to give up; nothing was stuck. The CI scene printed both copies of the
result as "sent 05:42:19.405" because a resend carries the first copy's `sentAt`; the scene now prints when the double
read each message, `sentAt` beside it (review S4).

## Red / green

`apply-injection.sh [checkout]` copies `injection-DelayedRestoreJournal.cs.txt` into the G2 project and wraps the
test's journal (the harness's `wrapJournal`; test side only, no product change). Armed right after the load, before its
give-up, it delays the restore's resend (`ResendOperationResultAsync` under `TrySettleInterruptedOperationAsync`) 3 s
before it goes out -- the restore-to-resend latency CI showed -- and is disarmed after the fixed waits, with
`Assert.True(delayed.Fired)` there. With `W2G_HMI393_MARKS` set it writes ARMED / FIRED / DISARMED times, so each run
shows that the delay took effect. Applied as uncommitted changes and undone with `git checkout` after the runs.

`runs.txt`: pre-fix 6/6 red, every one with FIRED and the CI shape (`red-scene-run1.txt`: stuck on the vehicle side,
the result owed and not given up, the resend read 3 s after FIRED, thread pool idle); fixed 6/6 green, every one with
FIRED. Once the delay fires the pre-fix wait cannot pass: the resend is no earlier than FIRED + 3 s and its giving up
no earlier than FIRED + 6 s, against a 5 s window that starts within tens of ms of FIRED (run 1: wait started 17.675, FIRED 17.703).

The first round of this evidence (before review) armed the injection only after the load's give-up. In one of five
pre-fix runs it passed, and it was not "a few hundred ms of drift" as said then: that run took 8 s, like a run with no
injection, so the restore had most likely read past the injected point before it was armed, and the delay never
fired; one of the "fixed" runs also took 8 s and was probably not injected either. The marks and the earlier arming
are what changed.

Non-ASCII vehicle log lines in the scene file are mis-decoded by the console capture.

## Why the injection delays the resend and not an ack on the double

Holding back the DurableAck of the SafetyStateChanged that goes out just before the resend (on the double) would not
delay the resend: the vehicle's durable sends are not serialized behind each other's acks -- each
`SendDurableCoreAsync` writes its line and waits for its own ack alone. On CI the resend was read after that
SafetyStateChanged because the vehicle was slow to get to it, not because it waited for that ack. So the injection
delays the resend itself, on the test side (a journal wrapper through the harness's `wrapJournal`), with no product
change.
