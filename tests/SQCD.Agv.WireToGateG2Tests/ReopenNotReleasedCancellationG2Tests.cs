using System.Runtime.CompilerServices;
using SQCD.Agv.Core;
using Xunit;
using Harness = SQCD.Agv.WireToGateG2Tests.MultiDemandJourneyG2Tests.Harness;
using ScriptStep = SQCD.Agv.WireToGateG2Tests.FakeIoModuleClient.ScriptStep;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 按了「重新开门」、锁却没弹开（输出亮了又灭、门一直锁着）：门读回锁闭、空、输出复位之后，取消照常按得出（8005-agv-onboard-hmi#392
/// 第二阶段审查 S3）。
/// </summary>
/// <remarks>
/// 执行器发脉冲前记下「开过、还没看到关好」的仓，取消按门没关好算。修复之前，这一形在执行器里走「等门关好复位」的分支回到
/// 等按键，那一记从不清掉，取消一直报 <c>LOCK_NOT_CLOSED</c>，屏上门明明锁着。
/// </remarks>
public sealed class ReopenNotReleasedCancellationG2Tests
{
    static ReopenNotReleasedCancellationG2Tests()
    {
        // The harness reads its credential and operator id from variables that class's static constructor sets.
        RuntimeHelpers.RunClassConstructor(typeof(MultiDemandJourneyG2Tests).TypeHandle);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task AfterAReopenWhoseLockDidNotReleaseTheCancellationCanBePressed()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { ScriptedHardware = true };
        io.ScriptPulse(
            1,
            new ScriptStep(TimeSpan.FromMilliseconds(40), (fake, slot) => fake.SetLockFeedback(slot, locked: false)),
            new ScriptStep(TimeSpan.FromMilliseconds(600), (fake, slot) => fake.SetUnlockOutput(slot, energised: false)),
            new ScriptStep(TimeSpan.FromMilliseconds(1200), (fake, slot) => fake.ShutDoor(slot, cargo: false)));
        // The pressed reopen: the output turns on and falls back, the lock never releases.
        io.ScriptPulse(
            2,
            new ScriptStep(TimeSpan.FromMilliseconds(600), (fake, slot) => fake.SetUnlockOutput(slot, energised: false)));
        await using Harness harness = await Harness.StartAsync(
            server =>
            {
                server.SendJourneySnapshotsAfterRecovery = true;
                server.SendSlotOperationCommandAfterRecovery = true;
                server.SlotOperationType = "LOAD";
                server.RespondToLoadCancellationRequests = true;
            },
            token,
            io: io,
            operationTimeout: TimeSpan.FromSeconds(2),
            unlockFeedbackTimeout: TimeSpan.FromSeconds(3),
            feedbackStableWindow: TimeSpan.FromMilliseconds(300));

        await WaitAsync(
            () => io.UnlockCount == 1 && harness.OnUi(() => harness.ViewModel.CanReopenOperation),
            "「重新开门」 offered over the empty door shut after pulse 1",
            token);
        await io.ScriptsFinishedAsync();
        Assert.Null(harness.Business.LoadCancellationBlockedReason);

        Assert.True(await harness.OnUi(() => harness.ViewModel.RequestReopenCurrentOperationAsync()));
        await WaitAsync(
            () => io.UnlockCount == 2 && harness.OnUi(() => harness.ViewModel.CanReopenOperation),
            "「重新开门」 offered again after the reopen that did not release",
            token);
        await io.ScriptsFinishedAsync();
        Assert.True(io.CurrentSnapshot.Lockers[0] is { IsLocked: true, HasCargo: false, UnlockOutputRaw: false });

        await WaitAsync(
            () => harness.Business.LoadCancellationBlockedReason is null,
            "the cancellation pressable over the door that reads locked, empty and reset",
            token);
        Assert.True(await harness.Business.RequestLoadCancellationAsync("现场确认不装了。", token));
        Assert.Contains(
            harness.Server.ReceivedEnvelopes,
            envelope => envelope.MessageType == "LoadCancellationStartRequested");
        Assert.Equal(2, io.UnlockCount);
    }

    private static async Task WaitAsync(Func<bool> predicate, string what, CancellationToken token)
    {
        DateTimeOffset until = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!predicate())
        {
            Assert.True(DateTimeOffset.UtcNow < until, $"timed out waiting for {what}");
            await Task.Delay(50, token);
        }
    }
}
