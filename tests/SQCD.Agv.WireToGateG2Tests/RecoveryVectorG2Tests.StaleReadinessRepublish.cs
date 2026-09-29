using System.Diagnostics;
using System.Reflection;
using SQCD.Agv.Contracts;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// A readiness line applied while an acknowledged safety message is being republished stays applied
/// (<c>trytoreachpeak0/8005-agv-control-server#380</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>What was seen.</b> CI real-rig run 36477303574, <c>real-onboard-cancelled-rebuild-cargo-proof</c>: an administrator
/// prepared a cargo handoff, the server judged the session <c>RECOVERY_REQUIRED</c> and appended that readiness to its
/// answer to the vehicle's next <c>SafetyStateSnapshot</c> -- the stored first response is 1234 bytes in that run and in
/// the passing one, against 1195 for the READY answers before it. The fault cargo handoff entry never appeared, and the
/// server, which announces readiness only on a change, never said it again.
/// </para>
/// <para>
/// <b>The interleaving.</b> The receive loop completes the snapshot's ack waiter, whose continuation runs on another
/// thread (<c>RunContinuationsAsynchronously</c>), and goes straight on to the readiness line after it. The waiter,
/// <c>PublishSafetyStateSnapshotAsync</c>, then republished the session state it read from <c>Current</c> to carry the
/// newly accepted safety version -- a read and a write with nothing between them held. Read before the loop applied
/// RECOVERY_REQUIRED and written after, it put READY back. The entry is offered only while the session says
/// RECOVERY_REQUIRED, so it stayed shut. <c>SendSafetyStateChangedAsync</c> ends the same way after its DurableAck, and
/// the server appends readiness to every SafetyStateChanged answer.
/// </para>
/// <para>
/// <b>How the interleaving is forced, not waited for.</b> The server double sends the ack at once and holds the
/// readiness line behind it (<see cref="FakeControlServer.ReadinessAfterSafetyAckHold"/>). The session's clock is read
/// inside the republish, after the session state has been read and before it is written; <see cref="RepublishGateClock"/>
/// stops the waiter's thread at that read, and only there -- by the frames on its stack. Then the readiness line is let
/// go, the receive loop runs until it has applied it, and the waiter is let go last. Nothing depends on which thread wins
/// on its own. Holding the line matters: without it the SafetyStateChanged waiter, which writes its journal before it
/// reads the session state, lost to the receive loop every time in this harness and the test was green over the defect.
/// A republish that reads and writes under the lock the receive loop also takes cannot be held that way without holding
/// the loop too; the gate then gives up after <see cref="LoopGrace"/> and lets the waiter finish first, which is the order
/// the fix guarantees.
/// </para>
/// <para>
/// The consequence is asserted, not the mechanism: the session's readiness is RECOVERY_REQUIRED and the fault cargo
/// handoff entry is offered, and they stay so.
/// </para>
/// </remarks>
public sealed partial class RecoveryVectorG2Tests
{
    /// <summary>
    /// How long the gate lets the receive loop run before it lets the waiter go. The loop applies one line; this bound
    /// only matters where the loop cannot run, because the republish holds the lock it needs.
    /// </summary>
    private static readonly TimeSpan LoopGrace = TimeSpan.FromSeconds(3);

    /// <summary>How long the final state is watched for a late overwrite.</summary>
    private static readonly TimeSpan SettleWatch = TimeSpan.FromSeconds(2);

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FAULT-CARGO-HANDOFF")]
    public async Task RecoveryRequiredAfterAMidSessionSnapshotAckSurvivesTheSnapshotsRepublish()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        using RepublishGateClock clock = new("PublishSafetyStateSnapshotAsync");
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            loadAlreadySettled: true,
            sessionClock: clock);
        await BringSessionToReadyAsync(harness, token);

        harness.Server.ReadinessReasonOverride = "SESSION_RECOVERY_REQUIRED";
        TaskCompletionSource readinessHold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Server.ReadinessAfterSafetyAckHold = readinessHold.Task;
        clock.Arm();
        await harness.Server.RequestSafetyStateSnapshotAsync();
        long before = harness.Session.Current.SafetyStateVersion;
        await RunGatedInterleavingAsync(harness, clock, readinessHold, token);

        await AssertRecoveryRequiredAndEntryOfferedAsync(harness, token);
        Assert.True(harness.Session.Current.SafetyStateVersion > before);
    }

    /// <summary>
    /// The other direction through the same interleaving: a READY that follows the ack, after a handoff or a recovery
    /// settled, takes effect too. A republish that simply refused to lower readiness, or one that kept the stronger of
    /// two, would keep the vehicle out of work here instead -- the way G3 FP-IS-07 resume-007 once did.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FAULT-CARGO-HANDOFF")]
    public async Task ReadyAfterAMidSessionSnapshotAckSurvivesTheSnapshotsRepublish()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        using RepublishGateClock clock = new("PublishSafetyStateSnapshotAsync");
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            loadAlreadySettled: true,
            sessionClock: clock);
        // The harness leaves the handshake RECOVERY_REQUIRED, departure unknown, and nothing announces readiness again.
        Assert.Equal(WireToGateSessionReadiness.RecoveryRequired, harness.Session.Current.Readiness);
        Assert.True(harness.Business.CanRequestFaultCargoHandoff);

        TaskCompletionSource readinessHold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Server.ReadinessAfterSafetyAckHold = readinessHold.Task;
        long before = harness.Session.Current.SafetyStateVersion;
        clock.Arm();
        await harness.Server.RequestSafetyStateSnapshotAsync();
        Assert.True(
            await clock.WaitUntilEnteredAsync(TimeSpan.FromSeconds(30), token),
            "The republish after the ack never read the session clock; the gate did not engage.");
        readinessHold.SetResult();
        Stopwatch grace = Stopwatch.StartNew();
        while (harness.Session.Current.Readiness != WireToGateSessionReadiness.Ready && grace.Elapsed < LoopGrace)
        {
            await Task.Delay(20, token);
        }

        clock.Release();

        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.Session.Current.Readiness == WireToGateSessionReadiness.Ready
                && !harness.Business.CanRequestFaultCargoHandoff,
            "the session to stay READY with the fault cargo handoff entry shut",
            token);
        Stopwatch watch = Stopwatch.StartNew();
        while (watch.Elapsed < SettleWatch)
        {
            Assert.Equal(WireToGateSessionReadiness.Ready, harness.Session.Current.Readiness);
            await Task.Delay(50, token);
        }

        Assert.True(harness.Session.Current.SafetyStateVersion > before);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FAULT-CARGO-HANDOFF")]
    public async Task RecoveryRequiredAfterASafetyStateChangedAckSurvivesTheChangesRepublish()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        using RepublishGateClock clock = new("SendSafetyStateChangedAsync");
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            loadAlreadySettled: true,
            sessionClock: clock);
        await BringSessionToReadyAsync(harness, token);

        harness.Server.SendReadinessAfterSafetyStateChangedAck = true;
        harness.Server.ReadinessReasonOverride = "SESSION_RECOVERY_REQUIRED";
        TaskCompletionSource readinessHold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Server.ReadinessAfterSafetyAckHold = readinessHold.Task;
        long version = harness.Session.Current.SafetyStateVersion + 100;
        clock.Arm();
        Task<string> change = harness.Session.SendSafetyStateChangedAsync(
            version,
            DateTimeOffset.UtcNow,
            new WireToGateSafetySummaryPayload(true, true, true, true, false, []),
            [1],
            token);
        await RunGatedInterleavingAsync(harness, clock, readinessHold, token);
        await change;

        await AssertRecoveryRequiredAndEntryOfferedAsync(harness, token);
        Assert.True(harness.Session.Current.SafetyStateVersion >= version);
    }

    /// <summary>
    /// The harness ends its handshake RECOVERY_REQUIRED (departure unknown until the vehicle is stopped). A requested
    /// snapshot, answered with the vehicle stopped, brings READY: the entry is then shut, which is where the handoff
    /// finds the session in the field.
    /// </summary>
    private static async Task BringSessionToReadyAsync(RecoveryVectorHarness harness, CancellationToken token)
    {
        await harness.Server.RequestSafetyStateSnapshotAsync();
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.Session.Current.Readiness == WireToGateSessionReadiness.Ready
                && !harness.Business.CanRequestFaultCargoHandoff,
            "the session to be READY with the fault cargo handoff entry shut",
            token);
    }

    /// <summary>
    /// Waits for the waiter to stop at the gate, lets the readiness line after the ack go and the receive loop apply
    /// it, then lets the waiter finish its republish.
    /// </summary>
    private static async Task RunGatedInterleavingAsync(
        RecoveryVectorHarness harness,
        RepublishGateClock clock,
        TaskCompletionSource readinessHold,
        CancellationToken token)
    {
        Assert.True(
            await clock.WaitUntilEnteredAsync(TimeSpan.FromSeconds(30), token),
            "The republish after the ack never read the session clock; the gate did not engage.");
        Assert.Equal(WireToGateSessionReadiness.Ready, harness.Session.Current.Readiness);
        readinessHold.SetResult();
        Stopwatch grace = Stopwatch.StartNew();
        while (harness.Session.Current.Readiness != WireToGateSessionReadiness.RecoveryRequired
            && grace.Elapsed < LoopGrace)
        {
            await Task.Delay(20, token);
        }

        clock.Release();
    }

    private static async Task AssertRecoveryRequiredAndEntryOfferedAsync(
        RecoveryVectorHarness harness,
        CancellationToken token)
    {
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.Session.Current.Readiness == WireToGateSessionReadiness.RecoveryRequired
                && harness.Business.CanRequestFaultCargoHandoff,
            "the session to stay RECOVERY_REQUIRED with the fault cargo handoff entry offered",
            token);
        Stopwatch watch = Stopwatch.StartNew();
        while (watch.Elapsed < SettleWatch)
        {
            Assert.Equal(WireToGateSessionReadiness.RecoveryRequired, harness.Session.Current.Readiness);
            Assert.True(harness.Business.CanRequestFaultCargoHandoff);
            await Task.Delay(50, token);
        }
    }

    /// <summary>
    /// A clock that, once armed, holds the first thread reading it from inside a session-state republish that the
    /// named async method of <see cref="WireToGateSessionClient"/> makes after its ack, until released.
    /// </summary>
    /// <remarks>
    /// Recognised by the stack: a frame of that method's state machine, and under it a synchronous
    /// <see cref="WireToGateSessionClient"/> method whose name contains <c>Publish</c> -- not the method's own lambdas,
    /// which are compiler-named and read the clock while building the message, before anything is sent.
    /// </remarks>
    private sealed class RepublishGateClock(string asyncMethod) : IClock, IDisposable
    {
        private readonly SemaphoreSlim _entered = new(0, 1);
        private readonly ManualResetEventSlim _released = new(false);
        private int _armed;

        public DateTimeOffset Now
        {
            get
            {
                if (Volatile.Read(ref _armed) == 1
                    && IsRepublishFromAsyncMethod()
                    && Interlocked.Exchange(ref _armed, 0) == 1)
                {
                    _entered.Release();
                    _released.Wait(TimeSpan.FromSeconds(60));
                }

                return DateTimeOffset.Now;
            }
        }

        public void Arm() => Volatile.Write(ref _armed, 1);

        public Task<bool> WaitUntilEnteredAsync(TimeSpan timeout, CancellationToken token) =>
            _entered.WaitAsync(timeout, token);

        public void Release() => _released.Set();

        public void Dispose()
        {
            _released.Set();
            _entered.Dispose();
            _released.Dispose();
        }

        private bool IsRepublishFromAsyncMethod()
        {
            StackFrame[] frames = new StackTrace(false).GetFrames();
            bool fromAsyncMethod = frames.Any(frame =>
                frame.GetMethod()?.DeclaringType?.Name.StartsWith($"<{asyncMethod}>", StringComparison.Ordinal) == true);
            bool inRepublish = frames.Any(frame => frame.GetMethod() is MethodBase method
                && method.DeclaringType == typeof(WireToGateSessionClient)
                && method.Name.Contains("Publish", StringComparison.Ordinal)
                && !method.Name.StartsWith('<')
                && !method.Name.EndsWith("Async", StringComparison.Ordinal));
            return fromAsyncMethod && inRepublish;
        }
    }
}
