using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SQCD.Agv.Application;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using SQCD.Agv.Wpf;
using SQCD.Agv.Wpf.ViewModels;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 管理员在服务端人工判故障的车载端一半（REQ-0359，CP-0005，<c>FP-IS-07</c>，批次8-13，
/// <c>trytoreachpeak0/8005-agv-onboard-hmi#215</c>）：经 <see cref="FakeControlServer"/> 下发
/// <c>SlotFaultDeclarationCommand</c>，看线上的 <c>SlotFaultDeclarationResult</c>、<c>OperationResult</c> 与日志里的判定。
/// </summary>
/// <remarks>
/// <para>
/// 放进 <see cref="StationDeadlineExpiredG2Tests"/> 是为了用它的夹具（真的业务服务、真的执行器、可以一直不动的操作员），
/// 期待动作超时那一组也是这样放的。这里不用夹具自带的单仓命令，改由测试自己发一条三仓装货（<see cref="DeclaredAttemptId"/>），
/// 判定落在第二仓上，前后各有一仓，才看得出「已完成按读数、后面 NOT_STARTED」。
/// </para>
/// <para>
/// 「等到某一刻」一律等真实的事实——执行器上报在等哪一仓、服务端收到哪条消息——不靠固定延时去碰窗口。
/// </para>
/// </remarks>
public sealed partial class StationDeadlineExpiredG2Tests
{
    private const string DeclaredAttemptId = "55555555-5555-4555-8555-555555555555";
    private const string FirstDeclarationId = "d1d1d1d1-d1d1-4d1d-8d1d-d1d1d1d1d1d1";
    private const string SecondDeclarationId = "d2d2d2d2-d2d2-4d2d-8d2d-d2d2d2d2d2d2";
    private static readonly int[] DeclaredSlots = [1, 2, 3];

    /// <summary>
    /// 判定生效（<c>CV-SLOT-FAULT-DECLARATION-APPLIED</c>）：三仓装货、第二仓等操作员时判定。线上先
    /// <c>SlotFaultDeclarationResult(APPLIED)</c> 后 <c>OperationResult</c>；结果里 1 号仓 COMPLETED、2 号仓
    /// UNKNOWN＋<c>SLOT_FAULT_DECLARED</c>、3 号仓 NOT_STARTED；判定之后零开锁；判定先写进日志、后进发件箱；
    /// 操作员看到一句说明，期待动作超时的等待随结果撤下。
    /// </summary>
    /// <remarks>
    /// 先红：照搬装货取消的「中止即沉默」（执行器被判定中止后以 <see cref="OperationCanceledException"/> 结束），
    /// <c>HandleSlotOperationAsync</c> 记一句「已被装货取消中止」就返回，服务端永远等不到 <c>OperationResult</c>，
    /// 等 <c>OperationResult</c> 那一步超时变红。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-SLOT-FAULT-DECLARATION-APPLIED")]
    public async Task ADeclarationOnTheAwaitedSlotIsJournaledAnsweredAppliedAndReportedUnknown()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        DeclarationOrderJournal? order = null;
        await using Harness harness = await StartForDeclarationAsync(
            token,
            wrapJournal: inner => order = new DeclarationOrderJournal(inner));
        await StartThreeSlotLoadAtSlotTwoAsync(harness, token);
        Assert.Equal(2, harness.Business.CurrentExpectedActionWait?.PhysicalSlotNumber);
        int unlocksBeforeDeclaration = harness.Io.UnlockCount;

        await harness.Server.SendCommandAsync(
            "SlotFaultDeclarationCommand",
            Guid.NewGuid().ToString("D"),
            DeclarationPayload(FirstDeclarationId, DeclaredAttemptId, 2));
        await harness.WaitForInboundAsync("OperationResult", token);

        JsonElement answer = harness.SingleResult("SlotFaultDeclarationResult");
        Assert.Equal(FirstDeclarationId, answer.GetProperty("declarationId").GetString());
        Assert.Equal(DeclaredAttemptId, answer.GetProperty("slotOperationAttemptId").GetString());
        Assert.Equal("APPLIED", answer.GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, answer.GetProperty("problem").ValueKind);
        Assert.Equal(
            ExpectedDeclarationResultMessageId(FirstDeclarationId),
            Assert.Single(harness.Server.ReceivedEnvelopes, item => item.MessageType == "SlotFaultDeclarationResult")
                .MessageId);

        // SEND_DECLARATION_RESULT_BEFORE_OPERATION_RESULT，且服务端在 OperationResult 到之前已回了判定应答的 DurableAck。
        string[] received = [.. harness.Server.Received.Select(item => item.MessageType)];
        Assert.True(
            Array.IndexOf(received, "SlotFaultDeclarationResult") < Array.IndexOf(received, "OperationResult"),
            string.Join(", ", received));
        Assert.Contains(harness.Server.SentEnvelopes, item => item.MessageType == "DurableAck"
            && item.WireLine.Contains(ExpectedDeclarationResultMessageId(FirstDeclarationId), StringComparison.Ordinal));

        JsonElement result = harness.SingleResult("OperationResult");
        Assert.Equal("UNKNOWN", result.GetProperty("overallOutcome").GetString());
        AssertWireSlot(result, 1, "COMPLETED", []);
        AssertWireSlot(result, 2, "UNKNOWN", ["SLOT_FAULT_DECLARED"]);
        AssertWireSlot(result, 3, "NOT_STARTED", []);
        Assert.Equal(unlocksBeforeDeclaration, harness.Io.UnlockCount);

        // JOURNAL_DECLARATION_BEFORE_APPLIED_RESULT（durableBeforeAck）：判定先进日志，应答后进发件箱。
        string[] steps = order!.Steps;
        Assert.True(
            Array.IndexOf(steps, "journal:SlotFaultDeclaration") >= 0
            && Array.IndexOf(steps, "journal:SlotFaultDeclaration")
                < Array.IndexOf(steps, "outbox:SlotFaultDeclarationResult"),
            string.Join(", ", steps));
        Assert.True(
            Array.IndexOf(steps, "outbox:SlotFaultDeclarationResult") < Array.IndexOf(steps, "outbox:OperationResult"),
            string.Join(", ", steps));
        WireToGateRecoveryState state = harness.ReadRecoveryState(token);
        Assert.Equal(FirstDeclarationId, state.SlotFaultDeclaration?.DeclarationId);
        Assert.Equal(DeclaredAttemptId, state.UnsettledSlotOperationAttemptId);

        await harness.WaitForEventAsync("SLOT_FAULT_DECLARED", token);
        Assert.Contains(
            "管理员已判定2号仓故障：锁，本次操作转人工恢复。",
            harness.DescribeEvents(),
            StringComparison.Ordinal);
        await Harness.WaitUntilAsync(
            () => harness.Business.CurrentExpectedActionWait is null,
            "the expected-action wait to be withdrawn with the result",
            token);
        Assert.Equal(WireToGateHmiOperationStage.RecoveryRequired, harness.Business.CurrentOperationSnapshot?.Stage);
    }

    /// <summary>
    /// 同一个 <c>declarationId</c> 再到两次：第一次的应答没收到 ack（还在发件箱里待答），第二次是 ack 收到之后。两次都按
    /// 发件箱里第一次写下的那一份重发——<c>messageId</c> 与载荷逐字节相同——执行器只中止一次，只有一条
    /// <c>OperationResult</c>。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-SLOT-FAULT-DECLARATION-APPLIED")]
    public async Task TheSameDeclarationAgainIsAnsweredWithTheFirstAnswerAndNothingIsStoppedTwice()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartForDeclarationAsync(
            token,
            configure: server => server.SlotFaultDeclarationResultAcksToDrop = 1);
        await StartThreeSlotLoadAtSlotTwoAsync(harness, token);
        object declaration = DeclarationPayload(FirstDeclarationId, DeclaredAttemptId, 2);

        await harness.Server.SendCommandAsync("SlotFaultDeclarationCommand", Guid.NewGuid().ToString("D"), declaration);
        await harness.WaitForInboundAsync("OperationResult", token);
        WireToGateDurableMessage first = Assert.IsType<WireToGateDurableMessage>(
            await harness.Journal.ReadOutgoingByDeduplicationKeyAsync(
                WireToGateSessionClient.SlotFaultDeclarationResultKey(FirstDeclarationId),
                token));
        Assert.False(first.Acknowledged);

        // The server's outbox resends the command it has no answer to yet.
        await harness.Server.SendCommandAsync("SlotFaultDeclarationCommand", Guid.NewGuid().ToString("D"), declaration);
        await WaitForCountAsync(harness, "SlotFaultDeclarationResult", 2, token);
        await Harness.WaitUntilAsync(
            () => harness.Journal
                .ReadOutgoingByDeduplicationKeyAsync(
                    WireToGateSessionClient.SlotFaultDeclarationResultKey(FirstDeclarationId),
                    token)
                .GetAwaiter()
                .GetResult()?.Acknowledged == true,
            "the resent answer to be acknowledged",
            token);

        // And once more after the answer is acknowledged.
        await harness.Server.SendCommandAsync("SlotFaultDeclarationCommand", Guid.NewGuid().ToString("D"), declaration);
        await WaitForCountAsync(harness, "SlotFaultDeclarationResult", 3, token);

        (int _, string _, string firstId, string firstLine) = harness.Server.ReceivedEnvelopes
            .First(item => item.MessageType == "SlotFaultDeclarationResult");
        Assert.Equal(first.MessageId, firstId);
        foreach ((int _, string _, string messageId, string line) in harness.Server.ReceivedEnvelopes
            .Where(item => item.MessageType == "SlotFaultDeclarationResult"))
        {
            Assert.Equal(first.MessageId, messageId);
            Assert.Equal(Payload(firstLine).GetRawText(), Payload(line).GetRawText());
        }

        Assert.Single(harness.Server.ReceivedEnvelopes, item => item.MessageType == "OperationResult");
        Assert.Single(
            harness.Logger.Entries,
            entry => entry.Message.StartsWith("已执行服务端的人工判故障", StringComparison.Ordinal));
    }

    /// <summary>
    /// 判定不生效之一，「操作员恰好关门闭环」（<c>CV-SLOT-FAULT-DECLARATION-NOT-APPLICABLE</c>）：闭环结果已经写进发件箱
    /// （服务端已收到 <c>OperationResult</c> COMPLETED）之后判定才到，回 <c>NOT_APPLICABLE</c>，不中止、不再结算，
    /// 发件箱里除了这份应答没有别的新东西。用真实时序构造：等服务端收到闭环结果，再发判定。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-SLOT-FAULT-DECLARATION-NOT-APPLICABLE")]
    public async Task ADeclarationArrivingAfterTheClosedLoopsResultIsInTheOutboxIsNotApplicable()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartForDeclarationAsync(token);
        await StartThreeSlotLoadAtSlotTwoAsync(harness, token);
        harness.Io.CloseDoor(1, cargo: true);
        await WaitForAwaitedSlotAsync(harness, 3, token);
        harness.Io.CloseDoor(2, cargo: true);
        await harness.WaitForInboundAsync("OperationResult", token);
        await harness.WaitForEventAsync("OPERATION_COMPLETED", token);
        DeclarationSnapshot before = await DeclarationSnapshot.TakeAsync(harness, token);

        await harness.Server.SendCommandAsync(
            "SlotFaultDeclarationCommand",
            Guid.NewGuid().ToString("D"),
            DeclarationPayload(FirstDeclarationId, DeclaredAttemptId, 3));
        await harness.WaitForInboundAsync("SlotFaultDeclarationResult", token);

        AssertNotApplicable(harness, "payload.slotOperationAttemptId");
        Assert.Equal("COMPLETED", harness.SingleResult("OperationResult").GetProperty("overallOutcome").GetString());
        await before.AssertUnchangedButTheAnswerAsync(harness, token);
    }

    /// <summary>
    /// 判定不生效之二：attempt 对、<c>slotNo</c> 不是此刻等操作员的那一仓——判的是 3 号仓，车在等 2 号仓。回
    /// <c>NOT_APPLICABLE</c>，执行器照旧在等 2 号仓；2 号仓关好后 3 号仓照常开锁、装货照常完成——判定没有落到下一个仓上。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-SLOT-FAULT-DECLARATION-NOT-APPLICABLE")]
    public async Task ADeclarationNamingAnotherSlotIsNotApplicableAndNeverLandsOnTheNextSlot()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartForDeclarationAsync(token);
        await StartThreeSlotLoadAtSlotTwoAsync(harness, token);
        DeclarationSnapshot before = await DeclarationSnapshot.TakeAsync(harness, token);

        await harness.Server.SendCommandAsync(
            "SlotFaultDeclarationCommand",
            Guid.NewGuid().ToString("D"),
            DeclarationPayload(FirstDeclarationId, DeclaredAttemptId, 3));
        await harness.WaitForInboundAsync("SlotFaultDeclarationResult", token);

        AssertNotApplicable(harness, "payload.slotNo");
        await before.AssertUnchangedButTheAnswerAsync(harness, token);
        Assert.Equal(2, harness.Business.CurrentExpectedActionWait?.PhysicalSlotNumber);

        harness.Io.CloseDoor(1, cargo: true);
        await WaitForAwaitedSlotAsync(harness, 3, token);
        harness.Io.CloseDoor(2, cargo: true);
        await harness.WaitForInboundAsync("OperationResult", token);
        JsonElement result = harness.SingleResult("OperationResult");
        Assert.Equal("COMPLETED", result.GetProperty("overallOutcome").GetString());
        Assert.Equal(3, harness.Io.UnlockCount);
        Assert.Null(harness.ReadRecoveryState(token).SlotFaultDeclaration);
    }

    /// <summary>
    /// 判定不生效之三：本车根本没有这个 attempt（或已换代）。回 <c>NOT_APPLICABLE</c>，正在执行的那一次照旧。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-SLOT-FAULT-DECLARATION-NOT-APPLICABLE")]
    public async Task ADeclarationForAnAttemptThisVehicleIsNotRunningIsNotApplicable()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartForDeclarationAsync(token);
        await StartThreeSlotLoadAtSlotTwoAsync(harness, token);
        DeclarationSnapshot before = await DeclarationSnapshot.TakeAsync(harness, token);

        await harness.Server.SendCommandAsync(
            "SlotFaultDeclarationCommand",
            Guid.NewGuid().ToString("D"),
            DeclarationPayload(FirstDeclarationId, "66666666-6666-4666-8666-666666666666", 2));
        await harness.WaitForInboundAsync("SlotFaultDeclarationResult", token);

        AssertNotApplicable(harness, "payload.slotOperationAttemptId");
        await before.AssertUnchangedButTheAnswerAsync(harness, token);
        Assert.Equal(2, harness.Business.CurrentExpectedActionWait?.PhysicalSlotNumber);
    }

    /// <summary>
    /// 判定不生效之四：该仓已是 UNKNOWN——同一 attempt 已按前一项判定生效，又来一个不同的 <c>declarationId</c>。回
    /// <c>NOT_APPLICABLE</c>（<c>payload.slotNo</c>），不再中止、不再结算：仍只有一条 <c>OperationResult</c>，日志里的判定
    /// 还是第一项。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-SLOT-FAULT-DECLARATION-NOT-APPLICABLE")]
    public async Task ASecondDeclarationOnAnAttemptAlreadyDeclaredIsNotApplicable()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartForDeclarationAsync(token);
        await StartThreeSlotLoadAtSlotTwoAsync(harness, token);
        await harness.Server.SendCommandAsync(
            "SlotFaultDeclarationCommand",
            Guid.NewGuid().ToString("D"),
            DeclarationPayload(FirstDeclarationId, DeclaredAttemptId, 2));
        await harness.WaitForInboundAsync("OperationResult", token);
        await WaitForAckedAsync(harness, FirstDeclarationId, token);
        DeclarationSnapshot before = await DeclarationSnapshot.TakeAsync(harness, token);

        await harness.Server.SendCommandAsync(
            "SlotFaultDeclarationCommand",
            Guid.NewGuid().ToString("D"),
            DeclarationPayload(SecondDeclarationId, DeclaredAttemptId, 2));
        await WaitForCountAsync(harness, "SlotFaultDeclarationResult", 2, token);

        JsonElement second = Payload(harness.Server.ReceivedEnvelopes
            .Last(item => item.MessageType == "SlotFaultDeclarationResult").WireLine);
        Assert.Equal(SecondDeclarationId, second.GetProperty("declarationId").GetString());
        Assert.Equal("NOT_APPLICABLE", second.GetProperty("outcome").GetString());
        Assert.Equal("payload.slotNo", second.GetProperty("problem").GetProperty("fieldPath").GetString());
        await before.AssertUnchangedButTheAnswerAsync(harness, token);
        Assert.Equal(FirstDeclarationId, harness.ReadRecoveryState(token).SlotFaultDeclaration?.DeclarationId);
    }

    /// <summary>
    /// 判定不生效之五：本次装货已由装货取消接手——操作员按了取消、取消请求还在等授权。回 <c>NOT_APPLICABLE</c>，
    /// 不中止执行器、不动取消那一份记录：这一次的结论归取消。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-SLOT-FAULT-DECLARATION-NOT-APPLICABLE")]
    public async Task ADeclarationOnALoadTheCancellationHasTakenOverIsNotApplicable()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartForDeclarationAsync(
            token,
            configure: server =>
            {
                server.RespondToLoadCancellationRequests = true;
                server.LoadCancellationAuthorizationsToDrop = 1;
            });
        await StartThreeSlotLoadAtSlotTwoAsync(harness, token);
        await Harness.WaitUntilAsync(
            () => harness.Business.CanRequestLoadCancellation,
            "the in-flight load cancellation entry to be offered",
            token);
        Assert.False(await harness.Business.RequestLoadCancellationAsync("现场不装了。", token));
        WireToGatePendingLoadCancellation pending = Assert.IsType<WireToGatePendingLoadCancellation>(
            harness.ReadRecoveryState(token).PendingLoadCancellation);
        DeclarationSnapshot before = await DeclarationSnapshot.TakeAsync(harness, token);

        await harness.Server.SendCommandAsync(
            "SlotFaultDeclarationCommand",
            Guid.NewGuid().ToString("D"),
            DeclarationPayload(FirstDeclarationId, DeclaredAttemptId, 2));
        await harness.WaitForInboundAsync("SlotFaultDeclarationResult", token);

        AssertNotApplicable(harness, "payload.slotOperationAttemptId");
        await before.AssertUnchangedButTheAnswerAsync(harness, token);
        Assert.Equal(pending, harness.ReadRecoveryState(token).PendingLoadCancellation);
    }

    /// <summary>
    /// 反方向（PR #247 审查 S1）：判定已生效，替身服务端扣下 APPLIED 的 ack、把窗口拉宽，此时操作员按「取消装货」，服务端也授权了。
    /// 车载端在中止执行器之前看到日志里本 attempt 的判定，拒绝这次取消：不出 <c>LoadCancellationResult</c>，不再驱动被判的仓，
    /// 不开任何别的仓；同一个 attempt 只有判定那一个结论（<c>OperationResult</c> UNKNOWN＋<c>SLOT_FAULT_DECLARED</c>），
    /// 待答取消记录清掉，操作员看到为什么。
    /// </summary>
    /// <remarks>
    /// 先红：去掉取消授权后、中止前那一道检查，服务端收到 <c>LoadCancellationResult</c>，同一 attempt 出了两个结论（审查的探针）。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-SLOT-FAULT-DECLARATION-APPLIED")]
    public async Task ALoadCancellationAuthorizedAfterTheDeclarationIsRefusedBeforeItTakesTheSlotsOver()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartForDeclarationAsync(
            token,
            configure: server =>
            {
                server.SlotFaultDeclarationResultAcksToDrop = 1;
                server.RespondToLoadCancellationRequests = true;
                server.LoadCancellationAuthorizedSlots = [1, 2, 3];
            });
        await StartThreeSlotLoadAtSlotTwoAsync(harness, token);
        int unlocksBeforeDeclaration = harness.Io.UnlockCount;

        await harness.Server.SendCommandAsync(
            "SlotFaultDeclarationCommand",
            Guid.NewGuid().ToString("D"),
            DeclarationPayload(FirstDeclarationId, DeclaredAttemptId, 2));
        // The APPLIED is on the wire and its ack withheld: the declaration is journaled and the run not yet stopped.
        await harness.WaitForInboundAsync("SlotFaultDeclarationResult", token);
        Assert.Equal(FirstDeclarationId, harness.ReadRecoveryState(token).SlotFaultDeclaration?.DeclarationId);

        Assert.False(await harness.Business.RequestLoadCancellationAsync("现场不装了。", token));
        await harness.WaitForInboundAsync("OperationResult", token);
        await Task.Delay(300, token);

        Assert.Contains(harness.Server.Received, item => item.MessageType == "LoadCancellationStartRequested");
        Assert.DoesNotContain(harness.Server.Received, item => item.MessageType == "LoadCancellationResult");
        JsonElement result = harness.SingleResult("OperationResult");
        Assert.Equal("UNKNOWN", result.GetProperty("overallOutcome").GetString());
        AssertWireSlot(result, 2, "UNKNOWN", ["SLOT_FAULT_DECLARED"]);
        Assert.Equal(unlocksBeforeDeclaration, harness.Io.UnlockCount);
        WireToGateRecoveryState state = harness.ReadRecoveryState(token);
        Assert.Null(state.PendingLoadCancellation);
        Assert.Null(state.RecoveryVector);
        Assert.Contains(
            OnboardCommandRejectionText.DescribeRecoveryBlocked("LOAD_CANCELLATION_AFTER_SLOT_FAULT_DECLARATION"),
            harness.DescribeEvents(),
            StringComparison.Ordinal);
        Assert.False(harness.HasEvent("RECOVERY_VECTOR_AUTHORIZED"));
    }

    /// <summary>
    /// 判定不生效之六：该仓在判定到达前已因 IO 读不出来被执行器判为 UNKNOWN、结果已在发件箱。回 <c>NOT_APPLICABLE</c>，
    /// 原来那份 <c>SLOT_STATE_UNKNOWN</c> 的结果不变，没有第二条 <c>OperationResult</c>。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-SLOT-FAULT-DECLARATION-NOT-APPLICABLE")]
    public async Task ADeclarationOnASlotAlreadyUnknownIsNotApplicable()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await StartForDeclarationAsync(token);
        await StartThreeSlotLoadAtSlotTwoAsync(harness, token);
        harness.Io.SetUnreadable(1);
        await harness.WaitForInboundAsync("OperationResult", token);
        DeclarationSnapshot before = await DeclarationSnapshot.TakeAsync(harness, token);

        await harness.Server.SendCommandAsync(
            "SlotFaultDeclarationCommand",
            Guid.NewGuid().ToString("D"),
            DeclarationPayload(FirstDeclarationId, DeclaredAttemptId, 2));
        await harness.WaitForInboundAsync("SlotFaultDeclarationResult", token);

        AssertNotApplicable(harness, "payload.slotOperationAttemptId");
        await before.AssertUnchangedButTheAnswerAsync(harness, token);
        AssertWireSlot(harness.SingleResult("OperationResult"), 2, "UNKNOWN", ["SLOT_STATE_UNKNOWN"]);
    }

    /// <summary>
    /// 崩溃点：判定已写进日志、结果还没发出时进程退出；重启时 2 号仓读数已是目标态（锁闭、有货、输出复位）。重启后先补发
    /// 同一份 <c>APPLIED</c>（<c>messageId</c> 是 <c>StableUuid</c>），再由中断结算报 <c>OperationResult</c>：2 号仓仍是
    /// UNKNOWN＋<c>SLOT_FAULT_DECLARED</c>，没有按实时读数结算成 COMPLETED；不开任何锁。服务端随后重发同一判定，
    /// 回的还是那一份，不再结算。
    /// </summary>
    /// <remarks>
    /// 先红：中断结算不认日志里的判定时，2 号仓「开过、在目标态」被报成 COMPLETED——正是这张票要防的。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-SLOT-FAULT-DECLARATION-APPLIED")]
    public async Task AfterARestartTheJournaledDeclarationIsAnsweredAgainAndTheSlotStaysUnknown()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { KeepSnapshotFresh = true };
        io.CloseDoor(0, cargo: true);
        io.CloseDoor(1, cargo: true);
        WireToGateSlotOperationCommand command = ThreeSlotLoad();
        WireToGateRecoveryState journaled = new(
            DeclaredAttemptId,
            WireToGateRecoveryCheckpoint.ActiveUnlockSet,
            [2],
            0,
            [])
        {
            OperationContext = WireToGateRecoveryOperationContext.FromCommand(command),
            CompletedSlots = [1],
            SlotResults = [new WireToGateSlotExecutionResult(1, "COMPLETED", "OCCUPIED", "LOCKED", "RESET", [])],
            SlotFaultDeclaration = new WireToGateSlotFaultDeclaration(FirstDeclarationId, DeclaredAttemptId, 2, "LOCK")
        };

        await using Harness harness = await StartForDeclarationAsync(token, io: io, seed: journaled);
        await harness.WaitForInboundAsync("OperationResult", token);

        string[] received = [.. harness.Server.Received.Select(item => item.MessageType)];
        Assert.True(
            Array.IndexOf(received, "SlotFaultDeclarationResult") is >= 0 and var answered
            && answered < Array.IndexOf(received, "OperationResult"),
            string.Join(", ", received));
        Assert.Equal(
            ExpectedDeclarationResultMessageId(FirstDeclarationId),
            Assert.Single(harness.Server.ReceivedEnvelopes, item => item.MessageType == "SlotFaultDeclarationResult")
                .MessageId);
        Assert.Equal("APPLIED", harness.SingleResult("SlotFaultDeclarationResult").GetProperty("outcome").GetString());
        JsonElement result = harness.SingleResult("OperationResult");
        Assert.Equal("UNKNOWN", result.GetProperty("overallOutcome").GetString());
        AssertWireSlot(result, 1, "COMPLETED", []);
        AssertWireSlot(result, 2, "UNKNOWN", ["SLOT_FAULT_DECLARED"]);
        AssertWireSlot(result, 3, "NOT_STARTED", []);
        Assert.Equal(0, harness.Io.UnlockCount);
        // The operator is told again after the restart, and the warning says which slot was not settled from the live IO
        // (review N2 of PR #247).
        await harness.WaitForEventAsync("SLOT_FAULT_DECLARED", token);
        Assert.Contains(
            "管理员已判定2号仓故障：锁，本次操作转人工恢复。",
            harness.DescribeEvents(),
            StringComparison.Ordinal);
        Assert.Contains(
            harness.Logger.Entries,
            entry => entry.Message.Contains("2号仓按日志里已生效的管理员判定", StringComparison.Ordinal));

        await WaitForAckedAsync(harness, FirstDeclarationId, token);
        await harness.Server.SendCommandAsync(
            "SlotFaultDeclarationCommand",
            Guid.NewGuid().ToString("D"),
            DeclarationPayload(FirstDeclarationId, DeclaredAttemptId, 2));
        await WaitForCountAsync(harness, "SlotFaultDeclarationResult", 2, token);
        string[] answers =
        [
            .. harness.Server.ReceivedEnvelopes
                .Where(item => item.MessageType == "SlotFaultDeclarationResult")
                .Select(item => Payload(item.WireLine).GetRawText())
        ];
        Assert.Equal(answers[0], answers[1]);
        Assert.Single(harness.Server.ReceivedEnvelopes, item => item.MessageType == "OperationResult");
    }

    /// <summary>
    /// 操作工界面上没有任何判定入口（REQ-0359「OnboardHmi 不提供判定入口」）：视图模型与业务服务对界面公开的成员里没有
    /// 一个与判故障有关。判定只从服务端来。
    /// </summary>
    [Fact]
    public void TheOperatorScreenOffersNoWayToDeclareASlotFaulty()
    {
        string[] offending =
        [
            .. new[] { typeof(MainViewModel), typeof(WireToGateBusinessService) }
                .SelectMany(type => type.GetMembers(
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                .Select(member => $"{member.DeclaringType?.Name}.{member.Name}")
                .Where(name => name.Contains("Declar", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("SlotFault", StringComparison.OrdinalIgnoreCase))
        ];

        Assert.Empty(offending);
    }

    private static Task<Harness> StartForDeclarationAsync(
        CancellationToken cancellationToken,
        Action<FakeControlServer>? configure = null,
        Func<IWireToGateJournal, IWireToGateJournal>? wrapJournal = null,
        FakeIoModuleClient? io = null,
        WireToGateRecoveryState? seed = null) =>
        Harness.StartAsync(
            io ?? new FakeIoModuleClient { OperatorNeverActs = true, KeepSnapshotFresh = true },
            cancellationToken,
            server =>
            {
                server.SendSlotOperationCommandAfterRecovery = false;
                server.StationDepartureDeadlineAt = null;
                configure?.Invoke(server);
            },
            seed: seed,
            wrapJournal: wrapJournal);

    /// <summary>
    /// Sends the three-slot load, lets the operator finish slot 1, and returns once the executor reports it is
    /// waiting on slot 2.
    /// </summary>
    private static async Task StartThreeSlotLoadAtSlotTwoAsync(Harness harness, CancellationToken cancellationToken)
    {
        await Harness.WaitUntilAsync(
            () => harness.Client.Current.Readiness == WireToGateSessionReadiness.Ready,
            "the session to be READY",
            cancellationToken);
        await harness.Server.SendCommandAsync(
            "SlotOperationCommand",
            Guid.NewGuid().ToString("D"),
            new
            {
                demandId = DemandId,
                operationSessionId = "33333333-3333-3333-3333-333333333333",
                slotOperationAttemptId = DeclaredAttemptId,
                operationType = "LOAD",
                slots = DeclaredSlots,
                expectedBasketCount = 3,
                expectedFinalPhysicalState = "OCCUPIED",
                commandContentSha256 = new string('0', 64)
            },
            Guid.NewGuid().ToString("D"));
        await WaitForAwaitedSlotAsync(harness, 1, cancellationToken);
        harness.Io.CloseDoor(0, cargo: true);
        await WaitForAwaitedSlotAsync(harness, 2, cancellationToken);
    }

    private static Task WaitForAwaitedSlotAsync(Harness harness, int slot, CancellationToken cancellationToken) =>
        Harness.WaitUntilAsync(
            () => harness.Business.CurrentExpectedActionWait is { } wait
                && wait.SlotOperationAttemptId == DeclaredAttemptId
                && wait.PhysicalSlotNumber == slot
                && harness.Business.CurrentOperationSnapshot?.Stage == WireToGateHmiOperationStage.WaitingOperator,
            $"the executor to wait on slot {slot}",
            cancellationToken,
            harness.DescribeEvents);

    private static Task WaitForCountAsync(
        Harness harness,
        string messageType,
        int count,
        CancellationToken cancellationToken) =>
        Harness.WaitUntilAsync(
            () => harness.Server.Received.Count(item => item.MessageType == messageType) >= count,
            $"{count} {messageType}",
            cancellationToken,
            harness.DescribeEvents);

    private static Task WaitForAckedAsync(Harness harness, string declarationId, CancellationToken cancellationToken) =>
        Harness.WaitUntilAsync(
            () => harness.Journal
                .ReadOutgoingByDeduplicationKeyAsync(
                    WireToGateSessionClient.SlotFaultDeclarationResultKey(declarationId),
                    cancellationToken)
                .GetAwaiter()
                .GetResult()?.Acknowledged == true,
            $"the answer to {declarationId} to be acknowledged",
            cancellationToken);

    private static WireToGateSlotOperationCommand ThreeSlotLoad() => new(
        "77777777-7777-4777-8777-777777777771",
        "77777777-7777-4777-8777-777777777772",
        1,
        DateTimeOffset.UtcNow,
        DemandId,
        "33333333-3333-3333-3333-333333333333",
        DeclaredAttemptId,
        OperationType.Load,
        [1, 2, 3],
        3,
        true,
        new string('0', 64));

    private static object DeclarationPayload(string declarationId, string attemptId, int slotNo) => new
    {
        declarationId,
        demandId = DemandId,
        slotOperationAttemptId = attemptId,
        slotNo,
        administrator = new
        {
            operatorId = "admin-007",
            verificationMethod = "SESSION",
            verifiedAt = DateTimeOffset.UtcNow
        },
        administratorRole = "MAINTENANCE_ADMINISTRATOR",
        faultCategory = "LOCK",
        note = "锁舌断裂：门已关好，锁传感器仍读未锁。",
        declaredAt = DateTimeOffset.UtcNow
    };

    /// <summary>
    /// <c>StableUuid</c> of the answer's outbox key, as the session client derives it: the answer's identity depends on
    /// the declaration alone, never on the attempt or on when it was sent.
    /// </summary>
    private static string ExpectedDeclarationResultMessageId(string declarationId)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"slot-fault-declaration-result:{declarationId}"));
        byte[] bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes).ToString("D");
    }

    private static void AssertNotApplicable(Harness harness, string fieldPath)
    {
        JsonElement answer = harness.SingleResult("SlotFaultDeclarationResult");
        Assert.Equal(FirstDeclarationId, answer.GetProperty("declarationId").GetString());
        Assert.Equal("NOT_APPLICABLE", answer.GetProperty("outcome").GetString());
        JsonElement problem = answer.GetProperty("problem");
        Assert.Equal("ACTION_NOT_ALLOWED_IN_STATE", problem.GetProperty("reasonCode").GetString());
        Assert.Equal(fieldPath, problem.GetProperty("fieldPath").GetString());
        Assert.False(string.IsNullOrEmpty(problem.GetProperty("displayMessage").GetString()));
        Assert.False(harness.HasEvent("SLOT_FAULT_DECLARED"));
    }

    private static void AssertWireSlot(JsonElement result, int slotNo, string outcome, string[] reasonCodes)
    {
        JsonElement slot = result.GetProperty("slotResults").EnumerateArray()
            .Single(item => item.GetProperty("slotNo").GetInt32() == slotNo);
        Assert.Equal(outcome, slot.GetProperty("outcome").GetString());
        string[] onTheWire = [.. slot.GetProperty("reasonCodes").EnumerateArray().Select(code => code.GetString()!)];
        Assert.Equal(reasonCodes, onTheWire);
    }

    private static JsonElement Payload(string wireLine)
    {
        using JsonDocument document = JsonDocument.Parse(wireLine);
        return document.RootElement.GetProperty("payload").Clone();
    }

    /// <summary>
    /// What a refused declaration must leave alone: the journal's recovery state, every outbox message the server has
    /// received but the answer, and the doors.
    /// </summary>
    private sealed record DeclarationSnapshot(string RecoveryState, string[] OtherMessages, int Unlocks)
    {
        public static async Task<DeclarationSnapshot> TakeAsync(Harness harness, CancellationToken cancellationToken)
        {
            WireToGateRecoveryState state = await harness.Journal.ReadRecoveryStateAsync(cancellationToken);
            return new(JsonSerializer.Serialize(state), MessagesOtherThanTheAnswer(harness), harness.Io.UnlockCount);
        }

        public async Task AssertUnchangedButTheAnswerAsync(Harness harness, CancellationToken cancellationToken)
        {
            // Long enough for anything the declaration set off -- an abort, a settlement -- to reach the wire.
            await Task.Delay(300, cancellationToken);
            WireToGateRecoveryState state = await harness.Journal.ReadRecoveryStateAsync(cancellationToken);
            Assert.Equal(RecoveryState, JsonSerializer.Serialize(state));
            Assert.Equal(OtherMessages, MessagesOtherThanTheAnswer(harness));
            Assert.Equal(Unlocks, harness.Io.UnlockCount);
        }

        private static string[] MessagesOtherThanTheAnswer(Harness harness) =>
        [
            .. harness.Server.ReceivedEnvelopes
                .Where(item => item.MessageType is not ("SlotFaultDeclarationResult" or "OperationProgress"
                    or "SafetyStateChanged" or "SafetyStateSnapshot" or "OnboardAlarmSnapshot" or "Heartbeat"))
                .Select(item => item.MessageId)
        ];
    }

    /// <summary>
    /// Records, in order, the journal write that first holds a slot fault declaration and every outbox row written
    /// before it is sent -- what tells "journaled, then answered" from the reverse.
    /// </summary>
    private sealed class DeclarationOrderJournal(IWireToGateJournal inner) : IWireToGateJournal
    {
        private readonly List<string> _steps = [];

        public string[] Steps
        {
            get
            {
                lock (_steps)
                {
                    return [.. _steps];
                }
            }
        }

        private void Record(string step)
        {
            lock (_steps)
            {
                if (!step.StartsWith("journal:", StringComparison.Ordinal) || !_steps.Contains(step))
                {
                    _steps.Add(step);
                }
            }
        }

        public async Task<WireToGateRecoveryState?> UpdateRecoveryStateAsync(
            Func<WireToGateRecoveryState, WireToGateRecoveryState?> change,
            CancellationToken cancellationToken = default)
        {
            WireToGateRecoveryState? written = await inner.UpdateRecoveryStateAsync(change, cancellationToken);
            if (written?.SlotFaultDeclaration is not null)
            {
                Record("journal:SlotFaultDeclaration");
            }

            return written;
        }

        public async Task<WireToGateRecoveryState?> UpdateRecoveryStateAsync(
            Func<WireToGateRecoveryState, WireToGateRecoveryState?> change,
            Action<WireToGateRecoveryState> settled,
            CancellationToken cancellationToken = default)
        {
            WireToGateRecoveryState? written = await inner.UpdateRecoveryStateAsync(change, settled, cancellationToken);
            if (written?.SlotFaultDeclaration is not null)
            {
                Record("journal:SlotFaultDeclaration");
            }

            return written;
        }

        public async Task<WireToGateDurableMessage> SaveOutgoingBeforeSendAsync(
            WireToGateDurableMessage message,
            CancellationToken cancellationToken = default)
        {
            WireToGateDurableMessage saved = await inner.SaveOutgoingBeforeSendAsync(message, cancellationToken);
            Record($"outbox:{message.MessageType}");
            return saved;
        }

        public Task<WireToGateRecoveryState> ReadRecoveryStateAsync(CancellationToken cancellationToken = default) =>
            inner.ReadRecoveryStateAsync(cancellationToken);

        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            inner.InitializeAsync(cancellationToken);

        public Task<string> ReadJournalEpochAsync(CancellationToken cancellationToken = default) =>
            inner.ReadJournalEpochAsync(cancellationToken);

        public Task<WireToGateDurableMessage> ReplaceOutgoingForReplayAsync(
            WireToGateDurableMessage expected,
            WireToGateDurableMessage replacement,
            CancellationToken cancellationToken = default) =>
            inner.ReplaceOutgoingForReplayAsync(expected, replacement, cancellationToken);

        public Task<WireToGateDurableMessage?> ReadOutgoingByDeduplicationKeyAsync(
            string deduplicationKey,
            CancellationToken cancellationToken = default) =>
            inner.ReadOutgoingByDeduplicationKeyAsync(deduplicationKey, cancellationToken);

        public Task<WireToGateDurableMessage?> ReadOutgoingByMessageIdAsync(
            string messageId,
            CancellationToken cancellationToken = default) =>
            inner.ReadOutgoingByMessageIdAsync(messageId, cancellationToken);

        public Task MarkOutgoingAcknowledgedAsync(
            string messageId,
            string acceptedContentSha256,
            CancellationToken cancellationToken = default) =>
            inner.MarkOutgoingAcknowledgedAsync(messageId, acceptedContentSha256, cancellationToken);

        public Task<IReadOnlyList<WireToGateDurableMessage>> ReadUnacknowledgedOutgoingAsync(
            CancellationToken cancellationToken = default) =>
            inner.ReadUnacknowledgedOutgoingAsync(cancellationToken);

        public Task<IReadOnlyList<WireToGateAppliedJourneySnapshot>> ReadAppliedJourneySnapshotsAsync(
            CancellationToken cancellationToken = default) =>
            inner.ReadAppliedJourneySnapshotsAsync(cancellationToken);

        public Task<WireToGateAppliedJourneySnapshot> SaveAppliedJourneySnapshotAsync(
            WireToGateAppliedJourneySnapshot snapshot,
            CancellationToken cancellationToken = default) =>
            inner.SaveAppliedJourneySnapshotAsync(snapshot, cancellationToken);

        public Task<string> ComputeContentSha256Async(CancellationToken cancellationToken = default) =>
            inner.ComputeContentSha256Async(cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
