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
            state.SettledLoadSubjects().Select(load => (load.DemandId, load.SlotOperationAttemptId)));
    }

    [Fact]
    public void AJournalWithoutTheListOffersItsLastLoadAndTakingThatOneOffLeavesNone()
    {
        WireToGateRecoveryState old = WireToGateRecoveryState.Empty with
        {
            LastCompletedLoadOperationContext = Load(DemandB, AttemptB, [5])
        };

        Assert.Null(old.LoadedDemandOperationContexts);
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
                read.LoadedDemandOperationContexts!.Select(load => $"{load.DemandId}:{string.Join(",", load.Slots)}"));
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
                    LoadedDemandOperationContexts = [Load(DemandA, AttemptA, [1]), Load(DemandA, AttemptA2, [2])]
                },
                token));
            Assert.Null((await journal.ReadRecoveryStateAsync(token)).LoadedDemandOperationContexts);
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
            Assert.DoesNotContain("loadedDemandOperationContexts\":[", json, StringComparison.Ordinal);

            await WriteContentJsonAsync(path, json.Insert(1, "\"aFieldFromAnotherVersion\":[{\"demandId\":\"x\"}],"), token);
            WireToGateRecoveryState read = await journal.ReadRecoveryStateAsync(token);

            Assert.Null(read.LoadedDemandOperationContexts);
            Assert.Equal([DemandB], read.SettledLoadSubjects().Select(load => load.DemandId));
        }
    }

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
