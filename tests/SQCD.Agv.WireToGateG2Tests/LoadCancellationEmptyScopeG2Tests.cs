using System.Runtime.CompilerServices;
using System.Text.Json;
using SQCD.Agv.Core;
using SQCD.Agv.Wpf;
using Xunit;
using Harness = SQCD.Agv.WireToGateG2Tests.MultiDemandJourneyG2Tests.Harness;
using ScriptStep = SQCD.Agv.WireToGateG2Tests.FakeIoModuleClient.ScriptStep;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 装货开始后的取消，按协议 6.0.0 候选与修订后的 <c>REQ-0211</c>（CP-0021 修订项一，8005-agv-onboard-hmi#392）：只有一篮未装、
/// 每个目标仓都读到空、门锁闭、开锁输出复位时才可按；服务端以空范围授权，车载端不为取消开任何门，不做仓位 IO，现读各仓报
/// <c>ALL_EMPTY</c>。
/// </summary>
/// <remarks>
/// <para>
/// <b>现场。</b>联跑 39654207191 的 <c>real-onboard-cancellation-authorization-lost</c>：v6 服务端对装货取消回空范围授权，车载端
/// 拿空范围与自己那条装货的仓位比对，报「恢复向量LOAD_CANCELLATION未执行：reason=RECOVERY_RESPONSE_SCOPE_MISMATCH」，取消没有
/// 结果、旅程不收口。10-21 agv03 @160：5 号仓已装进一篮仍能按出取消，授权后车开锁清空，结成 UNKNOWN 卡死。
/// </para>
/// <para>
/// 替身服务端按 v6 回空范围授权（<see cref="FakeControlServer.LoadCancellationAuthorizedSlots"/> 默认为空）。
/// </para>
/// </remarks>
public sealed class LoadCancellationEmptyScopeG2Tests
{
    private const int Slot = 1;

    /// <summary>The harness's operation timeout, the prompt cadence.</summary>
    private static readonly TimeSpan PromptCadence = TimeSpan.FromSeconds(2);

    static LoadCancellationEmptyScopeG2Tests()
    {
        // The harness reads its credential and operator id from variables that class's static constructor sets.
        RuntimeHelpers.RunClassConstructor(typeof(MultiDemandJourneyG2Tests).TypeHandle);
    }

    /// <summary>
    /// 一篮未装、空门关好锁上等「重新开门」时按取消：服务端空范围授权，车载端不再开锁，报 <c>ALL_EMPTY</c>，逐仓带现读值。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task AnEmptyShutLoadIsCancelledUnderAnEmptyScopeWithoutOpeningAnyDoor()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { ScriptedHardware = true };
        io.ScriptPulse(1, OpensThenShut(cargo: false));
        // Should the cancellation open the slot, the operator shuts it empty again; the count below catches it.
        io.ScriptPulse(2, OpensThenShut(cargo: false));
        await using Harness harness = await StartAsync(io, token, server => server.RespondToLoadCancellationRequests = true);

        await WaitForReopenOfferAsync(harness, token);
        Assert.True(harness.OnUi(() => harness.ViewModel.CanPressLoadCancellation));
        Assert.True(
            await harness.OnUi(() => harness.ViewModel.RequestLoadCancellationAsync(token)),
            "The press was not carried out: " + Said(harness));

        JsonElement result = await WaitForCancellationResultAsync(harness, token);
        Assert.Equal("ALL_EMPTY", result.GetProperty("overallOutcome").GetString());
        JsonElement slotResult = Assert.Single(result.GetProperty("slotResults").EnumerateArray());
        Assert.Equal(Slot, slotResult.GetProperty("slotNo").GetInt32());
        Assert.Equal("EMPTY", slotResult.GetProperty("finalPhysicalState").GetString());
        Assert.Equal(1, io.UnlockCount);
        Assert.Empty(Authorization(harness).GetProperty("slots").EnumerateArray());
        AssertNoOperationResult(harness);
    }

    /// <summary>
    /// 门开着等放货时：「取消装货」在原位但按不动，旁边说先关好仓门；绕过按钮直接按，也不发申请、不开门，说同一句话。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task WithADoorOpenTheCancellationCannotBePressedAndSaysToShutItFirst()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { ScriptedHardware = true };
        io.ScriptPulse(1, OpensAndStaysOpen());
        await using Harness harness = await StartAsync(io, token, server => server.RespondToLoadCancellationRequests = true);

        await WaitUntilAsync(
            harness,
            () => io.UnlockCount == 1 && harness.Business.CurrentExpectedActionWait is not null,
            "slot 1 open, waiting for the operator",
            token);
        await io.ScriptsFinishedAsync();
        await WaitUntilAsync(
            harness,
            () => harness.OnUi(() => harness.ViewModel.HasLoadCancellationBlockedHint),
            "the cancellation shown blocked",
            token);
        Assert.True(harness.OnUi(() => harness.ViewModel.CanRequestLoadCancellation));
        Assert.False(harness.OnUi(() => harness.ViewModel.CanPressLoadCancellation));
        Assert.Equal(
            WireToGateLoadCancellationText.ShutTheDoorFirst,
            harness.OnUi(() => harness.ViewModel.LoadCancellationBlockedHintText));

        Assert.True(await harness.Business.RequestLoadCancellationAsync("现场不装了。", cancellationToken: token));
        Assert.DoesNotContain(
            harness.Server.ReceivedEnvelopes,
            envelope => envelope.MessageType == "LoadCancellationStartRequested");
        Assert.Contains(harness.Events, item => item.Message.Contains(WireToGateLoadCancellationText.ShutTheDoorFirst, StringComparison.Ordinal));
        Assert.Equal(1, io.UnlockCount);
        AssertNoOperationResult(harness);
    }

    /// <summary>
    /// 10-21 agv03 @160 的形状：1 号仓已装进一篮、2 号仓空关锁闭等「重新开门」。「取消装货」按不动，旁边说已装进花篮、要少装按
    /// 「剩下的不装」，「剩下的不装」给出；直接按也不发申请、不开门。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-BASKET-LOADED")]
    public async Task OnceABasketIsLoadedTheCancellationCannotBePressedAndPointsToTheSkip()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { ScriptedHardware = true };
        io.ScriptPulse(1, OpensThenShut(cargo: true));
        io.ScriptPulse(2, OpensThenShut(cargo: false));
        await using Harness harness = await StartAsync(
            io,
            token,
            server => server.RespondToLoadCancellationRequests = true,
            slots: [1, 2]);

        await WaitForReopenOfferAsync(harness, token, unlockCount: 2);
        await WaitUntilAsync(
            harness,
            () => harness.OnUi(() => harness.ViewModel.CanRequestRemainingBasketsSkip),
            "「剩下的不装」 offered",
            token);
        Assert.True(harness.OnUi(() => harness.ViewModel.CanRequestLoadCancellation));
        Assert.False(harness.OnUi(() => harness.ViewModel.CanPressLoadCancellation));
        Assert.Equal(
            WireToGateLoadCancellationText.BasketLoaded,
            harness.OnUi(() => harness.ViewModel.LoadCancellationBlockedHintText));

        Assert.True(await harness.Business.RequestLoadCancellationAsync("现场不装了。", cancellationToken: token));
        Assert.DoesNotContain(
            harness.Server.ReceivedEnvelopes,
            envelope => envelope.MessageType == "LoadCancellationStartRequested");
        Assert.Contains(harness.Events, item => item.Message.Contains(WireToGateLoadCancellationText.BasketLoaded, StringComparison.Ordinal));
        Assert.Equal(2, io.UnlockCount);
        AssertNoOperationResult(harness);
    }

    private static ScriptStep[] OpensAndStaysOpen() =>
    [
        new(TimeSpan.FromMilliseconds(40), (fake, slot) => fake.SetLockFeedback(slot, locked: false)),
        new(TimeSpan.FromMilliseconds(600), (fake, slot) => fake.SetUnlockOutput(slot, energised: false)),
    ];

    private static string Said(Harness harness) =>
        string.Join(" | ", harness.Events.Select(item => $"{item.Kind}: {item.Message}"))
        + " || " + string.Join(" | ", harness.Logger.Tail(10));

    private static JsonElement Authorization(Harness harness)
    {
        string line = harness.Server.SentEnvelopes
            .Single(envelope => envelope.MessageType == "LoadCancellationAuthorization")
            .WireLine;
        using JsonDocument document = JsonDocument.Parse(line);
        return document.RootElement.GetProperty("payload").Clone();
    }

    private static ScriptStep[] OpensThenShut(bool cargo) =>
    [
        new(TimeSpan.FromMilliseconds(40), (fake, slot) => fake.SetLockFeedback(slot, locked: false)),
        new(TimeSpan.FromMilliseconds(600), (fake, slot) => fake.SetUnlockOutput(slot, energised: false)),
        new(TimeSpan.FromMilliseconds(1200), (fake, slot) => fake.ShutDoor(slot, cargo)),
    ];

    private static async Task<Harness> StartAsync(
        FakeIoModuleClient io,
        CancellationToken token,
        Action<FakeControlServer>? configure = null,
        IReadOnlyList<int>? slots = null) =>
        await Harness.StartAsync(
            server =>
            {
                server.SendJourneySnapshotsAfterRecovery = true;
                server.SendSlotOperationCommandAfterRecovery = true;
                server.SlotOperationType = "LOAD";
                if (slots is not null)
                {
                    server.SlotOperationSlots = slots;
                }

                configure?.Invoke(server);
            },
            token,
            io: io,
            operationTimeout: PromptCadence);

    /// <summary>Waits for 「重新开门」 to be offered after <paramref name="unlockCount"/> pulses, or for a result.</summary>
    private static async Task WaitForReopenOfferAsync(Harness harness, CancellationToken token, int unlockCount = 1)
    {
        await WaitUntilAsync(
            harness,
            () => OperationResultLine(harness) is not null
                || (harness.Io.UnlockCount == unlockCount && harness.OnUi(() => harness.ViewModel.CanReopenOperation)),
            $"「重新开门」 offered after pulse {unlockCount}, or a result",
            token);
        AssertNoOperationResult(harness);
    }

    private static async Task<JsonElement> WaitForCancellationResultAsync(Harness harness, CancellationToken token)
    {
        await WaitUntilAsync(
            harness,
            () => CancellationResultLine(harness) is not null,
            "the LoadCancellationResult",
            token);
        using JsonDocument document = JsonDocument.Parse(CancellationResultLine(harness)!);
        return document.RootElement.GetProperty("payload").Clone();
    }

    private static string? CancellationResultLine(Harness harness) =>
        harness.Server.ReceivedEnvelopes
            .Where(envelope => envelope.MessageType == "LoadCancellationResult")
            .Select(envelope => envelope.WireLine)
            .FirstOrDefault();

    private static string? OperationResultLine(Harness harness) =>
        harness.Server.ReceivedEnvelopes
            .Where(envelope => envelope.MessageType == "OperationResult")
            .Select(envelope => envelope.WireLine)
            .FirstOrDefault();

    private static void AssertNoOperationResult(Harness harness)
    {
        string? result = OperationResultLine(harness);
        Assert.True(result is null, $"The load ended on its own (pulses so far: {harness.Io.UnlockCount}): {result}");
    }

    private static async Task WaitUntilAsync(
        Harness harness,
        Func<bool> predicate,
        string expectation,
        CancellationToken token)
    {
        StallAwareDeadline deadline = new(TimeSpan.FromSeconds(20));
        while (!predicate())
        {
            if (deadline.HasExpired)
            {
                Assert.Fail(
                    $"Timed out after {deadline.Describe()} waiting for: {expectation}. Pulses: {harness.Io.UnlockCount}."
                    + Environment.NewLine
                    + string.Join(Environment.NewLine, harness.Events.Select(item => $"{item.Kind}: {item.Message}"))
                    + Environment.NewLine
                    + "Received: " + string.Join(", ", harness.Server.ReceivedEnvelopes.Select(envelope => envelope.MessageType))
                    + Environment.NewLine
                    + string.Join(Environment.NewLine, harness.Logger.Tail(30)));
            }

            await deadline.PollAsync(token);
        }
    }
}
