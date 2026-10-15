using Microsoft.Data.Sqlite;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// 车上每条已装需求各留一份装货上下文（8005-agv-onboard-hmi#209）：列表的增删、日志簿的存取，以及与没有这个字段的版本之间
/// 互相读写。
/// </summary>
public sealed class WireToGateLoadedDemandsTests
{
    private const string DemandA = "aaaaaaaa-0000-4000-8000-00000000000a";
    private const string DemandB = "bbbbbbbb-0000-4000-8000-00000000000b";
    private const string AttemptA = "44444444-0000-4444-8444-00000000000a";
    private const string AttemptA2 = "44444444-0000-4444-8444-0000000000a2";
    private const string AttemptB = "44444444-0000-4444-8444-00000000000b";

    [Fact]
    public void ALaterLoadOfTheSameDemandReplacesTheEarlierOne()
    {
        WireToGateRecoveryState state = WireToGateRecoveryState.Empty
            .WithLoadOnBoard(Load(DemandA, AttemptA, [1]))
            .WithLoadOnBoard(Load(DemandB, AttemptB, [5]))
            .WithLoadOnBoard(Load(DemandA, AttemptA2, [2]));

        Assert.Equal(
            [(DemandB, AttemptB), (DemandA, AttemptA2)],
            state.SettledLoadSubjects().Select(load => (load.DemandId, load.Load.SlotOperationAttemptId)));
    }

    [Fact]
    public void AJournalWithoutTheListOffersItsLastLoadAndTakingThatOneOffLeavesNone()
    {
        WireToGateRecoveryState old = WireToGateRecoveryState.Empty with
        {
            LastCompletedLoadOperationContext = Load(DemandB, AttemptB, [5])
        };

        Assert.Null(old.LoadsOnBoard);
        Assert.Equal([DemandB], old.SettledLoadSubjects().Select(load => load.DemandId));
        Assert.Empty(old.WithoutLoadOnBoard(DemandB).SettledLoadSubjects());
        // Another demand's ending leaves the last load where it was.
        Assert.Equal([DemandB], old.WithoutLoadOnBoard(DemandA).SettledLoadSubjects().Select(load => load.DemandId));
    }

    /// <summary>
    /// The first load after an upgrade starts the list empty: the last load an older journal kept may belong to a journey
    /// long delivered, and seeded into the list it would stand beside the new load as a second subject.
    /// </summary>
    [Fact]
    public void TheFirstLoadAfterAnUpgradeDoesNotCarryTheOldLastLoadIntoTheList()
    {
        WireToGateRecoveryState old = WireToGateRecoveryState.Empty with
        {
            LastCompletedLoadOperationContext = Load(DemandA, AttemptA, [1])
        };

        Assert.Equal([DemandB], old.WithLoadOnBoard(Load(DemandB, AttemptB, [5])).SettledLoadSubjects()
            .Select(load => load.DemandId));
    }

    [Fact]
    public async Task TheListIsKeptByTheJournal()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        (SqliteWireToGateJournal journal, _) = await CreateJournalAsync(token);
        await using (journal)
        {
            await journal.UpdateRecoveryStateAsync(
                state => state
                    .WithLoadOnBoard(Load(DemandA, AttemptA, [1, 2]))
                    .WithLoadOnBoard(Load(DemandB, AttemptB, [5])),
                token);

            WireToGateRecoveryState read = await journal.ReadRecoveryStateAsync(token);
            Assert.Equal(
                ["aaaaaaaa-0000-4000-8000-00000000000a:1,2", "bbbbbbbb-0000-4000-8000-00000000000b:5"],
                read.LoadsOnBoard!.Select(load => $"{load.DemandId}:{string.Join(",", load.Load.Slots)}"));
        }
    }

    [Fact]
    public async Task TwoLoadsOfOneDemandAreRefusedByTheJournal()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        (SqliteWireToGateJournal journal, _) = await CreateJournalAsync(token);
        await using (journal)
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => journal.UpdateRecoveryStateAsync(
                state => state with
                {
                    LoadsOnBoard = [new(Load(DemandA, AttemptA, [1])), new(Load(DemandA, AttemptA2, [2]))]
                },
                token));
            Assert.Null((await journal.ReadRecoveryStateAsync(token)).LoadsOnBoard);
        }
    }

    /// <summary>
    /// Both directions of a version change go through the same reader (<c>ReadRecoveryStateCoreAsync</c>: System.Text.Json
    /// with the web defaults, no <c>JsonUnmappedMemberHandling.Disallow</c>). A journal written before the list existed has
    /// no such field and reads as <c>null</c>; a field this version does not know -- what the list is to the version before
    /// it -- is skipped, not refused.
    /// </summary>
    [Fact]
    public async Task AJournalFromAnotherVersionIsReadWithTheFieldsThisOneKnows()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        (SqliteWireToGateJournal journal, string path) = await CreateJournalAsync(token);
        await using (journal)
        {
            await journal.UpdateRecoveryStateAsync(
                state => state with { LastCompletedLoadOperationContext = Load(DemandB, AttemptB, [5]) },
                token);
            string json = await ReadContentJsonAsync(path, token);
            Assert.DoesNotContain("loadsOnBoard\":[", json, StringComparison.Ordinal);

            await WriteContentJsonAsync(path, json.Insert(1, "\"aFieldFromAnotherVersion\":[{\"demandId\":\"x\"}],"), token);
            WireToGateRecoveryState read = await journal.ReadRecoveryStateAsync(token);

            Assert.Null(read.LoadsOnBoard);
            Assert.Equal([DemandB], read.SettledLoadSubjects().Select(load => load.DemandId));
        }
    }

    /// <summary>A handed-off load stays a subject, marked; the other loads are untouched (review S4).</summary>
    [Fact]
    public void AHandedOffLoadStaysOnBoardMarked()
    {
        WireToGateRecoveryState state = TwoOnBoard().WithLoadHandedOff(DemandA);

        Assert.Equal(
            [(DemandA, true), (DemandB, false)],
            state.SettledLoadSubjects().Select(load => (load.DemandId, load.HandedOffAwaitingServer)));
    }

    /// <summary>The journey's closure -- a business state with no transport purpose -- empties the list.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("IDLE_RETURN")]
    [InlineData("CHARGING")]
    [InlineData("CLEARING_MAINTENANCE")]
    public void AClosedJourneyEmptiesTheLoadsOnBoard(string? purpose)
    {
        WireToGateRecoveryState state = TwoOnBoard().WithLoadsOnBoardFor(Journey(purpose, [DemandA]));

        Assert.Empty(state.LoadsOnBoard!);
    }

    /// <summary>A transport journey keeps them; a journey with no business state yet says nothing.</summary>
    [Fact]
    public void ATransportJourneyOrNoBusinessStateKeepsTheLoadsOnBoard()
    {
        WireToGateRecoveryState onBoard = TwoOnBoard();

        Assert.Equal(2, onBoard.WithLoadsOnBoardFor(Journey("TRANSPORT", [DemandA, DemandB])).LoadsOnBoard!.Count);
        Assert.Same(onBoard, onBoard.WithLoadsOnBoardFor(WireToGateJourneySnapshot.Empty));
    }

    /// <summary>
    /// A plan naming one demand is the journey's anchor: unstamped loads take it, and a later plan of another anchor drops
    /// them -- the closure of their journey was missed. A plan naming several demands stamps nothing and drops nothing.
    /// </summary>
    [Fact]
    public void APlanOfAnotherJourneyDropsTheLoadsOfAnEarlierOne()
    {
        const string NextAnchor = "cccccccc-0000-4000-8000-00000000000c";
        WireToGateRecoveryState stamped = TwoOnBoard().WithLoadsOnBoardFor(Journey("TRANSPORT", [DemandA]));
        Assert.Equal([DemandA, DemandA], stamped.LoadsOnBoard!.Select(load => load.JourneyAnchorDemandId));
        Assert.Same(stamped, stamped.WithLoadsOnBoardFor(Journey("TRANSPORT", [DemandA])));

        WireToGateRecoveryState next = stamped.WithLoadOnBoard(Load(NextAnchor, AttemptA2, [3]));
        WireToGateRecoveryState aligned = next.WithLoadsOnBoardFor(Journey("TRANSPORT", [NextAnchor]));

        Assert.Equal([NextAnchor], aligned.LoadsOnBoard!.Select(load => load.DemandId));
        Assert.Equal(NextAnchor, aligned.LoadsOnBoard![0].JourneyAnchorDemandId);

        WireToGateRecoveryState unstamped = TwoOnBoard().WithLoadsOnBoardFor(Journey("TRANSPORT", [DemandA, DemandB]));
        Assert.All(unstamped.LoadsOnBoard!, load => Assert.Null(load.JourneyAnchorDemandId));
    }

    /// <summary>
    /// The journey's closure also drops the last completed load (8005-agv-onboard-hmi#330): the load correction entry and the
    /// compensation fallback read it, and a vehicle gone on to charge or clear a charger was still offered both over the
    /// previous journey's sublot. With nothing on the list -- the shape the field met, the list emptied by an earlier
    /// snapshot -- it is dropped all the same.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("IDLE_RETURN")]
    [InlineData("CHARGING")]
    [InlineData("CLEARING_MAINTENANCE")]
    public void AClosedJourneyDropsTheLastCompletedLoad(string? purpose)
    {
        WireToGateRecoveryState withList = TwoOnBoard() with { LastCompletedLoadOperationContext = Load(DemandB, AttemptB, [5]) };
        WireToGateRecoveryState listEmptied = WireToGateRecoveryState.Empty with
        {
            LoadsOnBoard = [],
            LastCompletedLoadOperationContext = Load(DemandB, AttemptB, [5])
        };

        Assert.Null(withList.WithLoadsOnBoardFor(Journey(purpose, [])).LastCompletedLoadOperationContext);
        WireToGateRecoveryState dropped = listEmptied.WithLoadsOnBoardFor(Journey(purpose, []));
        Assert.Null(dropped.LastCompletedLoadOperationContext);
        Assert.Empty(dropped.SettledLoadSubjects());
        Assert.Same(dropped, dropped.WithLoadsOnBoardFor(Journey(purpose, [])));
    }

    /// <summary>A transport journey, or one with no business state yet, keeps the last completed load.</summary>
    [Fact]
    public void ATransportJourneyOrNoBusinessStateKeepsTheLastCompletedLoad()
    {
        WireToGateRecoveryState state = WireToGateRecoveryState.Empty with
        {
            LoadsOnBoard = [],
            LastCompletedLoadOperationContext = Load(DemandB, AttemptB, [5])
        };

        Assert.Same(state, state.WithLoadsOnBoardFor(Journey("TRANSPORT", [DemandB])));
        Assert.Same(state, state.WithLoadsOnBoardFor(WireToGateJourneySnapshot.Empty));
    }

    /// <summary>
    /// Anything still under way that may be about the last completed load keeps it through the closure: an unsettled
    /// operation, a recovery vector, a pending load cancellation, a bound recovery session, or one requested and not yet
    /// answered with its id (8005-agv-onboard-hmi#330 and its review L1). The
    /// loads on board go as before.
    /// </summary>
    [Theory]
    [InlineData("unsettled")]
    [InlineData("vector")]
    [InlineData("cancellation")]
    [InlineData("session")]
    [InlineData("requested")]
    public void AClosedJourneyKeepsTheLastCompletedLoadWhileRecoveryWorkIsUnderWay(string underWay)
    {
        WireToGateRecoveryOperationContext last = Load(DemandB, AttemptB, [5]);
        WireToGateRecoveryState state = TwoOnBoard() with { LastCompletedLoadOperationContext = last };
        state = underWay switch
        {
            "unsettled" => state with { UnsettledSlotOperationAttemptId = AttemptA2 },
            "vector" => state with
            {
                RecoveryVector = new WireToGateRecoveryVectorContext(
                    WireToGateRecoveryVectorTypes.LoadCorrection, Guid.NewGuid().ToString("D"), null, DemandB, AttemptB, null,
                    [5], null, "OP-1", "BADGE", null)
            },
            "cancellation" => state with
            {
                PendingLoadCancellation = new WireToGatePendingLoadCancellation(
                    Guid.NewGuid().ToString("D"), AttemptB, "OP-1", "BADGE", DateTimeOffset.UnixEpoch, "reason")
            },
            "session" => state with { ExceptionRecoverySessionId = Guid.NewGuid().ToString("D") },
            "requested" => state with { RecoverySessionRequestId = Guid.NewGuid().ToString("D") },
            _ => throw new ArgumentOutOfRangeException(nameof(underWay))
        };

        WireToGateRecoveryState next = state.WithLoadsOnBoardFor(Journey("CLEARING_MAINTENANCE", []));

        Assert.Same(last, next.LastCompletedLoadOperationContext);
        Assert.Empty(next.LoadsOnBoard!);
    }

    // ---- 8005-agv-onboard-hmi#352: which stop a load on board was made at ----

    /// <summary>
    /// A load is stamped with the stop the vehicle stands at when a worklist carries its operation session: the pickup leg's
    /// <c>movementLegId</c>, and its station. That is what lets a machine pickup whose last item was cancelled still know a
    /// basket went on board there (C11's follow-up ②), and the stop -- not the station -- is the unit (review M3).
    /// </summary>
    [Fact]
    public void ALoadIsStampedWithTheStopOfTheWorklistOfItsOperationSession()
    {
        WireToGateRecoveryState next = TwoOnBoard().WithLoadsOnBoardFor(AtPickup("leg-1", "ST-WB-01", LoadSession));

        Assert.All(next.LoadsOnBoard!, load => Assert.Equal(("ST-WB-01", "leg-1"), (load.StationId, load.StopLegId)));
        Assert.True(next.HasLoadOnBoardAtStop("leg-1"));
        Assert.False(next.HasLoadOnBoardAtStop("leg-2"));
    }

    /// <summary>Another operation session, or a worklist with none -- every ended stop's -- stamps nothing.</summary>
    [Theory]
    [InlineData("55555555-0000-4555-8555-000000000002")]
    [InlineData(null)]
    public void AWorklistOfAnotherOrNoOperationSessionStampsNothing(string? session)
    {
        WireToGateRecoveryState next = TwoOnBoard().WithLoadsOnBoardFor(AtPickup("leg-1", "ST-WB-01", session));

        Assert.All(next.LoadsOnBoard!, load => Assert.Null(load.StopLegId));
        Assert.False(next.HasLoadOnBoardAtStop("leg-1"));
    }

    /// <summary>
    /// No stop to name -- no plan, or the current leg is not the pickup at the worklist's station -- stamps nothing, so a later
    /// worklist of the same session at the stop can still stamp it.
    /// </summary>
    [Fact]
    public void WithoutThePickupLegAtTheWorklistsStationNothingIsStamped()
    {
        WireToGateRecoveryState noPlan = TwoOnBoard().WithLoadsOnBoardFor(
            WireToGateJourneySnapshot.Empty with { CurrentStopWorklist = WorklistAt("ST-WB-01", LoadSession) });
        WireToGateRecoveryState otherStation = TwoOnBoard().WithLoadsOnBoardFor(AtPickup("leg-1", "ST-WB-02", LoadSession) with
        {
            CurrentStopWorklist = WorklistAt("ST-WB-01", LoadSession)
        });

        Assert.All(noPlan.LoadsOnBoard!, load => Assert.Null(load.StopLegId));
        Assert.All(otherStation.LoadsOnBoard!, load => Assert.Null(load.StopLegId));
    }

    /// <summary>The stamp is written once: the same session seen later at another stop does not move it.</summary>
    [Fact]
    public void AStampIsNotMoved()
    {
        WireToGateRecoveryState stamped = TwoOnBoard().WithLoadsOnBoardFor(AtPickup("leg-1", "ST-WB-01", LoadSession));
        WireToGateRecoveryState again = stamped.WithLoadsOnBoardFor(AtPickup("leg-2", "ST-WB-02", LoadSession));

        Assert.Same(stamped, again);
    }

    /// <summary>
    /// Review M3 (b): the same station visited again as a second stop -- its own leg -- where nothing was loaded and the last
    /// item was cancelled. The basket of the first stop is no basket of this one.
    /// </summary>
    [Fact]
    public void ASecondStopAtTheSameStationDoesNotInheritTheFirstStopsBasket()
    {
        WireToGateRecoveryState stamped = TwoOnBoard().WithLoadsOnBoardFor(AtPickup("leg-1", "ST-WB-01", LoadSession));

        Assert.True(stamped.HasLoadOnBoardAtStop("leg-1"));
        Assert.False(stamped.HasLoadOnBoardAtStop("leg-3"));
    }

    /// <summary>
    /// Review M3 (a): an appended stop whose demands were all cancelled loaded nothing, whatever the stop before it loaded.
    /// </summary>
    [Fact]
    public void AnAppendedStopWhoseDemandsWereAllCancelledHasNoBasket()
    {
        WireToGateRecoveryState stamped = TwoOnBoard()
            .WithLoadsOnBoardFor(AtPickup("leg-1", "ST-WB-01", LoadSession))
            .WithLoadsOnBoardFor(AtPickup("leg-2", "ST-WB-02", null));

        Assert.False(stamped.HasLoadOnBoardAtStop("leg-2"));
    }

    /// <summary>A load handed off, awaiting the server, is no basket the stop can be said to have loaded.</summary>
    [Fact]
    public void AHandedOffLoadDoesNotCountAsLoadedAtItsStop()
    {
        WireToGateRecoveryState stamped = WireToGateRecoveryState.Empty
            .WithLoadOnBoard(Load(DemandA, AttemptA, [1]))
            .WithLoadsOnBoardFor(AtPickup("leg-1", "ST-WB-01", LoadSession));

        Assert.True(stamped.HasLoadOnBoardAtStop("leg-1"));
        Assert.False(stamped.WithLoadHandedOff(DemandA).HasLoadOnBoardAtStop("leg-1"));
    }

    /// <summary>
    /// Review L1: the stamp is also taken from the journey on screen when the load is recorded, without waiting for the next
    /// snapshot -- and taking it there changes nothing else: a business state without a transport does not empty the list.
    /// </summary>
    [Fact]
    public void StampingFromTheJourneyOnScreenOnlyStamps()
    {
        WireToGateJourneySnapshot idleAtPickup = AtPickup("leg-1", "ST-WB-01", LoadSession) with
        {
            VehicleBusinessState = Journey("IDLE_RETURN", []).VehicleBusinessState
        };

        WireToGateRecoveryState stamped = TwoOnBoard().WithLoadStopsStampedFrom(idleAtPickup);

        Assert.Equal(2, stamped.LoadsOnBoard!.Count);
        Assert.True(stamped.HasLoadOnBoardAtStop("leg-1"));
        Assert.Same(stamped, stamped.WithLoadStopsStampedFrom(idleAtPickup));
    }

    [Fact]
    public async Task TheStampIsKeptByTheJournal()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        (SqliteWireToGateJournal journal, _) = await CreateJournalAsync(token);
        await using (journal)
        {
            await journal.UpdateRecoveryStateAsync(
                state => state
                    .WithLoadOnBoard(Load(DemandA, AttemptA, [1]))
                    .WithLoadsOnBoardFor(AtPickup("leg-1", "ST-WB-01", LoadSession)),
                token);

            Assert.True((await journal.ReadRecoveryStateAsync(token)).HasLoadOnBoardAtStop("leg-1"));
        }
    }

    private const string LoadSession = "55555555-0000-4555-8555-000000000001";

    private static WireToGateCurrentStopWorklist WorklistAt(string stationId, string? operationSessionId) =>
        new(stationId, 3, operationSessionId, null, [], new string('0', 64), "COMPLETED");

    /// <summary>The vehicle standing at the pickup leg <paramref name="legId"/> at <paramref name="stationId"/>.</summary>
    private static WireToGateJourneySnapshot AtPickup(string legId, string stationId, string? operationSessionId) =>
        WireToGateJourneySnapshot.Empty with
        {
            CurrentStopWorklist = WorklistAt(stationId, operationSessionId),
            UpcomingStopPlan = new WireToGateUpcomingStopPlan(
                2,
                [
                    new WireToGateMovementLeg(legId, "TO_PICKUP", "BUSINESS", DemandA, null, 1, stationId, "MAP-26", "ARRIVED"),
                    new WireToGateMovementLeg("leg-9", "TO_DROPOFF", "BUSINESS", DemandA, null, 2, "WB-09", "MAP-26", "PLANNED")
                ],
                new string('0', 64))
        };

    private static WireToGateRecoveryState TwoOnBoard() =>
        WireToGateRecoveryState.Empty
            .WithLoadOnBoard(Load(DemandA, AttemptA, [1]))
            .WithLoadOnBoard(Load(DemandB, AttemptB, [5]));

    private static WireToGateJourneySnapshot Journey(string? purpose, string[] planDemands) =>
        WireToGateJourneySnapshot.Empty with
        {
            VehicleBusinessState = new WireToGateVehicleBusinessState(
                2,
                "READY",
                purpose,
                false,
                "SUFFICIENT",
                "NOT_CHARGING",
                null,
                [],
                new DateTimeOffset(2026, 10, 8, 0, 0, 0, TimeSpan.Zero),
                new string('0', 64)),
            UpcomingStopPlan = new WireToGateUpcomingStopPlan(
                2,
                [
                    .. planDemands.Select((demand, index) => new WireToGateMovementLeg(
                        $"leg-{index}",
                        "TO_DROPOFF",
                        "BUSINESS",
                        demand,
                        null,
                        index + 1,
                        "ST-01",
                        "MAP-26",
                        "PLANNED"))
                ],
                new string('0', 64))
        };

    private static WireToGateRecoveryOperationContext Load(string demandId, string attemptId, int[] slots) =>
        new(
            Guid.NewGuid().ToString("D"),
            null,
            1,
            new DateTimeOffset(2026, 10, 8, 0, 0, 0, TimeSpan.Zero),
            demandId,
            "55555555-0000-4555-8555-000000000001",
            attemptId,
            OperationType.Load,
            slots,
            slots.Length,
            true,
            new string('0', 64));

    private static async Task<(SqliteWireToGateJournal Journal, string Path)> CreateJournalAsync(CancellationToken token)
    {
        string directory = Path.Combine(Path.GetTempPath(), "w2g-journal-loaded-demands", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "journal.db");
        SqliteWireToGateJournal journal = new(path);
        await journal.InitializeAsync(token);
        return (journal, path);
    }

    private static async Task<string> ReadContentJsonAsync(string path, CancellationToken token)
    {
        await using SqliteConnection connection = new($"Data Source={path};Pooling=False");
        await connection.OpenAsync(token);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT ContentJson FROM WireToGateRecoveryState WHERE Id = 1";
        return (string)(await command.ExecuteScalarAsync(token))!;
    }

    private static async Task WriteContentJsonAsync(string path, string json, CancellationToken token)
    {
        await using SqliteConnection connection = new($"Data Source={path};Pooling=False");
        await connection.OpenAsync(token);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE WireToGateRecoveryState SET ContentJson = $json WHERE Id = 1";
        command.Parameters.AddWithValue("$json", json);
        await command.ExecuteNonQueryAsync(token);
    }
}
