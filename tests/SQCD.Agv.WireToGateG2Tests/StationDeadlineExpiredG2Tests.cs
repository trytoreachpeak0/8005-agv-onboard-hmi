using System.Net;
using System.Text.Json;
using SQCD.Agv.Application;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using SQCD.Agv.Wpf;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 离站期限到期之后车载端的行为（批次5-29，onboard-hmi#78，program#55 已定）：期限前后一样，读到相反态就重开、
/// 不判死；放弃装货的唯一出口是持工号的操作员按取消，出厂配置下也有这个入口；授权后原执行器先停下，再由取消
/// 执行器清空。
/// </summary>
/// <remarks>
/// 每个用例起一个真的 <see cref="WireToGateBusinessService"/>，接 <see cref="FakeControlServer"/> 与
/// <see cref="FakeIoModuleClient"/>。服务端下发的装货命令只有 1 号仓（替身的 <c>SingleSlot</c>），attempt 固定为
/// <see cref="AttemptId"/>。
/// </remarks>
public sealed partial class StationDeadlineExpiredG2Tests
{
    private const string CredentialVariable = "W2G_G2_DEADLINE_CREDENTIAL";
    private const string OperatorVariable = "W2G_G2_DEADLINE_OPERATOR";

    /// <summary>The attempt <see cref="FakeControlServer"/>'s load command always names.</summary>
    private const string AttemptId = "44444444-4444-4444-4444-444444444444";

    /// <summary>The demand that load command names.</summary>
    private const string DemandId = "11111111-1111-1111-1111-111111111111";

    private const string ExpiredPrompt = "请放入货物并关闭1号仓门；不装了请按取消。";

    /// <summary>
    /// Named apart from every other G2 class: environment variables are process-wide and xUnit runs the
    /// classes in parallel collections.
    /// </summary>
    static StationDeadlineExpiredG2Tests()
    {
        Environment.SetEnvironmentVariable(CredentialVariable, "g2-deadline-credential");
        Environment.SetEnvironmentVariable(OperatorVariable, "operator-078");
    }

    /// <summary>
    /// 验收第 1 条：期限过后空关多次仍重开，结果里没有 <c>FAILED</c>、没有 <c>OPERATOR_TIMEOUT</c>；
    /// 验收第 2 条的一半：每一轮提示都是期限后的文案。
    /// </summary>
    /// <remarks>
    /// 「先红」那一格：批次5-20（onboard-hmi#72）合入后执行器本来就没有期限，重开次数与结果那两格在当前代码上
    /// 已经成立，本用例把它们钉住；不成立的是提示文案——期限过后仍是「请向1号仓放入货物并关门。」。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    public async Task PastTheDeadlineEveryEmptyCloseIsReopenedAndPromptedWithTheWayOut()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await Harness.StartAsync(
            new FakeIoModuleClient { SimulateOperatorLoad = true, EmptyClosesBeforeLoad = 3 },
            token);

        await harness.WaitForEventAsync("OPERATION_COMPLETED", token);

        Assert.Equal(4, harness.Io.UnlockCount);
        JsonElement result = harness.SingleResult("OperationResult");
        Assert.Equal("COMPLETED", result.GetProperty("overallOutcome").GetString());
        Assert.DoesNotContain(harness.Server.ReceivedEnvelopes, envelope =>
            envelope.WireLine.Contains("OPERATOR_TIMEOUT", StringComparison.Ordinal)
            || envelope.WireLine.Contains("\"FAILED\"", StringComparison.Ordinal));

        string[] waiting = harness.ProgressMessages(WireToGateHmiOperationStage.WaitingOperator);
        Assert.Equal(4, waiting.Length);
        Assert.All(waiting, prompt => Assert.StartsWith(ExpiredPrompt, prompt, StringComparison.Ordinal));
        Assert.Equal(ExpiredPrompt, waiting[0]);
        Assert.EndsWith("（第2次提示）", waiting[1], StringComparison.Ordinal);
        Assert.EndsWith("（第4次提示）", waiting[3], StringComparison.Ordinal);
    }

    /// <summary>
    /// 验收第 2 条：期限过后仓门开着时，提示行是规定文案、每个 <c>OperationTimeout</c> 节拍重复一次，倒计时那一行随时钟
    /// 显示已过期时长；门关上、装货完成后倒计时那一行撤回通用文案。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    public async Task PastTheDeadlineTheCountdownLineNamesTheOpenDoorUntilTheLoadIsDone()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        DateTimeOffset deadline = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(90);
        await using Harness harness = await Harness.StartAsync(
            new FakeIoModuleClient { OperatorNeverActs = true },
            token,
            server => server.StationDepartureDeadlineAt = deadline);

        await harness.WaitForStageAsync(WireToGateHmiOperationStage.WaitingOperator, token);
        DateTimeOffset now = deadline + TimeSpan.FromSeconds(95);
        Assert.Equal("已过期 01:35", harness.Business.DescribeExpiredStationDeadline(Context(deadline, now)));

        // The door stays open: one OperationTimeout (2 s here) later the prompt is repeated, in the same
        // words, as a round of its own -- no pulse, no end.
        await Harness.WaitUntilAsync(
            () => harness.ProgressMessages(WireToGateHmiOperationStage.WaitingOperator).Length >= 2,
            "the prompt to be repeated on the OperationTimeout cadence",
            token);
        string repeated = harness.ProgressMessages(WireToGateHmiOperationStage.WaitingOperator)[1];
        Assert.StartsWith(ExpiredPrompt, repeated, StringComparison.Ordinal);
        Assert.EndsWith("（第2次提示）", repeated, StringComparison.Ordinal);
        Assert.Equal(1, harness.Io.UnlockCount);

        harness.Io.CloseDoor(0, cargo: true);
        await harness.WaitForEventAsync("OPERATION_COMPLETED", token);

        Assert.Null(harness.Business.DescribeExpiredStationDeadline(Context(deadline, now)));
    }

    /// <summary>
    /// 验收第 3 条：出厂配置（<c>recoveryResumeEnabled=false</c>）下，在途装货的取消入口出现——它是站点操作员的
    /// 普通一步，与扫码前取消一致；补偿、纠错、恢复与另外两个恢复向量的入口仍不出现。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task WithTheRecoveryEntryOffTheInFlightCancellationIsStillOffered()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await Harness.StartAsync(
            new FakeIoModuleClient { OperatorNeverActs = true },
            token);

        await harness.WaitForStageAsync(WireToGateHmiOperationStage.WaitingOperator, token);

        await Harness.WaitUntilAsync(
            () => harness.Business.CanRequestLoadCancellation,
            "the in-flight load cancellation entry to be offered",
            token);
        Assert.False(harness.Business.CanRequestLoadCompensation);
        Assert.False(harness.Business.CanRequestLoadCorrection);
        Assert.False(harness.Business.CanRequestResumeAfterRepair);
        Assert.False(harness.Business.CanRequestFaultCargoHandoff);
        Assert.False(harness.Business.CanRequestForcedMechanicalRecovery);
    }

    /// <summary>
    /// 同一条入口也要求持工号：没有操作员工号时不出现。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task WithoutAnOperatorIdTheInFlightCancellationIsNotOffered()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await Harness.StartAsync(
            new FakeIoModuleClient { OperatorNeverActs = true },
            token,
            operatorVariable: "W2G_G2_DEADLINE_OPERATOR_UNSET");

        // The recovery state the entries read is refreshed when the load starts, before the door opens.
        await harness.WaitForStageAsync(WireToGateHmiOperationStage.WaitingOperator, token);

        Assert.False(harness.Business.CanRequestLoadCancellation);
    }

    /// <summary>
    /// 验收第 4、5 条：授权取消时 1 号仓门开着。原执行器先停下——之后操作员空关，它也不再开锁；取消执行器接手
    /// 这扇开着的门，不再打脉冲，操作员关上、仓空、锁闭、输出复位即计为已清空，报 <c>ALL_EMPTY</c>。之后服务端重发
    /// 同一条 <c>SlotOperationCommand</c>——取消收尾之前与之后各一次——都不再执行。
    /// </summary>
    /// <remarks>
    /// 先红：取消执行器那半在当前代码上把开着的门判 <c>LOCK_NOT_CLOSED</c>、整次取消 <c>FAILED</c>；原执行器没有
    /// 中止通道，两个执行器同时驱动 1 号仓；取消收尾后清空了日志，重发的命令被当成新命令、再开一次锁。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task AnAuthorizedCancellationStopsTheLoadAndClearsTheDoorItLeftOpen()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await Harness.StartAsync(
            new FakeIoModuleClient { OperatorNeverActs = true },
            token,
            server =>
            {
                server.RespondToLoadCancellationRequests = true;
                server.LoadCancellationAuthorizedSlots = [1];
            });
        await harness.WaitForStageAsync(WireToGateHmiOperationStage.WaitingOperator, token);
        await Harness.WaitUntilAsync(
            () => harness.Business.CanRequestLoadCancellation,
            "the in-flight load cancellation entry to be offered",
            token);

        Task<bool> cancellation = harness.Business.RequestLoadCancellationAsync("现场不装了。", token);
        await Harness.WaitUntilAsync(
            () => harness.ReadRecoveryState(token).RecoveryVector is not null,
            "the authorized cancellation to be journaled as a vector",
            token);


        // Resent while the cancellation holds the door: not executed.
        await harness.Server.ResendSlotOperationCommandAsync();
        await harness.WaitForEventCountAsync("OPERATION_REPLAY", 1, token);

        harness.Io.CloseDoor(0, cargo: false);
        Assert.True(await cancellation, harness.DescribeEvents());

        Assert.Equal(1, harness.Io.UnlockCount);
        JsonElement result = harness.SingleResult("LoadCancellationResult");
        Assert.Equal("ALL_EMPTY", result.GetProperty("overallOutcome").GetString());
        JsonElement slot = Assert.Single(result.GetProperty("slotResults").EnumerateArray());
        Assert.Equal(1, slot.GetProperty("slotNo").GetInt32());
        Assert.Equal("COMPLETED", slot.GetProperty("outcome").GetString());
        Assert.Equal("EMPTY", slot.GetProperty("finalPhysicalState").GetString());
        Assert.Equal("LOCKED", slot.GetProperty("lockState").GetString());
        Assert.Equal("RESET", slot.GetProperty("unlockOutputState").GetString());

        // Resent after the cancellation settled and the journal was cleared: still not executed.
        await harness.Server.ResendSlotOperationCommandAsync();
        await harness.WaitForEventCountAsync("OPERATION_REPLAY", 2, token);

        Assert.Equal(1, harness.Io.UnlockCount);
        Assert.DoesNotContain(harness.Server.Received, item =>
            item.MessageType is "OperationResult" or "SlotOperationCommandRejected");
    }

    /// <summary>
    /// 验收第 6 条（<c>1acb018</c>）：按了取消、授权应答没到，车载端重启。重启后日志里是一次开过锁、没结算的装货，
    /// 带着待答取消记录。中断结算不把它报成 <c>UNKNOWN</c>，而是沿用首发内容重发取消请求，拿到授权后照常清空。
    /// </summary>
    /// <remarks>
    /// 先红：当前代码会话一就绪就走中断结算，发出 <c>OperationResult</c> <c>UNKNOWN</c>／
    /// <c>RECOVERY_CHECKPOINT_NOT_UNIQUE</c>，服务端据此判 RecoveryRequired，而它那边可能早已授权了取消。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task AfterARestartAnUnansweredCancellationIsSentAgainInsteadOfSettlingUnknown()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string journalPath = Harness.NewJournalPath();
        FakeControlServer before;
        await using (Harness beforeRestart = await Harness.StartAsync(
            new FakeIoModuleClient { OperatorNeverActs = true },
            token,
            server =>
            {
                server.RespondToLoadCancellationRequests = true;
                server.LoadCancellationAuthorizedSlots = [1];
                server.LoadCancellationAuthorizationsToDrop = 1;
                server.ReplayJourneySnapshotsWithStableIdentity = true;
            },
            journalPath: journalPath))
        {
            before = beforeRestart.Server;
            await beforeRestart.WaitForStageAsync(WireToGateHmiOperationStage.WaitingOperator, token);
            await Harness.WaitUntilAsync(
                () => beforeRestart.Business.CanRequestLoadCancellation,
                "the in-flight load cancellation entry to be offered",
                token);
            Assert.False(await beforeRestart.Business.RequestLoadCancellationAsync("现场不装了。", token));
            Assert.NotNull(beforeRestart.ReadRecoveryState(token).PendingLoadCancellation);
        }

        // The operator shut the door empty while the vehicle was down.
        await using Harness afterRestart = await Harness.StartAsync(
            new FakeIoModuleClient(),
            token,
            server =>
            {
                // The stop is pushed again once the reconnected session is READY. Stable snapshot content makes that a replay
                // the vehicle takes, as the real server's outbox replay is (it rebinds only the session generation), rather than
                // a revision content conflict.
                server.ReplayJourneySnapshotsWithStableIdentity = true;
                server.RespondToLoadCancellationRequests = true;
                server.LoadCancellationAuthorizedSlots = [1];
                server.AdoptDurableRecoveryMemoryFrom(before);
            },
            journalPath: journalPath,
            baselineRevision: 2);

        await afterRestart.WaitForInboundAsync("LoadCancellationResult", token);

        Assert.DoesNotContain(afterRestart.Server.Received, item => item.MessageType == "OperationResult");
        string first = Assert.Single(
            before.ReceivedEnvelopes,
            envelope => envelope.MessageType == "LoadCancellationStartRequested").WireLine;
        string resent = Assert.Single(
            afterRestart.Server.ReceivedEnvelopes,
            envelope => envelope.MessageType == "LoadCancellationStartRequested").WireLine;
        using (JsonDocument firstDocument = JsonDocument.Parse(first))
        using (JsonDocument resentDocument = JsonDocument.Parse(resent))
        {
            Assert.Equal(
                firstDocument.RootElement.GetProperty("payload").GetRawText(),
                resentDocument.RootElement.GetProperty("payload").GetRawText());
            Assert.NotEqual(
                firstDocument.RootElement.GetProperty("messageId").GetString(),
                resentDocument.RootElement.GetProperty("messageId").GetString());
        }

        Assert.Empty(afterRestart.Server.RecoveryRequestConflicts);
        Assert.Equal(
            "ALL_EMPTY",
            afterRestart.SingleResult("LoadCancellationResult").GetProperty("overallOutcome").GetString());
        Assert.Equal(0, afterRestart.Io.UnlockCount);
    }

    /// <summary>
    /// 验收第 7 条（onboard-hmi#36）：本地没有这次装货的作业上下文——日志里只剩一条待答取消——时，重启后不因为
    /// 服务端的任何消息而开门，也不替服务端补发取消；入口不出现，按下什么都不发。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task WithoutALocalOperationContextNoDoorIsOpenedAndNothingIsSentOnTheServersWord()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FakeIoModuleClient io = new() { OperatorNeverActs = true };
        io.SetCargoPresent(0, true);
        await using Harness harness = await Harness.StartAsync(
            io,
            token,
            server =>
            {
                server.SendSlotOperationCommandAfterRecovery = false;
                server.RespondToLoadCancellationRequests = true;
                server.LoadCancellationAuthorizedSlots = [1];
                server.RecoverySlotOperationAttemptId = AttemptId;
            },
            seed: WireToGateRecoveryState.Empty with
            {
                PendingLoadCancellation = new WireToGatePendingLoadCancellation(
                    "c7b1f2a4-9d3e-4c8a-8f52-0a1b2c3d4e5f",
                    AttemptId,
                    "operator-078",
                    "SESSION",
                    DateTimeOffset.UtcNow,
                    "上一次按下的取消，还没等到应答。")
            });
        await harness.WaitForInboundAsync("SafetyStateChanged", token);
        await Task.Delay(300, token);

        Assert.False(harness.Business.CanRequestLoadCancellation);
        Assert.False(await harness.Business.RequestLoadCancellationAsync("又按了一次。", token));
        Assert.DoesNotContain(harness.Server.Received, item => item.MessageType == "LoadCancellationStartRequested");
        Assert.Equal(0, harness.Io.UnlockCount);
    }

    private static StationDepartureCountdownContext Context(DateTimeOffset deadline, DateTimeOffset now) =>
        new(deadline, now, StationDepartureCountdownFormatter.Format(deadline, now));

    /// <summary>
    /// One vehicle at a pickup whose deadline has already passed, sent a one-slot load as soon as its
    /// session is ready. The recovery entry is off, as it ships (<c>recoveryResumeEnabled=false</c>),
    /// unless a test turns it on; the vehicle is stopped throughout.
    /// </summary>
    private sealed partial class Harness : IAsyncDisposable
    {
        private readonly WireToGateSessionService _session;
        private readonly List<WireToGateOperatorEvent> _events = [];

        private Harness(
            FakeControlServer server,
            FakeIoModuleClient io,
            WireToGateSessionService session,
            WireToGateBusinessService business,
            SqliteWireToGateJournal journal,
            OnboardAlarmBoard alarmBoard)
        {
            Server = server;
            Io = io;
            _session = session;
            Business = business;
            Journal = journal;
            AlarmBoard = alarmBoard;
            business.OperatorEventPublished += (_, args) =>
            {
                lock (_events)
                {
                    _events.Add(args.Value);
                }
            };
        }

        public FakeControlServer Server { get; }

        /// <summary>What the vehicle logged, for a test whose assertion is about a warning.</summary>
        public RecordingLogger Logger { get; private init; } = null!;

        public FakeIoModuleClient Io { get; }

        public WireToGateBusinessService Business { get; }

        public SqliteWireToGateJournal Journal { get; }

        /// <summary>The board the session client publishes <c>OnboardAlarmSnapshot</c> from.</summary>
        public OnboardAlarmBoard AlarmBoard { get; }

        public WireToGateSessionClient Client => _session.Client;

        public static async Task<Harness> StartAsync(
            FakeIoModuleClient io,
            CancellationToken cancellationToken,
            Action<FakeControlServer>? configure = null,
            bool recoveryResumeEnabled = false,
            string operatorVariable = OperatorVariable,
            string? journalPath = null,
            WireToGateRecoveryState? seed = null,
            long baselineRevision = 1,
            Action<WireToGateBusinessService>? observe = null,
            Func<IWireToGateJournal, IWireToGateJournal>? wrapJournal = null,
            Action<WireToGateSessionService>? observeSession = null)
        {
            FakeControlServer server = NewServer();
            configure?.Invoke(server);
            journalPath ??= NewJournalPath();
            SqliteWireToGateJournal journal = new(journalPath);
            if (seed is not null)
            {
                await journal.InitializeAsync(cancellationToken);
                await journal.WriteRecoveryStateAsync(seed, cancellationToken);
            }

            RecordingLogger logger = new();
            StoppedVehicle vehicle = new();
            OnboardAlarmBoard alarmBoard = new("AGV-8005-01", TimeProvider.System);
            WireToGateSessionService session = new(
                new WireToGateSessionOptions(
                    "127.0.0.1",
                    server.Port,
                    "AGV-8005-01",
                    Guid.NewGuid().ToString("D"),
                    new string('a', 40),
                    CredentialVariable,
                    G2SessionTimeouts.Connect,
                    TimeSpan.FromSeconds(2),
                    baselineRevision,
                    baselineRevision,
                    "eight-slot-v1",
                    "eight-slot-modbus-v1",
                    SupportsBatchUnlock: false),
                io,
                wrapJournal?.Invoke(journal) ?? journal,
                logger,
                new SystemClock(),
                vehicle,
                alarmBoard,
                new SlotConfigurationActivationCoordinator(
                    new DocumentActiveSlotConfigurationStore(
                        new G2SlotConfigurationFixtures.InMemoryAtomicDocument(),
                        G2SlotConfigurationFixtures.Approved()),
                    TimeProvider.System),
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromMilliseconds(500));
            WireToGateBusinessService business = new(
                session,
                io,
                logger,
                new SystemClock(),
                () => true,
                new WireToGateSlotOperationExecutorOptions(
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromMilliseconds(10),
                    TimeSpan.FromSeconds(30)),
                operatorVariable,
                vehicle,
                TimeSpan.FromSeconds(30),
                TimeSpan.FromMilliseconds(500),
                new WireToGateRecoveryOptions(
                    recoveryResumeEnabled,
                    "W2G_G2_DEADLINE_PROOF",
                    "MAINTENANCE_ADMINISTRATOR",
                    "CONFIGURED_PROOF"));
            Harness harness = new(server, io, session, business, journal, alarmBoard) { Logger = logger };
            // Before Start, so an observer sees the command's very first progress report.
            observe?.Invoke(business);
            // Also before Start: a handler on the session runs ahead of the business service's own.
            observeSession?.Invoke(session);
            business.Start();
            await session.Client.ConnectAndRecoverAsync(cancellationToken);
            return harness;
        }

        /// <summary>
        /// The double every harness starts with: ready after the recovery report, a worklist whose
        /// deadline passed 30 s ago, then a one-slot load.
        /// </summary>
        public static FakeControlServer NewServer() =>
            new(IPAddress.Loopback)
            {
                SendReadinessAfterRecoveryAck = true,
                SendJourneySnapshotsAfterRecovery = true,
                SendSlotOperationCommandAfterRecovery = true,
                StationDepartureDeadlineAt = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(30)
            };

        public static string NewJournalPath()
        {
            string directory = Path.Combine(Path.GetTempPath(), "w2g-deadline", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, "journal.db");
        }

        public string[] ProgressMessages(WireToGateHmiOperationStage stage)
        {
            lock (_events)
            {
                return
                [
                    .. _events
                        .Where(item => item.Kind == "OPERATION_PROGRESS" && item.Operation?.Stage == stage)
                        .Select(item => item.Message)
                ];
            }
        }

        public bool HasEvent(string kind)
        {
            lock (_events)
            {
                return _events.Any(item => item.Kind == kind);
            }
        }

        /// <summary>Every operator event so far, one per line -- what a failed assertion should show.</summary>
        public string DescribeEvents()
        {
            lock (_events)
            {
                return string.Join(Environment.NewLine, _events.Select(item => $"{item.Kind}: {item.Message}"));
            }
        }

        public Task WaitForEventCountAsync(string kind, int count, CancellationToken cancellationToken) =>
            WaitUntilAsync(
                () =>
                {
                    lock (_events)
                    {
                        return _events.Count(item => item.Kind == kind) >= count;
                    }
                },
                $"{count} operator events {kind}",
                cancellationToken);

        public WireToGateRecoveryState ReadRecoveryState(CancellationToken cancellationToken) =>
            Journal.ReadRecoveryStateAsync(cancellationToken).GetAwaiter().GetResult();

        public Task WaitForEventAsync(string kind, CancellationToken cancellationToken) =>
            WaitUntilAsync(() => HasEvent(kind), $"an operator event {kind}", cancellationToken);

        public Task WaitForStageAsync(WireToGateHmiOperationStage stage, CancellationToken cancellationToken) =>
            WaitUntilAsync(
                () => ProgressMessages(stage).Length > 0,
                $"an operation progress at {stage}",
                cancellationToken);

        public Task WaitForInboundAsync(string messageType, CancellationToken cancellationToken) =>
            WaitUntilAsync(
                () => Server.Received.Any(item => item.MessageType == messageType),
                $"the control server to receive {messageType}",
                cancellationToken,
                DescribeEvents);

        public JsonElement SingleResult(string messageType)
        {
            string line = Assert.Single(
                Server.ReceivedEnvelopes,
                envelope => envelope.MessageType == messageType).WireLine;
            using JsonDocument document = JsonDocument.Parse(line);
            return document.RootElement.GetProperty("payload").Clone();
        }

        /// <summary>The payload of the first <paramref name="messageType"/> received, for one that may be resent.</summary>
        public JsonElement FirstResult(string messageType)
        {
            string line = Server.ReceivedEnvelopes.First(envelope => envelope.MessageType == messageType).WireLine;
            using JsonDocument document = JsonDocument.Parse(line);
            return document.RootElement.GetProperty("payload").Clone();
        }

        public static async Task WaitUntilAsync(
            Func<bool> predicate,
            string expectation,
            CancellationToken cancellationToken,
            Func<string>? describe = null)
        {
            StallAwareDeadline deadline = new(TimeSpan.FromSeconds(10));
            while (!predicate())
            {
                if (deadline.HasExpired)
                {
                    Assert.Fail(
                        $"Timed out after {deadline.Describe()} waiting for: {expectation}"
                        + $"{Environment.NewLine}{describe?.Invoke()}");
                }

                await deadline.PollAsync(cancellationToken);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Business.DisposeAsync();
            await _session.DisposeAsync();
            await Server.DisposeAsync();
        }
    }

    private sealed class StoppedVehicle : IVehicleSafetySignalProvider
    {
        public VehicleSafetySignal Read() => new(VehicleMotionState.Stopped, DateTimeOffset.UtcNow, "G2_TEST");
    }
}
