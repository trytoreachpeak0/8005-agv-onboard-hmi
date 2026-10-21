# onboard-hmi#396 evidence

Same structure as onboard-hmi#393: a G2 wait gives a fact the usual 5 s, but the fact can only land after a product
timer that runs inside those 5 s, so processing latency under load pushes it past the window. Test-only change.

## What each wait waits behind

**`MultiDemandJourneyG2Tests.AForgottenCompensationOverAnUnsettledLoadLeavesItsAttemptMarked`**
(`HandoffOverUnrecordedLoad.cs`, the `WaitForPayloadAsync(harness, "LoadCompensationResult", …)` that went red at :165).
Slot 1's lock feedback is made unreadable, so the compensation's wait for an emptied, relocked door
(`WireToGateRecoveryVectorExecutor.ExecuteClearAsync`, `MinTimeout(_options.OperationTimeout, GetRemaining(deadline))`)
can only end by running out. The deadline is `OperationTimeout` from the slot's turn, which starts before the pulse the
test has just seen, so the UNKNOWN result reaches the double no earlier than that timer (2 s in this harness) plus
whatever the vehicle takes to save and send it. The red scene below shows WAITING_OPERATOR at 24.028 and PAUSED (the
timer running out) at 26.001.

Fix: `Harness.WaitPastOperationTimeoutUntilAsync`, the usual 5 s on top of the `OperationTimeout` the harness handed
the business service (`Harness.SlotOperationOptions`, the options object itself, not a copy of the number); a base past
`StallAwareDeadline.StallBudget` is refused, as `WaitPastMessageTimeoutUntilAsync` does. The test waits for the result
with it before reading it.

**RecoveryVectorG2Tests, the issue's rows marked 「是」.** Every one waits for a log line the business service writes
only after the session's own `DurableAck` wait (`WireToGateSessionClient`, `WaitAsync(_options.MessageTimeout)`, 2 s in
this harness) has thrown:

| call site | line waited for | written by | ack withheld by |
| --- | --- | --- | --- |
| `WaitForAckPendingAsync` (`AckOutsideTheSend.cs`), callers `:60`, `:108`, `:148`, `:190`, `:364`, `ClosureBeforeSettlement.cs:43`, `DoorNotProvenShut.cs:142`, and `DoorNotProvenShut.cs:236` (not in the issue's table, same helper) | 恢复向量结果暂未收到DurableAck | `WireToGateBusinessService.RecoveryVectors.cs` catch around `sendResult` | `LoadCompensationResultAcksToDrop`, `LoadCompensationResultAckDelay = 1 h`, or `HoldNextDurableAck` |
| `SettledVectorRelease.cs:141`, `:205` (inline) | 恢复向量结果暂未收到DurableAck | same | `LoadCompensationResultAcksToDrop = 1` |
| `ResumeRejected.cs:147` (inline) | 续行命令的拒绝已写入发件箱，暂未收到DurableAck | `WireToGateBusinessService.SendResumeRejectedCoreAsync` catch | `SlotOperationCommandRejectedAcksToDrop = 1` |

All checked: none of these sessions sets a `messageTimeout`, so each runs the 2 s default, and none of these lines can
appear before that timer. Fix: `RecoveryVectorHarness.WaitPastMessageTimeoutUntilAsync`, the same rule as the
MultiDemand harness's (session's `MessageTimeout` + 5 s, refused past `StallBudget`); the helper and the three inline
waits use it. The 5 s `WaitUntilAsync` is unchanged for everything else.

Not changed, as the issue's table says: `ResumeDuringSettlement.cs:105` asserts that a line is absent (no wait) and
`:273` already waits 40 s on a 15 s message timeout session.

**Added after the review (M1, S1).**

- `HandoffOverUnrecordedLoad.cs`, the four other waits after `io.SetUnreadable(0)`: `AnUnknownHandoffOverALoadWhoseAckCameLate…`
  (both cases), `AnUnknownCompensationOverARecordedLoad…`, `ARefusedHandoffEndedAfterTheManualCheck…` (waits for
  `CanCloseConflictedRecoveryAfterReview`, which follows the refusal of the handoff's result) and
  `TheMarkedSettlement…` (waits for the vector to be forgotten, which follows the handoff's result's ack). Handoff and
  compensation both run `ExecuteClearAsync`, so each result is held behind the same `OperationTimeout` as AForgotten's.
  Each test now waits for the result to reach the double with `WaitPastOperationTimeoutUntilAsync`, then keeps its own
  5 s wait for what follows.
- `BlockedBeforeUnlock.cs:262`, `AnUnacknowledgedRefusalStillLetsTheVehicleOpenAnotherSession`: the refused vector is
  released (`ReleaseRefusedVectorAsync`) only after `SendRecoveryVectorResultAsync` throws on the dropped ack, after the
  message timeout (`RecoveryVectors.cs`, the catch before it). Now `WaitPastMessageTimeoutUntilAsync`.
- The harness start's wait for the seeded attempt's settlement (`RecoveryVectorG2Tests.cs`, `FinishStartAsync`): its
  RESULT_ACK_PENDING ending is only published once the settlement's ack wait runs out; `RefusedSettlement.cs:429`
  (`OperationResultAcksToDrop = 3`, "one for the start settlement's own result") takes it. The wait is now the
  message timeout + 5 s, and refused with an exception past `StallBudget`, as the other two methods do. Every session
  with a longer message timeout (15 s and 30 s, in `ResumeDuringSettlement` and `ResumeHeldAttempt`) already passes
  `awaitStartSettlement: false`, so none reaches it.

Swept for this ticket: `HandoffOverUnrecordedLoad.cs` and every `RecoveryVectorG2Tests*.cs` wait on a DurableAck
give-up line or event. Not swept: the other `MultiDemandJourneyG2Tests` files that make a door unreadable (about 35
`SetUnreadable` call sites, e.g. `CancellationOverRecordedLoad`, `RefusedSafetyChange`, `StaleSafetyResend`); whether
any of their waits sits behind `OperationTimeout` is not known.

## Red / green

`apply-injection.sh [checkout] [delay-ms]` copies `injection-DelayedOutgoingJournal.cs.txt` into the G2 project and
wraps the test's journal through the harnesses' `wrapJournal` (test side only, no product change). Armed just before
the compensation is requested (or the command sent), it holds the vehicle's first save-before-send of a
`LoadCompensationResult` for 3.5 s -- the processing latency between the timer running out and the result reaching
the double -- then lets it through; disarmed after the wait under test, with `Assert.True(delayed.Fired)`. Marks
(ARMED / FIRED / DISARMED) go to `W2G_HMI396_MARKS`.

Two tests carry it: the red one, and `AnUnknownResultWhoseAckArrivesLateIsForgottenWithoutAReconnect`
(`WaitForAckPendingAsync` right after the command, the shape the issue calls closest to #393; the result's executor
returns at once there because `lockerWaitTimesOut` throws). Pre-fix = the two test files as on `226c6fd`, harness file
from this branch (the pre-fix tests do not call the new methods).

`runs.txt`, CPU affinity 0xF, through `Invoke-HeavyLocal.ps1`:
- pre-fix: 6/6 red each, FIRED in every run, each timing out on the wait under test.
  AForgotten: FIRED 2.3-3.1 s after ARMED (the operation timer), result no earlier than FIRED + 3.5 s, against a window
  that started ~0.3 s after ARMED (run 1: wait 24.045-29.060, FIRED 26.056, `red-scene-forgotten-run1.txt`: stuck on
  nothing on the wire, outbox owing nothing, thread pool idle -- the result is still inside the injected delay).
  AckOutsideTheSend: FIRED 0.2-0.3 s after ARMED; the ack-pending line is no earlier than FIRED + 3.5 s + 2 s.
- fixed: 6/6 green each, FIRED in every run.

M1 (`apply-injection-m1.sh`, the same wrapper on the four waits above, every mark labelled with its test; one
`dotnet test` per round with the five cases, which run one after another; pre-fix = the file at `bc0447d`):
- pre-fix: 5 rounds, 25/25 red, FIRED in all 25, each on the wait under test (the result, or for
  `TheMarkedSettlement…`/`ARefusedHandoff…` the fact after it).
- fixed: 5 rounds, 23/25 green, FIRED in all 25. The two reds timed out on the new 7 s wait: the compensation in round 1
  (PAUSED 4.1 s after WAITING_OPERATOR against a timer of under 2 s, then the injected 3.5 s) and the refused handoff in
  round 5 (PAUSED 2.7 s after WAITING_OPERATOR, the saved result on the wire 2.5 s after the injected delay ended).
  Natural latency after the timer of 1.5 s or more on top of the injected 3.5 s is past the 5 s the window leaves; see
  the next section.

### Where the time after the timer goes (`diag-marks.txt`)

The issue (PAUSED 4.4 s after WAITING_OPERATOR), the review's natural red (about 2.9 s late) and the two reds above all
look like the 2 s timer running late. Five diagnostic rounds (fixed + M1 injection + temporary marks in the fake IO and
the journal wrapper, not committed) say it is not the timer: the fake IO's door wait gave up at most 15 ms past its own
deadline in all 25 cases, every time. What comes after is slow: the journal's `UpdateRecoveryStateAsync` (the
executor's `WriteVectorStateAsync` before PAUSED, among others) took 0.3-2.2 s in 6 of 25 rounds, with one test running
at a time on 4 cores; round 4's red was a 2.19 s write right after the timer, then the injected 3.5 s. This is the
processing latency the window is for, and nothing shows a product defect in it; why a SQLite write in a temp
directory sometimes takes 2 s on this machine was not looked into.

Without injection: see `runs.txt` (each family in its own process, G2 and UnitTests `ArchitectureTests`, `dotnet format
--verify-no-changes`, at `519b2cd` and again after the review's rework). A first run of both classes in one process
(433 tests on 4 cores) had two reds in tests this ticket does not touch, with no product timer in their windows; see
`runs.txt`.

## Not handled here

- Residual risk: the 5 s left after the timer also has to absorb the latency the section above found right after it:
  up to 2.2 s here, about 2.4 s in the issue's timeline, about 2.9 s in the review's natural red. With 2.9 s gone, the
  save and send have about 2.1 s. Not widened further (the coordinator's ruling); the window stays timer + 5 s.
