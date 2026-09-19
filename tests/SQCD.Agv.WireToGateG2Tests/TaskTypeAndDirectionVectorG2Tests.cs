using System.Net;
using System.Text.Json;
using SQCD.Agv.Application;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using SQCD.Agv.Wpf.ViewModels;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 旅程事实一行里的方向与任务类型（批次6-03，<c>trytoreachpeak0/8005-agv-onboard-hmi#115</c>）：服务端快照经
/// <see cref="MainViewModel"/> 真实的更新路径变成界面绑定的两个属性。
/// </summary>
/// <remarks>
/// 文案本身由单元测试 <c>WireToGateStopFactsTests</c> 覆盖；这里钉的是界面那一步。放在本项目是因为
/// <see cref="MainViewModel"/> 在 <c>net8.0-windows</c> 的 WPF 程序集里。
/// </remarks>
public sealed class TaskTypeAndDirectionVectorG2Tests
{
    private const string CredentialVariable = "W2G_G2_TASK_TYPE_CREDENTIAL";

    static TaskTypeAndDirectionVectorG2Tests()
    {
        Environment.SetEnvironmentVariable(CredentialVariable, "g2-test-credential");
    }

    /// <summary>
    /// <c>CV-REVERSED-DIRECTION-JOURNEY</c>, onboard half: a <c>STAGING_TO_WIRE</c> journey is
    /// received in the vector's order, both snapshots are acknowledged, and the direction shown is
    /// the one the server planned -- drop-off at the AREA machine, where <c>WIRE_TO_GATE</c> would
    /// pick up (<c>DISPLAY_DIRECTION_AS_PLANNED</c>).
    /// </summary>
    /// <remarks>
    /// The journey is the reversed one: the first leg went to the dispatch staging point to pick up
    /// and is complete, the second has arrived at the AREA machine to drop off. A direction derived
    /// from the task type would read <c>STAGING_TO_WIRE</c> by <c>WIRE_TO_GATE</c>'s rule -- "the
    /// AREA end is where you load" -- and show <c>取货</c> here.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-11")]
    [Trait("ProtocolVector", "CV-REVERSED-DIRECTION-JOURNEY")]
    public async Task AReversedJourneyShowsTheDirectionAsPlanned()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback)
        {
            SendReadinessAfterRecoveryAck = true,
            VectorJourneySnapshotsAfterRecovery = ["UpcomingStopPlanSnapshot", "CurrentStopWorklistSnapshot"],
            VectorPlanLegs =
            [
                ("TO_PICKUP", "COMPLETED", "ST-STAGING"),
                ("TO_DROPOFF", "ARRIVED", "ST-AREA-N01")
            ],
            JourneyWorkType = "STAGING_TO_WIRE",
            JourneyStopRole = "DROPOFF"
        };
        await using Harness harness = await Harness.StartAsync(server, token);

        await WaitUntilAsync(() => AcknowledgedKinds(server).Length == 2, token);

        Assert.Equal(["UPCOMING_STOP_PLAN", "CURRENT_STOP_WORKLIST"], AcknowledgedKinds(server));
        Assert.DoesNotContain(server.Received, item => item.MessageType == "ProtocolProblem");

        // What the operator saw after each snapshot: the plan alone, then plan and worklist.
        (bool HasWorklist, string Direction, string TaskType)[] seen =
        [
            .. harness.Seen
                .Where(item => item.HasPlan)
                .Select(item => (item.HasWorklist, item.Direction, item.TaskType))
                .Distinct()
        ];
        Assert.Equal(
            [(false, "卸货", string.Empty), (true, "卸货", "待送→焊线机台")],
            seen);
        Assert.Equal("ST-AREA-N01 / SUBLOT-001", harness.ViewModel.VisitText);
    }

    /// <summary>
    /// <c>CV-TASK-TYPE-ADMISSION-FAIL-CLOSED</c>, onboard half: a plan and a business state are
    /// received in the vector's order and both acknowledged, and with no worklist item no task type
    /// is shown -- not from the plan or its station function, and not from a <c>blockingFacts</c>
    /// entry that names one (<c>NEVER_INFER_UNBOUND_TASK_TYPE</c>).
    /// </summary>
    /// <remarks>
    /// The vector's other onboard assertion, <c>DISPLAY_ADMISSION_BLOCK_REASON</c>, is deliberately
    /// not claimed here or anywhere on this end: spec section 5.3 keeps the admission block reason on
    /// the control server and the dashboard and never sends it through <c>blockingFacts</c>, so the
    /// assertion has no producer. The conflict is registered for <c>protocol-v3.0.0</c> in
    /// <c>trytoreachpeak0/8005-agv-program#125</c>.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-10")]
    [Trait("ProtocolVector", "CV-TASK-TYPE-ADMISSION-FAIL-CLOSED")]
    public async Task AnUnboundTaskTypeIsNeverInferredFromThePlanOrTheBlockingFacts()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using FakeControlServer server = new(IPAddress.Loopback)
        {
            SendReadinessAfterRecoveryAck = true,
            VectorJourneySnapshotsAfterRecovery = ["UpcomingStopPlanSnapshot", "VehicleBusinessStateSnapshot"],
            VectorPlanLegs = [("TO_PICKUP", "PLANNED", "ST-STAGING")],
            // The v2 server keeps it null; a value here proves the station function is not read either.
            VectorPublicStationFunction = "WIRE_STAGING",
            VectorBlockingFacts = [("ACTION_NOT_ALLOWED_IN_STATE", "TASK_TYPE", "STAGING_TO_WIRE")]
        };
        await using Harness harness = await Harness.StartAsync(server, token);

        await WaitUntilAsync(() => AcknowledgedKinds(server).Length == 2, token);

        Assert.Equal(["UPCOMING_STOP_PLAN", "VEHICLE_BUSINESS_STATE"], AcknowledgedKinds(server));
        Assert.DoesNotContain(server.Received, item => item.MessageType == "ProtocolProblem");

        // The blocking fact did arrive -- what is proved is that it was not read as a task type.
        WireToGateJourneySnapshot journey = harness.Journey;
        Assert.Null(journey.CurrentStopWorklist);
        WireToGateBlockingFact fact = Assert.Single(journey.VehicleBusinessState!.BlockingFacts);
        Assert.Equal("STAGING_TO_WIRE", fact.SubjectId);
        Assert.Equal("WIRE_STAGING", Assert.Single(journey.UpcomingStopPlan!.Legs).PublicStationFunction);

        Assert.NotEmpty(harness.Seen);
        Assert.All(harness.Seen, item => Assert.Equal(string.Empty, item.TaskType));
        Assert.Equal(string.Empty, harness.ViewModel.TaskTypeText);
        Assert.Equal("旅程未同步", harness.ViewModel.VisitText);
    }

    [Fact]
    public async Task TheJourneyFactLineCarriesTheDirectionAndTheTaskTypeOfTheStop()
    {
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller);

        viewModel.UpdateWireToGateJourney(Journey("STAGING_TO_WIRE", "DROPOFF"));

        Assert.Equal("卸货", viewModel.StopDirectionText);
        Assert.Equal("待送→焊线机台", viewModel.TaskTypeText);
        Assert.Equal("ST-01 / SUBLOT-001", viewModel.VisitText);
    }

    [Fact]
    public async Task ALaterSnapshotReplacesBothAndAnEmptyJourneyClearsThem()
    {
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller);
        viewModel.UpdateWireToGateJourney(Journey("STAGING_TO_WIRE", "DROPOFF"));

        viewModel.UpdateWireToGateJourney(Journey("WIRE_TO_GATE", "PICKUP", revision: 2));

        Assert.Equal("取货", viewModel.StopDirectionText);
        Assert.Equal("焊线→质检关卡", viewModel.TaskTypeText);

        viewModel.UpdateWireToGateJourney(WireToGateJourneySnapshot.Empty);

        Assert.Equal(string.Empty, viewModel.StopDirectionText);
        Assert.Equal(string.Empty, viewModel.TaskTypeText);
    }

    [Fact]
    public async Task BothAreRaisedAsPropertyChangesSoTheBindingsFollow()
    {
        await using OnboardController controller = Controller();
        MainViewModel viewModel = await ViewModel(controller);
        List<string?> changed = [];
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        viewModel.UpdateWireToGateJourney(Journey("WIRE_TO_OPTICAL", "PICKUP"));

        Assert.Contains(nameof(MainViewModel.StopDirectionText), changed);
        Assert.Contains(nameof(MainViewModel.TaskTypeText), changed);
    }

    private static string[] AcknowledgedKinds(FakeControlServer server) =>
    [
        .. server.ReceivedEnvelopes
            .Where(item => item.MessageType == "SnapshotAppliedAck")
            .Select(item =>
            {
                using JsonDocument document = JsonDocument.Parse(item.WireLine);
                return document.RootElement.GetProperty("payload").GetProperty("snapshotKind").GetString()!;
            })
    ];

    private static async Task WaitUntilAsync(Func<bool> predicate, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!predicate())
        {
            await Task.Delay(5, timeout.Token);
        }
    }

    /// <summary>
    /// A real session client against the fake server, wired to a <see cref="MainViewModel"/> the way
    /// <c>App.xaml.cs</c> wires them: every <c>JourneyChanged</c> goes through
    /// <see cref="MainViewModel.UpdateWireToGateJourney"/>, and what the view model shows after each
    /// one is recorded.
    /// </summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly List<(bool HasPlan, bool HasWorklist, string Direction, string TaskType)> _seen = [];
        private readonly object _sync = new();
        private readonly OnboardController _controller;
        private readonly WireToGateSessionClient _client;

        private Harness(OnboardController controller, MainViewModel viewModel, WireToGateSessionClient client)
        {
            _controller = controller;
            ViewModel = viewModel;
            _client = client;
        }

        public MainViewModel ViewModel { get; }

        public WireToGateJourneySnapshot Journey => _client.CurrentJourney;

        public IReadOnlyList<(bool HasPlan, bool HasWorklist, string Direction, string TaskType)> Seen
        {
            get
            {
                lock (_sync)
                {
                    return [.. _seen];
                }
            }
        }

        public static async Task<Harness> StartAsync(FakeControlServer server, CancellationToken cancellationToken)
        {
            OnboardController controller = Controller();
            MainViewModel viewModel = await ViewModel(controller);
            WireToGateSessionClient client = CreateClient(server);
            Harness harness = new(controller, viewModel, client);
            client.JourneyChanged += (_, args) =>
            {
                viewModel.UpdateWireToGateJourney(args.Value);
                lock (harness._sync)
                {
                    harness._seen.Add((
                        args.Value.UpcomingStopPlan is not null,
                        args.Value.CurrentStopWorklist is not null,
                        viewModel.StopDirectionText,
                        viewModel.TaskTypeText));
                }
            };
            await client.ConnectAndRecoverAsync(cancellationToken);
            return harness;
        }

        public async ValueTask DisposeAsync()
        {
            await _client.DisposeAsync();
            await _controller.DisposeAsync();
        }

        private static WireToGateSessionClient CreateClient(FakeControlServer server) => new(
            new WireToGateSessionOptions(
                "127.0.0.1",
                server.Port,
                "AGV-8005-01",
                Guid.NewGuid().ToString("D"),
                new string('a', 40),
                CredentialVariable,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(2),
                1,
                1,
                "eight-slot-v1",
                "eight-slot-modbus-v1",
                SupportsBatchUnlock: false),
            new FakeIoModuleClient(),
            new SqliteWireToGateJournal(NewJournalPath()),
            new SystemClock(),
            new StoppedVehicle(),
            new OnboardAlarmBoard("AGV-G2", TimeProvider.System),
            new SlotConfigurationActivationCoordinator(
                new DocumentActiveSlotConfigurationStore(
                    new G2SlotConfigurationFixtures.InMemoryAtomicDocument(),
                    G2SlotConfigurationFixtures.Approved()),
                TimeProvider.System),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(500));

        private static string NewJournalPath()
        {
            string directory = Path.Combine(Path.GetTempPath(), "w2g-g2", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, "journal.db");
        }
    }

    private sealed class StoppedVehicle : IVehicleSafetySignalProvider
    {
        public VehicleSafetySignal Read() => new(VehicleMotionState.Stopped, DateTimeOffset.UtcNow, "G2_TEST");
    }

    private static WireToGateJourneySnapshot Journey(string workType, string stopRole, long revision = 1) => new(
        null,
        new WireToGateCurrentStopWorklist(
            "ST-01",
            revision,
            null,
            null,
            [new WireToGateWorklistItem(
                "11111111-1111-1111-1111-111111111111",
                "TD-001",
                "SUBLOT-001",
                workType,
                stopRole,
                1)],
            new string('a', 64)),
        null,
        DateTimeOffset.UnixEpoch);

    private static async Task<MainViewModel> ViewModel(OnboardController controller)
    {
        MainViewModel viewModel = new(
            controller,
            new RecordingLogger(),
            "agv02",
            OnboardActiveSlotConfigurationFactory.Create(new WireToGateSettings(), new IoModuleSettings()));
        await viewModel.InitializeAsync();
        return viewModel;
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
}
