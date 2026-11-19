using System.Runtime.CompilerServices;
using System.Text.Json;
using SQCD.Agv.Core;
using SQCD.Agv.Wpf.ScreenState;
using Xunit;
using Harness = SQCD.Agv.WireToGateG2Tests.MultiDemandJourneyG2Tests.Harness;
using ScriptStep = SQCD.Agv.WireToGateG2Tests.FakeIoModuleClient.ScriptStep;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 门在等操作员时被空关（装货仍空、卸货仍有货）之后，车不再自己开锁，停着等人按「重新开门」；按键后的开锁里锁没弹开或
/// 弹开又落回、而门关着锁着、货的状态读得到时，退回等按键，不判 UNKNOWN（8005-agv-onboard-hmi#385，用户 2026-10-21 定的
/// 方案 (c)+(a)，CP-0019）。
/// </summary>
/// <remarks>
/// <para>
/// <b>两次现场按 IO 序列回放。</b>10-19 agv03 装货（attempt <c>ff73d54f</c>，5 号仓）：两次空关、各被车自动再开锁；第三次
/// 再开锁时锁 DI 在脉冲后 0.13 秒弹开、0.24 秒后又落回，输出 0.6 秒复位——没撑满 300 毫秒的稳定窗口，开锁反馈等待超时，
/// 结成 UNKNOWN／<c>ACTION_NOT_ALLOWED_IN_STATE</c>。10-21 agv02 卸货（attempt <c>4bee85f5</c>，3 号仓）：四次带篮关门、各被
/// 自动再开锁；第五次再开锁时锁 DI 一直是 1，输出 0.58 秒复位，同样结成 UNKNOWN。时刻取自两份车载日志
/// （<c>C:/work/8005-scratch/slot-unknown-scan-20261020/agv03/agv-20261019.log</c> 第 1149～1162 行，
/// <c>C:/work/8005-scratch/agv02-unload-20261021/agv-20261021.log</c> 第 1246～1251、1418～1426 行）；替身服务端的命令只有
/// 1 号仓，仓号不同不影响判定。稳定窗口取现场的 300 毫秒，开锁反馈超时取现场的 3 秒。
/// </para>
/// <para>
/// <b>先红。</b>修之前车在每次空关后自己再开锁，按回放的序列走到最后一次再开锁就结成 UNKNOWN。每条用例先等「重新开门
/// 出现，或者结果已经发出」，结果先到就把结果原文写进失败信息，红证据里看得到现场那个 UNKNOWN 与原因码。
/// </para>
/// </remarks>
public sealed class DoorShutOverOppositeOccupancyG2Tests
{
    private const int Slot = 1;
    private const int SlotIndex = 0;

    /// <summary>The field's stable window: the lock that released for 0.24 s did not hold it.</summary>
    private static readonly TimeSpan FieldStableWindow = TimeSpan.FromMilliseconds(300);

    /// <summary>The field's <c>UnlockFeedbackTimeoutMs</c>.</summary>
    private static readonly TimeSpan FieldUnlockFeedbackTimeout = TimeSpan.FromSeconds(3);

    /// <summary>The harness's operation timeout, the prompt cadence: two seconds.</summary>
    private static readonly TimeSpan PromptCadence = TimeSpan.FromSeconds(2);

    static DoorShutOverOppositeOccupancyG2Tests()
    {
        // The harness reads its credential and operator id from variables that class's static constructor sets.
        RuntimeHelpers.RunClassConstructor(typeof(MultiDemandJourneyG2Tests).TypeHandle);
    }

    /// <summary>
    /// 10-19 agv03 装货回放：空关之后不再自动开锁，屏上出现「重新开门」；按了才开。第三次开锁锁弹开 0.24 秒又落回，门
    /// 关着锁着、仓仍空，退回等按键，不判 UNKNOWN；再按一次，锁正常弹开，放入篮子关门，装货 COMPLETED。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    public async Task Agv03LoadShutEmptyWaitsForThePressAndALockThatFellBackIsNotUnknown()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { ScriptedHardware = true };
        io.ScriptPulse(1, OpensThenShut(cargo: false));
        io.ScriptPulse(2, OpensThenShut(cargo: false));
        // agv03 14:37:06.640 pulse; 06.771 lock 1->0; 07.005 lock 0->1; 07.239 DO 1->0.
        io.ScriptPulse(
            3,
            new(TimeSpan.FromMilliseconds(130), (fake, slot) => fake.SetLockFeedback(slot, locked: false)),
            new(TimeSpan.FromMilliseconds(365), (fake, slot) => fake.SetLockFeedback(slot, locked: true)),
            new(TimeSpan.FromMilliseconds(600), (fake, slot) => fake.SetUnlockOutput(slot, energised: false)));
        io.ScriptPulse(4, OpensThenShut(cargo: true));
        await using Harness harness = await StartAsync(io, "LOAD", token);

        await WaitForReopenOfferAsync(harness, token);
        Assert.Equal(1, io.UnlockCount);
        SlotExpectedActionWait firstWait = Assert.IsType<SlotExpectedActionWait>(harness.Business.CurrentExpectedActionWait);

        // On screen: the button, enabled, in the reopen slot's words, and the waiting state as a raw code.
        HmiScreenState waiting = harness.ScreenState();
        HmiButtonState reopen = Assert.Single(waiting.Buttons, button => button.Key == "OnReopenOperationClick");
        Assert.True(reopen.Visible);
        Assert.True(reopen.Enabled);
        Assert.Equal("重新开门", reopen.Content);
        HmiRawCodeState status = Assert.Single(waiting.RawCodes, code => code.AutomationId == "ReopenOperation");
        Assert.True(status.Visible);
        Assert.Equal($"SHUT_OVER_OPPOSITE:{Slot}", status.Value);
        Assert.True(harness.OnUi(() => harness.ViewModel.CanRequestLoadCancellation));

        // A full prompt cadence later the door is still shut and nothing was pulsed: no automatic reopen.
        await Task.Delay(PromptCadence + TimeSpan.FromMilliseconds(500), token);
        Assert.Equal(1, io.UnlockCount);
        AssertNoResult(harness);
        Assert.True(harness.OnUi(() => harness.ViewModel.CanReopenOperation));

        Assert.True(await Press(harness));
        await WaitForReopenOfferAsync(harness, token, unlockCount: 2);
        Assert.Equal(firstWait, harness.Business.CurrentExpectedActionWait);
        Assert.True(await Press(harness));

        // The third pulse: the lock released for 0.24 s and fell back. Back to waiting for the press, not UNKNOWN.
        await WaitForReopenOfferAsync(harness, token, unlockCount: 3);
        await io.ScriptsFinishedAsync();
        AssertNoResult(harness);
        Assert.Equal(
            $"REOPEN_NOT_RELEASED:{Slot}",
            Assert.Single(harness.ScreenState().RawCodes, code => code.AutomationId == "ReopenOperation").Value);
        // REQ-0358: counted from the slot's first unlock, through the wait for the press and every pressed reopen.
        Assert.Equal(firstWait, harness.Business.CurrentExpectedActionWait);

        Assert.True(await Press(harness));
        JsonElement result = await WaitForResultAsync(harness, token);
        Assert.Equal(4, io.UnlockCount);
        Assert.Equal("COMPLETED", result.GetProperty("overallOutcome").GetString());
        JsonElement slotResult = Assert.Single(result.GetProperty("slotResults").EnumerateArray());
        Assert.Equal("COMPLETED", slotResult.GetProperty("outcome").GetString());
        Assert.Equal("OCCUPIED", slotResult.GetProperty("finalPhysicalState").GetString());
        Assert.Empty(slotResult.GetProperty("reasonCodes").EnumerateArray());
        AssertNeverSaid(harness, "ACTION_NOT_ALLOWED_IN_STATE", "\"UNKNOWN\"");
    }

    /// <summary>
    /// 10-21 agv02 卸货回放：带篮关门之后不再自动开锁，只有「重新开门」，卸货没有取消入口（REQ-0222）。第五次开锁锁没弹开，
    /// 门关着锁着、仓里有篮，退回等按键；再按一次锁弹开，取出篮子关门，卸货 COMPLETED。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-04")]
    [Trait("ProtocolVector", "CV-DESTINATION-UNLOAD-ALL-EMPTY")]
    public async Task Agv02UnloadShutOverTheBasketWaitsForThePressAndALockThatDidNotReleaseIsNotUnknown()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { ScriptedHardware = true };
        io.SetCargoPresent(SlotIndex, present: true);
        for (int pulse = 1; pulse <= 4; pulse++)
        {
            io.ScriptPulse(pulse, OpensThenShut(cargo: true));
        }

        // agv02 02:39:37.266 pulse; the lock DI stays 1; 37.847 DO 1->0.
        io.ScriptPulse(5, new ScriptStep(TimeSpan.FromMilliseconds(580), (fake, slot) => fake.SetUnlockOutput(slot, energised: false)));
        io.ScriptPulse(6, OpensThenShut(cargo: false));
        await using Harness harness = await StartAsync(io, "UNLOAD", token);

        for (int pulse = 1; pulse <= 4; pulse++)
        {
            await WaitForReopenOfferAsync(harness, token, unlockCount: pulse);
            Assert.False(harness.OnUi(() => harness.ViewModel.CanRequestLoadCancellation));
            Assert.True(await Press(harness));
        }

        await WaitForReopenOfferAsync(harness, token, unlockCount: 5);
        await io.ScriptsFinishedAsync();
        AssertNoResult(harness);
        Assert.Equal(
            $"REOPEN_NOT_RELEASED:{Slot}",
            Assert.Single(harness.ScreenState().RawCodes, code => code.AutomationId == "ReopenOperation").Value);

        Assert.True(await Press(harness));
        JsonElement result = await WaitForResultAsync(harness, token);
        Assert.Equal(6, io.UnlockCount);
        Assert.Equal("COMPLETED", result.GetProperty("overallOutcome").GetString());
        JsonElement slotResult = Assert.Single(result.GetProperty("slotResults").EnumerateArray());
        Assert.Equal("EMPTY", slotResult.GetProperty("finalPhysicalState").GetString());
        AssertNeverSaid(harness, "ACTION_NOT_ALLOWED_IN_STATE", "\"UNKNOWN\"");
    }

    /// <summary>
    /// 真的不可信照旧 UNKNOWN（决策 2 不放宽）：按键开锁后锁没弹开，开锁输出也一直不复位。原因码说出是哪一项：
    /// <c>UNLOCK_OUTPUT_NOT_RESET</c>，不再是 <c>ACTION_NOT_ALLOWED_IN_STATE</c>。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    public async Task AReopenWhoseOutputNeverResetsIsStillUnknownAndSaysSo()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { ScriptedHardware = true };
        io.ScriptPulse(1, OpensThenShut(cargo: false));
        io.ScriptPulse(2);
        await using Harness harness = await StartAsync(io, "LOAD", token);

        await WaitForReopenOfferAsync(harness, token);
        Assert.True(await Press(harness));

        JsonElement slotResult = AssertSingleUnknownSlot(await WaitForResultAsync(harness, token));
        Assert.Equal(["UNLOCK_OUTPUT_NOT_RESET"], ReasonCodes(slotResult));
        Assert.Equal("ACTIVE", slotResult.GetProperty("unlockOutputState").GetString());
    }

    /// <summary>
    /// 真的不可信照旧 UNKNOWN：按键开锁后锁反馈读不到了。原因码 <c>SLOT_STATE_UNKNOWN</c>。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    public async Task AReopenWhoseLockFeedbackTurnsUnreadableIsStillUnknown()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { ScriptedHardware = true };
        io.ScriptPulse(1, OpensThenShut(cargo: false));
        io.ScriptPulse(
            2,
            new(TimeSpan.FromMilliseconds(50), (fake, slot) => fake.SetUnreadable(slot)),
            new(TimeSpan.FromMilliseconds(600), (fake, slot) => fake.SetUnlockOutput(slot, energised: false)));
        await using Harness harness = await StartAsync(io, "LOAD", token);

        await WaitForReopenOfferAsync(harness, token);
        Assert.True(await Press(harness));

        JsonElement slotResult = AssertSingleUnknownSlot(await WaitForResultAsync(harness, token));
        Assert.Equal(["SLOT_STATE_UNKNOWN"], ReasonCodes(slotResult));
    }

    /// <summary>
    /// 同一次等待里按两次「重新开门」只开一次锁：第二下回 false，屏上照常说状态已变。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    public async Task ASecondPressOfTheSameWaitOpensNothingMore()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { ScriptedHardware = true };
        io.ScriptPulse(1, OpensThenShut(cargo: false));
        io.ScriptPulse(2, OpensThenShut(cargo: true));
        await using Harness harness = await StartAsync(io, "LOAD", token);

        await WaitForReopenOfferAsync(harness, token);
        Task<bool> first = Press(harness);
        Task<bool> second = Press(harness);
        bool[] answers = await Task.WhenAll(first, second);

        Assert.Equal(1, answers.Count(answer => answer));
        JsonElement result = await WaitForResultAsync(harness, token);
        Assert.Equal(2, io.UnlockCount);
        Assert.Equal("COMPLETED", result.GetProperty("overallOutcome").GetString());
    }

    /// <summary>
    /// 装货空关后等按键时，「取消装货」照常可用：授权之后原执行器停下、不再开锁，取消执行器接手这扇关着的空门，报
    /// <c>ALL_EMPTY</c>，原装货不发结果。授权是空范围（协议 6.0.0，8005-agv-onboard-hmi#392）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task WhileWaitingForThePressTheLoadCanStillBeCancelled()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { ScriptedHardware = true };
        io.ScriptPulse(1, OpensThenShut(cargo: false));
        // Should the cancellation open the slot to check it, the operator shuts it empty again.
        io.ScriptPulse(2, OpensThenShut(cargo: false));
        await using Harness harness = await StartAsync(
            io,
            "LOAD",
            token,
            server => server.RespondToLoadCancellationRequests = true);

        await WaitForReopenOfferAsync(harness, token);
        Assert.True(harness.OnUi(() => harness.ViewModel.CanRequestLoadCancellation));
        Assert.True(await harness.Business.RequestLoadCancellationAsync("现场不装了。", cancellationToken: token));

        await WaitUntilAsync(
            harness,
            () => harness.Server.ReceivedEnvelopes.Any(envelope => envelope.MessageType == "LoadCancellationResult"),
            "the cancellation's result",
            token);
        Assert.Contains(
            "\"ALL_EMPTY\"",
            harness.Server.ReceivedEnvelopes.Single(envelope => envelope.MessageType == "LoadCancellationResult").WireLine,
            StringComparison.Ordinal);
        Assert.False(harness.OnUi(() => harness.ViewModel.CanReopenOperation));
        AssertNoResult(harness);
    }

    /// <summary>
    /// 真的不可信照旧 UNKNOWN：按键开锁后锁反馈一直抖（放开 0.1 秒、锁上 0.1 秒，持续 5 秒，输出已复位），哪一态都撑不满
    /// 300 毫秒稳定窗口。不能当成「门又被关上」退回等按键（审查 M1：退回判定的稳定窗口改成 0 时原有用例全存活；单元测试的
    /// 替身 IO 不看稳定窗口，只能在这里守）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    public async Task AReopenWhoseLockKeepsBouncingIsStillUnknown()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { ScriptedHardware = true };
        io.ScriptPulse(1, OpensThenShut(cargo: false));
        List<ScriptStep> bounce = [new(TimeSpan.FromMilliseconds(300), (fake, slot) => fake.SetUnlockOutput(slot, energised: false))];
        for (int step = 0; step < 50; step++)
        {
            bool locked = step % 2 == 1;
            bounce.Add(new(TimeSpan.FromMilliseconds(100 * (step + 1)), (fake, slot) => fake.SetLockFeedback(slot, locked)));
        }

        io.ScriptPulse(2, [.. bounce.OrderBy(item => item.After)]);
        await using Harness harness = await StartAsync(io, "LOAD", token);

        await WaitForReopenOfferAsync(harness, token);
        Assert.True(await Press(harness));

        JsonElement slotResult = AssertSingleUnknownSlot(await WaitForResultAsync(harness, token));
        Assert.Equal(["SLOT_INOPERABLE"], ReasonCodes(slotResult));
        Assert.Equal(2, io.UnlockCount);
    }

    /// <summary>
    /// 「取消装货」按下、等着答复时，取消拥有这次装货：「重新开门」不再给出，按了也不开锁（审查 M1：去掉这一判断时原有
    /// 用例全存活）。替身服务端不答复取消，取消一直停在待答。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task APendingLoadCancellationTakesTheReopenAway()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { ScriptedHardware = true };
        io.ScriptPulse(1, OpensThenShut(cargo: false));
        await using Harness harness = await StartAsync(io, "LOAD", token);

        await WaitForReopenOfferAsync(harness, token);
        Task<bool> cancellation = harness.Business.RequestLoadCancellationAsync("现场不装了。", cancellationToken: token);

        await WaitUntilAsync(
            harness,
            () => !harness.Business.CanReopenWaitingSlot && !harness.OnUi(() => harness.ViewModel.CanReopenOperation),
            "「重新开门」 taken away while the cancellation waits for its answer",
            token);
        Assert.Equal(string.Empty, harness.OnUi(() => harness.ViewModel.ReopenWaitStatus));
        Assert.False(await Press(harness));
        Assert.False(await harness.Business.RequestSlotReopenAsync(token));
        Assert.Equal(1, io.UnlockCount);
        // The request itself gives up on the double's silence after the message timeout; what stays is the pending
        // cancellation on file, which is what takes the button away. Its own answer is not this test's subject.
        _ = await cancellation;
    }

    private static ScriptStep[] OpensThenShut(bool cargo) =>
    [
        // agv02 02:32:04.378 pulse; 04.419 lock 1->0; 04.982 DO 1->0. The operator shuts the door a moment later.
        new(TimeSpan.FromMilliseconds(40), (fake, slot) => fake.SetLockFeedback(slot, locked: false)),
        new(TimeSpan.FromMilliseconds(600), (fake, slot) => fake.SetUnlockOutput(slot, energised: false)),
        new(TimeSpan.FromMilliseconds(1200), (fake, slot) => fake.ShutDoor(slot, cargo)),
    ];

    private static async Task<Harness> StartAsync(
        FakeIoModuleClient io,
        string operationType,
        CancellationToken token,
        Action<FakeControlServer>? configure = null) =>
        await Harness.StartAsync(
            server =>
            {
                server.SendJourneySnapshotsAfterRecovery = true;
                server.SendSlotOperationCommandAfterRecovery = true;
                server.SlotOperationType = operationType;
                configure?.Invoke(server);
            },
            token,
            io: io,
            operationTimeout: PromptCadence,
            unlockFeedbackTimeout: FieldUnlockFeedbackTimeout,
            feedbackStableWindow: FieldStableWindow);

    private static Task<bool> Press(Harness harness) =>
        harness.OnUi(() => harness.ViewModel.RequestReopenCurrentOperationAsync());

    /// <summary>
    /// Waits for 「重新开门」 to be offered after <paramref name="unlockCount"/> pulses, or for the operation's result -- the
    /// field's ending, which fails the test with the result's own words.
    /// </summary>
    private static async Task WaitForReopenOfferAsync(Harness harness, CancellationToken token, int unlockCount = 1)
    {
        await WaitUntilAsync(
            harness,
            () => ResultLine(harness) is not null
                || (harness.Io.UnlockCount == unlockCount && harness.OnUi(() => harness.ViewModel.CanReopenOperation)),
            $"「重新开门」 offered after pulse {unlockCount}, or a result",
            token);
        AssertNoResult(harness);
    }

    private static async Task<JsonElement> WaitForResultAsync(Harness harness, CancellationToken token)
    {
        await WaitUntilAsync(harness, () => ResultLine(harness) is not null, "the OperationResult", token);
        using JsonDocument document = JsonDocument.Parse(ResultLine(harness)!);
        return document.RootElement.GetProperty("payload").Clone();
    }

    private static string? ResultLine(Harness harness) =>
        harness.Server.ReceivedEnvelopes
            .Where(envelope => envelope.MessageType == "OperationResult")
            .Select(envelope => envelope.WireLine)
            .FirstOrDefault();

    private static void AssertNoResult(Harness harness)
    {
        string? result = ResultLine(harness);
        Assert.True(
            result is null,
            $"The operation ended instead of waiting for 「重新开门」 (pulses so far: {harness.Io.UnlockCount}): {result}");
    }

    private static void AssertNeverSaid(Harness harness, params string[] fragments) =>
        Assert.All(fragments, fragment => Assert.DoesNotContain(
            harness.Server.ReceivedEnvelopes,
            envelope => envelope.WireLine.Contains(fragment, StringComparison.Ordinal)));

    private static JsonElement AssertSingleUnknownSlot(JsonElement result)
    {
        Assert.Equal("UNKNOWN", result.GetProperty("overallOutcome").GetString());
        JsonElement slotResult = Assert.Single(result.GetProperty("slotResults").EnumerateArray());
        Assert.Equal("UNKNOWN", slotResult.GetProperty("outcome").GetString());
        return slotResult;
    }

    private static string[] ReasonCodes(JsonElement slotResult) =>
        [.. slotResult.GetProperty("reasonCodes").EnumerateArray().Select(code => code.GetString()!)];


    /// <summary>The harness's own wait gives five seconds; the field replays here run up to about fifteen.</summary>
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
