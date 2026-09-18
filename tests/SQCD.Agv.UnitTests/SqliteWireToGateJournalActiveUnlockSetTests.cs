using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// The journal's own guard on REQ-0357 and ADR-cross-0061: the active unlock set is the one door that
/// may be standing open, so it holds zero or one slot. The executors keep to that already; the journal
/// refuses to record anything else, so a regression in any writer fails at the write instead of leaving
/// a second open door on disk for recovery to trust.
/// </summary>
public sealed class SqliteWireToGateJournalActiveUnlockSetTests
{
    [Fact]
    public async Task AnActiveUnlockSetOfMoreThanOneSlotIsRefused()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using SqliteWireToGateJournal journal = await CreateJournalAsync(token);

        InvalidDataException refused = await Assert.ThrowsAsync<InvalidDataException>(() =>
            journal.WriteRecoveryStateAsync(State([1, 2]), token));

        Assert.Contains("ACTIVE_UNLOCK_SET", refused.Message, StringComparison.Ordinal);
        Assert.Empty((await journal.ReadRecoveryStateAsync(token)).ActiveUnlockSlots);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task AnActiveUnlockSetOfAtMostOneSlotIsRecorded(int activeSlot)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        int[] active = activeSlot == 0 ? [] : [activeSlot];
        await using SqliteWireToGateJournal journal = await CreateJournalAsync(token);

        await journal.WriteRecoveryStateAsync(State(active), token);

        Assert.Equal(active, (await journal.ReadRecoveryStateAsync(token)).ActiveUnlockSlots);
    }

    private static WireToGateRecoveryState State(IReadOnlyList<int> active) =>
        new(
            "11111111-1111-4111-8111-111111111111",
            WireToGateRecoveryCheckpoint.ActiveUnlockSet,
            active,
            0,
            []);

    private static async Task<SqliteWireToGateJournal> CreateJournalAsync(CancellationToken token)
    {
        string directory = Path.Combine(Path.GetTempPath(), "w2g-journal-active-set", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        SqliteWireToGateJournal journal = new(Path.Combine(directory, "journal.db"));
        await journal.InitializeAsync(token);
        return journal;
    }
}
