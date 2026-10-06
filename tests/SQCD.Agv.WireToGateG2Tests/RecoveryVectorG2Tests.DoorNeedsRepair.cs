using SQCD.Agv.Core;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// A door in doubt whose cause is not a door left open (review of onboard-hmi#273): the lock cannot be read, the unlock
/// output will not reset, or the IO reading is too old. The command is refused all the same, and the operator is told to
/// call maintenance -- not to shut a door, which would change nothing.
/// </summary>
public sealed partial class RecoveryVectorG2Tests
{
    [Theory]
    [InlineData("unreadable", "3号仓门锁状态读不到")]
    [InlineData("output", "3号仓开锁输出未复位")]
    [InlineData("stale", "IO 读数过期或已断开，无法确认3号仓的门锁状态")]
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("ProtocolVector", "CV-RELIABLE-RETRY-DIFFERENT-CONTENT")]
    public async Task ACommandOverADoorThatNeedsRepairIsRefusedAndTheOperatorIsSentToMaintenance(
        string cause,
        string expectedCause)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(token, nothingOnFile: true);
        // As in ACommandOverADoorNotProvenShutIsRefusedUntilTheDoorIsShut: stands for the real server's own-operation
        // exemption, the moment it sends the next slot command.
        harness.Server.RequireSafeSafetyForReadiness = false;
        await harness.Server.SendSessionReadinessAsync();
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.Session.Current.Readiness == WireToGateSessionReadiness.Ready,
            "the session to be ready",
            token);
        await harness.RewriteRecoveryStateAsync(state => state with { ActiveUnlockSlots = [3] }, token);
        switch (cause)
        {
            case "unreadable":
                harness.Io.SetUnreadable(2);
                break;
            case "output":
                harness.Io.SetUnlockOutputActive(2);
                break;
            default:
                // Older than the 30 s the harness's executor trusts.
                harness.Io.MakeStale(TimeSpan.FromMinutes(2));
                break;
        }

        const string newAttemptId = "9f9f9f9f-9f9f-4f9f-8f9f-9f9f9f9f9f9e";
        await SendNewLoadCommandAsync(harness.Server, Guid.NewGuid().ToString("D"), newAttemptId);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.Logger.Entries.Any(entry =>
                entry.Message.StartsWith("拒收SlotOperationCommand：日志簿记录的仓门未能确认已关好", StringComparison.Ordinal)
                && entry.Message.Contains(newAttemptId, StringComparison.Ordinal)),
            "the command to be refused",
            token);

        Assert.Equal(1, RefusalsOf(harness, newAttemptId));
        WireToGateOperatorEvent told = Assert.Single(harness.OperatorEvents, item => item.Kind == "DOOR_NOT_PROVEN_SHUT");
        Assert.Contains(expectedCause, told.Message, StringComparison.Ordinal);
        Assert.Contains("请联系维护人员检查", told.Message, StringComparison.Ordinal);
        Assert.Contains("未打开任何仓门", told.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("关好后这条命令会自动重新执行", told.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("#", told.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("调度", told.Message, StringComparison.Ordinal);
        Assert.Equal(0, harness.Io.UnlockCount);
        Assert.Equal([3], (await harness.ReadRecoveryStateAsync(token)).ActiveUnlockSlots);
    }
}
