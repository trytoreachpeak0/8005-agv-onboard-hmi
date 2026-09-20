using System.Collections.Concurrent;
using System.Net;
using SQCD.Agv.Application;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using SQCD.Agv.Wpf;
using SQCD.Agv.Wpf.ViewModels;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 录入后拒收的车载端半边（批次5-28，onboard-hmi#77）：操作员录入的子批被服务端按 BR-013 重算后拒收时，
/// 车上显示真实原因，不再把 <c>SublotRejected</c> 当恢复消息。
/// </summary>
/// <remarks>
/// <para>
/// 向量 <c>CV-SUBLOT-REJECTED-AFTER-ENTRY</c> 是 <c>SublotEntryRequested</c> → <c>SublotSubmitted</c> →
/// <c>SublotRejected</c>（<c>PACKAGE_CAPACITY_UNRESOLVED</c>）。服务端半边是 control-server#82（批次5-17），
/// 替身按候选包形状拒收；两端联调在 control-server#87 的 <c>g3-sublot-rejected</c>。
/// </para>
/// <para>
/// 修改前，业务层清掉录入请求后落进恢复消息分支，经恢复安全策略发出「收到恢复消息 SublotRejected，
/// 当前安全条件不允许执行」——操作员看到的是一条与拒收无关的恢复告警。
/// </para>
/// </remarks>
public sealed class SublotRejectedAfterEntryG2Tests
{
    private const string OperatorVariable = "W2G_G2_SUBLOT_REJECTED_OPERATOR";
    private const string CredentialVariable = "W2G_G2_SUBLOT_REJECTED_CREDENTIAL";
    private const string OperationSessionId = "77777777-7777-4777-8777-777777777777";

    static SublotRejectedAfterEntryG2Tests()
    {
        Environment.SetEnvironmentVariable(CredentialVariable, "g2-sublot-rejected-credential");
        Environment.SetEnvironmentVariable(OperatorVariable, "operator-001");
    }

    /// <summary>
    /// 向量三步走完：拒收以自己的事件显示真实原因与被拒的子批，不发 <c>RECOVERY_BLOCKED</c>。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-SUBLOT-REJECTED-AFTER-ENTRY")]
    public async Task ARejectionAfterEntryIsShownAsItsReasonAndNotAsABlockedRecovery()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RejectionHarness harness = await RejectionHarness.StartAsync(
            server => server.RejectSublotSubmissionsWith = "PACKAGE_CAPACITY_UNRESOLVED",
            token);

        await harness.Business.SubmitSublotAsync("SUBLOT-001", "SCANNER", token);

        // Either answer is observable, so the wait ends on whichever the build gives: before #77 the
        // rejection fell into the recovery branch and came out as RECOVERY_BLOCKED.
        await RejectionHarness.WaitUntilAsync(
            () => harness.Events.Any(item => item.Kind is "SUBLOT_REJECTED" or "RECOVERY_BLOCKED"),
            "the rejection to reach the operator",
            token);
        Assert.DoesNotContain(harness.Events, item => item.Kind == "RECOVERY_BLOCKED");
        WireToGateOperatorEvent rejected = Assert.Single(
            harness.Events, item => item.Kind == "SUBLOT_REJECTED");
        Assert.Contains("SUBLOT-001", rejected.Message, StringComparison.Ordinal);
        Assert.Contains("花篮容量未登记或有冲突", rejected.Message, StringComparison.Ordinal);
        Assert.True(harness.Session.Current.Connected);
    }

    /// <summary>
    /// 拒收里的 <c>currentWorklistRevision</c> 等于录入请求的修订：录入请求保留，操作员不等服务端重发请求就能
    /// 直接再扫一次。
    /// </summary>
    /// <remarks>
    /// 替身这里不重发录入请求（<c>ResendSublotEntryRequestAfterRejection</c> 保持关闭），所以第二次提交能发出去，
    /// 只能是因为车载端留住了原来那条请求。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-SUBLOT-REJECTED-AFTER-ENTRY")]
    public async Task ARejectionAtTheSameRevisionKeepsTheEntryRequestSoTheOperatorCanEnterAgain()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RejectionHarness harness = await RejectionHarness.StartAsync(
            server =>
            {
                server.RejectSublotSubmissionsWith = "SUBLOT_BOX_COUNT_UNAVAILABLE";
                server.RejectionWorklistRevision = 1;
            },
            token);

        await harness.Business.SubmitSublotAsync("SUBLOT-001", "SCANNER", token);
        await harness.WaitForEventAsync("SUBLOT_REJECTED", token);

        Assert.True(harness.Business.CanSubmitSublot);
        Assert.True(harness.Business.CurrentSublotRejection?.EntryRequestKept);
        await harness.Business.SubmitSublotAsync("SUBLOT-001", "SCANNER", token);
        await RejectionHarness.WaitUntilAsync(
            () => harness.SubmissionCount == 2,
            "the second entry to reach the server without a resent entry request",
            token);
        Assert.Equal(
            1,
            harness.Server.SentEnvelopes.Count(envelope => envelope.MessageType == "SublotEntryRequested"));
    }

    /// <summary>
    /// 操作员再录入一次，上一次的拒收原因就从提示区撤下；新的拒收到来时换成新的那条。
    /// </summary>
    [Fact]
    public async Task EnteringAgainWithdrawsThePreviousRejection()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RejectionHarness harness = await RejectionHarness.StartAsync(
            server => server.RejectSublotSubmissionsWith = "SUBLOT_NOT_IN_DISPATCH_SCOPE",
            token);
        await harness.Business.SubmitSublotAsync("SUBLOT-001", "SCANNER", token);
        await harness.WaitForEventAsync("SUBLOT_REJECTED", token);
        WireToGateSublotRejection first = Assert.IsType<WireToGateSublotRejection>(
            harness.Business.CurrentSublotRejection);

        harness.Server.RejectSublotSubmissionsWith = null;
        await harness.Business.SubmitSublotAsync("SUBLOT-001", "KEYBOARD", token);

        Assert.Null(harness.Business.CurrentSublotRejection);
        Assert.Equal("SUBLOT_NOT_IN_DISPATCH_SCOPE", first.ReasonCode);
    }

    /// <summary>
    /// 服务端为另一个操作会话发来录入请求（停靠已经换了），上一站的拒收原因撤下；同一会话重发的请求不撤。
    /// </summary>
    [Fact]
    public async Task AnEntryRequestForAnotherOperationSessionWithdrawsTheRejection()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RejectionHarness harness = await RejectionHarness.StartAsync(
            server =>
            {
                server.RejectSublotSubmissionsWith = "SUBLOT_NOT_IN_DISPATCH_SCOPE";
                server.ResendSublotEntryRequestAfterRejection = true;
            },
            token);

        await harness.Business.SubmitSublotAsync("SUBLOT-001", "SCANNER", token);
        await harness.WaitForEventAsync("SUBLOT_REJECTED", token);
        await RejectionHarness.WaitUntilAsync(
            () => harness.Events.Count(item => item.Kind == "SUBLOT_ENTRY_REQUESTED") == 2,
            "the same session's entry request to be resent",
            token);
        Assert.NotNull(harness.Business.CurrentSublotRejection);

        harness.Server.OperationSessionId = "66666666-6666-4666-8666-666666666666";
        await harness.Business.SubmitSublotAsync("SUBLOT-001", "SCANNER", token);
        await RejectionHarness.WaitUntilAsync(
            () => harness.Events.Count(item => item.Kind == "SUBLOT_ENTRY_REQUESTED") == 3,
            "the next session's entry request to arrive",
            token);

        Assert.Null(harness.Business.CurrentSublotRejection);
    }

    /// <summary>
    /// 拒收里的修订比录入请求新：清单已经变了，录入请求清掉，再扫会在本地被拒，提示操作员等服务端的新请求。
    /// 这里拒收带了 <c>demandId</c>，与 null 的情形显示方式相同。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-SUBLOT-REJECTED-AFTER-ENTRY")]
    public async Task ARejectionAtANewerRevisionClearsTheEntryRequest()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RejectionHarness harness = await RejectionHarness.StartAsync(
            server =>
            {
                server.RejectSublotSubmissionsWith = "EXPECTED_BASKET_COUNT_MISMATCH";
                server.RejectionWorklistRevision = 2;
                server.RejectionDemandId = "11111111-1111-1111-1111-111111111111";
            },
            token);

        await harness.Business.SubmitSublotAsync("SUBLOT-001", "SCANNER", token);
        WireToGateOperatorEvent rejected = await harness.WaitForEventAsync("SUBLOT_REJECTED", token);

        Assert.False(harness.Business.CanSubmitSublot);
        WireToGateSublotRejection notice = Assert.IsType<WireToGateSublotRejection>(
            harness.Business.CurrentSublotRejection);
        Assert.False(notice.EntryRequestKept);
        Assert.Equal("11111111-1111-1111-1111-111111111111", notice.DemandId);
        Assert.Contains("花篮数量与已预留仓位数不符", rejected.Message, StringComparison.Ordinal);
        Assert.Contains("等待服务端新的录入请求", rejected.Message, StringComparison.Ordinal);
        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Business.SubmitSublotAsync("SUBLOT-001", "SCANNER", token));
        Assert.Equal("WIRE_TO_GATE_JOURNEY_NOT_READY", refused.Message);
        Assert.Equal(1, harness.SubmissionCount);
    }

    /// <summary>
    /// 拒收之后服务端直接开始装货（没有再录入）：车载端接手仓位命令时撤下拒收，提示区不在装货进行中挂着
    /// 上一次的拒收原因（#77 审查）。界面这一步照 <c>App</c> 的接法把 <see cref="MainViewModel"/> 接到业务服务上。
    /// </summary>
    [Fact]
    public async Task ALoadStartingAfterARejectionWithdrawsItFromThePromptArea()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RejectionHarness harness = await RejectionHarness.StartAsync(
            server =>
            {
                server.RejectSublotSubmissionsWith = "SUBLOT_NOT_IN_DISPATCH_SCOPE";
                server.SendSlotOperationCommandAfterRejection = true;
            },
            token);
        await using OnboardController controller = Controller();
        MainViewModel viewModel = new(
            controller,
            new RecordingLogger(),
            "agv02",
            OnboardActiveSlotConfigurationFactory.Create(new WireToGateSettings(), new IoModuleSettings()))
        {
            StationDepartureCountdownDispatcher = null
        };
        await viewModel.InitializeAsync();
        viewModel.ConfigureWireToGate(
            (sublot, _, cancellationToken) => harness.Business.SubmitSublotAsync(sublot, "SCANNER", cancellationToken),
            () => harness.Business.CanSubmitSublot,
            sublotRejection: () => harness.Business.CurrentSublotRejection);
        viewModel.UpdateWireToGateStatus(harness.Session.Current);
        ConcurrentQueue<(string Kind, bool HasSublotRejection)> shown = new();
        harness.Business.OperatorEventPublished += (_, args) =>
        {
            viewModel.ApplyWireToGateOperatorEvent(args.Value);
            shown.Enqueue((args.Value.Kind, viewModel.HasSublotRejection));
        };

        await harness.Business.SubmitSublotAsync("SUBLOT-001", "SCANNER", token);
        await RejectionHarness.WaitUntilAsync(
            () => shown.Any(item => item.Kind == "OPERATION_PROGRESS"),
            "the slot operation to start after the rejection",
            token);

        Assert.Contains(shown, item => item is { Kind: "SUBLOT_REJECTED", HasSublotRejection: true });
        Assert.Null(harness.Business.CurrentSublotRejection);
        Assert.False(viewModel.HasSublotRejection);
        Assert.All(
            shown.Where(item => item.Kind == "OPERATION_PROGRESS"),
            item => Assert.False(item.HasSublotRejection));
    }

    private static OnboardController Controller() => new(
        new FakeIoModuleClient(),
        new IdleRuleGateway(),
        new RecordingLogger(),
        new SystemClock(),
        new OnboardWorkflowOptions(
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(3),
            TimeSpan.FromMinutes(2),
            TimeSpan.FromMilliseconds(300),
            TimeSpan.FromSeconds(1),
            128,
            2));

    private sealed class IdleRuleGateway : IRuleGateway
    {
        public bool IsConnected => false;

        public VisitContext? CurrentVisit => null;

        public event EventHandler<ValueChangedEventArgs<bool>>? ConnectionChanged;

        public event EventHandler<ValueChangedEventArgs<VisitContext?>>? VisitChanged;

        public Task StartAsync(CancellationToken applicationStopping) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ScanAuthorization> VerifyScanAsync(
            ScanVerificationRequest request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("本测试不扫码。");

        public Task<bool> ReportOperationAsync(OperationResult result, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("本测试不上报。");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RejectionHarness : IAsyncDisposable
    {
        private readonly FakeControlServer _server;
        private readonly WireToGateSessionService _session;
        private readonly ConcurrentQueue<WireToGateOperatorEvent> _events;

        private RejectionHarness(
            FakeControlServer server,
            WireToGateSessionService session,
            WireToGateBusinessService business,
            ConcurrentQueue<WireToGateOperatorEvent> events)
        {
            _server = server;
            _session = session;
            _events = events;
            Business = business;
        }

        public WireToGateBusinessService Business { get; }

        public WireToGateSessionService Session => _session;

        public FakeControlServer Server => _server;

        public IReadOnlyList<WireToGateOperatorEvent> Events => [.. _events];

        public int SubmissionCount =>
            _server.ReceivedEnvelopes.Count(envelope => envelope.MessageType == "SublotSubmitted");

        public static async Task<RejectionHarness> StartAsync(
            Action<FakeControlServer> configure,
            CancellationToken cancellationToken)
        {
            FakeControlServer server = new(IPAddress.Loopback)
            {
                SendReadinessAfterRecoveryAck = true,
                SendJourneySnapshotsAfterRecovery = true,
                OperationSessionId = OperationSessionId,
                SublotEntryExpectedSublots = ["SUBLOT-001"]
            };
            configure(server);

            try
            {
                FakeIoModuleClient io = new();
                RecordingLogger logger = new();
                StoppedVehicle safety = new();
                string journalPath = Path.Combine(
                    Path.GetTempPath(), "w2g-sublot-rejected", Guid.NewGuid().ToString("N"), "journal.db");
                Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);

                WireToGateSessionService session = new(
                    new WireToGateSessionOptions(
                        "127.0.0.1",
                        server.Port,
                        "AGV-8005-01",
                        Guid.NewGuid().ToString("D"),
                        new string('a', 40),
                        CredentialVariable,
                        G2SessionTimeouts.Connect,
                        G2SessionTimeouts.Message,
                        1,
                        1,
                        "eight-slot-v1",
                        "eight-slot-modbus-v1",
                        SupportsBatchUnlock: false),
                    io,
                    new SqliteWireToGateJournal(journalPath),
                    logger,
                    new SystemClock(),
                    safety,
                    new OnboardAlarmBoard("AGV-8005-01", TimeProvider.System),
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
                    () => safety.Read().MotionState == VehicleMotionState.Stopped,
                    new WireToGateSlotOperationExecutorOptions(
                        TimeSpan.FromSeconds(1),
                        TimeSpan.FromSeconds(1),
                        TimeSpan.FromSeconds(2),
                        TimeSpan.FromMilliseconds(10),
                        TimeSpan.FromSeconds(30)),
                    OperatorVariable,
                    safety,
                    TimeSpan.FromSeconds(30),
                    TimeSpan.FromMilliseconds(500));
                ConcurrentQueue<WireToGateOperatorEvent> events = new();
                business.OperatorEventPublished += (_, args) => events.Enqueue(args.Value);

                await session.Client.ConnectAndRecoverAsync(cancellationToken);
                business.Start();
                await WaitUntilAsync(
                    () => business.CanSubmitSublot
                        && session.CurrentJourney.CurrentStopWorklist is not null,
                    "the entry request and its worklist to arrive",
                    cancellationToken);

                return new RejectionHarness(server, session, business, events);
            }
            catch
            {
                await server.DisposeAsync();
                throw;
            }
        }

        public async Task<WireToGateOperatorEvent> WaitForEventAsync(
            string kind,
            CancellationToken cancellationToken)
        {
            await WaitUntilAsync(
                () => _events.Any(item => item.Kind == kind),
                $"an operator event of kind {kind}",
                cancellationToken);
            return _events.Last(item => item.Kind == kind);
        }

        public static async Task WaitUntilAsync(
            Func<bool> predicate,
            string expectation,
            CancellationToken cancellationToken)
        {
            StallAwareDeadline deadline = new(TimeSpan.FromSeconds(5));
            while (!predicate())
            {
                if (deadline.HasExpired)
                {
                    Assert.Fail($"Timed out after {deadline.Describe()} waiting for: {expectation}");
                }

                await deadline.PollAsync(cancellationToken);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Business.DisposeAsync();
            await _session.DisposeAsync();
            await _server.DisposeAsync();
        }

        private sealed class StoppedVehicle : IVehicleSafetySignalProvider
        {
            public VehicleSafetySignal Read() =>
                new(VehicleMotionState.Stopped, DateTimeOffset.UtcNow, "SUBLOT_REJECTED_TEST");
        }
    }
}
