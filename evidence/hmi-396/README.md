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

Without injection, at the fix commit: MultiDemand + G2 `ArchitectureTests` 208/208, RecoveryVector 225/225, UnitTests
`ArchitectureTests` 203/203, `dotnet format --verify-no-changes` exit 0. A first run of both classes in one process
(433 tests on 4 cores) had two reds in tests this ticket does not touch, with no product timer in their windows; see
`runs.txt`.

## Not handled here

- The issue's timeline (the reviewer's run, 208 tests in parallel) had PAUSED 4.4 s after WAITING_OPERATOR, where the
  timer is 2 s; locally PAUSED follows WAITING_OPERATOR by ~2 s. Presumably the fake IO's polling loop
  (`FakeIoModuleClient.WaitForLockerAsync`, `Task.Delay(1)` under `OperatorNeverActs`) or the thread pool running late
  under the parallel run; not investigated (no scene of that run was kept). The new window leaves 5 s beyond the timer
  for it and for the save and send.
