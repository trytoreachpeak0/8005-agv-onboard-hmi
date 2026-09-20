using System.Net;
using System.Text.Json;
using SQCD.Agv.Application;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;
using SQCD.Agv.Wpf;
using SQCD.Agv.Wpf.ViewModels;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// A stop that carries more than one demand, and a plan of more than two legs, driven from the fake
/// control server through the real session client and business service into
/// <see cref="MainViewModel"/> (batch 7-13, <c>trytoreachpeak0/8005-agv-onboard-hmi#134</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the whole stack.</b> Widening the inbound check alone let two items through to five places
/// that assumed a single demand, and the one that crashes is on the UI thread: the view model's
/// <c>SingleOrDefault()</c> throws inside <c>RunOnUiThread</c>, and <c>App.OnDispatcherUnhandledException</c>
/// turns that into the fatal fault <c>UNHANDLED_UI_ERROR</c>, which stops the whole vehicle. The
/// harness wires the view model the way <c>App.xaml.cs</c> does, including that last step, so a
/// crash shows up here as the fault the operator would see.
/// </para>
/// </remarks>
public sealed partial class MultiDemandJourneyG2Tests
{
    private const string OperatorVariable = "W2G_G2_MULTI_DEMAND_OPERATOR";
    private const string CredentialVariable = "W2G_G2_MULTI_DEMAND_CREDENTIAL";
    private const string OperationSessionId = "77777777-7777-4777-8777-777777777777";
    private const string DemandA = "aaaaaaaa-0000-4000-8000-00000000000a";
    private const string DemandB = "bbbbbbbb-0000-4000-8000-00000000000b";

    static MultiDemandJourneyG2Tests()
    {
        Environment.SetEnvironmentVariable(CredentialVariable, "g2-multi-demand-credential");
        Environment.SetEnvironmentVariable(OperatorVariable, "operator-134");
    }

    /// <summary>
    /// The crash point: a worklist of two items reaches the view model, the journey gate and sublot
    /// entry, and none of the three throws or puts the vehicle into <c>UNHANDLED_UI_ERROR</c>.
    /// </summary>
    /// <remarks>
    /// Both items are pickups at <c>ST-01</c>; the plan names both demands, one per drop-off, so the
    /// worklist's demands are a subset of the plan's. The entry goes in for the second item, which a
    /// single-demand check would read as "not the expected sublot".
    /// </remarks>
    [Fact]
    public async Task TwoWorklistItemsPassTheViewModelTheJourneyGateAndEntryWithoutAnUnhandledUiError()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Harness harness = await Harness.StartAsync(
            server =>
            {
                server.SendJourneySnapshotsAfterRecovery = true;
                server.SublotEntryExpectedSublots = ["SUBLOT-A", "SUBLOT-B"];
                server.JourneySnapshotPayloads = new Dictionary<string, object>
                {
                    ["VehicleBusinessStateSnapshot"] = Payloads.BusinessState(1, loadingPhase: null),
                    ["CurrentStopWorklistSnapshot"] = Payloads.Worklist(1, Payloads.ItemA, Payloads.ItemB),
                    ["UpcomingStopPlanSnapshot"] = Payloads.Plan(1, Payloads.TwoDemandLegs)
                };
            },
            token);

        await harness.WaitUntilAsync(
            () => harness.UiErrors.Count > 0
                || harness.Business.CanSubmitSublot
                    && harness.Session.CurrentJourney.CurrentStopWorklist is not null
                    && harness.Session.CurrentJourney.UpcomingStopPlan is not null,
            "the two-item worklist, the plan and the entry request to arrive",
            token);

        Assert.Empty(harness.UiErrors);
        Assert.NotEqual("UNHANDLED_UI_ERROR", harness.Controller.Current.ErrorCode);
        Assert.Equal(2, harness.Session.CurrentJourney.CurrentStopWorklist!.Items.Count);
        // The journey gate App.xaml.cs hands the controller.
        Assert.True(harness.Session.CurrentJourney.CanAcceptSublotAt(
            DateTimeOffset.Now,
            TimeSpan.FromSeconds(30)));

        await harness.Business.SubmitSublotAsync("SUBLOT-B", "SCANNER", token);
        JsonElement submitted = await harness.WaitForSubmissionAsync(token);

        Assert.Equal("SUBLOT-B", submitted.GetProperty("sublot").GetString());
        Assert.Empty(harness.UiErrors);
        Assert.NotEqual("UNHANDLED_UI_ERROR", harness.Controller.Current.ErrorCode);
    }

    /// <summary>
    /// Snapshot payloads in the shape protocol v2 froze, for the stops these tests drive.
    /// </summary>
    internal static class Payloads
    {
        public static readonly object ItemA = Item(DemandA, "TD-A", "SUBLOT-A", "WIRE_TO_GATE", "PICKUP", 2);

        public static readonly object ItemB = Item(DemandB, "TD-B", "SUBLOT-B", "WIRE_TO_OPTICAL", "PICKUP", 1);

        /// <summary>Pick both up at ST-01, then drop A at the gate and B at the optical station.</summary>
        public static readonly object[] TwoDemandLegs =
        [
            Leg(1, "TO_PICKUP", "BUSINESS", DemandA, "ST-01", "ARRIVED"),
            Leg(2, "TO_DROPOFF", "BUSINESS", DemandA, "ST-GATE", "PLANNED"),
            Leg(3, "TO_DROPOFF", "BUSINESS", DemandB, "ST-OPT", "PLANNED")
        ];

        public static object Item(
            string demandId,
            string transportDemandKey,
            string sublot,
            string workType,
            string stopRole,
            int expectedBasketCount) =>
            new
            {
                demandId,
                transportDemandKey,
                sublot,
                workType,
                stopRole,
                expectedBasketCount
            };

        public static object Leg(
            int sequence,
            string? legType,
            string stopPurposeCategory,
            string? demandId,
            string stationId,
            string state) =>
            new
            {
                movementLegId = $"22222222-2222-4222-8222-{sequence:D12}",
                legType,
                stopPurposeCategory,
                demandId,
                publicStationFunction = (string?)null,
                sequence,
                stationId,
                mapId = "MAP-26",
                state
            };

        public static object Worklist(
            long revision,
            params object[] items) =>
            WorklistAt(revision, null, items);

        public static object WorklistAt(
            long revision,
            DateTimeOffset? stationDepartureDeadlineAt,
            params object[] items) =>
            new
            {
                stationId = "ST-01",
                worklistRevision = revision,
                operationSessionId = OperationSessionId,
                stationDepartureDeadlineAt,
                items
            };

        public static object Plan(long revision, IEnumerable<object> legs) =>
            new
            {
                planRevision = revision,
                legs = legs.ToArray()
            };

        public static object BusinessState(long revision, object? loadingPhase) =>
            new
            {
                vehicleBusinessStateRevision = revision,
                readiness = "READY",
                activePurpose = "TRANSPORT",
                manualChargingHold = false,
                batteryState = "SUFFICIENT",
                chargingCycleState = "NOT_CHARGING",
                loadingPhase,
                blockingFacts = Array.Empty<object>(),
                observedAt = DateTimeOffset.UtcNow
            };

        public static object LoadingPhase(
            string state,
            DateTimeOffset? cargoHoldingDeadlineAt = null,
            string? closedReason = null) =>
            new
            {
                state,
                cargoHoldingDeadlineAt,
                closedReason
            };
    }

    /// <summary>
    /// A real session service, business service, controller and view model against the fake server,
    /// wired the way <c>App.xaml.cs</c> wires them.
    /// </summary>
    internal sealed class Harness : IAsyncDisposable
    {
        private readonly object _sync = new();
        private readonly List<Exception> _uiErrors = [];
        private readonly List<WireToGateOperatorEvent> _events = [];

        private Harness(
            FakeControlServer server,
            FakeIoModuleClient io,
            string journalPath,
            WireToGateSessionService session,
            WireToGateBusinessService business,
            OnboardController controller,
            MainViewModel viewModel)
        {
            Server = server;
            Io = io;
            JournalPath = journalPath;
            Session = session;
            Business = business;
            Controller = controller;
            ViewModel = viewModel;
            // Subscribed here rather than beside the view-model handlers below: what a projection
            // carried is only visible on the event itself, and one raised during the handshake would
            // otherwise be over before a test could subscribe.
            business.OperatorEventPublished += (_, args) =>
            {
                lock (_events)
                {
                    _events.Add(args.Value);
                }
            };
        }

        /// <summary>Every operator event this vehicle has published, oldest first.</summary>
        public IReadOnlyList<WireToGateOperatorEvent> Events
        {
            get
            {
                lock (_events)
                {
                    return [.. _events];
                }
            }
        }

        public FakeControlServer Server { get; }

        public FakeIoModuleClient Io { get; }

        public string JournalPath { get; }

        public WireToGateSessionService Session { get; }

        public WireToGateBusinessService Business { get; }

        public OnboardController Controller { get; }

        public MainViewModel ViewModel { get; }

        /// <summary>What <c>App.OnDispatcherUnhandledException</c> would have caught on the UI thread.</summary>
        public IReadOnlyList<Exception> UiErrors
        {
            get
            {
                lock (_sync)
                {
                    return [.. _uiErrors];
                }
            }
        }

        public IReadOnlyList<string> Submissions =>
        [
            .. Server.ReceivedEnvelopes
                .Where(envelope => envelope.MessageType == "SublotSubmitted")
                .Select(envelope => envelope.WireLine)
        ];

        public static async Task<Harness> StartAsync(
            Action<FakeControlServer> configure,
            CancellationToken cancellationToken,
            string? journalPath = null,
            FakeIoModuleClient? io = null,
            Func<IWireToGateJournal, IWireToGateJournal>? wrapJournal = null)
        {
            FakeControlServer server = new(IPAddress.Loopback)
            {
                SendReadinessAfterRecoveryAck = true,
                OperationSessionId = OperationSessionId
            };
            configure(server);
            try
            {
                Harness started = await StartAgainstAsync(
                    server,
                    cancellationToken,
                    journalPath,
                    io,
                    wrapJournal);
                started._ownsServer = true;
                return started;
            }
            catch
            {
                await server.DisposeAsync();
                throw;
            }
        }

        /// <summary>
        /// A new vehicle process against a server that already exists: a restart. The harness that created
        /// the server keeps owning it.
        /// </summary>
        /// <param name="wrapJournal">
        /// A decorator over the journal the session, the business service and the operations feed all
        /// share, for a test that has to fix an interleaving rather than wait for one.
        /// </param>
        public static async Task<Harness> StartAgainstAsync(
            FakeControlServer server,
            CancellationToken cancellationToken,
            string? journalPath = null,
            FakeIoModuleClient? io = null,
            Func<IWireToGateJournal, IWireToGateJournal>? wrapJournal = null)
        {
            io ??= new FakeIoModuleClient();
            RecordingLogger logger = new();
            StoppedVehicle safety = new();
            journalPath ??= NewJournalPath();
            SqliteWireToGateJournal journal = new(journalPath);

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
                    1,
                    1,
                    "eight-slot-v1",
                    "eight-slot-modbus-v1",
                    SupportsBatchUnlock: false),
                io,
                wrapJournal?.Invoke(journal) ?? journal,
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
            OnboardController controller = new(
                io,
                new DisabledRuleGateway(),
                logger,
                new SystemClock(),
                new OnboardWorkflowOptions(
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromMilliseconds(10),
                    TimeSpan.FromSeconds(30),
                    128,
                    2),
                () => session.Current.Readiness == WireToGateSessionReadiness.Ready,
                () =>
                {
                    WireToGateJourneySnapshot journey = session.CurrentJourney;
                    return journey.CanAcceptSublotAt(DateTimeOffset.Now, TimeSpan.FromSeconds(30))
                        ? journey
                        : null;
                });
            MainViewModel viewModel = new(
                controller,
                logger,
                "agv02",
                // Slots 1-4 front, 5-8 rear: the layout the side of a worklist item is read from.
                OnboardActiveSlotConfigurationFactory.Create(new WireToGateSettings(), new IoModuleSettings()));
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
                TimeSpan.FromMilliseconds(500),
                recoveryOptions: null,
                // 与 App 接的是同一根线（8005-agv-onboard-hmi#171）。夹具不接，这里就证不到
                // 「锁存之后扫码真的被拒」——而那正是故障横幅对操作员说的那句话。
                fatalFaultLatched: () => controller.IsFatalFaultLatched);
            Harness harness = new(server, io, journalPath, session, business, controller, viewModel);

            session.StateChanged += (_, args) => harness.OnUiThread(() =>
            {
                viewModel.UpdateWireToGateStatus(args.Value);
                controller.RefreshExternalSafetyState();
            });
            JournaledOperationsFeed journaledOperations = new(
                session.Journal,
                state => harness.OnUiThread(() => viewModel.UpdateJournaledOperations(state)),
                logger);
            session.ServerCommandReceived += (_, args) =>
            {
                if (args.Value is WireToGateSlotOperationCommand command)
                {
                    harness.OnUiThread(() => viewModel.RecordSlotOperationCommand(command));
                }
            };
            session.JourneyChanged += (_, args) => harness.OnUiThread(() =>
            {
                viewModel.UpdateWireToGateJourney(args.Value);
                controller.RefreshExternalSafetyState();
                _ = journaledOperations.RefreshAsync();
            });
            business.SublotEntryRequested += (_, _) => harness.OnUiThread(viewModel.RefreshWireToGateInputState);
            business.OperatorEventPublished += (_, args) => harness.OnUiThread(() =>
            {
                viewModel.ApplyWireToGateOperatorEvent(args.Value);
                _ = journaledOperations.RefreshAsync();
            });
            viewModel.ConfigureWireToGate(
                (sublot, inputMethod, token) => business.SubmitSublotAsync(
                    sublot,
                    inputMethod == ScanInputMethod.Scanner ? "SCANNER" : "KEYBOARD",
                    token),
                () => business.CanSubmitSublot,
                () => business.CanRequestResumeAfterRepair,
                business.RequestResumeAfterRepairAsync,
                () => business.CanRequestLoadCancellation,
                (selectedDemandId, token) => business.RequestLoadCancellationAsync(
                    WireToGateBusinessService.LoadCancellationDefaultReason,
                    selectedDemandId,
                    token),
                () => business.IsLoadCancellationDemandSelectionRequired,
                () => business.CanRequestLoadCompensation,
                business.RequestLoadCompensationAsync,
                () => business.CanRequestLoadCorrection,
                token => business.RequestLoadCorrectionAsync(cancellationToken: token),
                () => business.CanRequestFaultCargoHandoff,
                business.RequestFaultCargoHandoffAsync,
                () => business.CanRequestForcedMechanicalRecovery,
                business.RequestForcedMechanicalRecoveryAsync,
                () => business.CanRequestManualChargingReturnToService,
                token => business.RequestManualChargingReturnToServiceAsync(cancellationToken: token),
                () => business.IsLoadCancellationBeforeSublotOpen,
                () => business.CurrentSublotRejection,
                () => business.RecoveryReasonAlreadyGiven,
                () => business.RecoveryFallbackDemandId);
            await viewModel.InitializeAsync();

            try
            {
                await session.Client.ConnectAndRecoverAsync(cancellationToken);
                business.Start();
                return harness;
            }
            catch
            {
                await business.DisposeAsync();
                await session.DisposeAsync();
                await controller.DisposeAsync();
                throw;
            }
        }

        /// <summary>
        /// Runs a view-model update the way the WPF dispatcher would, and treats what escapes it the way
        /// <c>App.OnDispatcherUnhandledException</c> does.
        /// </summary>
        private void OnUiThread(Action action)
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                lock (_sync)
                {
                    _uiErrors.Add(exception);
                }

                // The routing decision is the product's, read from the same registry
                // App.OnDispatcherUnhandledException reads (8005-agv-onboard-hmi#171). A copy of the
                // rule here would let this harness keep proving the old behaviour after the product
                // changed -- which is exactly what it did while "latch everything" was the rule.
                //
                // **代价，写下来免得下一个人以为 G2 还在守着分类**：夹具跟着登记表走，所以往
                // OperatorRejections 里加一个本该锁存的码，整个 G2 套件不会有任何东西变红
                // （审查，判据路条目 11）。守分类的是
                // LocalFailureCodeRegistryArchitectureTests，不是这里。这里仍然无条件
                // _uiErrors.Add，所以各处的 Assert.Empty(harness.UiErrors) 还有牙。
                switch (OnboardFailureClassification.Classify(exception))
                {
                    case OnboardCommandFailureKind.ControlledCancellation:
                        break;

                    case OnboardCommandFailureKind.OperatorRejection:
                        ViewModel.ReportOperatorRejection(exception.Message);
                        break;

                    default:
                        Controller.EnterFatalFault(
                            "UNHANDLED_UI_ERROR",
                            OnboardFatalFaultBanner.UnhandledUiError);
                        break;
                }
            }
        }

        /// <summary>
        /// The worklist rows as (sublot, side code), read without tripping over a rebuild.
        /// </summary>
        /// <remarks>
        /// <para>
        /// In the product every view-model update runs on the WPF dispatcher, so nothing reads a
        /// collection while it is being rebuilt. There is no dispatcher here: the journal read that
        /// gives a row its side publishes on whatever thread it finished on, and a test enumerating at
        /// that instant sees "Collection was modified". That is this harness's race, not the product's,
        /// so it is read again rather than asserted on.
        /// </para>
        /// <para>
        /// <b>The race has two exceptions, not one.</b> Enumerating across a change throws
        /// <c>InvalidOperationException</c>; an indexed read past a collection that has shrunk in the
        /// meantime throws <c>ArgumentOutOfRangeException</c>, which <c>Select</c> reaches through its
        /// <c>IList</c> fast path. Measured here on 2026-09-20: a rebuild of the worklist rows during
        /// a read produced the second one and failed a test for a reason that had nothing to do with
        /// what it was asserting.
        /// </para>
        /// </remarks>
        public (string Sublot, string SideCode)[] WorklistRows() =>
            ReadStable(() => ViewModel.WorklistItems.Select(row => (row.Sublot, row.SideCode)).ToArray());

        public string[] PlanLegStatuses() =>
            ReadStable(() => ViewModel.JourneyPlanLegs.Select(row => row.ItemStatus).ToArray());

        private static T[] ReadStable<T>(Func<T[]> read)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    return read();
                }
                catch (Exception exception)
                    when (exception is InvalidOperationException or ArgumentOutOfRangeException
                        && attempt < 50)
                {
                    Thread.Sleep(5);
                }
            }
        }

        public async Task<JsonElement> WaitForSubmissionAsync(CancellationToken cancellationToken)
        {
            await WaitUntilAsync(
                () => Submissions.Count > 0,
                "the control server to receive SublotSubmitted",
                cancellationToken);

            using JsonDocument document = JsonDocument.Parse(Submissions[0]);
            return document.RootElement.GetProperty("payload").Clone();
        }

        public async Task WaitUntilAsync(
            Func<bool> predicate,
            string expectation,
            CancellationToken cancellationToken)
        {
            StallAwareDeadline deadline = new(TimeSpan.FromSeconds(5));
            while (!predicate())
            {
                if (deadline.HasExpired)
                {
                    string errors = string.Join(" | ", UiErrors.Select(error => error.Message));
                    Assert.Fail(
                        $"Timed out after {deadline.Describe()} waiting for: {expectation}. UI errors: [{errors}]");
                }

                await deadline.PollAsync(cancellationToken);
            }
        }

        private bool _vehicleStopped;
        private bool _serverStopped;
        private bool _ownsServer;

        /// <summary>Stops this vehicle process, leaving the server and the journal in place.</summary>
        public async ValueTask StopVehicleAsync()
        {
            if (_vehicleStopped)
            {
                return;
            }

            _vehicleStopped = true;
            await Business.DisposeAsync();
            await Session.DisposeAsync();
            await Controller.DisposeAsync();
        }

        /// <summary>The control server goes away: its connections close under the running vehicle.</summary>
        public async ValueTask StopServerAsync()
        {
            if (_ownsServer && !_serverStopped)
            {
                _serverStopped = true;
                await Server.DisposeAsync();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await StopVehicleAsync();
            await StopServerAsync();
        }

        internal static string NewJournalPath()
        {
            string directory = Path.Combine(Path.GetTempPath(), "w2g-multi-demand", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, "journal.db");
        }

        private sealed class StoppedVehicle : IVehicleSafetySignalProvider
        {
            public VehicleSafetySignal Read() =>
                new(VehicleMotionState.Stopped, DateTimeOffset.UtcNow, "MULTI_DEMAND_TEST");
        }
    }
}
