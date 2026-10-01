using Microsoft.Data.Sqlite;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// The production journal reports SQLite failing underneath it as <see cref="IOException"/>, from every member
/// (8005-agv-onboard-hmi#233).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it matters.</b> Everything that has to answer the control server when the journal fails -- a resume's
/// escape path and its result send, the interrupted settlement -- filters on <see cref="IOException"/> and its
/// three neighbours. <see cref="SqliteException"/> is none of them. Before this, a resume that had pulsed a door and
/// then met a read-only, full or locked journal went past every filter and answered nobody (review of PR #234:
/// SQLite Error 8 from a read-only file).
/// </para>
/// <para>
/// <b>Two shapes of the same failure.</b> A file that is not a database fails every member, reads included, at its
/// first statement (Error 26). A read-only file fails the writes (Error 8), which is the shape the review measured.
/// The last test counts the conversions against the members that take the journal's lock, so a member added later
/// without one fails here rather than in the field.
/// </para>
/// </remarks>
public sealed class JournalSqliteFailureTests
{
    private static readonly string AnyMessageId = "11111111-2222-4333-8444-555555555555";

    private static readonly WireToGateDurableMessage AnyMessage = new(
        "operation-result:any",
        "OperationResult",
        AnyMessageId,
        new string('a', 64),
        "{}\n",
        DateTimeOffset.UnixEpoch,
        false);

    public static TheoryData<string> Members() =>
    [
        "InitializeAsync",
        "ReadJournalEpochAsync",
        "ReadRecoveryStateAsync",
        "UpdateRecoveryStateAsync",
        "SaveOutgoingBeforeSendAsync",
        "ReplaceOutgoingForReplayAsync",
        "ReadOutgoingByDeduplicationKeyAsync",
        "ReadOutgoingByMessageIdAsync",
        "MarkOutgoingAcknowledgedAsync",
        "ReadUnacknowledgedOutgoingAsync",
        "ReadAppliedJourneySnapshotsAsync",
        "SaveAppliedJourneySnapshotAsync",
        "ComputeContentSha256Async"
    ];

    [Theory]
    [MemberData(nameof(Members))]
    public async Task EveryMemberReportsAFileThatIsNotADatabaseAsIOException(string member)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string path = await CreateInitializedJournalFileAsync(token);
        SqliteConnection.ClearAllPools();
        File.Delete(path + "-wal");
        File.Delete(path + "-shm");
        await File.WriteAllBytesAsync(path, Enumerable.Repeat((byte)'x', 8192).ToArray(), token);

        await using SqliteWireToGateJournal journal = new(path);
        IOException failure = await Assert.ThrowsAsync<IOException>(() => Call(journal, member, token));

        SqliteException cause = Assert.IsType<SqliteException>(failure.InnerException);
        Assert.Equal(26, cause.SqliteErrorCode);
    }

    [Fact]
    public async Task AWriteToAReadOnlyJournalIsReportedAsIOException()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string path = await CreateInitializedJournalFileAsync(token);
        SqliteConnection.ClearAllPools();
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            await using SqliteWireToGateJournal journal = new(path);
            IOException failure = await Assert.ThrowsAsync<IOException>(() =>
                journal.UpdateRecoveryStateAsync(
                    state => state with { RecoverySessionRequestId = Guid.NewGuid().ToString("D") },
                    token));

            SqliteException cause = Assert.IsType<SqliteException>(failure.InnerException);
            Assert.Equal(8, cause.SqliteErrorCode);
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    /// <summary>
    /// SQLite's constraint failure (Error 19) on an outbox insert is a business conflict, not the journal failing:
    /// it stays <see cref="InvalidDataException"/> <c>MESSAGE_ID_CONTENT_CONFLICT</c> and is not turned into an
    /// <see cref="IOException"/> by the catch every member has. Callers tell "refuse this message" from "the journal is
    /// down" by exactly that difference (second incremental review of PR #234: B19).
    /// </summary>
    [Fact]
    public async Task AMessageIdConflictOnTheOutboxStaysABusinessConflict()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string path = await CreateInitializedJournalFileAsync(token);
        await using SqliteWireToGateJournal journal = new(path);
        await journal.SaveOutgoingBeforeSendAsync(AnyMessage, token);

        InvalidDataException conflict = await Assert.ThrowsAsync<InvalidDataException>(() =>
            journal.SaveOutgoingBeforeSendAsync(
                AnyMessage with { DeduplicationKey = "operation-result:another-key" },
                token));

        Assert.Equal("MESSAGE_ID_CONTENT_CONFLICT", conflict.Message);
        SqliteException cause = Assert.IsType<SqliteException>(conflict.InnerException);
        Assert.Equal(19, cause.SqliteErrorCode);
    }

    [Fact]
    public void EveryMemberThatTakesTheJournalLockConvertsSqliteFailures()
    {
        string source = File.ReadAllText(Path.Combine(
            ProtocolIdentityArchitectureTests.RepositoryRoot(),
            "src",
            "SQCD.Agv.Infrastructure",
            "SqliteWireToGateJournal.cs")).ReplaceLineEndings("\n");
        int locked = Count(source, "await _gate.WaitAsync(");
        int converted = Count(
            source,
            "        catch (SqliteException exception)\n        {\n            throw JournalFailure(exception);\n        }\n        finally\n        {\n            _gate.Release();");

        Assert.True(locked > 0, "No member of the journal takes its lock: this guard no longer reads the right file.");
        Assert.Equal(Members().Count, locked);
        Assert.Equal(locked, converted);
    }

    private static int Count(string source, string needle)
    {
        int count = 0;
        for (int at = source.IndexOf(needle, StringComparison.Ordinal);
             at >= 0;
             at = source.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static async Task<string> CreateInitializedJournalFileAsync(CancellationToken token)
    {
        string path = Path.Combine(Path.GetTempPath(), "w2g-journal-failure", Guid.NewGuid().ToString("N"), "journal.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using SqliteWireToGateJournal setup = new(path);
        await setup.InitializeAsync(token);
        return path;
    }

    private static Task Call(SqliteWireToGateJournal journal, string member, CancellationToken token) => member switch
    {
        "InitializeAsync" => journal.InitializeAsync(token),
        "ReadJournalEpochAsync" => journal.ReadJournalEpochAsync(token),
        "ReadRecoveryStateAsync" => journal.ReadRecoveryStateAsync(token),
        "UpdateRecoveryStateAsync" => journal.UpdateRecoveryStateAsync(state => state, token),
        "SaveOutgoingBeforeSendAsync" => journal.SaveOutgoingBeforeSendAsync(AnyMessage, token),
        "ReplaceOutgoingForReplayAsync" => journal.ReplaceOutgoingForReplayAsync(AnyMessage, AnyMessage, token),
        "ReadOutgoingByDeduplicationKeyAsync" => journal.ReadOutgoingByDeduplicationKeyAsync(AnyMessage.DeduplicationKey, token),
        "ReadOutgoingByMessageIdAsync" => journal.ReadOutgoingByMessageIdAsync(AnyMessageId, token),
        "MarkOutgoingAcknowledgedAsync" => journal.MarkOutgoingAcknowledgedAsync(AnyMessageId, AnyMessage.ContentSha256, token),
        "ReadUnacknowledgedOutgoingAsync" => journal.ReadUnacknowledgedOutgoingAsync(token),
        "ReadAppliedJourneySnapshotsAsync" => journal.ReadAppliedJourneySnapshotsAsync(token),
        "SaveAppliedJourneySnapshotAsync" => journal.SaveAppliedJourneySnapshotAsync(
            new WireToGateAppliedJourneySnapshot(
                "JourneyPlanSnapshot",
                AnyMessageId,
                1,
                new string('b', 64),
                "{}",
                DateTimeOffset.UnixEpoch),
            token),
        "ComputeContentSha256Async" => journal.ComputeContentSha256Async(token),
        _ => throw new ArgumentOutOfRangeException(nameof(member), member, null)
    };
}
