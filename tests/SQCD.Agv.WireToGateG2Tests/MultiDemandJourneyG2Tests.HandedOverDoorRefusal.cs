using System.Text.Json;
using SQCD.Agv.Core;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

public sealed partial class MultiDemandJourneyG2Tests
{
    /// <summary>
    /// 在途取消的整体前检拒绝时，交接过来、仍开着的门留在活动开锁集里：结果报 <c>UNKNOWN</c>，重连握手的
    /// <c>RecoveryStateReport</c> 照实报出这扇门（8005-agv-onboard-hmi#249）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>构造。</b>1 号仓装好关门，2 号仓开着等操作员，操作员一直不动（<c>OperatorNeverActs</c>）。替身在写出取消授权之前，
    /// 把 1 号仓的锁反馈设成读不到——授权回来、装货中止、取消向量开始执行时，1 号仓前检必然不过（<c>SLOT_STATE_UNKNOWN</c>），
    /// 而 2 号仓仍开着。
    /// </para>
    /// <para>
    /// <b>修复之前</b>这条拒绝把活动开锁集写成空、报 <c>FAILED</c>：下一次握手告诉服务端「没有门开着」，2 号仓却开着。
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task ACancellationRefusedWhileTheHandedOverDoorStandsOpenReportsThatDoorInTheHandshake()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        await using Harness harness = await StartLatchStopAsync(
            io,
            token,
            server =>
            {
                server.RespondToLoadCancellationRequests = true;
                server.LoadCancellationAuthorizedSlots = [1, 2];
                server.BeforeLoadCancellationAuthorization = () => io.SetUnreadable(0);
            });

        await SendSlotCommandAsync(harness, DemandA, AttemptA, [1, 2]);
        await WaitForDoorOpenAsync(harness, io, AttemptA, pulses: 1, token);
        io.CloseDoor(0, cargo: true);
        await WaitForDoorOpenAsync(harness, io, AttemptA, pulses: 2, token);
        await harness.WaitUntilAsync(
            () => harness.Business.CanRequestLoadCancellation,
            "the in-flight cancellation entry",
            token);

        Task<bool> press = harness.Business.RequestLoadCancellationAsync("现场确认不装了。", token);
        await harness.WaitUntilAsync(
            () => io.UnlockCount > 2 || ReceivedPayloads(harness, "LoadCancellationResult").Length > 0,
            "the cancellation result, or a third pulse",
            token);
        await press;

        Assert.Equal(2, io.UnlockCount);
        JsonElement result = Assert.Single(ReceivedPayloads(harness, "LoadCancellationResult"));
        Assert.Equal("UNKNOWN", result.GetProperty("overallOutcome").GetString());
        JsonElement[] slots = [.. result.GetProperty("slotResults").EnumerateArray()];
        JsonElement refused = Assert.Single(slots, slot => slot.GetProperty("slotNo").GetInt32() == 1);
        Assert.Equal("NOT_STARTED", refused.GetProperty("outcome").GetString());
        Assert.Equal(["SLOT_STATE_UNKNOWN"], ReasonCodes(refused));
        JsonElement open = Assert.Single(slots, slot => slot.GetProperty("slotNo").GetInt32() == 2);
        Assert.Equal("UNKNOWN", open.GetProperty("outcome").GetString());
        Assert.Equal("UNLOCKED", open.GetProperty("lockState").GetString());
        Assert.Equal(["LOCK_NOT_CLOSED"], ReasonCodes(open));
        Assert.Equal([2], ReadJournal(harness, token).ActiveUnlockSlots);

        // The harness connects the client itself, so the test reconnects it, as the other reconnect cases do.
        int firstConnection = harness.Server.ReceivedEnvelopes.Max(envelope => envelope.Connection);
        await harness.Session.Client.DisconnectAsync();
        await harness.Session.Client.ConnectAndRecoverAsync(token);

        (int _, string _, string _, string wireLine) = Assert.Single(
            harness.Server.ReceivedEnvelopes,
            envelope => envelope.Connection > firstConnection && envelope.MessageType == "RecoveryStateReport");
        using JsonDocument report = JsonDocument.Parse(wireLine);
        JsonElement payload = report.RootElement.GetProperty("payload");
        Assert.Equal(
            [2],
            payload.GetProperty("activeUnlockSlots").EnumerateArray().Select(slot => slot.GetInt32()));
        // Reconnecting opened nothing.
        Assert.Equal(2, io.UnlockCount);
    }
}
